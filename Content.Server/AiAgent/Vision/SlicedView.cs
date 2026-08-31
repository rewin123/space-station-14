using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Content.Server.AiAgent.Threading;
using Content.Shared.Power.EntitySystems;
using Content.Shared.StationAi;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;
using Content.Shared.Silicons.StationAi;

namespace Content.Server.AiAgent.Vision;

/// <summary>
/// Обзор станционного ИИ, посчитанный ПО КУСКАМ — столько за кадр, сколько разрешает бюджет.
///
/// <para>
/// <b>Зачем понадобилась своя копия апстримового <c>StationAiVisionSystem.GetView</c>.</b>
/// Тот считает всё одним неделимым вызовом. Пока каждый <c>look</c> стоил круга через модель,
/// это было терпимо: один вызов на агента раз в четырнадцать секунд, и в нашем же комментарии
/// рядом было записано, что вызовы «ограничены тиком агента, а не могут спамиться». Режим Lua
/// это допущение снял — скрипт зовёт <c>look()</c> в цикле, — и в живом раунде 20.08.2026 главный
/// поток получил 280 перерасходов бюджета: в среднем 28 мс на вызов при кадре в 33 мс, худший 85.
/// </para>
/// <para>
/// Профиль по фазам не оставил выбора, куда чинить: <c>view=18.4 gather=3.4 rows=2.4</c> — три
/// четверти времени сидят в теневом касте, а не в построении строк. Ограничивать «не больше
/// одного обзора за кадр» бессмысленно, когда один обзор и есть кадр целиком; резать надо
/// ВНУТРИ него. Ровно для этого в <see cref="IWorldJob"/> и заведён <c>Step(JobBudget)</c>,
/// у которого до сих пор не было ни одной реализации кроме атомарной.
/// </para>
/// <para>
/// <b>Почему копия, а не кэш.</b> Кэш видимости пришлось бы инвалидировать по каждой открытой
/// двери и каждому сломанному окну, а промах такой инвалидации — это агент, который не видит
/// вошедшего человека и ведёт себя правдоподобно. Копия алгоритма стоит дороже в написании и
/// ничего не прячет: тот же вход даёт тот же выход, просто растянутый на несколько кадров.
/// </para>
/// <para>
/// <b>Алгоритм перенесён дословно</b> с <c>Content.Shared/Silicons/StationAi/StationAiVisionSystem.cs</c>
/// (он, в свою очередь, портирован из OpenDream <c>ViewAlgorithm.cs</c>). Менять его здесь нельзя:
/// расхождение с апстримом означало бы, что ИИ видит не то же, что видел бы игрок на этой роли, а
/// это прямое нарушение паритета. Эквивалентность стережёт тест, который гоняет обе реализации
/// по одной станции в одном кадре и требует совпадения множеств тайл в тайл.
/// </para>
/// </summary>
public sealed class SlicedView
{
    /// <summary>Сколько тайлов проверяем на непрозрачность между проверками бюджета.</summary>
    /// <remarks>
    /// Бюджет спрашивается не на каждом тайле: <c>Stopwatch.GetTimestamp()</c> сам по себе не
    /// бесплатен, а один <c>IsOccluded</c> — это поход в broadphase, то есть десятки микросекунд.
    /// Тридцать две штуки между проверками дают зерно около миллисекунды: мельче незачем, крупнее
    /// начнёт перебирать бюджет.
    /// </remarks>
    private const int OcclusionGrain = 32;

    private enum Phase : byte
    {
        Seeds,
        Occlusion,
        Cast,
        Done,
    }

    private enum CastStage : byte
    {
        Prepare,
        Diagonal,
        Straight,
        Finish,
    }

    private readonly IEntityManager _ents;
    private readonly EntityLookupSystem _lookup;
    private readonly SharedMapSystem _maps;
    private readonly SharedTransformSystem _xforms;
    private readonly SharedPowerReceiverSystem _power;
    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<TransformComponent> _xformQuery;

    // --------------------------------------------------------------- вход

    private Entity<BroadphaseComponent, MapGridComponent> _grid;
    private Box2 _localAabb;
    private Box2 _expandedAabb;

    // --------------------------------------------- состояние между срезами

    private Phase _phase = Phase.Seeds;

    private readonly List<Entity<StationAiVisionComponent>> _data = new();
    private readonly HashSet<Entity<StationAiVisionComponent>> _seeds = new();

    /// <summary>Тайлы, которые вообще попадают в кадр обзора. Только они уезжают в результат.</summary>
    private readonly HashSet<Vector2i> _viewportTiles = new();

