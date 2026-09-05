using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Perception;

/// <summary>
/// One timer the agent has set.
/// </summary>
/// <param name="DueAt">
/// Round time of firing, not wall-clock time. That's the only correct choice here:
/// <c>game.auto_pause_empty</c> freezes the simulation on an empty server, and <c>CurTime</c> freezes
/// along with it, as does the round hour the agent sees in every observation. A wall-clock timer would
/// keep ticking through that and wake the agent up mid-pause — i.e. force it to act in a world that
/// hasn't advanced a single tick, and pay a model call for it.
/// </param>
/// <param name="Every">
/// Repeat interval, or null for a one-shot. After firing, a repeating timer is rearmed from the MOMENT
/// IT FIRED, not from its previous due time: otherwise a timer that slept through a pause would fire
/// once for every interval that fit inside the downtime, and the agent would get a stack of identical
/// reminders about the same thing.
/// </param>
public sealed record AgentTimer(string Name, string Message, TimeSpan DueAt, TimeSpan? Every);

/// <summary>What came out of trying to set a timer. A refusal always names the reason in words for the model.</summary>
public sealed record TimerSetResult(bool Ok, string Message, AgentTimer? Timer = null, bool Replaced = false);

/// <summary>
/// The agent's alarms: the only way for it to bring itself back to something just agreed on that has
/// to happen later.
///
/// Without them the loop has exactly two reasons to start a turn — someone spoke, or an idle tick
/// expired — and "I'll check back in ten minutes" became a promise with nothing to keep it: the next
/// turn would arrive off someone else's line, in a different context, and the check-in would never
/// come up again. A fired timer goes into the same <see cref="ObservationQueue"/> as crew speech, and
/// wakes the loop with the same signal — to the agent it's a world event just like a radio call.
///
/// The store lives in <see cref="AgentState"/> (rule: survives the turn, lives there) and knows
/// nothing about entities or clocks: time is handed to it from outside. A lock is needed because
/// writes can come from the agent thread (tools), while reads come from the main thread (the tick)
/// and from the debug bus thread.
/// </summary>
public sealed class TimerStore
{
    private readonly object _lock = new();
    private readonly List<AgentTimer> _timers = new();

    /// <summary>No point a timer outliving the round: the session ends along with the shift anyway.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(2);

    public int Count
    {
        get
        {
            lock (_lock)
                return _timers.Count;
        }
    }

    /// <summary>All timers in ascending due-time order. Order is fixed so the SELF line doesn't jitter.</summary>
    public IReadOnlyList<AgentTimer> All()
    {
        lock (_lock)
            return _timers.OrderBy(t => t.DueAt).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Names()
    {
        lock (_lock)
            return _timers.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Set a timer, or reschedule an existing one with the same name.
    ///
    /// A matching name reschedules rather than refuses: "remind me again in ten minutes" is the most
    /// common edit to one's own plan, and refusing on a name already in use would force the model to
    /// delete first, spending a second call out of the six a turn is given. The replacement is called
    /// out by name in the response so the overwrite doesn't read as setting a second timer.
    /// </summary>
    /// <param name="now">Current round time.</param>
    /// <param name="max">Cap on the number of timers, from <c>ai.max_timers</c>.</param>
    public TimerSetResult Set(string name, string message, TimeSpan after, TimeSpan? every, TimeSpan now, int max)
    {
        var timer = new AgentTimer(name, message, now + after, every);

        lock (_lock)
        {
            var index = _timers.FindIndex(t => Same(t.Name, name));

            if (index < 0 && _timers.Count >= max)
            {
                return new TimerSetResult(false,
                    $"уже заведено {_timers.Count} таймеров, это потолок — удали ненужный через del_timer");
            }

            if (index < 0)
            {
                _timers.Add(timer);
                return new TimerSetResult(true, "заведён", timer);
            }

            _timers[index] = timer;
            return new TimerSetResult(true, "переставлен", timer, Replaced: true);
        }
    }

    public bool Remove(string name, out AgentTimer? removed)
    {
        lock (_lock)
        {
            var index = _timers.FindIndex(t => Same(t.Name, name));
            if (index < 0)
            {
                removed = null;
                return false;
            }

            removed = _timers[index];
            _timers.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    /// Take the ones that fired and roll repeating ones over to the next cycle. Called from the tick.
    ///
    /// Returns a list rather than one at a time: several can come due in a single tick, and handing
    /// them out one by one would mean waking the agent that many times in a row.
    /// </summary>
    public IReadOnlyList<AgentTimer> TakeDue(TimeSpan now)
    {
        lock (_lock)
        {
            if (_timers.Count == 0)
                return Array.Empty<AgentTimer>();

            List<AgentTimer>? fired = null;

            for (var i = _timers.Count - 1; i >= 0; i--)
            {
                var timer = _timers[i];
                if (timer.DueAt > now)
                    continue;

                (fired ??= new List<AgentTimer>()).Add(timer);

                if (timer.Every is { } every)
                    _timers[i] = timer with { DueAt = now + every };
                else
                    _timers.RemoveAt(i);
            }

            if (fired == null)
                return Array.Empty<AgentTimer>();

            // The reverse iteration order would give firing order back to front; sort by due time,
            // like everything else here, so the same state always produces the same bytes.
            fired.Sort((a, b) => a.DueAt != b.DueAt
                ? a.DueAt.CompareTo(b.DueAt)
                : string.CompareOrdinal(a.Name, b.Name));

            return fired;
        }
    }

    /// <summary>Names closest by spelling — for a clear refusal on a near-miss in del_timer.</summary>
    public IReadOnlyList<string> Nearest(string name, int count = 3)
    {
        lock (_lock)
        {
            return _timers
                .Select(t => t.Name)
                .OrderBy(n => Tools.AiToolRegistry.Distance(n.ToLowerInvariant(), name.ToLowerInvariant()))
                .ThenBy(n => n, StringComparer.Ordinal)
                .Take(count)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
            _timers.Clear();
    }

    /// <summary>Restore from a snapshot. Fully replaces the contents — the snapshot is the state.</summary>
    public void Restore(IEnumerable<AgentTimer> timers)
    {
        lock (_lock)
        {
            _timers.Clear();
            _timers.AddRange(timers);
        }
    }

    /// <summary>Name comparison is case-insensitive: the model treats "Patrol" and "patrol" as the same.</summary>
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
