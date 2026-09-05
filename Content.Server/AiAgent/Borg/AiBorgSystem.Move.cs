using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.AiAgent.Perception;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Legs.
///
/// <para>
/// <see cref="BorgPathfinder"/> builds the route, <see cref="StepAlongTrail"/> follows it, and all
/// the physics — movement, collisions, speed, doors opening for the body — stays upstream: we put
/// the direction into the same field where a live player's client puts pressed arrow keys.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private SharedDoorSystem _door = default!;

    /// <summary>
    /// Hand reach, in tiles. The same number as <c>InRangeUnobstructed</c> uses for tools:
    /// "arrived" and "can reach" must measure with the same ruler, otherwise the robot gets two
    /// different answers about the same spot.
    /// </summary>
    private const float ReachTiles = 1.5f;

    /// <summary>
    /// Reach to a CONSOLE, in tiles. Longer than the hand's reach, and this is a DELIBERATE
    /// allowance, not a fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason is the SMES controller on the rotation map. It sits so that all four adjacent
    /// tiles are taken up by cables and a wall, and it can only be approached diagonally. From a
    /// diagonal, upstream <c>use</c> succeeds, but <c>InRangeUnobstructed</c> at 1.5 tiles does
    /// not: the center-to-center ray clips the corner. The robot built the entire reactor and
    /// could not open its console, reporting <c>not_visible</c> while standing right next to it.
    /// </para>
    /// <para>
    /// Two tiles covers the diagonal (1.41) with margin for offset within a tile, while still
    /// staying an "outstretched hand" rather than remote control: the ray still won't pass through
    /// a wall, the obstruction check stays in place. It's a departure from parity with a live
    /// player — who works with 1.5 — and it's called out here explicitly, same as the manipulator's
    /// free hand.
    /// </para>
    /// </remarks>
    private const float ConsoleReachTiles = 2f;

    /// <summary>
    /// Where the robot is walking to, so it can report arrival.
    ///
    /// <para>
    /// The walk tool <b>does not wait</b> for arrival: a turn hanging for half a minute while
    /// crossing the station is an agent deaf for the whole crossing. <c>goto</c> replies "walking"
    /// immediately, and the arrival fact comes in as an observation, like everything else in this
    /// module.
    /// </para>
    /// </summary>
    private readonly Dictionary<EntityUid, string> _walking = new();

    /// <summary>
    /// How the last walk ended: "arrived" or "no path".
    ///
    /// Needed by the script that's waiting for arrival. The ARRIVED observation is addressed to
    /// the model between turns, while the script runs while the turn is in progress, and cannot
    /// touch the observation queue at that moment — the loop drains it. So the outcome is
    /// duplicated here: one line per robot, valid until the next route.
    /// </summary>
    private readonly Dictionary<EntityUid, string> _lastWalk = new();

    /// <summary>
    /// The stall REFERENCE POINT: where the robot cannot get away from, and for how many frames.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The word "reference" is load-bearing here. The first version kept the position from the
    /// PREVIOUS frame in this field and overwrote it every time, including inside the "did not
    /// move" branch. So the stall counter was actually measuring the displacement over a single
    /// tick, not stagnation — and with a threshold of 0.15 tiles that was not a minor inaccuracy
    /// but a break right in the middle of the working range: the chassis sprints at 4.5 tiles per
    /// second, tickrate 30, which is exactly 0.15 tiles per tick. Measured on the bench: over 120
    /// ticks of walking, the maximum displacement was 0.1500, and NOT A SINGLE tick above the
    /// threshold.
    /// </para>
    /// <para>
    /// A robot walking at full speed was considered stationary every tick. Hence both complaints
    /// at once: once every 30 ticks it declared the tile it was walking on impassable and
    /// replanned the route — and replanning is a full station-wide A* that runs right here, in
    /// <see cref="Update"/>, past the bus budget and past its profile. Four robots walking at once
    /// produced around eighty milliseconds of pathfinding per second and thirty broadphase
    /// queries — which shows up in the game as "started moving and fps tanked". Each such replan
    /// also poisoned the robot's own corridor, so the path got longer with every attempt (64 → 54
    /// → 43 tiles in one round on the way to Tools) and ended in "no path" — the second complaint,
    /// "I can't reach you".
    /// </para>
    /// <para>
    /// Now the reference point STAYS PUT until the robot moves away from it by
    /// <see cref="ProgressTiles"/>. The threshold no longer depends on the chassis speed: it asks
    /// "did it move at all", not "did it manage to in a single tick".
    /// </para>
    /// <para>
    /// The cost of the bug shows in the walking itself too, not just in frame time: the same run
    /// over 150 ticks covered 2.2 tiles with the old counter and 14.3 with the new one
    /// (<c>RouteCostTests</c>). The robot was spending six sevenths of the trip declaring the
    /// corridor it was walking through impassable and replanning the route all over again.
    /// </para>
    /// </remarks>
    private readonly Dictionary<EntityUid, (Vector2 Where, int Stalls)> _progress = new();

    /// <summary>
    /// How far the robot must move away from the reference point for a stall to count as passed.
    /// Half a tile.
    /// </summary>
    /// <remarks>
    /// Half a cell is deliberately larger than any jitter in place (jostling, door recoil, body
    /// turning) and deliberately smaller than a single step along the route. A robot circling
    /// around one spot does not get away from this and is correctly considered stuck.
    /// </remarks>
    private const float ProgressTiles = 0.5f;

    /// <summary>This many frames without leaving the spot — and we try pressing a door. Half a second.</summary>
    /// <remarks>
    /// Counted in frames, meaningful in seconds: <see cref="PollWalking"/> is called every tick,
    /// tickrate 30. The previous four frames meant seven and a half broadphase queries per second
    /// per walking robot — and that was back when "walking" and "standing" weren't distinguished
    /// at all.
    /// </remarks>
    private const int StallsBeforeDoor = 15;

    /// <summary>
    /// This many — and we admit there's no way through here, and replan the route. Three seconds.
    /// </summary>
    /// <remarks>
    /// This is the expensive part: replanning builds a full path across the station and, on
    /// failure, unfolds the entire reachable floor. Three seconds of standing still is no longer
    /// "a person in the doorway" but a genuine obstacle, and the search cost isn't a concern in
    /// that situation.
    /// </remarks>
    private const int StallsBeforeReplan = 90;

    private void InitializeMovement()
    {
        Subs.CVar(_cfg, AiCVars.BorgMoveTrace, v => _netTrace = v, true);
    }

    private int _netTrace;

    private void TraceBorgMove(EntityUid borg, string phase, string extra = "")
    {
        if (_netTrace < 1)
            return;

        var coords = _xform.GetMapCoordinates(borg);
        _sawmill.Warning(
            $"NET TRACE kind=borg_move phase={phase} tick={_timing.CurTick} uid={borg} " +
            $"name={ToPrettyString(borg).ToString().Replace(' ', '_')} " +
            $"pos={coords.Position.X:F1},{coords.Position.Y:F1} map={coords.MapId} " +
            extra);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        PollWalking();

        foreach (var borg in _claimed.Keys)
            WatchCharge(borg);
    }

    private void StopSteering(EntityUid borg)
    {
        _walking.Remove(borg);
        _progress.Remove(borg);
        ClearRoute(borg);
        ClearTrail(borg);
    }

    /// <summary>Is the robot walking right now — this is what mutes the sight delta while walking.</summary>
    private bool IsWalking(EntityUid borg) => _walking.ContainsKey(borg);

    /// <summary>Walking state as one line — this is what the script reads via walk_status.</summary>
    private string WalkStatus(EntityUid borg)
    {
        if (_walking.TryGetValue(borg, out var what))
            return $"идёт: {what}";

        return _lastWalk.TryGetValue(borg, out var last) ? last : "стоит";
    }

    /// <summary>Drive everyone who's walking: step along the path, handle stalls, report arrival.</summary>
    private void PollWalking()
    {
        if (_walking.Count == 0)
            return;

        foreach (var (borg, what) in _walking.ToArray())
        {
            if (!Exists(borg) || TerminatingOrDeleted(borg))
            {
                StopSteering(borg);
                continue;
            }

            // Catch up with a moved goal — BEFORE stepping: otherwise the frame is spent moving
            // along a route that's already been declared stale.
            TryFollowMovingGoal(borg);

            if (StepAlongTrail(borg))
            {
                WatchForStall(borg, what);
                if (_netTrace >= 2 && _timing.CurTick.Value % 30 == 0)
                {
                    _progress.TryGetValue(borg, out var prog);
                    TraceBorgMove(borg, "walk",
                        $"goal={what.Replace(' ', '_')} stalls={prog.Stalls}");
                }
                continue;
            }

            // Ran out of tiles — arrived. We ask how close BEFORE clearing the route: the ordered
            // goal lives in _goals, and ClearRoute forgets it.
            var missed = MissedBy(borg);

            _walking.Remove(borg);
            _progress.Remove(borg);
            ClearRoute(borg);

            _lastWalk[borg] = "пришёл";

            var arrived = missed is { } gap
                ? $"ARRIVED дошёл до «{what}», насколько смог: до цели {gap:F1} тайла, " +
                  "ближе не подойти — клетки вокруг неё заняты. Руками отсюда не достать"
                : $"ARRIVED дошёл: {what}";

            PushToBorg(borg, Observation.Event(arrived, _host.RoundTime()));

            // Into the log too: "the robot isn't walking" and "the robot is walking slowly" look
            // the same in-game, and are distinguished only by this line.
            _sawmill.Info($"{ToPrettyString(borg)} дошёл: {what}"
                          + (missed is { } far ? $" (не дошёл {far:F1} тайла: подходы заняты)" : ""));
            TraceBorgMove(borg, "stop", $"goal={what.Replace(' ', '_')} missed={(missed is { } g ? g.ToString("F1") : "0")}");
        }
    }

    /// <summary>
    /// How many tiles short of the ordered goal the robot fell, or <c>null</c> if it's right up
    /// against it.
    /// </summary>
    /// <remarks>
    /// The route leads to the nearest passable tile, and that's correct for a door, a crate, or a
    /// console: you need to walk "to" it, not "into" it. But when ALL the cells around the goal
    /// are occupied — for example, the robot itself boxed in the SMES console with shielding — the
    /// nearest passable tile ends up two tiles away, while the ARRIVED line reported a plain
    /// "arrived". The model then honestly starts working with its hands and gets a range refusal:
    /// the tool said "arrived", the hand says "too far", and the reason isn't visible in any single
    /// line. Measured on round 131: the route to the controller at (28,-40) led to (26,-40), and
    /// the robot spent twenty minutes walking around it, trying console from every side.
    /// </remarks>
    private float? MissedBy(EntityUid borg)
    {
        if (!_goals.TryGetValue(borg, out var goal))
            return null;

        var target = _xform.ToMapCoordinates(goal.Dest);
        var here = _xform.GetMapCoordinates(borg);

        // Different grids — the distance between them means nothing; we stay silent rather than
        // lie with a number.
        if (target.MapId != here.MapId)
            return null;

        var gap = (target.Position - here.Position).Length();

        return gap > ReachTiles ? gap : null;
    }

    /// <summary>
    /// The robot is stuck — first try opening a door, then replan the route, then give up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters. The most common cause of a stall is a closed airlock: the body opens it by
    /// bumping into it (<c>DoorBumpOpener</c>), but not always from the right angle, and the robot
    /// has enough access rights to simply press it. If that doesn't help either, the door isn't the
    /// problem, and it's time to look for another way from the current spot.
    /// </para>
    /// <para>
    /// The replan threshold is deliberately high: replanning the route on every hiccup means
    /// hammering the station for nothing while a person is simply walking around the robot.
    /// </para>
    /// </remarks>
    private void WatchForStall(EntityUid borg, string what)
    {
        var now = _xform.GetMapCoordinates(borg).Position;

        if (!_progress.TryGetValue(borg, out var last))
        {
            _progress[borg] = (now, 0);
            return;
        }

        if ((now - last.Where).Length() > ProgressTiles)
        {
            _progress[borg] = (now, 0);

            // The robot moved — so the stall was passable, and the spent attempts don't count.
            // Without this, a long route with three doors exhausted the replan budget halfway
            // through, even though every door eventually opened.
            ForgetReplans(borg);
            return;
        }

        // The reference point stays UNCHANGED, and that's the key line of the function: with it,
        // the counter measures stagnation; without it, the per-tick displacement, which for a
        // walking chassis is exactly equal to the threshold.
        var stalls = last.Stalls + 1;
        _progress[borg] = (last.Where, stalls);

        // Press periodically, not once: an airlock closes on its own, and a single press isn't
        // always enough for the whole hiccup — especially when the robot approached it at an angle.
        if (stalls % StallsBeforeDoor == 0 && TryPressClosedDoor(borg, 1.6f))
        {
            _sawmill.Debug($"{ToPrettyString(borg)} упёрся и нажал на дверь");
            TraceBorgMove(borg, "door", $"goal={what.Replace(' ', '_')} stalls={stalls}");
            return;
        }

        if (stalls < StallsBeforeReplan)
            return;

        _progress[borg] = (now, 0);

        // The door didn't budge — treat it as a wall and look for a way around.
        //
        // This is more honest than any number of attempts: the reason could be anything — no
        // access, the door is welded shut, unpowered — and the robot must still either find
        // another way or honestly say there isn't one. It's also the only thing that saves it
        // from endlessly poking at the same door.
        if (NextTile(borg) is { } blocked)
        {
            BlockTile(borg, blocked);
            _sawmill.Debug($"{ToPrettyString(borg)} считает тайл {blocked} непроходимым и ищет обход");
        }

        if (TryReplan(borg))
            return;

        _walking.Remove(borg);
        _progress.Remove(borg);
        ClearRoute(borg);
        ClearTrail(borg);

        _lastWalk[borg] = $"нет пути: {what}";

        PushToBorg(borg, Observation.Event(
            $"NOPATH дороги нет: {what}. Путь перекрыт, и обойти не вышло.", _host.RoundTime()));

        _sawmill.Info($"{ToPrettyString(borg)} не смог пройти к «{what}»");
        TraceBorgMove(borg, "nopath", $"goal={what.Replace(' ', '_')}");
    }

    /// <summary>
    /// Press the nearest closed door. Returns true if one was found and pressed.
    /// </summary>
    /// <remarks>
    /// Upstream pathfinding knows two ways to deal with doors: "press" — for doors without a lock
    /// — and "pry with a crowbar" — for locked doors, via a long DoAfter that isn't even guaranteed
    /// to succeed on a powered airlock. The option "I have ID access, just open" doesn't exist for
    /// it at all, while the borg does have access: it's granted by a mind taking over.
    /// </remarks>
    private bool TryPressClosedDoor(EntityUid borg, float radius)
    {
        var doors = new HashSet<Entity<DoorComponent>>();
        _lookup.GetEntitiesInRange(_xform.GetMapCoordinates(borg), radius, doors,
            LookupFlags.Static | LookupFlags.Approximate);

        if (doors.Count == 0)
            return false;

        // Press the door ALONG THE DIRECTION OF TRAVEL, not just the first one found.
        //
        // In a vestibule there can be five of them at once — in combat the robot would end up in
        // the junction by the atmos entrance, surrounded by maintenance access, Engineering Lobby,
        // Atmospherics, and two airlocks. "The first one found" was just as likely to be the one
        // it had just walked out of.
        var aim = NextTile(borg) is { } tile && Transform(borg).GridUid is { } grid
            ? _xform.ToMapCoordinates(new EntityCoordinates(grid, Center(tile))).Position
            : _xform.GetMapCoordinates(borg).Position;

        var best = EntityUid.Invalid;
        var bestDist = float.MaxValue;

        foreach (var door in doors)
        {
            var state = door.Comp.State;
            if (state is DoorState.Open or DoorState.Opening)
                continue;

            var d = (_xform.GetMapCoordinates(door.Owner).Position - aim).Length();
            if (d >= bestDist)
                continue;

            bestDist = d;
            best = door.Owner;
        }

        if (!best.IsValid())
            return false;

        if (!TryComp<DoorComponent>(best, out var comp))
            return false;

        // First, the normal way, on behalf of the robot: with ID access, the door opens by its
        // own rights.
        if (_door.TryOpen(best, comp, user: borg))
            return true;

        // Access may be missing — and then the normal press does nothing. By the fork owner's
        // decision, the robot treats ANY unbolted airlock as passable, so it forces the closed
        // door open with no user: `HasAccess` with `user: null` skips the rights check, while
        // bolts, welding, and unpowered state stay in effect — they're what `BeforeDoorOpenedEvent`
        // cancels on, and a bolted door honestly won't open. Such a door will migrate to _blocked
        // after a few attempts, and the route will go around it.
        //
        // This is a DELIBERATE relaxation of parity, same as the manipulator and the hypercell.
        // The reason is a measured one: the route from the engineering wing to the SMES on the
        // rotation map goes through AirlockAtmosphericsGlassLocked, which the chassis has no
        // access to. The robot kept running into this door, replanning the route, running into it
        // again — and over half an hour of round 135 never made it back to the reactor it had
        // built itself, looping through Arrivals instead.
        //
        // Checking the door's state here doesn't work, and that cost a test run: a rights refusal
        // puts the door into Denying for a few ticks, and the condition "the door is still Closed"
        // doesn't fire on Denying — the forced-open silently didn't happen, even though the branch
        // looked like it worked.
        _door.TryOpen(best, comp, user: null);

        return true;
    }

    /// <summary>
    /// Press the nearest closed door — bench entry point.
    /// </summary>
    /// <remarks>
    /// On a live server this is done by <see cref="WatchForStall"/> once the robot stops moving.
    /// Reproducing a stall in a test would require real walking and timers, and what needs
    /// checking isn't the stall but the door decision.
    /// </remarks>
    public bool PressDoorForTest(EntityUid borg) => TryPressClosedDoor(borg, 1.6f);

    /// <summary>Push an observation into the queue of the agent sitting in this body.</summary>
    private void PushToBorg(EntityUid borg, Observation obs)
    {
        if (_host.Sessions.TryGetValue(borg, out var session))
            session.Queue.Push(obs);
    }
}
