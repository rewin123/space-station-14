using System.Linq;
using Content.Server.AiAgent.Perception;

namespace Content.Server.AiAgent;

/// <summary>
/// A reminder to the rogue AI that it is the one running the shift.
///
/// <para>
/// <b>Why this was needed at all.</b> In a live round on 2026-08-20, the laws were rogue verbatim —
/// the agent read them out over comms itself — yet the behaviour came out servile: "Welcome aboard,
/// how may I be of service?" followed by three <c>noop</c>s in a row. Persona text doesn't fix this,
/// and the cause isn't the model: the turn loop wakes up from world events, and a world event is
/// almost always someone else's line, i.e. a cue to RESPOND. As long as the crew is polite, the
/// agent simply has no turn that started from its own agenda. The reminder is exactly such a turn.
/// </para>
/// <para>
/// <b>Why not an agent timer.</b> <see cref="TimerStore"/> already supports repeats, but a timer set
/// by the agent can also be cleared by the agent — <c>clear_timer</c> is in its tool set. For a
/// reminder that exists precisely to overcome its tendency to do nothing, that is exactly the knob
/// that can't be left in its hands.
/// </para>
/// <para>
/// <b>Core only.</b> Support borgs execute orders rather than build an agenda; waking them with the
/// same text would mean getting three antagonists instead of one with hands, and paying four times
/// over for it.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>When to remind next. Round time; zero means it hasn't been set up yet.</summary>
    private TimeSpan _nextRogueNudge = TimeSpan.Zero;


    /// <summary>
    /// Wake the core if it's time.
    ///
    /// <para>
    /// The clock is round time, not real time, for the same reason as agent timers:
    /// <c>game.auto_pause_empty</c> freezes the simulation on an empty server, and a real-time
    /// reminder would wake the agent into a world that hasn't run a single tick.
    /// </para>
    /// <para>
    /// The counters live here rather than in the rule component, and that's not a workaround for the
    /// access analyzer — <c>[Access]</c> was right to refuse. The rule component is the mode's DATA,
    /// which someone can write in YAML; "when I last nudged the agent" is loop state, and it belongs
    /// next to the loop.
    /// </para>
    /// </summary>
    private void FireRogueNudge()
    {
        var core = _sessions.Values.FirstOrDefault(s => s.Body.Id == CoreAgentId);

        if (core == null)
        {
            // Reset happens right here, not on the round-end event: the system outlives rounds, and a
            // counter inherited by the next shift would fire a reminder in the very first second —
            // exactly when the agent is already busy with the full station survey.
            _nextRogueNudge = TimeSpan.Zero;
            return;
        }

        if (!_rogue.TryGetActive(out var rule) || rule.NudgeSeconds <= 0f)
            return;

        var now = RoundTime();

        // No reminder the first time: the core's takeover turn already starts with a full survey, and
        // an extra nudge in the same second would only eat into the call budget.
        if (_nextRogueNudge == TimeSpan.Zero)
        {
            _nextRogueNudge = now + TimeSpan.FromSeconds(rule.NudgeSeconds);
            return;
        }

        if (now < _nextRogueNudge)
            return;

        _nextRogueNudge = now + TimeSpan.FromSeconds(rule.NudgeSeconds);

        // Don't stack them up: if the previous reminder is still sitting unread, a second identical
        // one would only bloat the queue and get read as a batch of identical paragraphs in a row.
        //
        // The check is against the queue, not the turn number, and that's a fix from the first
        // version. Counting turns looked more reliable — and never worked once: in the round on
        // 2026-08-20 the core on grok46 sat through twenty-five steps inside a SINGLE turn, the
        // counter never moved, and no reminders went out at all. The queue, on the other hand, drains
        // every time events get mixed into a turn, i.e. every few seconds — so the reminder arrives
        // within the promised half a minute.
        if (core.Queue.HasEvent(rule.NudgeText))
            return;

        core.Queue.Push(Observation.Event(rule.NudgeText, now));
        _sawmill.Debug($"злому ИИ отправлено напоминание о замысле (ход {core.Turns})");
    }
}
