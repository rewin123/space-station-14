using System;
using System.Linq;
using System.Numerics;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Pinpointer;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

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
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private TurfSystem _turf = default!;

    /// <summary>
    /// Найти станции пригодное место и поставить туда робота.
    /// </summary>
    /// <param name="beaconName">Часть названия маяка, или <c>null</c> — любой подходящий.</param>
    public bool TrySpawnBorg(string? beaconName, out EntityUid borg, out string reason)
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

            borg = Spawn("AiBorgChassis", where);
            reason = $"поставлен у «{beacon.Text}»";
            return true;
        }

        reason = "рядом с маяками не нашлось свободного пола — назови координаты";
        return false;
    }

    /// <summary>Сетка станции: та, на которой стоит ИИ-ядро.</summary>
    public bool TryFindGrid(out EntityUid grid)
    {
        grid = default;

        var query = EntityQueryEnumerator<Shared.Silicons.StationAi.StationAiCoreComponent>();
        if (!query.MoveNext(out var core, out _))
            return false;

        if (Transform(core).GridUid is not { } found)
            return false;

        grid = found;
        return true;
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

                    var candidate = new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f));

                    if (requireNavmesh && _pathfinding.GetPoly(candidate) == null)
                        continue;

                    where = candidate;
                    return true;
                }
            }
        }

        return false;
    }
}
