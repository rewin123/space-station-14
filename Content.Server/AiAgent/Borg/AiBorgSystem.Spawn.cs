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
/// Where to place a borg.
///
/// <para>
/// Lives in the system rather than in the console command for exactly the reason the first
/// version lived in the command: tests that spawned a borg "somewhere near the core" were testing
/// the wrong thing. The AI core room is sealed off: a borg placed there reports "couldn't find a
/// route" to any target, and that is the <b>correct</b> answer, just given from the wrong place.
/// Placement is part of the mechanic, not operator convenience.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// What <c>aiborg spawn</c> places without further specification. The evil-AI mode passes its
    /// own prototypes explicitly: a combat borg has both a different type and a different
    /// personality.
    /// </summary>
    public const string DefaultChassis = "AiBorgChassis";

    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private TurfSystem _turf = default!;

    /// <summary>
    /// Find a suitable spot on the station and place a borg there.
    /// </summary>
    /// <param name="beaconName">Part of a beacon's name, or <c>null</c> for any suitable one.</param>
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

        // We iterate over beacons instead of taking the first one: a beacon is typically a sign
        // on a wall, and there may be no free floor near a particular one.
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
    /// Tiles we have already placed someone on this round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because <c>IsTileBlocked</c> does NOT SEE a just-spawned borg. The mode places three
    /// of them in one pass, in a single frame; a new entity's physics fixture appears immediately,
    /// but the broadphase tree that <c>IsTileBlocked</c> actually queries only updates on the next
    /// physics step. Within a single frame, the query honestly reports "free" for a tile someone
    /// is already standing on.
    /// </para>
    /// <para>
    /// On live round 159 this put three borgs at the exact same point, literally inside each
    /// other. Adding <c>CollisionGroup.MobMask</c> to the check doesn't fix this half of the
    /// problem for the same reason — there is simply nothing to look at yet, the tree is still
    /// empty. Hence our own bookkeeping.
    /// </para>
    /// <para>
    /// Lives until the end of the round, not just the end of a batch: a borg placed via console
    /// mid-shift also shouldn't land on top of one the mode placed at round start.
    /// </para>
    /// </remarks>
    private readonly HashSet<(EntityUid Grid, Vector2i Tile)> _takenTiles = new();

    /// <summary>Forget the taken tiles — the next round's map will be different.</summary>
    public void ForgetTakenTiles() => _takenTiles.Clear();

    /// <summary>The station's grid: the one the REAL station's AI core stands on.</summary>
    /// <remarks>
    /// <para>
    /// <b>The check for station membership is mandatory, and this is a fix (2026-08-20).</b> The
    /// previous version took whatever core came up first from the component query and declared
    /// its grid the station. There is more than one core on the map: Central Command has its own,
    /// and the iteration order of <c>EntityQueryEnumerator</c> makes no promise that ours comes
    /// first.
    /// </para>
    /// <para>
    /// On live round 159 it picked someone else's: all three support cyborgs ended up on the
    /// <c>Central Command</c> grid at point (21.5, -30.5), hundreds of tiles from the station,
    /// whose coordinates lie in the 200-400 range. The crew never saw or heard them, and they
    /// couldn't move themselves either, honestly reporting "I don't see floor under me or the
    /// target" — there was, of course, no station navigation map under them. Meanwhile the mode
    /// reported "support cyborgs: 3 of 3", i.e. it looked healthy.
    /// </para>
    /// <para>
    /// <see cref="StationMemberComponent"/> is exactly the marker that distinguishes the station's
    /// grid from any other grid on the map: <c>StationSystem</c> attaches it on
    /// <c>Adding grid N to station</c>. Central Command, shuttles, and debris don't carry it.
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
    /// The nearest tile that can be stood on and that has somewhere to go from it.
    /// </summary>
    /// <remarks>
    /// Checks not only "not a wall and not space" but also whether a navmesh polygon exists: a
    /// tile can be physically walkable and still not be part of the pathfinding graph, and then
    /// the borg would get stuck solid, reporting "couldn't find a route" even to the adjacent tile.
    /// </remarks>
    public bool TryFreeTileNear(EntityUid grid, Vector2 origin, out EntityCoordinates where)
    {
        // Two passes. First we look for a tile that is BOTH walkable AND already in the
        // pathfinding graph; if there is none, we settle for merely walkable.
        //
        // The two passes aren't pedantry but a consequence of timing: navmesh chunks build
        // asynchronously after round start, and for the first few seconds GetPoly returns null
        // EVERYWHERE. A single-pass version requiring a polygon refused to place a borg at all —
        // "no free floor near the beacons" on an otherwise completely normal station.
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
                    // Only the border of the current radius — the interior was checked on
                    // previous passes.
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;

                    var tile = start + new Vector2i(dx, dy);

                    if (!_maps.TryGetTileRef(grid, gridComp, tile, out var tileRef))
                        continue;

                    if (_turf.IsSpace(tileRef) || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                        continue;

                    // MOBS OCCUPY A TILE TOO. The check above doesn't see them: a borg has the
                    // MobMask, not Impassable, and the tile under an already-standing body was
                    // considered free. The mode places three in a row with one call per beacon,
                    // and all three would get the SAME tile — on round 159 all three ended up at
                    // point (21.5, -30.5), literally inside each other.
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
