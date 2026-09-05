using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Perception;
using Content.Shared.Examine;
using Content.Shared.IdentityManagement;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// The robot's eyes — and the world diff.
///
/// <para>
/// <b>Why not Station AI's upstream vision.</b> The temptation to reuse
/// <c>StationAiVisionSystem.GetView</c> is strong and wrong: it collects the <em>union across all
/// seeds</em> of <c>StationAiVisionComponent</c> within a radius, i.e. across every camera around.
/// A robot with that vision would see the station through the surveillance network's eyes while
/// standing in a dark corridor — meaning it would gain exactly the capability its whole point as a
/// body is to not have. On top of that it's expensive (30-100 ms per call) and its fast path is
/// broken for rotated grids.
/// </para>
/// <para>
/// The robot looks the same way upstream NPCs do: a radius sample plus one ray per candidate
/// (<c>InRangeUnOccluded</c>) against the occluder tree. One raycast instead of hundreds of
/// tile queries.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ExamineSystemShared _examine = default!;

    /// <summary>The robot's sight radius in tiles. The same as half a human's screen frame.</summary>
    private const float SightRange = 8.5f;

    /// <summary>
    /// What the robot saw at the end of the previous turn: handle → short state.
    ///
    /// This is the baseline for the world diff. Keyed by body, not by session, because it's
    /// recomputed on the main thread when an observation is assembled.
    /// </summary>
    private readonly Dictionary<EntityUid, Dictionary<string, string>> _lastSeen = new();

    private void InitializeSight()
    {
    }

    private void ForgetSight(EntityUid borg) => _lastSeen.Remove(borg);

    /// <summary>
    /// Everything the robot currently sees: nearby and not behind a wall.
    /// </summary>
    /// <remarks>
    /// The <c>Uncontained | Approximate</c> flags are mandatory. By default
    /// <c>EntityLookupSystem</c> also pulls in container contents — i.e. the innards of every
    /// backpack and locker in range — and that's exactly the expense that once cost a full second
    /// on Station AI's view.
    /// </remarks>
    private List<EntityUid> VisibleFrom(EntityUid borg)
    {
        var result = new List<EntityUid>();

        if (!Exists(borg))
            return result;

        var origin = _xform.GetMapCoordinates(borg);
        var candidates = _lookup.GetEntitiesInRange(origin, SightRange,
            LookupFlags.Uncontained | LookupFlags.Approximate);

        foreach (var uid in candidates)
        {
            if (uid == borg || TerminatingOrDeleted(uid))
                continue;

            // Nameless means walls, floors, and other geometry: there's nothing to name them by, and no reason to.
            if (string.IsNullOrWhiteSpace(Name(uid)))
                continue;

            if (!_examine.InRangeUnOccluded(borg, uid, SightRange))
                continue;

            result.Add(uid);
        }

        return result;
    }

    /// <summary>
    /// Field-of-view diff since the previous turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>While walking, NOTHING is computed — not the lines, not even the field of view itself.</b>
    /// For a stationary eye, "appeared/disappeared" is a clean signal. For a walking robot, moving
    /// across ten tiles means half the station appears and disappears: the lines would push a radio
    /// call out of the queue — exactly the line the queue exists for. So while the robot is
    /// walking, the comparison baseline is reset, and the "what's around" summary is delivered on
    /// arrival instead.
    /// </para>
    /// <para>
    /// Computed once per turn, when the observation is assembled, not every tick: the cost is one
    /// radius scan, and there's no reason to pay it thirty times a second.
    /// </para>
    /// </remarks>
    private List<(string Label, string Text)> SightDelta(EntityUid borg, AgentSession session)
    {
        var lines = new List<(string, string)>();

        // THE WALKING CHECK IS THE FIRST LINE, AND THIS IS A FIX, NOT A REORDERING (2026-08-20).
        //
        // It used to sit AFTER the scan, and the comment above — "while walking, the diff isn't
        // computed at all" — described the intent, not the code: only printing the lines was
        // skipped, while everything else was still paid for. The radius scan, `InRangeUnOccluded`
        // per candidate, and `ShortState`, which for most entities falls through to a power-grid
        // poll — all of it ran on every turn of a walking robot. In a live round this produced 117
        // main-thread budget overruns, the worst at 45 ms against a 33 ms frame, and from the
        // outside it looked like "the moment the robot walks, fps dies."
        //
        // The comparison baseline isn't updated while walking, it's RESET. Updating it would mean
        // paying exactly the cost we're avoiding; a reset routes the first turn after arrival into
        // the already-existing "first turn in this body" branch — it silently remembers the new
        // surroundings. Nothing is lost: a diff against the last walking step would be meaningless
        // anyway, since the robot just crossed half the station, and it learns "what's around" from
        // ARRIVED and its own look.
        if (IsWalking(borg))
        {
            _lastSeen.Remove(borg);
            return lines;
        }

        var now = new Dictionary<string, string>();
        foreach (var uid in VisibleFrom(borg))
        {
            var handle = session.Handles.GetOrCreate(uid, _host.KindOf(uid));
            now[handle] = _host.ShortState(uid);
        }

        if (!_lastSeen.TryGetValue(borg, out var before))
        {
            // First turn in this body: there's nothing to compare against, and "40 items appeared"
            // is noise, not an observation. Just record it.
            _lastSeen[borg] = now;
            return lines;
        }

        foreach (var (handle, state) in now)
        {
            if (!before.TryGetValue(handle, out var was))
                lines.Add((session.Locale.ObsAppeared, $"{handle} {NameFor(session, handle)} | {state}"));
            else if (was != state)
                lines.Add((session.Locale.ObsChanged, $"{handle} {NameFor(session, handle)} | {was} → {state}"));
        }

        foreach (var (handle, _) in before)
        {
            if (!now.ContainsKey(handle))
                lines.Add((session.Locale.ObsGone, $"{handle} {NameFor(session, handle)}"));
        }

        _lastSeen[borg] = now;
        return lines;
    }

    /// <summary>Compute the diff and push it onto the observation queue. Main thread.</summary>
    private void PushSightDelta(AgentSession session, EntityUid borg)
    {
        // Pushed as Observed rather than a separate category, and that's deliberate: the
        // field-of-view diff is a stream just like other people's actions in frame, and it needs
        // exactly the same dedicated queue ceiling so that ambient noise doesn't push out a radio
        // call. The label ("появилось") lands in the same OBSERVED grammar slot the model already knows.
        var now = _host.RoundTime();
        foreach (var (label, text) in SightDelta(borg, session))
            session.Queue.Push(Observation.Observed(label, text, now));
    }

    private string NameFor(AgentSession session, string handle) =>
        session.Handles.TryResolve(handle, out var uid) && Exists(uid)
            ? Identity.Name(uid, EntityManager)
            : "?";
}
