using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vision;
using Content.Shared.Access.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Power.Components;
using Content.Shared.Silicons.StationAi;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Power.Components;
using Content.Shared.SurveillanceCamera.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Server.AiAgent;

/// <summary>
/// Куда ушло время у look.
///
/// «look тормозит» — не диагноз. Между теневым кастом апстрима и сбором сущностей разница в два
/// порядка, и без разбивки спор о том, что чинить, решается рассуждением, а не числом. Живой
/// сервер за сутки дал 111 вызовов и 111 перерасходов бюджета, медиану 98 мс и максимум 1908 —
/// но не сказал ни слова о том, где именно они потрачены.
///
/// <see cref="Queries"/> стоит здесь не ради времени. Это счётчик походов в broadphase, и он
/// обязан быть равен единице независимо от того, сколько тайлов вернул обзор. Именно его стережёт
/// тест: миллисекунды на сборочной машине меряют железо и шумят, а единица меряет алгоритм и не
/// шумит вовсе.
/// </summary>
internal struct LookProfile
{
    /// <summary>Фаза A1: теневой каст апстрима, <c>StationAiVisionSystem.GetView</c>.</summary>
    public double ViewMs;

    /// <summary>Фаза A2: превращение тайлов в сущности.</summary>
    public double GatherMs;

    /// <summary>Фазы B..E: отсев, хендлы, построение строк, сортировка.</summary>
    public double RowsMs;

    /// <summary>Сколько видимых тайлов вернул обзор.</summary>
    public int Tiles;

    /// <summary>Сколько сущностей отдал broadphase до отсева.</summary>
    public int Candidates;

    /// <summary>Сколько пережило <c>IsOnScreen</c> и проверку попадания в видимый тайл.</summary>
    public int OnScreen;

    /// <summary>Сколько строк ушло модели.</summary>
    public int Rows;

    /// <summary>Походов в broadphase. Инвариант быстрого пути: 1.</summary>
    public int Queries;
}

