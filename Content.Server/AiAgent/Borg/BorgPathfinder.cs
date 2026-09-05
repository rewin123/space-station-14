using System.Collections.Generic;
using Content.Shared.Pinpointer;
using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Our own pathfinder covering the whole station.
///
/// <para>
/// <b>Why we needed our own.</b> The upstream <c>PathfindingSystem</c> isn't built for
/// crossings the length of the station: <c>NodeLimit = 512</c> cuts off graph expansion, and A*
/// returns <c>NoPath</c>. That's plenty for regular NPCs — they fight and clean up within a
/// room — but a robot told "go to Engineering" hit the limit and reported "no path," while
/// standing in a perfectly walkable corridor. Measured in combat: three steps east — "arrived,"
/// Bar → Bridge — "no path."
/// </para>
/// <para>
/// The workaround with a chain of navigation beacons handled this poorly: beacons are placed by
/// meaning, not by walkability, and a chain of "nearest" ones ran into locked compartments. Here
/// we do an honest search instead.
/// </para>
/// <para>
/// <b>What we search over.</b> The <see cref="NavMapComponent"/> — the very same map the game
/// already builds and maintains for the handheld navigation tablet. It's a bitmap of the whole
/// station: floor, walls, and airlocks, in 8×8 chunks. Not a single broadphase query, not a single
/// entity traversal — which is why a full search across the station costs less than one
/// <c>look</c>.
/// </para>
/// <para>
/// <b>What this search does NOT do.</b> It doesn't drive the robot: the found path is cut into
/// short legs and handed off to the upstream steering system. That system knows everything the
/// map doesn't — physics, dodging furniture and people, opening doors. The split is deliberate:
/// the global route is ours, local movement is someone else's and battle-tested.
/// </para>
/// </summary>
public static class BorgPathfinder
{
    /// <summary>
    /// What one search cost.
    /// </summary>
    /// <remarks>
    /// Set up because route replanning doesn't go through the world bus but runs straight inside
    /// <c>Update</c> — meaning its cost showed up in neither the frame budget, nor the profiler,
    /// nor <c>aiagent cost</c>. Precisely because of this blind spot, the pathfinding-stall bug
    /// stayed alive under a clean profile: the log showed 'look' and 'observation', while eighty
    /// milliseconds of search per second was invisible anywhere.
    /// </remarks>
    public sealed class PathStats
    {
        /// <summary>Nodes popped off the queue.</summary>
        public int Expanded;

        /// <summary>Walkability checks. The most expensive part: each one is a trip into someone else's navmesh.</summary>
        public int Probes;
    }

    /// <summary>
    /// Ceiling on expanded nodes.
    ///
    /// An order of magnitude above the upstream 512, and still cheap: a node is a chunk-dictionary
    /// read plus a bit comparison. It exists as insurance against searching through infinite empty
    /// space, not as a real budget — an actual station-wide crossing lands in the thousands.
    /// </summary>
    public const int NodeLimit = 60_000;

    /// <summary>
    /// How many times more expensive it is to pass through an airlock.
    ///
    /// A door is physically walkable, but it has to be opened, which costs seconds and sometimes
    /// an access denial. The penalty makes the search prefer a detour through the corridor —
    /// exactly the choice a person would make.
    /// </summary>
    private const float DoorCost = 4f;

    /// <summary>A tile is walkable if there's floor underfoot and no wall. An airlock is walkable if the borg has access.</summary>
    public static bool Passable(NavMapComponent navMap, Vector2i tile)
    {
        if (!TryGetTileData(navMap, tile, out var data))
            return false;

        if ((data & SharedNavMapSystem.FloorMask) == 0)
            return false;

        // Both walls and windows fall under the Wall category. A window is transparent to eyes but not to a chassis.
        return (data & SharedNavMapSystem.WallMask) == 0;
    }

    private static bool IsDoor(NavMapComponent navMap, Vector2i tile) =>
        TryGetTileData(navMap, tile, out var data) && (data & SharedNavMapSystem.AirlockMask) != 0;

    private static bool TryGetTileData(NavMapComponent navMap, Vector2i tile, out int data)
    {
        data = 0;

        var origin = SharedMapSystem.GetChunkIndices(tile, SharedNavMapSystem.ChunkSize);

        if (!navMap.Chunks.TryGetValue(origin, out var chunk))
            return false;

        var relative = SharedMapSystem.GetChunkRelative(tile, SharedNavMapSystem.ChunkSize);
        data = chunk.TileData[SharedNavMapSystem.GetTileIndex(relative)];
        return true;
    }

