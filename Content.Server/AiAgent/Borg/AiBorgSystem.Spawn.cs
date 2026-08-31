using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Station.Components;
using Content.Shared.Pinpointer;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Куда ставить робота.
///
/// <para>
/// Живёт в системе, а не в консольной команде, ровно потому, что первая версия жила в команде — и
/// тесты, спавнившие борга «где-нибудь рядом с ядром», проверяли не то. Комната ИИ-ядра заперта:
/// робот в ней сообщает «не нашёл дороги» на любую цель, и это <b>правильный</b> ответ, просто
/// заданный из неправильного места. Место постановки — часть механики, а не удобство оператора.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// Кого ставит <c>aiborg spawn</c> без уточнения. Режим злого ИИ передаёт свои прототипы
    /// явно: у боевого робота и тип другой, и личность другая.
    /// </summary>
    public const string DefaultChassis = "AiBorgChassis";

    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private TurfSystem _turf = default!;

    /// <summary>
    /// Найти станции пригодное место и поставить туда робота.
    /// </summary>
    /// <param name="beaconName">Часть названия маяка, или <c>null</c> — любой подходящий.</param>
    public bool TrySpawnBorg(string? beaconName, out EntityUid borg, out string reason, EntProtoId? proto = null)
    {
        borg = default;

        if (!TryFindGrid(out var grid) || !TryComp<NavMapComponent>(grid, out var navMap))
        {
            reason = "не нашёл сетку станции с навигационной картой";
            return false;
        }

        var beacons = navMap.Beacons.Values
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .Where(b => beaconName == null || b.Text!.Contains(beaconName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (beacons.Count == 0)
        {
            var have = string.Join(", ", navMap.Beacons.Values
                .Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(12));

            reason = beaconName == null
                ? "на карте нет маяков — назови координаты"
                : $"нет маяка '{beaconName}'. Есть, например: {have}";
            return false;
        }

        // Перебираем маяки, а не берём первый: маяк — это, как правило, вывеска на стене, и
        // свободного пола рядом с конкретным может не найтись.
        foreach (var beacon in beacons)
        {
            if (!TryFreeTileNear(grid, beacon.Position, out var where))
                continue;

            borg = Spawn(proto ?? DefaultChassis, where);
            reason = $"поставлен у «{beacon.Text}»";
            return true;
        }

        reason = "рядом с маяками не нашлось свободного пола — назови координаты";
        return false;
    }

    /// <summary>
    /// Клетки, на которые мы уже кого-то поставили в этом раунде.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Нужен потому, что <c>IsTileBlocked</c> НЕ ВИДИТ только что заспавненного робота. Режим
    /// ставит троих одним заходом, в одном кадре; физическая фикстура у новой сущности появляется
    /// сразу, а вот дерево broadphase, по которому и спрашивает <c>IsTileBlocked</c>, обновляется
    /// на следующем шаге физики. Внутри одного кадра запрос честно отвечает «свободно» про клетку,
    /// где уже кто-то стоит.
    /// </para>
    /// <para>
    /// На боевом раунде 159 это дало трёх роботов в одной точке буквально друг в друге. Добавление
    /// <c>CollisionGroup.MobMask</c> к проверке эту половину беды не лечит по той же причине —
    /// смотреть просто некуда, дерево ещё пустое. Поэтому учёт свой.
    /// </para>
    /// <para>
    /// Живёт до конца раунда, а не до конца пачки: робот, поставленный консолью посреди смены,
    /// тоже не должен встать в того, кого режим поставил на старте.
    /// </para>
    /// </remarks>
    private readonly HashSet<(EntityUid Grid, Vector2i Tile)> _takenTiles = new();

    /// <summary>Забыть занятые клетки — карта следующего раунда будет другой.</summary>
    public void ForgetTakenTiles() => _takenTiles.Clear();

    /// <summary>Сетка станции: та, на которой стоит ИИ-ядро НАСТОЯЩЕЙ станции.</summary>
    /// <remarks>
    /// <para>
    /// <b>Проверка на принадлежность станции обязательна, и это починка (20.08.2026).</b> Прежняя
    /// версия брала первое попавшееся ядро из запроса по компоненту и объявляла его сетку
    /// станцией. Ядро на карте не одно: своё есть у Central Command, и порядок обхода
    /// <c>EntityQueryEnumerator</c> ничем не обещает, что первым попадётся наше.
    /// </para>
    /// <para>
    /// На боевом раунде 159 попалось чужое: все три киборга поддержки встали на сетке
    /// <c>Central Command</c> в точке (21.5, −30.5), за сотни тайлов от станции, координаты
    /// которой лежат в диапазоне 200–400. Экипаж их не видел и не слышал, а сами они не могли
    /// сдвинуться и честно докладывали «не вижу пола ни под собой, ни у цели» — навигационной
    /// карты станции под ними, разумеется, не было. Режим при этом рапортовал «киборгов
    /// поддержки: 3 из 3», то есть выглядел исправным.
    /// </para>
    /// <para>
    /// <see cref="StationMemberComponent"/> — ровно тот признак, который отличает сетку станции от
    /// любой другой на карте: его вешает <c>StationSystem</c> при <c>Adding grid N to station</c>.
    /// Центком, шаттлы и обломки его не носят.
    /// </para>
    /// </remarks>
    public bool TryFindGrid(out EntityUid grid)
    {
        grid = default;

        var query = EntityQueryEnumerator<Shared.Silicons.StationAi.StationAiCoreComponent>();
        while (query.MoveNext(out var core, out _))
        {
            if (Transform(core).GridUid is not { } found)
                continue;

            if (!HasComp<StationMemberComponent>(found))
                continue;

            grid = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ближайший тайл, на котором можно стоять и с которого есть куда идти.
    /// </summary>
    /// <remarks>
    /// Проверяется не только «не стена и не космос», но и наличие полигона навмеша: тайл может быть
    /// физически проходим и при этом не входить в граф путепоиска, и тогда робот встанет намертво,
    /// сообщая «не нашёл дороги» даже на соседний тайл.
    /// </remarks>
    public bool TryFreeTileNear(EntityUid grid, Vector2 origin, out EntityCoordinates where)
    {
        // Два прохода. Сначала ищем тайл, который И проходим, И уже попал в граф путепоиска;
        // если такого нет — довольствуемся просто проходимым.
        //
        // Двухпроходность не педантизм, а следствие тайминга: чанки навмеша строятся асинхронно
        // после старта раунда, и в первые секунды GetPoly возвращает null ВЕЗДЕ. Однопроходная
        // версия с обязательным полигоном отказывалась ставить робота вообще — «рядом с маяками
        // не нашлось свободного пола» на полностью нормальной станции.
        return TryFreeTileNear(grid, origin, requireNavmesh: true, out where)
               || TryFreeTileNear(grid, origin, requireNavmesh: false, out where);
    }

    private bool TryFreeTileNear(EntityUid grid, Vector2 origin, bool requireNavmesh, out EntityCoordinates where)
    {
        where = default;

        if (!TryComp<MapGridComponent>(grid, out var gridComp))
            return false;

        var start = new Vector2i((int) MathF.Floor(origin.X), (int) MathF.Floor(origin.Y));

        for (var radius = 0; radius <= 10; radius++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    // Только рамка текущего радиуса — внутренность проверена на прошлых витках.
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;

                    var tile = start + new Vector2i(dx, dy);

                    if (!_maps.TryGetTileRef(grid, gridComp, tile, out var tileRef))
                        continue;

                    if (_turf.IsSpace(tileRef) || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                        continue;

                    // МОБЫ ТОЖЕ ЗАНИМАЮТ ТАЙЛ. Проверка выше их не видит: у робота маска
                    // MobMask, а не Impassable, и тайл под уже стоящим корпусом считался
                    // свободным. Режим ставит троих подряд одним вызовом на маяк, и все трое
                    // получали ОДНУ И ТУ ЖЕ клетку — на раунде 159 все три оказались в точке
                    // (21.5, −30.5) буквально друг в друге.
                    if (_turf.IsTileBlocked(tileRef, CollisionGroup.MobMask))
                        continue;

                    if (_takenTiles.Contains((grid, tile)))
                        continue;

                    var candidate = new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f));

                    if (requireNavmesh && _pathfinding.GetPoly(candidate) == null)
                        continue;

                    _takenTiles.Add((grid, tile));
                    where = candidate;
                    return true;
                }
            }
        }

        return false;
    }
}