/// <summary>
/// The vision bridge and the device gate chain — the two things every phase-2 tool stands on.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// Переиспользуемый буфер кандидатов.
    ///
    /// Не свежий <c>HashSet</c> на вызов: обзор строится только на главном потоке и только из
    /// <c>LookAsync</c>, реентрантности нет по построению. На большой станции здесь бывает под
    /// тысячу сущностей, и выбрасывать их вместе с набором на каждый вызов — это отдавать GC
    /// работу, которой можно не быть.
    /// </summary>
    private readonly HashSet<EntityUid> _lookCandidates = new();

    /// <summary>
    /// Сколько тайлов вширь и ввысь мы готовы обойти ради одной многотайловой сущности.
    ///
    /// Потолок, а не размер: реальные машины на станции занимают один-два тайла, три — это
    /// сингулярность с её полем. Восемь взято с запасом на порядок. Если что-то не влезло, лучше
    /// отдать промах, чем на одной сущности просканировать полстанции и вернуть тик к тому, из-за
    /// чего всё это затевалось.
    /// </summary>
    private const int MaxSpanTiles = 8;

    /// <summary>
    /// What the AI can actually see, as entities.
    ///
    /// Upstream gives us half of this: <c>StationAiVisionSystem.GetView</c> returns the visible
    /// <em>tile indices</em> using its shadowcasting algorithm, and there is no "entities I can
    /// see" API anywhere. The other half used to be
    /// <c>EntityLookupSystem.GetLocalEntitiesIntersecting(grid, IEnumerable&lt;Vector2i&gt;)</c>,
    /// which happens to accept exactly a set of grid indices — so the two composed into a single
    /// bridge with no approximation in between.
    ///
    /// Композиция была красивой и стоила секунды тика. Почему — подробно в
    /// <see cref="GatherByBounds"/>; коротко: тот перегруз обещает в комментарии «Faster than
    /// doing each tile individually» и буквально делает каждый тайл отдельно, а на каждом из них
    /// заново обходит весь уже накопленный набор.
    ///
    /// Runs on the main thread inside the tick, and <c>GetView</c> carries an upstream comment
    /// reading "yes this is expensive. Yes it needs optimising", so callers are rate-limited by
    /// the agent's tick rather than being free to spam it.
    /// </summary>
    /// <param name="fastOverride">
    /// Заставить конкретный путь сбора вместо того, что велит <see cref="AiCVars.LookFast"/>.
    /// Только для теста эквивалентности: ему нужно прогнать оба пути в ОДНОМ кадре, иначе он
    /// сравнивает две разные секунды жизни станции и ловит не геометрию, а чьи-то шаги.
    /// </param>
    private List<EntityUid> GetVisibleEntities(
        EntityUid brain,
        float expansion,
        out string? failure,
        ref LookProfile profile,
        bool? fastOverride = null)
    {
        failure = null;
        var result = new List<EntityUid>();

        if (!_stationAi.TryGetCore(brain, out var core) || core.Comp?.RemoteEntity == null)
        {
            failure = "нет доступа к ядру — камеры недоступны";
            return result;
        }

        var eye = core.Comp.RemoteEntity.Value;
        var eyeXform = Transform(eye);
        var gridUid = eyeXform.GridUid;

        if (gridUid == null
            || !TryComp<MapGridComponent>(gridUid, out var mapGrid)
            || !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
        {
            failure = "глаз не на станции";
            return result;
        }

        var worldPos = _xform.GetWorldPosition(eyeXform);
        var half = expansion;
        var bounds = new Box2Rotated(
            new Box2(worldPos.X - half, worldPos.Y - half, worldPos.X + half, worldPos.Y + half),
            Angle.Zero,
            worldPos);

        var tiles = new HashSet<Vector2i>();

        var viewStart = Stopwatch.GetTimestamp();
        _vision.GetView((gridUid.Value, broadphase, mapGrid), bounds, tiles);
        profile.ViewMs = Stopwatch.GetElapsedTime(viewStart).TotalMilliseconds;
        profile.Tiles = tiles.Count;

        if (tiles.Count == 0)
            return result;

        var gatherStart = Stopwatch.GetTimestamp();

        if (fastOverride ?? _cfg.GetCVar(AiCVars.LookFast))
            GatherByBounds(gridUid.Value, mapGrid, tiles, eye, brain, core.Owner, result, ref profile);
        else
            GatherByTile(gridUid.Value, tiles, eye, brain, core.Owner, result, ref profile);

        profile.GatherMs = Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;
        profile.OnScreen = result.Count;

        return result;
    }

    /// <summary>
    /// Проход обзора, растянутый на несколько кадров.
    ///
    /// Держит то, что посчитано один раз на старте (глаз, сетка, ядро) и переживает срезы вместе с
    /// самим <see cref="SlicedView"/>. Отдельным объектом, а не полями системы, потому что проходов
    /// в полёте может быть несколько: у каждого агента свой, и складывать их в систему значило бы
    /// поймать чужие тайлы при <c>ai.max_agents &gt; 1</c>.
    /// </summary>
    private sealed class ViewPass
    {
        public required SlicedView View;
        public required EntityUid Grid;
        public required MapGridComponent MapGrid;
        public required EntityUid Eye;
        public required EntityUid Core;
        public double ViewMs;
    }

    /// <summary>
    /// Разрешить глаз и сетку. Общий кусок для атомарного и нарезаемого путей — чтобы они не
    /// разъехались в том, что считают «глазом»; расхождение здесь было бы расхождением в паритете.
    /// </summary>
    private bool TryResolveEye(
        EntityUid brain,
        out EntityUid eye,
        out EntityUid core,
        out EntityUid grid,
        out BroadphaseComponent broadphase,
        out MapGridComponent mapGrid,
        out string? failure)
    {
        eye = default;
        core = default;
        grid = default;
        broadphase = default!;
        mapGrid = default!;

        if (!_stationAi.TryGetCore(brain, out var found) || found.Comp?.RemoteEntity == null)
        {
            failure = "нет доступа к ядру — камеры недоступны";
            return false;
        }

        core = found.Owner;
        eye = found.Comp.RemoteEntity.Value;

        var gridUid = Transform(eye).GridUid;

        if (gridUid == null
            || !TryComp(gridUid, out MapGridComponent? foundGrid)
            || !TryComp(gridUid, out BroadphaseComponent? foundBroadphase))
        {
            failure = "глаз не на станции";
            return false;
        }

        grid = gridUid.Value;
        mapGrid = foundGrid;
        broadphase = foundBroadphase;
        failure = null;
        return true;
    }

    /// <summary>
    /// Завести проход обзора. Тяжёлого здесь ничего нет: только разрешение глаза и рамка.
    /// </summary>
    private ViewPass? BeginSlicedView(EntityUid brain, float expansion, out string? failure)
    {
        if (!TryResolveEye(brain, out var eye, out var core, out var grid, out var broadphase, out var mapGrid, out failure))
            return null;

        var worldPos = _xform.GetWorldPosition(Transform(eye));
        var bounds = new Box2Rotated(
            new Box2(worldPos.X - expansion, worldPos.Y - expansion, worldPos.X + expansion, worldPos.Y + expansion),
            Angle.Zero,
            worldPos);

        var view = new SlicedView(EntityManager, _lookup, _mapSystem, _xform, _power);
        view.Begin((grid, broadphase, mapGrid), bounds, expansion);

        return new ViewPass
        {
            View = view,
            Grid = grid,
            MapGrid = mapGrid,
            Eye = eye,
            Core = core,
        };
    }

    /// <summary>
    /// Собрать сущности по посчитанным тайлам. Тот же сбор, что и у атомарного пути, — резать его
    /// незачем: по профилю это 3-4 мс против 18-22 у каста.
    /// </summary>
    private List<EntityUid> GatherFromPass(ViewPass pass, EntityUid brain, ref LookProfile profile)
    {
        var result = new List<EntityUid>();

        profile.ViewMs = pass.ViewMs;
        profile.Tiles = pass.View.VisibleTiles.Count;

        if (pass.View.VisibleTiles.Count == 0)
            return result;

        var gatherStart = Stopwatch.GetTimestamp();

        if (_cfg.GetCVar(AiCVars.LookFast))
            GatherByBounds(pass.Grid, pass.MapGrid, pass.View.VisibleTiles, pass.Eye, brain, pass.Core, result, ref profile);
        else
            GatherByTile(pass.Grid, pass.View.VisibleTiles, pass.Eye, brain, pass.Core, result, ref profile);

        profile.GatherMs = Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;
        profile.OnScreen = result.Count;

        return result;
    }

    /// <summary>
    /// Быстрый путь: один обход дерева по общей рамке, дальше отсев по принадлежности тайлу.
    ///
    /// <para>
    /// Медленный путь спрашивал у broadphase «кто пересекает вот этот тайл» и делал это по разу на
    /// каждый видимый тайл — от 289 при <c>expand:0</c> до 1681 при <c>expand:3</c>. На каждом
    /// тайле это до четырёх обходов дерева, узкая фаза на кандидата и вызов <c>AddContained</c>,
    /// который заново проходит ВЕСЬ накопленный набор, спрашивает у каждого элемента
    /// <c>ContainerManagerComponent</c> и рекурсивно добавляет содержимое всех контейнеров — со
    /// свежим списком на каждый тайл. При T тайлах и наборе в E сущностей это O(T·E) по времени и
    /// O(T) аллокаций внутри тика. Отсюда и брались 1908 мс: T и E растут вместе, произведение —
    /// четвёртой степенью от радиуса.
    /// </para>
    /// <para>
    /// Здесь наоборот: кандидаты собираются разом, а потом у каждого спрашивается, где он стоит.
    /// Один поход в дерево вместо тысячи, дальше линейный проход.
    /// </para>
    /// <para>
    /// <b>Ответ от этого не беднеет, и это доказуемо, а не на глаз.</b> Апстрим тестировал фикстуру
    /// против тайла, сжатого на <c>TileEnlargementRadius</c> (величина отрицательная), — мы
    /// тестируем рамку сущности против несжатого. Рамка ⊇ фикстуры, несжатый тайл ⊇ сжатого,
    /// значит новый набор — надмножество старого. Ошибка возможна только в сторону «на границе
    /// попал лишний», и для паритета эта сторона безопасная: спрайт на экране игрока рисуется по
    /// позиции, а не по фикстуре, так что рамочная проверка ближе к «что видно человеку», чем
    /// узкая фаза. Тест эквивалентности гоняет оба пути и требует включения.
    /// </para>
    /// </summary>
    private void GatherByBounds(
        EntityUid gridUid,
        MapGridComponent mapGrid,
        HashSet<Vector2i> tiles,
        EntityUid eye,
        EntityUid brain,
        EntityUid coreUid,
        List<EntityUid> result,
        ref LookProfile profile)
    {
        var min = new Vector2i(int.MaxValue, int.MaxValue);
        var max = new Vector2i(int.MinValue, int.MinValue);

        foreach (var tile in tiles)
        {
            min = Vector2i.ComponentMin(min, tile);
            max = Vector2i.ComponentMax(max, tile);
        }

        var size = mapGrid.TileSize;
        var box = new Box2(min.X * size, min.Y * size, (max.X + 1) * size, (max.Y + 1) * size);

        // Contained снят намеренно, и из ответа это не убирает ничего: IsOnScreen отказывает всему,
        // что лежит в контейнере, — рюкзакам, шкафам, начинке машин. Мы платили квадратом за то,
        // что не попадало в ответ ни разу.
        //
        // Approximate — тоже намеренно. Узкая фаза против общей рамки бессмысленна: принадлежность
        // конкретному тайлу мы всё равно проверяем сами и проверяем строже.
        _lookCandidates.Clear();
        _lookup.GetLocalEntitiesIntersecting(gridUid, box, _lookCandidates,
            LookupFlags.Uncontained | LookupFlags.Approximate);
        profile.Queries++;
        profile.Candidates = _lookCandidates.Count;

        // Считается один раз на весь обзор: применять её придётся к каждому кандидату.
        var invGrid = _xform.GetInvWorldMatrix(gridUid);

        foreach (var uid in _lookCandidates)
        {
            if (uid == eye || uid == brain || uid == coreUid)
                continue;

            // IsOnScreen ПЕРЕД геометрией, а не после. Основная масса кандидатов на станции — это
            // кабель, труба и мусоропровод под плитой: они SubFloorHide, отсекаются одним
            // сравнением и до тайловой арифметики не доходят вовсе.
            if (!IsOnScreen(uid))
                continue;

            if (!CoversVisibleTile(uid, size, invGrid, tiles))
                continue;

            result.Add(uid);
        }
    }

    /// <summary>
    /// Медленный путь. Существует ради теста эквивалентности и ради отката рубильником в бою —
    /// см. <see cref="AiCVars.LookFast"/>. Новый код сюда не добавлять.
    /// </summary>
    private void GatherByTile(
        EntityUid gridUid,
        HashSet<Vector2i> tiles,
        EntityUid eye,
        EntityUid brain,
        EntityUid coreUid,
        List<EntityUid> result,
        ref LookProfile profile)
    {
        var entities = _lookup.GetLocalEntitiesIntersecting(gridUid, tiles);

        profile.Queries += tiles.Count;
        profile.Candidates = entities.Count;

        foreach (var uid in entities)
        {
            if (uid == eye || uid == brain || uid == coreUid)
                continue;

            // IsOnScreen здесь, а не у вызывающего, — чтобы оба пути отдавали ОДИН И ТОТ ЖЕ вид
            // множества и тест эквивалентности сравнивал сравнимое. Раньше этот отсев жил в
            // LookAsync; сместить его сюда ничего не меняет по результату (проверка чистая и
            // идемпотентная), зато делает утверждение «быстрый путь ничего не потерял»
            // проверяемым одной строкой вместо пересборки условий в тесте.
            if (!IsOnScreen(uid))
                continue;

            result.Add(uid);
        }
    }

    /// <summary>
    /// Попадает ли сущность хоть одним тайлом в видимый набор.
    ///
    /// Сначала тайл самого трансформа — у подавляющего большинства он единственный, и это одно
    /// матричное умножение. Рамка считается только для промахнувшихся, а промах здесь означает
    /// одно из двух: либо многотайловая машина на границе видимого, у которой центр оказался в
    /// закрытом стеной тайле, — такую терять нельзя, она у игрока на экране; либо честный мусор за
    /// стеной, который и должен отсеяться. Промахов мало, так что дорогая ветка почти не берётся.
    /// </summary>
    private bool CoversVisibleTile(EntityUid uid, ushort size, Matrix3x2 invGrid, HashSet<Vector2i> tiles)
    {
        var world = _xform.GetWorldPosition(uid);
        var local = Vector2.Transform(world, invGrid);

        var tile = new Vector2i(
            (int) MathF.Floor(local.X / size),
            (int) MathF.Floor(local.Y / size));

        if (tiles.Contains(tile))
            return true;

        // Все ЧЕТЫРЕ угла, а не два противоположных.
        //
        // Рамка приходит выровненной по осям МИРА, а станция стоит под углом — Box, например,
        // повёрнута, и это видно невооружённым глазом по числу тайлов в обзоре. После перевода в
        // координаты сетки прямоугольник превращается в ромб, у которого крайние точки — вовсе не
        // те два угла, что были крайними в мире. Версия на двух углах теряла лампу, лежащую на
        // стыке тайлов: диапазон получался скошенным и не накрывал тот единственный тайл, который
        // и был виден. Один потерянный предмет из двух с половиной тысяч — ровно та поломка,
        // которую замечает тест и не замечает человек.
        var aabb = _lookup.GetWorldAABB(uid);

        var c0 = Vector2.Transform(aabb.BottomLeft, invGrid);
        var c1 = Vector2.Transform(aabb.BottomRight, invGrid);
        var c2 = Vector2.Transform(aabb.TopLeft, invGrid);
        var c3 = Vector2.Transform(aabb.TopRight, invGrid);

        var minX = MathF.Min(MathF.Min(c0.X, c1.X), MathF.Min(c2.X, c3.X));
        var minY = MathF.Min(MathF.Min(c0.Y, c1.Y), MathF.Min(c2.Y, c3.Y));
        var maxX = MathF.Max(MathF.Max(c0.X, c1.X), MathF.Max(c2.X, c3.X));
        var maxY = MathF.Max(MathF.Max(c0.Y, c1.Y), MathF.Max(c2.Y, c3.Y));

        var x0 = (int) MathF.Floor(minX / size);
        var y0 = (int) MathF.Floor(minY / size);
        var x1 = (int) MathF.Floor(maxX / size);
        var y1 = (int) MathF.Floor(maxY / size);

        if (x1 - x0 >= MaxSpanTiles || y1 - y0 >= MaxSpanTiles)
            return false;

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            if (tiles.Contains(new Vector2i(x, y)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Turn a map position into grid coordinates the eye may legally occupy.
    ///
    /// Same camera-coverage test the entity path uses, so reaching a point by number is no more
    /// permissive than reaching it by handle — a tile no camera watches refuses either way.
    /// </summary>
    private bool TryPointOnGrid(EntityUid eye, float x, float y, out EntityCoordinates coords, out ToolResult? failure)
    {
        coords = default;
        failure = null;

        var eyeXform = Transform(eye);
        var gridUid = eyeXform.GridUid;

        if (gridUid == null
            || !TryComp<MapGridComponent>(gridUid, out var mapGrid)
            || !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
        {
            failure = ToolResult.Fail(ToolError.Internal,
                "твой глаз сейчас не на станции — вернись к ядру через jump_to_core", retry: "later");
            return false;
        }

        var point = new MapCoordinates(new Vector2(x, y), eyeXform.MapID);
        var candidate = _xform.ToCoordinates(gridUid.Value, point);
        var tile = _mapSystem.LocalToTile(gridUid.Value, mapGrid, candidate);

        // A point over open space is not a camera problem, it is a different structure entirely —
        // the arrivals shuttle, a salvage wreck, someone drifting outside. Saying "no cameras there"
        // sends the model hunting for a camera that could never exist, and the crew gets told to
        // walk somewhere that will not help.
        if (!_mapSystem.TryGetTileRef(gridUid.Value, mapGrid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
        {
            failure = ToolResult.Fail(ToolError.NotVisible,
                string.Create(CultureInfo.InvariantCulture,
                    $"точка ({x:F0},{y:F0}) не на твоей станции — это другой корабль, обломок или открытый космос; " +
                    $"туда ты не видишь в принципе"),
                retry: "other_target");
            return false;
        }

        if (!_vision.IsAccessible((gridUid.Value, broadphase, mapGrid), tile, fastPath: false))
        {
            failure = ToolResult.Fail(ToolError.NotVisible,
                string.Create(CultureInfo.InvariantCulture,
                    $"в точку ({x:F0},{y:F0}) {DeviceGateExt.NoCameraDetail}"),
                retry: "other_target");
            return false;
        }

        coords = candidate;
        return true;
    }

    /// <summary>
    /// Cardinal directions in Russian, keyed the way the crew actually talks.
    ///
    /// North is up the screen. That equivalence is what makes "открой дверь надо мной" answerable
    /// at all: the client renders the station world-aligned with no eye rotation, so world north
    /// and screen up are the same thing for both the player and the AI.
    /// </summary>
    private static string DirectionRu(Direction dir) => dir switch
    {
        Direction.North => "север",
        Direction.NorthEast => "северо-восток",
        Direction.East => "восток",
        Direction.SouthEast => "юго-восток",
        Direction.South => "юг",
        Direction.SouthWest => "юго-запад",
        Direction.West => "запад",
        Direction.NorthWest => "северо-запад",
        _ => "рядом",
    };

    /// <summary>
    /// Where <paramref name="target"/> lies as seen from <paramref name="from"/>: "север 3".
    ///
    /// Within half a tile there is no meaningful bearing — the two are on the same square, which is
    /// what "прямо рядом" means to a person describing their surroundings.
    /// </summary>
    private static string BearingFrom(Vector2 from, Vector2 target)
    {
        var delta = target - from;
        var dist = delta.Length();

        if (dist < 0.5f)
            return "вплотную";

        return string.Create(CultureInfo.InvariantCulture, $"{DirectionRu(delta.GetDir())} {dist:F0}");
    }

    /// <summary>
    /// Where a thing is, as two pairs of numbers: the offset from whatever the listing is measured
    /// from, and the absolute position.
    ///
    /// The offset replaces an eight-way bearing with a rounded distance, which threw away exactly
    /// the part that settles "which door am I standing at" — <c>(3,1)</c> and <c>(1,3)</c> both
    /// printed as "северо-восток 3", and on a station those are two doors in two different walls.
    /// One live request returned fifty-five doors, four of them tied at the same bearing and
    /// distance and most named "secure windoor"; the agent opened the wrong one twice, then gave up.
    ///
    /// The absolute pair is there because it is what <c>move_camera</c> takes. Without it, looking
    /// at something and then going to see it meant a separate <c>map</c> call and a guess.
    /// </summary>
    private static string PositionFrom(Vector2 from, Vector2 target) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Δ({target.X - from.X:F0},{target.Y - from.Y:F0}) ({target.X:F0},{target.Y:F0})");

    /// <summary>Which way a mob is facing, for "открой дверь, на которую я смотрю".</summary>
    private string FacingRu(EntityUid uid) => DirectionRu(_xform.GetWorldRotation(uid).GetDir());

    /// <summary>Classify an entity into a handle kind. Order matters: most specific first.</summary>
    public string KindOf(EntityUid uid)
    {
        if (HasComp<MobStateComponent>(uid))
            return "crew";
        if (HasComp<DoorComponent>(uid))
            return "door";
        if (HasComp<ApcComponent>(uid))
            return "apc";
        if (HasComp<SurveillanceCameraComponent>(uid))
            return "camera";
        if (HasComp<Content.Shared.Holopad.HolopadComponent>(uid))
            return "holopad";
        if (HasComp<Content.Shared.TurretController.DeployableTurretControllerComponent>(uid))
            return "turretctl";
        if (HasComp<Content.Shared.Turrets.DeployableTurretComponent>(uid))
            return "turret";
        if (HasComp<AirAlarmComponent>(uid))
            return "airalarm";
        if (HasComp<StationAiWhitelistComponent>(uid))
            return "device";

        // Everything below this line is scenery the AI cannot operate — and it used to be dropped
        // from look entirely. That made the agent blind to exactly what the crew asks about: it
        // told an engineer standing beside the SMES bank that no such device was in view. A player
        // sees the whole room; withholding the uncontrollable half is not parity, it is a handicap.
        if (HasComp<Content.Server.Power.Components.PowerNetworkBatteryComponent>(uid)
            || HasComp<Content.Shared.Power.Components.BatteryComponent>(uid))
            return "power";
        if (HasComp<Content.Shared.Atmos.Piping.Unary.Components.GasCanisterComponent>(uid))
            return "canister";
        if (HasComp<Content.Server.Construction.Components.ComputerComponent>(uid))
            return "computer";
        if (HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(uid))
            return "locker";

        return "obj";
    }

    /// <summary>
    /// Would a player looking at this tile see the thing at all?
    ///
    /// Handing the model literally every entity in the broadphase is not "what a player sees": it
    /// is also the cable under the floor, the pipe beneath it and the spare pen inside someone's
    /// backpack. Those are invisible on screen, and listing them buries the room the crew is
    /// actually asking about.
    /// </summary>
    private bool IsOnScreen(EntityUid uid)
    {
        // Under the plating: cables, pipes, disposals. Hidden from players too.
        if (HasComp<Content.Shared.SubFloor.SubFloorHideComponent>(uid))
            return false;

        // Inside a bag, a locker or a machine — including the AI's own brain in its core.
        if (_container.IsEntityInContainer(uid))
            return false;

        // Markers, spawn points and other invisible bookkeeping carry no name worth reporting.
        return !string.IsNullOrWhiteSpace(Name(uid));
    }

    /// <summary>
    /// Walk the same gate chain a human player's click walks, and report which link refused.
    ///
    /// The order is not arbitrary — it mirrors <c>SharedStationAiSystem.Held.cs::OnHeldInteraction</c>
    /// followed by <c>OnAiBuiCheck</c> and the per-device <c>AccessReaderSystem.IsAllowed</c> call.
    /// Skipping any of it would make the LLM strictly more capable than a player, which is the one
    /// thing this whole design is trying not to do.
    /// </summary>
    private DeviceGate CheckGate(EntityUid brain, EntityUid target, AgentMode mode, bool needAccess = true)
    {
        if (!IsPlayable(brain))
            return DeviceGate.Dead;

        if (mode == AgentMode.Carded)
            return DeviceGate.Carded;

        if (!TryComp<StationAiWhitelistComponent>(target, out var whitelist))
            return DeviceGate.NotWhitelisted;

        if (!whitelist.Enabled)
            return DeviceGate.WireCut;

        if (!_power.IsPowered(target))
            return DeviceGate.Unpowered;

        if (!IsVisibleToAi(brain, target))
            return DeviceGate.NotVisible;

        if (needAccess && !_access.IsAllowed(brain, target))
            return DeviceGate.NoAccess;

        return DeviceGate.Ok;
    }

    /// <summary>
    /// Same-grid plus camera coverage — the gate a human player's click passes through.
    ///
    /// <b>Deliberately not <c>fastPath: true</c>.</b> That branch of <c>ViewJob</c> is broken for
    /// any grid that is not sitting at the world origin: it builds the seed's coverage circle from
    /// <c>GetWorldPosition</c> and hands it to <c>GetLocalTilesIntersecting</c>, which expects grid
    /// coordinates. On a station loaded at, say, (-570, 86) the circle lands hundreds of tiles away
    /// in empty space, no tile is ever visible, and every gated tool refuses — a door one tile from
    /// the eye reports "no cameras". It costs nothing on a test grid at the origin, which is exactly
    /// why the benchmarks were green while the live station could not open a single door.
    ///
    /// The slow path derives the seed tile with <c>GetTileRef(..., seedXform.Coordinates)</c> —
    /// grid-local, correct — and is what upstream's own interaction check (<c>OnAiInRange</c>) uses.
    /// It also runs the occluder sweep, so the answer respects walls, which is the honest reading of
    /// "can the AI see this" anyway.
    /// </summary>
    private bool IsVisibleToAi(EntityUid brain, EntityUid target)
    {
        if (!_stationAi.TryGetCore(brain, out var core) || core.Comp?.RemoteEntity == null)
            return false;

        var eyeXform = Transform(core.Comp.RemoteEntity.Value);
        var targetXform = Transform(target);

        if (eyeXform.GridUid == null || eyeXform.GridUid != targetXform.GridUid)
            return false;

        var gridUid = eyeXform.GridUid.Value;
        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid) ||
            !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
            return false;

        var tile = _mapSystem.LocalToTile(gridUid, mapGrid, targetXform.Coordinates);
        return _vision.IsAccessible((gridUid, broadphase, mapGrid), tile, fastPath: false);
    }
}
