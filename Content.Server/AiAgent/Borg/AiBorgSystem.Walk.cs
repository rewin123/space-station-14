using System.Collections.Generic;
using System.Numerics;
using Content.Shared.Movement.Components;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// The robot walks its OWN path itself, without the upstream steering system.
///
/// <para>
/// <b>Why this was necessary.</b> Originally our pathfinder built the route, and following it was
/// left to <c>NPCSteeringSystem</c> — via short legs, each one guaranteed to be within its limits.
/// On the rotation map this ran into a wall: the robot walked 27 of 47 tiles and stopped at
/// (28, -25) at the entrance to atmospherics, reporting "no path". Our path there had been built
/// and validated by its OWN walkability rule — the navmesh polygon exists, the collision doesn't
/// conflict — and it still refused, identically with six-tile legs and with three-tile ones.
/// </para>
/// <para>
/// Betting on "our global route, someone else's local movement" didn't pay off, and sticking with
/// it further would have meant propping up someone else's pathfinder with ever more workarounds.
/// So movement is ours too: since the path already exists and is correct, following it is a
/// ten-line job.
/// </para>
/// <para>
/// <b>What is NOT lost in the process.</b> The robot moves the same way as any mob in the game —
/// through <c>InputMoverComponent.CurTickSprintMovement</c>, i.e. exactly the field a live
/// player's client writes pressed arrow keys into. Physics, collisions, speed, weightlessness, and
/// opening doors by body contact (<c>DoorBumpOpener</c>) all remain upstream. We only set the
/// direction.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private IGameTiming _timing = default!;

    /// <summary>The tiles the robot still has left to walk, and where it's ultimately headed.</summary>
    private readonly Dictionary<EntityUid, Queue<Vector2i>> _trail = new();

    /// <summary>How close it needs to get to a tile to count it as reached.</summary>
    private const float TileReached = 0.35f;

    /// <summary>Approach speed for the last tile: we walk to the target at a crawl to avoid overshooting.</summary>
    // Strictly less than half a cell, and this isn't an eyeballed tweak.
    //
    // Half a cell is 0.5. A threshold of 0.6 meant "arrived" while the robot was already standing
    // on the NEXT tile over: unnoticeable for "walk up to the door", but fatal for construction —
    // the package lands in the wrong spot and the shielding square doesn't close up. Caught by the
    // build test: nine cells ordered, all nine times the robot stopped short, by exactly one cell.
    private const float ArriveDistance = 0.3f;

    private void SetTrail(EntityUid borg, List<Vector2i> tiles)
    {
        var queue = new Queue<Vector2i>();

        foreach (var t in tiles)
            queue.Enqueue(t);

        _trail[borg] = queue;
    }

    private void ClearTrail(EntityUid borg)
    {
        _trail.Remove(borg);
        Halt(borg);
    }

    /// <summary>Stop the legs. Without this the robot keeps riding on its last input.</summary>
    private void Halt(EntityUid borg)
    {
        if (!TryComp<InputMoverComponent>(borg, out var mover))
            return;

        mover.CurTickSprintMovement = Vector2.Zero;
        mover.LastInputTick = _timing.CurTick;
        mover.LastInputSubTick = ushort.MaxValue;
    }

    /// <summary>
    /// One step of guidance: move the robot toward the next tile of the path.
    /// </summary>
    /// <returns><c>true</c> while still walking; <c>false</c> once the path is complete.</returns>
    private bool StepAlongTrail(EntityUid borg)
    {
        if (!_trail.TryGetValue(borg, out var trail) || !TryComp<InputMoverComponent>(borg, out var mover))
            return false;

        var xform = Transform(borg);

        if (xform.GridUid is not { } grid)
            return false;

        var here = xform.LocalPosition;

        // Consume all tiles already reached: at speed, more than one can be covered per tick.
        while (trail.Count > 0)
        {
            var target = Center(trail.Peek());
            var last = trail.Count == 1;

            if ((target - here).Length() > (last ? ArriveDistance : TileReached))
                break;

            trail.Dequeue();
        }

        if (trail.Count == 0)
        {
            _trail.Remove(borg);
            Halt(borg);
            return false;
        }

        var next = Center(trail.Peek());
        var delta = next - here;

        if (delta.LengthSquared() < 0.0001f)
            return true;

        // The same field and the same marks that the upstream steering system sets in
        // SetDirection: the engine doesn't distinguish our input from a live player's keys, and
        // all the physics comes to us for free.
        mover.CurTickSprintMovement = Vector2.Normalize(delta);
        mover.LastInputTick = _timing.CurTick;
        mover.LastInputSubTick = ushort.MaxValue;

        return true;
    }

    /// <summary>The next tile of the path, if the robot is walking anywhere.</summary>
    private Vector2i? NextTile(EntityUid borg) =>
        _trail.TryGetValue(borg, out var trail) && trail.Count > 0 ? trail.Peek() : null;

    private static Vector2 Center(Vector2i tile) => new(tile.X + 0.5f, tile.Y + 0.5f);
}
