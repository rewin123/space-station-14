using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Content.Shared.NPC;
using Content.Shared.Pinpointer;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Route across the whole station.
///
/// <para>
/// <b>Why this even exists.</b> Upstream pathfinding isn't built for station-wide crossings:
/// <c>PathfindingSystem.Common.cs</c> sets <c>NodeLimit = 512</c>, and A* simply stops
/// expanding the graph, returning <c>NoPath</c>. For regular NPCs this isn't a problem — they fight
/// and clean up within a room — but a borg told to "go to engineering" hits this limit and reports
/// "no path" while standing in the bar. Verified on the live server: three steps
/// east — "arrived," Bar → Bridge — "no path."
/// </para>
/// <para>
/// So the route is built by <see cref="BorgPathfinder"/> — our own search over the station map —
/// while upstream steering gets short legs, each of which fits within its limit.
/// The global route is ours, the local movement is theirs and battle-tested.
/// </para>
/// <para>
/// The first version cut the road along navigation beacons. That approach was bad because beacons
/// are placed by meaning, not by walkability: a chain of "nearest" beacons ran into locked
/// compartments, and the borg would stop at a transfer point even though walkable corridors
/// remained toward the goal.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// Where the borg was actually heading, how many times we've already replanned the route, and
    /// WHERE THE TARGET WAS when the route was built.
    /// </summary>
    /// <remarks>
    /// The last field is needed for pursuit. A target bound by handle is attached to the entity
    /// itself (<c>new EntityCoordinates(target, zero)</c>) precisely so the person the borg went
    /// after can keep moving — that's what <c>TryResolveDestination</c> does. But the binding only
    /// did half the job: the coordinate followed the person, while the path stayed laid out to the
    /// spot where they stood at the moment <c>goto</c> was called. The borg would reach empty floor
    /// and report arrival. The difference between "where the target is now" and "where it was when
    /// planned" is the only cheap way to notice this: the coordinate can be queried every frame, but
    /// replanning the route can't — that's a full station-wide A*.
    /// </remarks>
    private readonly Dictionary<EntityUid, (EntityCoordinates Dest, string Goal, int Replans, Vector2 PlannedAt)> _goals = new();

    /// <summary>How many frames have passed since the last replan for a moved target.</summary>
    private readonly Dictionary<EntityUid, int> _sinceRetarget = new();

    /// <summary>
    /// How many tiles the target must move away before the route gets replanned.
    /// </summary>
    /// <remarks>
    /// Three is noticeably more than hand range (1.5) and noticeably less than a room. A smaller
    /// threshold would make it chase every step the person takes; a larger one would land the borg
    /// in the next compartment.
    /// </remarks>
    private const float RetargetTiles = 3f;

    /// <summary>No more often than once per this many frames. One and a half seconds at tickrate 30.</summary>
    /// <remarks>
    /// Otherwise, chasing a running person turns into a pathfind every frame — exactly the breakage
    /// that used to bring the server down when borgs moved. One and a half seconds means a worst
    /// case of about six milliseconds of search per second for one pursuing borg.
    /// </remarks>
    private const int RetargetEvery = 45;

    /// <summary>
    /// What pathfinding has cost since the server started: how many times, how much total, and the
    /// worst case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not decoration, not curiosity. Pathfinding is the only heavy work the agent does that runs
    /// OUTSIDE <see cref="Content.Server.AiAgent.Threading.WorldBus"/>: replanning is called from
    /// <c>Update</c>, i.e. already on the main thread, and routing it through the bus would serve
    /// no purpose — it would only add latency. But along with the bus, the search also lost its
    /// profiling and its frame-overrun warning.
    /// </para>
    /// <para>
    /// The price of this blind spot was a whole round of troubleshooting: the log honestly showed
    /// 'look' and 'observation,' while eighty milliseconds of search per second showed up nowhere,
    /// leaving "observation is fixed but the lag remains" as the only available conclusion. Now the
    /// overrun line is written with the same text as the bus uses, and found by the same grep.
    /// </para>
    /// </remarks>
    public (int Searches, double TotalMs, double WorstMs, int WorstProbes) RouteCost { get; private set; }

    /// <summary>Reset the search counters — for the bench, which measures a single scenario.</summary>
    public void ResetRouteCost() => RouteCost = default;

    /// <summary>
    /// How many times to replan the route before declaring that there's no path.
    ///
    /// <para>
    /// It used to be three, and that was enough while replanning was just a repeat: with the same
    /// set of obstacles it produced the same path, and there was no point hitting it more than three
    /// times. Now every attempt LEARNS SOMETHING — an impassable tile goes into <see cref="_blocked"/>,
    /// and the next path routes around it — so attempts aren't wasted. The vestibule at the atmos
    /// entrance on the rotation map is surrounded by five doors at once, and three attempts there
    /// used to run out before getting through even half of them.
    /// </para>
    /// </summary>
    private const int MaxReplans = 10;

    /// <summary>
    /// Tiles that the borg has marked impassable on this route.
    ///
    /// Populated on the spot: a door that didn't open, a hatch that got welded shut, a tile the
    /// chassis just doesn't fit through. Lives until the end of the route — a new task starts with
    /// a clean slate, because by then the door might have been opened.
    /// </summary>
    private readonly Dictionary<EntityUid, HashSet<Vector2i>> _blocked = new();

    /// <summary>Mark a tile impassable for the current route.</summary>
    private void BlockTile(EntityUid borg, Vector2i tile)
    {
        if (!_blocked.TryGetValue(borg, out var set))
            _blocked[borg] = set = new HashSet<Vector2i>();

        set.Add(tile);
    }

    /// <summary>
    /// Build a route to a point and follow it.
    /// </summary>
    public bool TryStartRoute(EntityUid borg, EntityCoordinates destination, string goal, out string why)
    {
        why = string.Empty;

        var xform = Transform(borg);
        var grid = xform.GridUid;

        if (grid == null || !TryComp<NavMapComponent>(grid.Value, out var navMap))
        {
            why = "я вне сетки станции — идти отсюда некуда";
            return false;
        }

        var from = ToTile(xform.LocalPosition);

        // The target is converted into the GRID's coordinate system, not read as-is.
        //
        // EntityCoordinates.Position is an offset relative to the PARENT, while a target bound by
        // handle is attached to the entity itself: its Position is (0,0). Reading it as grid
        // coordinates sent the borg to the station's coordinate origin — in live play this looked
        // like the borg walking halfway across the station in the wrong direction on a "go to the
        // door two steps away" order.
        var destMap = _xform.ToMapCoordinates(destination);
        var to = ToTile(Vector2.Transform(destMap.Position, _xform.GetInvWorldMatrix(grid.Value)));

        // The target is almost never walkable by itself: a beacon is a sign on the wall, a door
        // handle is the door itself. You have to walk "to" it, not "into" it.
        // Walkability is checked against steering's navmesh BY ITS OWN RULE.
        //
        // Having a polygon isn't enough: upstream keeps a tile with collision impassable for us on
        // the navmesh, but GetTileCost returns zero for it, i.e. "can't go here." That's what
        // machines, lockers, and tables look like — our map doesn't see them at all, while steering
        // sees and avoids them. We repeat its condition verbatim, otherwise our path leads somewhere
        // it won't go: in live play the borg walked 27 of 47 tiles and stopped in a corridor with no
        // door within four tiles.
        var (ourLayer, ourMask) = TryComp<FixturesComponent>(borg, out var fixtures)
            ? _physics.GetHardCollision(borg, fixtures)
            : (0, 0);

        _blocked.TryGetValue(borg, out var blocked);

        bool Walkable(Vector2i t)
        {
            if (blocked != null && blocked.Contains(t))
                return false;

            var poly = _pathfinding.GetPoly(new EntityCoordinates(grid.Value, ToLocal(t)));

            if (poly == null)
                return false;

            var data = poly.Data;

            if ((ourLayer & data.CollisionMask) == 0 && (ourMask & data.CollisionLayer) == 0)
                return true;

            // There's a collision — but we open doors and climb over railings. The same
            // allowances our set of PathFlags gives to steering.
            return (data.Flags & PathfindingBreadcrumbFlag.Door) != 0
                   || (data.Flags & PathfindingBreadcrumbFlag.Climb) != 0;
        }

        var goalTile = BorgPathfinder.NearestPassable(navMap, to, walkable: Walkable);
        var startTile = BorgPathfinder.NearestPassable(navMap, from, walkable: Walkable);

        if (startTile == null || goalTile == null)
        {
            why = $"не вижу пола ни под собой, ни у цели «{goal}»";
            return false;
        }

        // Requested tile vs. the chosen one.
        //
        // Debug, not Info: for a door, crate, or console the offset is CORRECT — you have to walk
        // "to" it, not "into" it, and on such targets this line would spam constantly. But for bare
        // coordinates an offset means "the tile was deemed impassable," and this is the only way to
        // tell "the route went the wrong way" apart from "the walker didn't arrive." This exact line
        // is what revealed that the coordinate was being read as a tile corner, and rounding error
        // was pushing it into the neighboring tile.
        if (goalTile.Value != to)
            _sawmill.Debug($"заказан тайл {to}, маршрут ведёт в {goalTile.Value} («{goal}»)");

        var stats = new BorgPathfinder.PathStats();
        var started = Stopwatch.GetTimestamp();
        var path = BorgPathfinder.FindPath(navMap, startTile.Value, goalTile.Value, Walkable, stats);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        ObserveSearch(ms, stats);

        if (path == null)
        {
            why = $"дороги до «{goal}» нет: всё перекрыто либо цель на другой сетке";
            return false;
        }

        // We steer ourselves through every tile of the path. There are no transfers at all: the
        // reason they existed — to fit within someone else's limit — stopped being a concern along
        // with someone else's steering.
        SetTrail(borg, path);
        // The outcome of the previous walk is forgotten here: otherwise a script that asked
        // walk_status right after starting a new route would get "arrived" from the previous one
        // and move on.
        _lastWalk.Remove(borg);
        _walking[borg] = goal;

        TraceBorgMove(borg, "start", $"goal={goal.Replace(' ', '_')} tiles={path.Count}");

        // The target point is remembered ALWAYS, while the replan counter resets only on a task
        // change: one says "where it was," the other "how many times we've already hit a wall,"
        // and they must not be mixed up.
        _goals[borg] = !_goals.TryGetValue(borg, out var known) || known.Goal != goal
            ? (destination, goal, 0, destMap.Position)
            : (known.Dest, known.Goal, known.Replans, destMap.Position);

        _sinceRetarget[borg] = 0;

        _sawmill.Info(
            $"{ToPrettyString(borg)} маршрут до «{goal}»: {path.Count} тайлов; " +
            $"старт {startTile.Value} цель {goalTile.Value}");

        return true;
    }

    /// <summary>Record the cost of one search and complain if it ate a frame.</summary>
    private void ObserveSearch(double ms, BorgPathfinder.PathStats stats)
    {
        var c = RouteCost;

        RouteCost = (c.Searches + 1, c.TotalMs + ms,
            Math.Max(c.WorstMs, ms),
            Math.Max(c.WorstProbes, stats.Probes));

        var budget = _cfg.GetCVar(AiCVars.MainThreadBudgetMs);

        if (ms <= budget)
            return;

        // Worded identically to the bus (WorldBus.Observe): a single grep must be able to find both
        // sources of overrun, otherwise troubleshooting runs into "profile is clean but there's
        // still lag" again.
        _sawmill.Warning(
            $"main-thread call 'route' took {ms:F1}ms (budget {budget:F1}ms), " +
            $"узлов {stats.Expanded}, проверок проходимости {stats.Probes}");
    }

    private static Vector2i ToTile(Vector2 local) =>
        new((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y));

    private static Vector2 ToLocal(Vector2i tile) =>
        new(tile.X + 0.5f, tile.Y + 0.5f);


    private void ClearRoute(EntityUid borg)
    {
        _goals.Remove(borg);
        _blocked.Remove(borg);
        _sinceRetarget.Remove(borg);
    }

    /// <summary>The borg made progress — reset the replan counter.</summary>
    private void ForgetReplans(EntityUid borg)
    {
        if (_goals.TryGetValue(borg, out var g) && g.Replans != 0)
            _goals[borg] = (g.Dest, g.Goal, 0, g.PlannedAt);
    }

    /// <summary>
    /// The target moved — catch up to it by replanning the route.
    /// </summary>
    /// <returns><c>true</c> if the route was replanned.</returns>
    /// <remarks>
    /// <para>
    /// Called every frame for every walking borg, so the cheap part goes first: comparing two
    /// points can be done as many times as needed, but building a path can't.
    /// </para>
    /// <para>
    /// The replan counter (<see cref="MaxReplans"/>) is NOT spent here, and that's not an
    /// oversight. That budget answers the question "is there a path at all," while pursuit answers
    /// a different one — "am I even going the right way." A person fleeing the borg across half the
    /// station would exhaust the shared budget within half a minute, and the borg would declare "no
    /// path" about a perfectly walkable corridor.
    /// </para>
    /// </remarks>
    private bool TryFollowMovingGoal(EntityUid borg)
    {
        if (!_goals.TryGetValue(borg, out var goal))
            return false;

        // A compartment or bare coordinate doesn't move: it's bound to the grid, not to an entity.
        if (!Exists(goal.Dest.EntityId) || HasComp<MapGridComponent>(goal.Dest.EntityId))
            return false;

        var since = _sinceRetarget.TryGetValue(borg, out var n) ? n + 1 : 1;
        _sinceRetarget[borg] = since;

        if (since < RetargetEvery)
            return false;

        _sinceRetarget[borg] = 0;

        var now = _xform.ToMapCoordinates(goal.Dest).Position;

        if ((now - goal.PlannedAt).Length() < RetargetTiles)
            return false;

        if (!TryStartRoute(borg, goal.Dest, goal.Goal, out _))
            return false;

        // The stuck-detection reference point is reset from scratch: the borg started moving in a
        // different direction, and the old point would have declared it stuck on the very first
        // step back.
        _progress.Remove(borg);

        _sawmill.Debug($"{ToPrettyString(borg)} догоняет ушедшую цель «{goal.Goal}»");
        return true;
    }

    /// <summary>
    /// A leg failed — replan the route FROM THE CURRENT LOCATION.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first version simply skipped the failed leg and moved on to the next one, and that was
    /// worse than nothing: the next leg is <b>further away</b>, and upstream steering has its own
    /// limit of 512 nodes. Every skip made things worse, and the route fell apart entirely — in
    /// live play "get to the AME" ended in "no path," even though our own search found a path in
    /// 47 tiles.
    /// </para>
    /// <para>
    /// The reason a leg fails at all: our map knows floors, walls, and airlocks, but not furniture
    /// or machinery. A transfer point could land on a tile occupied by a table. Replanning from the
    /// current spot fixes this too: the new path will route around the occupied tile, because the
    /// borg is no longer standing where it used to.
    /// </para>
    /// </remarks>
    private bool TryReplan(EntityUid borg)
    {
        if (!_goals.TryGetValue(borg, out var goal))
            return false;

        if (goal.Replans >= MaxReplans)
            return false;

        _goals[borg] = (goal.Dest, goal.Goal, goal.Replans + 1, goal.PlannedAt);

        if (TryStartRoute(borg, goal.Dest, goal.Goal, out _))
        {
            _sawmill.Info($"{ToPrettyString(borg)} перекладывает маршрут до «{goal.Goal}» " +
                           $"(попытка {goal.Replans + 1} из {MaxReplans})");
            return true;
        }

        return false;
    }

}