    /// <summary>Непрозрачные тайлы. Считаются порциями — это самая дорогая фаза после каста.</summary>
    private readonly HashSet<Vector2i> _opaque = new();

    /// <summary>Список тайлов на проверку непрозрачности и позиция в нём.</summary>
    private readonly List<Vector2i> _pending = new();
    private int _pendingAt;

    // ------------------------------------------------- состояние одного каста

    private int _seedAt;
    private CastStage _stage = CastStage.Prepare;
    private int _d;
    private int _maxDepthMax;
    private int _sumDepthMax;
    private Vector2i _eyePos;
    private readonly Dictionary<Vector2i, int> _vis1 = new();
    private readonly Dictionary<Vector2i, int> _vis2 = new();
    private readonly HashSet<Vector2i> _seedTiles = new();
    private readonly HashSet<Vector2i> _boundary = new();

    /// <summary>Переиспользуемый буфер под окклюдеры одного тайла. См. <see cref="IsOccluded"/>.</summary>
    private readonly HashSet<Entity<OccluderComponent>> _occluders = new();

    /// <summary>Куда складывается результат. Тот же контракт, что у апстрима.</summary>
    public HashSet<Vector2i> VisibleTiles { get; } = new();

    /// <summary>Сколько срезов заняло вычисление. Для журнала и для теста «резка вообще работает».</summary>
    public int Slices { get; private set; }

    /// <summary>
    /// Сколько времени обзор реально ЗАНЯЛ главный поток, без пауз между кадрами.
    ///
    /// Настенные часы здесь не годятся принципиально: нарезанный обзор живёт секунду, из которой
    /// в главном потоке проведено миллисекунд двадцать. Мерить его секундомером снаружи — значит
    /// объявить починку регрессией, что при первом же прогоне и произошло.
    /// </summary>
    public double BusyMs { get; private set; }

    public SlicedView(
        IEntityManager ents,
        EntityLookupSystem lookup,
        SharedMapSystem maps,
        SharedTransformSystem xforms,
        SharedPowerReceiverSystem power)
    {
        _ents = ents;
        _lookup = lookup;
        _maps = maps;
        _xforms = xforms;
        _power = power;
        _occluderQuery = ents.GetEntityQuery<OccluderComponent>();
        _xformQuery = ents.GetEntityQuery<TransformComponent>();
    }

    /// <summary>
    /// Задать вход и начать сначала. Зовётся один раз перед первым <see cref="Step"/>.
    /// </summary>
    public void Begin(Entity<BroadphaseComponent, MapGridComponent> grid, Box2Rotated worldBounds, float expansionSize)
    {
        _grid = grid;

        var invMatrix = _xforms.GetInvWorldMatrix(grid.Owner);
        _localAabb = invMatrix.TransformBox(worldBounds);
        _expandedAabb = invMatrix.TransformBox(worldBounds.Enlarged(expansionSize));

        _phase = Phase.Seeds;
        _data.Clear();
        _seeds.Clear();
        _viewportTiles.Clear();
        _opaque.Clear();
        _pending.Clear();
        _pendingAt = 0;
        _seedAt = 0;
        _stage = CastStage.Prepare;
        VisibleTiles.Clear();
        Slices = 0;
    }