    /// <summary>
    /// The nearest walkable tile to a point, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because the goal is almost never a walkable tile itself: a navigation beacon is a
    /// sign on a wall, and a door handle is the door itself. You need to walk "to" it, not "into" it.
    /// </para>
    /// <para>
    /// Within a ring, tiles are iterated BY DISTANCE, not by index order. The loop used to run
    /// <c>dx</c> from <c>-r</c>, <c>dy</c> from <c>-r</c>, and the first candidate of ring r=1
    /// turned out to be the corner (−1,−1): the robot would be placed diagonally from the target
    /// even though the adjacent side was free. The distance itself came out to 1.5 and passed the
    /// range check, but <c>InRangeUnobstructed</c> from the diagonal catches the corner and
    /// refuses — "arrived, but can't work," without a single line about why.
    /// </para>
    /// </remarks>
    public static Vector2i? NearestPassable(NavMapComponent navMap, Vector2i around, int radius = 12,
        Func<Vector2i, bool>? walkable = null)
    {
        bool Open(Vector2i t) => Passable(navMap, t) && (walkable == null || walkable(t));

        if (Open(around))
            return around;

        var ring = new List<Vector2i>();

        for (var r = 1; r <= radius; r++)
        {
            ring.Clear();

            for (var dx = -r; dx <= r; dx++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;

                    ring.Add(around + new Vector2i(dx, dy));
                }
            }

            // Squared distance is an integer, so the comparison is exact. A side (1) sorts before a corner (2).
            ring.Sort((a, b) => Sq(a - around).CompareTo(Sq(b - around)));

            foreach (var candidate in ring)
            {
                if (Open(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static int Sq(Vector2i v) => v.X * v.X + v.Y * v.Y;

    /// <summary>
    /// A tile-by-tile path from start to goal, or <c>null</c> if none exists.
    /// </summary>
    /// <remarks>
    /// Neighbors are only the four cardinal sides. Diagonals would shave a few percent off the
    /// route length but would cost cut-corner checks: a chassis can't pass diagonally between two
    /// walls, and a path the map considers valid would end with the robot stuck in a doorframe.
    /// </remarks>
    /// <param name="walkable">
    /// An extra tile check — usually "does the upstream navmesh see it too."
    ///
    /// <para>
    /// Without it the search lies in one direction: the <see cref="NavMapComponent"/> map knows
    /// floors, walls, and airlocks, but does NOT know about machines, furniture, or anything else
    /// sitting on the floor. The path was happily laid straight through a tile occupied by
    /// equipment, the local steering system stalled on it, and replanning from the same spot
    /// produced the exact same path. In combat this looked like: the robot walked 27 of 47 tiles
    /// and then stopped in an open corridor, with not a single door within four tiles.
    /// </para>
    /// <para>
    /// With this check, the search runs over the SAME graph as the steering system, and differs
    /// from it in exactly one way — the absence of the 512-node ceiling, which is the whole reason
    /// this exists.
    /// </para>
    /// </param>
    public static List<Vector2i>? FindPath(NavMapComponent navMap, Vector2i start, Vector2i goal,
        Func<Vector2i, bool>? walkable = null, PathStats? stats = null)
    {
        bool Open(Vector2i t)
        {
            if (stats != null)
                stats.Probes++;

            return Passable(navMap, t) && (walkable == null || walkable(t));
        }

        if (start == goal)
            return new List<Vector2i> { start };

        if (!Open(start) || !Open(goal))
            return null;

        var frontier = new PriorityQueue<Vector2i, float>();
        var cameFrom = new Dictionary<Vector2i, Vector2i>();
        var costSoFar = new Dictionary<Vector2i, float> { [start] = 0f };

        frontier.Enqueue(start, 0f);

        var expanded = 0;
        var found = false;

        while (frontier.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                found = true;
                break;
            }

            if (++expanded > NodeLimit)
                break;

            if (stats != null)
                stats.Expanded = expanded;

            foreach (var dir in Neighbours)
            {
                var next = current + dir;

                if (!Open(next))
                    continue;

                var step = IsDoor(navMap, next) ? DoorCost : 1f;
                var cost = costSoFar[current] + step;

                if (costSoFar.TryGetValue(next, out var known) && cost >= known)
                    continue;

                costSoFar[next] = cost;
                cameFrom[next] = current;
                frontier.Enqueue(next, cost + Heuristic(next, goal));
            }
        }

        if (!found)
            return null;

        var path = new List<Vector2i>();
        var node = goal;

        while (node != start)
        {
            path.Add(node);

            if (!cameFrom.TryGetValue(node, out node))
                return null;
        }

        path.Add(start);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Cut the path into legs the upstream steering system can handle.
    /// </summary>
    /// <remarks>
    /// Each leg has to fit within its <c>NodeLimit = 512</c> polygons, so we take a point every
    /// <paramref name="every"/> tiles. The last tile is always added: without it the robot would
    /// stop a few steps short of the goal.
    /// </remarks>
    public static List<Vector2i> ToLegs(List<Vector2i> path, int every = 6, Func<Vector2i, bool>? reachable = null)
    {
        var legs = new List<Vector2i>();

        for (var i = every; i < path.Count; i += every)
        {
            var tile = path[i];

            // A handoff point must be somewhere the local steering system can actually reach. Our
            // map knows floors, walls, and airlocks, but not furniture and machines: a point every
            // six tiles can easily land on a tile occupied by a table, and then the leg won't
            // complete. If we can see that in advance, shift to a neighboring tile on the path.
            if (reachable != null && !reachable(tile))
            {
                var shifted = false;

                for (var back = 1; back <= 2 && i - back > 0; back++)
                {
                    if (!reachable(path[i - back]))
                        continue;

                    legs.Add(path[i - back]);
                    shifted = true;
                    break;
                }

                if (shifted)
                    continue;
            }

            legs.Add(tile);
        }

        if (legs.Count == 0 || legs[^1] != path[^1])
            legs.Add(path[^1]);

        return legs;
    }

    private static readonly Vector2i[] Neighbours =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
    };

    private static float Heuristic(Vector2i a, Vector2i b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
