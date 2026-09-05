using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Windows into the robot's internals — bench only.
/// </summary>
/// <remarks>
/// A separate file for the same reason as <c>StationAiAgentSystem.Testing</c>: test entry points
/// must not sit mixed in with production code, or within a month someone starts calling them from
/// production.
/// </remarks>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// How many frames in a row the robot has failed to move away from the reference point.
    /// −1 means nobody is tracking it.
    /// </summary>
    /// <remarks>
    /// The only way to verify the key property of the stall counter: a walking robot does not
    /// accumulate stalls. Nothing visible from outside reveals this — not position, not the route,
    /// not the log: the broken counter looked healthy right up until the count reached thirty and
    /// the robot declared the corridor it was walking through impassable.
    /// </remarks>
    public int StallsForTest(EntityUid borg) =>
        _progress.TryGetValue(borg, out var p) ? p.Stalls : -1;

    /// <summary>Tiles the robot considered impassable on the current route.</summary>
    public int BlockedTilesForTest(EntityUid borg) =>
        _blocked.TryGetValue(borg, out var set) ? set.Count : 0;

    /// <summary>The same string the script sees via <c>walk_status</c>.</summary>
    public string WalkStatusForTest(EntityUid borg) => WalkStatus(borg);

    /// <summary>
    /// Hide or restore the body's subtree without spinning up an agent.
    /// </summary>
    /// <remarks>
    /// A bench with an actual connected client needs exactly this step, not the whole takeover:
    /// takeover requires <c>ai.enabled</c> to be on and a live model, which drags the loop's turn
    /// along with its log into a test about the makeup of the PVS packet. The hiding itself, on
    /// takeover, is verified separately, via the visibility mask on the server.
    /// </remarks>
    public void SetSubtreeHiddenForTest(EntityUid borg, bool hidden)
    {
        if (hidden)
            HideSubtree(borg);
        else
            ShowSubtree(borg);
    }

    /// <summary>
    /// Full sweep of the field of view: how many entities the radius query returned and how many
    /// passed the ray check.
    /// </summary>
    /// <remarks>
    /// The bench uses this to measure the cost of <c>BeforeObservation</c>. These two numbers
    /// specifically explain the cost: the radius query costs one broadphase call, while the
    /// <c>InRangeUnOccluded</c> ray is paid for per candidate, individually.
    /// </remarks>
    public (int Visible, int Candidates) SightDeltaCostForTest(EntityUid borg)
    {
        var candidates = _lookup.GetEntitiesInRange(_xform.GetMapCoordinates(borg), 8.5f,
            LookupFlags.Uncontained | LookupFlags.Approximate);

        return (VisibleFrom(borg).Count, candidates.Count);
    }

    /// <summary>
    /// Push a HIT into the queue the same way a real swing would — bench entry point.
    /// </summary>
    public void ReportHitForTest(EntityUid borg, EntityUid who, EntityUid used = default) =>
        ReportHit(borg, who, used);
}