    /// <summary>
    /// Отработать один срез. <c>true</c> — обзор посчитан целиком, <see cref="VisibleTiles"/> готов.
    /// </summary>
    public bool Step(JobBudget budget)
    {
        Slices++;
        var started = Stopwatch.GetTimestamp();

        try
        {
            switch (_phase)
            {
                case Phase.Seeds:
                    return StepSeeds();

                case Phase.Occlusion:
                    return StepOcclusion(budget);

                case Phase.Cast:
                    return StepCast(budget);

                default:
                    return true;
            }
        }
        finally
        {
            BusyMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    // ------------------------------------------------------------ фаза семян

    /// <summary>
    /// Собрать камеры и разложить тайлы кадра. Один срез: обход broadphase здесь ровно один, а
    /// перечисление тайлов без проверки непрозрачности дёшево — платим мы за <c>IsOccluded</c>.
    /// </summary>
    private bool StepSeeds()
    {
        _lookup.GetLocalEntitiesIntersecting(_grid.Owner, _expandedAabb, _seeds,
            flags: LookupFlags.All | LookupFlags.Approximate);

        foreach (var seed in _seeds)
        {
            if (!seed.Comp.Enabled)
                continue;

            if (seed.Comp.NeedsPower && !_power.IsPowered(seed.Owner))
                continue;

            if (seed.Comp.NeedsAnchoring && !_xformQuery.GetComponent(seed.Owner).Anchored)
                continue;

            _data.Add(seed);
        }

        if (_data.Count == 0)
        {
            _phase = Phase.Done;
            return true;
        }

        // Порядок тот же, что у апстрима: сначала тайлы кадра, потом расширенная рамка без тех,
        // что уже в кадре. Разница между списками несёт смысл — в результат уезжают только тайлы
        // кадра, а непрозрачность нужна и снаружи него, иначе стена за краем экрана не отбросит
        // тень внутрь.
        var viewport = _maps.GetLocalTilesEnumerator(_grid.Owner, _grid.Comp2, _localAabb, ignoreEmpty: false);
        while (viewport.MoveNext(out var tileRef))
        {
            _viewportTiles.Add(tileRef.GridIndices);
            _pending.Add(tileRef.GridIndices);
        }

        var expanded = _maps.GetLocalTilesEnumerator(_grid.Owner, _grid.Comp2, _expandedAabb, ignoreEmpty: false);
        while (expanded.MoveNext(out var tileRef))
        {
            if (_viewportTiles.Contains(tileRef.GridIndices))
                continue;

            _pending.Add(tileRef.GridIndices);
        }

        _phase = Phase.Occlusion;
        return false;
    }

    // --------------------------------------------------- фаза непрозрачности

    private bool StepOcclusion(JobBudget budget)
    {
        while (_pendingAt < _pending.Count)
        {
            var end = Math.Min(_pendingAt + OcclusionGrain, _pending.Count);

            for (; _pendingAt < end; _pendingAt++)
            {
                if (IsOccluded(_pending[_pendingAt]))
                    _opaque.Add(_pending[_pendingAt]);
            }

            if (budget.Exhausted)
                return false;
        }

        _phase = Phase.Cast;
        return false;
    }

    /// <summary>
    /// Перенос апстримового <c>IsOccluded</c> слово в слово.
    ///
    /// Набор переиспользуется, а не создаётся на каждый тайл, — ровно как в апстриме. В первой
    /// версии он создавался внутри, и это стоило тысяч аллокаций на один обзор: тест удержания
    /// тика поймал это немедленно, худший вызов перевалил за 150 мс.
    /// </summary>
    private bool IsOccluded(Vector2i tile)
    {
        var tileBounds = _lookup.GetLocalBounds(tile, _grid.Comp2.TileSize).Enlarged(-0.05f);

        var occluders = _occluders;
        occluders.Clear();
        _lookup.GetLocalEntitiesIntersecting((_grid.Owner, _grid.Comp1), tileBounds, occluders,
            query: _occluderQuery, flags: LookupFlags.Static | LookupFlags.Approximate);

        foreach (var occluder in occluders)
        {
            if (occluder.Comp.Enabled)
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------- фаза каста

    private bool StepCast(JobBudget budget)
    {
        while (_seedAt < _data.Count)
        {
            if (StepOneSeed(budget))
            {
                _seedAt++;
                _stage = CastStage.Prepare;
            }

            if (budget.Exhausted)
                return false;
        }

        _phase = Phase.Done;
        return true;
    }

    /// <summary>
    /// Один шаг каста от одной камеры. <c>true</c> — эта камера досчитана.
    ///
    /// Внутри камеры резка идёт по глубине <c>d</c>: оба теневых цикла — это O(глубина × тайлы),
    /// и на большой дальности один цикл сам по себе перебрал бы кадр.
    /// </summary>
    private bool StepOneSeed(JobBudget budget)
    {
        var seed = _data[_seedAt];

        if (_stage == CastStage.Prepare)
        {
            var seedXform = _xformQuery.GetComponent(seed.Owner);

            // Быстрый путь апстрима: камера без окклюзии видит круг целиком. Резать тут нечего,
            // и ради одного этого случая усложнять состояние незачем.
            if (!seed.Comp.Occluded)
            {
                var squircles = _maps.GetLocalTilesIntersecting(_grid.Owner, _grid.Comp2,
                    new Circle(_xforms.GetWorldPosition(seedXform), seed.Comp.Range), ignoreEmpty: false);

                foreach (var tile in squircles)
                    VisibleTiles.Add(tile.GridIndices);

                return true;
            }

            _vis1.Clear();
            _vis2.Clear();
            _seedTiles.Clear();
            _boundary.Clear();

            _maxDepthMax = 0;
            _sumDepthMax = 0;
            _eyePos = _maps.GetTileRef(_grid.Owner, _grid.Comp2, seedXform.Coordinates).GridIndices;

            var range = seed.Comp.Range;

            for (var x = Math.Floor(_eyePos.X - range); x <= _eyePos.X + range; x++)
            {
                for (var y = Math.Floor(_eyePos.Y - range); y <= _eyePos.Y + range; y++)
                {
                    var tile = new Vector2i((int)x, (int)y);
                    var delta = tile - _eyePos;
                    var xDelta = Math.Abs(delta.X);
                    var yDelta = Math.Abs(delta.Y);

                    _maxDepthMax = Math.Max(_maxDepthMax, Math.Max(xDelta, yDelta));
                    _sumDepthMax = Math.Max(_sumDepthMax, xDelta + yDelta);
                    _seedTiles.Add(tile);
                }
            }

            _d = 0;
            _stage = CastStage.Diagonal;
            return false;
        }

        if (_stage == CastStage.Diagonal)
        {
            for (; _d < _maxDepthMax; _d++)
            {
                foreach (var tile in _seedTiles)
                {
                    if (MaxDelta(tile, _eyePos) == _d + 1 && NeighborsVis(_vis2, tile, _d))
                        _vis2[tile] = _opaque.Contains(tile) ? -1 : _d + 1;
                }

                if (budget.Exhausted)
                {
                    _d++;
                    return false;
                }
            }

            _d = 0;
            _stage = CastStage.Straight;
            return false;
        }

        if (_stage == CastStage.Straight)
        {
            for (; _d < _sumDepthMax; _d++)
            {
                foreach (var tile in _seedTiles)
                {
                    if (SumDelta(tile, _eyePos) != _d + 1 || !NeighborsVis(_vis1, tile, _d))
                        continue;

                    if (_opaque.Contains(tile))
                        _vis1[tile] = -1;
                    else if (_vis2.GetValueOrDefault(tile) != 0)
                        _vis1[tile] = _d + 1;
                }

                if (budget.Exhausted)
                {
                    _d++;
                    return false;
                }
            }

            _stage = CastStage.Finish;
            return false;
        }

        // Хвост апстрима: глаз видит сам себя, углы стен показываются, в результат уезжает только
        // то, что попало в кадр. Всё вместе — линейный проход по тайлам камеры, резать незачем.
        _vis1[_eyePos] = 1;

        foreach (var tile in _seedTiles)
            _vis2[tile] = _vis1.GetValueOrDefault(tile, 0);

        foreach (var tile in _seedTiles)
        {
            if (!_opaque.Contains(tile))
                continue;

            if (_vis1.GetValueOrDefault(tile) != 0)
                continue;

            if (IsCorner(tile, Vector2i.UpRight) || IsCorner(tile, Vector2i.UpLeft)
                || IsCorner(tile, Vector2i.DownLeft) || IsCorner(tile, Vector2i.DownRight))
            {
                _boundary.Add(tile);
            }
        }

        foreach (var tile in _boundary)
            _vis1[tile] = -1;

        foreach (var tile in _seedTiles)
        {
            if (!_viewportTiles.Contains(tile))
                continue;

            if (_vis1.GetValueOrDefault(tile, 0) != 0)
                VisibleTiles.Add(tile);
        }

        return true;
    }

    // ------------------------------------------------------------ помощники

    private static int MaxDelta(Vector2i tile, Vector2i center)
    {
        var delta = tile - center;
        return Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
    }

    private static int SumDelta(Vector2i tile, Vector2i center)
    {
        var delta = tile - center;
        return Math.Abs(delta.X) + Math.Abs(delta.Y);
    }

    private static bool NeighborsVis(Dictionary<Vector2i, int> vis, Vector2i index, int d)
    {
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                    continue;

                if (vis.GetValueOrDefault(index + new Vector2i(x, y)) == d)
                    return true;
            }
        }

        return false;
    }

    private bool IsCorner(Vector2i index, Vector2i delta)
    {
        var diagonalIndex = index + delta;

        if (!_seedTiles.TryGetValue(diagonalIndex, out var diagonal))
            return false;

        var cardinal1 = new Vector2i(index.X, diagonal.Y);
        var cardinal2 = new Vector2i(diagonal.X, index.Y);

        return _vis1.GetValueOrDefault(diagonal) != 0
               && _vis1.GetValueOrDefault(cardinal1) != 0
               && _vis1.GetValueOrDefault(cardinal2) != 0
               && _opaque.Contains(cardinal1)
               && _opaque.Contains(cardinal2)
               && !_opaque.Contains(diagonal);
    }
}
