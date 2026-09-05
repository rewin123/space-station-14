using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Perception;

/// <summary>
/// Bounded buffer of perceived lines.
///
/// <para>
/// <b>Three threads touch it, not one.</b> This used to say "written from the main thread, read from
/// the main thread — no contention"; that stopped being true and was misleading exactly where it
/// mattered. Today: <see cref="Push"/> is the main thread (event handlers, off a player's click);
/// <see cref="Drain"/> is also main, inside a marshalled call; and <see cref="PeekUnread"/> is called
/// from the <b>agent thread</b> on every tool result, while <see cref="Count"/> is also read from the
/// debug HTTP thread.
/// </para>
/// <para>
/// All of it under one lock, so it's correct. But "no contention" was a false premise, and it led to
/// the conclusion that anything goes under the lock: <c>PeekUnread</c> used to hold it across two full
/// passes over up to six hundred elements plus building the strings, while the main thread waited on
/// <c>Push</c> the whole time. Holding the lock longer than it takes to pick the elements is not
/// allowed here.
/// </para>
///
/// On overflow the <em>oldest</em> entries are dropped and the count is reported to the model as
/// a "DROPPED n" line. Silently losing them would leave the agent with a blind spot it cannot
/// know about, which is far worse than a short gap it can reason around.
/// </summary>
public sealed class ObservationQueue
{
    private readonly object _lock = new();

    /// <summary>
    /// A linked list rather than a <c>Queue</c>, for the sake of one operation: dropping the oldest
    /// line of a SPECIFIC category without touching the rest. A plain queue has no such operation at
    /// all — it would have to be rebuilt from scratch for every excess element, and excess elements
    /// arrive as a stream.
    /// </summary>
    private readonly LinkedList<Observation> _items = new();

    private int _dropped;
    private int _observed;

    public int Capacity { get; set; }

    /// <summary>
    /// How many <see cref="ObsKind.Observed"/> lines the queue holds at once.
    ///
    /// A separate cap is needed because the overall one drops the OLDEST regardless of kind. Every
    /// other category is a rare message, while this one arrives as a stream: any commotion in frame
    /// would push a radio call out of the queue, i.e. the agent would stop hearing requests exactly
    /// when there are the most of them. Here it's the oldest OBSERVED that gets trimmed, and only that.
    /// </summary>
    public int ObservedCapacity { get; set; }

    /// <param name="observedCapacity">
    /// Uncapped by default: a bare queue behaves exactly as it did before observations existed, and
    /// tests that just need a queue as such don't have to know about this knob. A live session always
    /// sets a value — see <c>ai.observe_buffer</c>.
    /// </param>
    public ObservationQueue(int capacity, int observedCapacity = int.MaxValue)
    {
        Capacity = capacity;
        ObservedCapacity = observedCapacity;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _items.Count;
        }
    }

    /// <summary>
    /// Raised the moment something lands, so the loop can start on it instead of sleeping out the
    /// rest of its tick.
    ///
    /// Polling alone made the agent's response time a coin flip between nothing and a full tick,
    /// and the crew feels that most exactly when it matters least to wait: someone shouting about
    /// a fire does not care that the poll had six seconds left on it.
    /// </summary>
    public Action? Arrived { get; set; }

    public void Push(Observation obs)
    {
        lock (_lock)
        {
            _items.AddLast(obs);

            if (obs.Kind == ObsKind.Observed)
                _observed++;

            // Its own cap first, then the overall one. Order matters: if the OBSERVED stream has
            // already hit its own limit, the overall one never has to trigger, and speech stays in
            // the queue untouched.
            while (_observed > ObservedCapacity && TrimOldest(ObsKind.Observed))
            {
            }

            while (_items.Count > Capacity)
            {
                var first = _items.First!;
                if (first.Value.Kind == ObsKind.Observed)
                    _observed--;

                _items.RemoveFirst();
                _dropped++;
            }
        }

        // Outside the lock. The handler wakes the agent thread, and holding a perception lock while
        // another thread starts a turn is how a deadlock gets written.
        Arrived?.Invoke();
    }

    /// <summary>
    /// Drop the oldest line of the given kind. Returns false if there is none of that kind in the
    /// queue.
    ///
    /// Counted into the overall loss counter, not a per-kind one: the agent is told "you missed this
    /// many lines", and there is no reason to split that loss by kind — it can't get them back either
    /// way. Called with the lock already held.
    /// </summary>
    private bool TrimOldest(ObsKind kind)
    {
        for (var node = _items.First; node != null; node = node.Next)
        {
            if (node.Value.Kind != kind)
                continue;

            _items.Remove(node);
            _dropped++;

            if (kind == ObsKind.Observed)
                _observed--;

            return true;
        }

        return false;
    }

    /// <summary>Take everything buffered and reset the drop counter.</summary>
    public (List<Observation> Items, int Dropped) Drain()
    {
        lock (_lock)
        {
            var items = _items.ToList();
            var dropped = _dropped;
            _items.Clear();
            _observed = 0;
            _dropped = 0;
            return (items, dropped);
        }
    }


    /// <summary>
    /// Has this exact line already been buffered as radio, moments ago?
    ///
    /// Speech and radio arrive from two different upstream events for the same utterance, and the
    /// radio one always lands first — <c>RadioSystem</c> and <c>HeadsetSystem</c> handle
    /// <c>EntitySpokeEvent</c> as <em>directed</em> subscriptions, which RobustToolbox dispatches
    /// before any broadcast one. So by the time a broadcast handler sees a transmitted line, the
    /// radio copy is already in here, and this is how it recognises it.
    /// </summary>
    /// <summary>
    /// Is there an event in the queue with exactly this text?
    ///
    /// Needed by exactly one place — the periodic reminder to the evil AI — and specifically so that
    /// reminders don't pile up while the agent is busy with a long turn. Compared by text rather than
    /// by identifier, because the reminder's text is its identity: it's defined as a single line in
    /// the rule prototype, and there should never be a second one like it in the queue.
    /// </summary>
    public bool HasEvent(string text)
    {
        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (item.Kind == ObsKind.Event && item.Text == text)
                    return true;
            }

            return false;
        }
    }

    public bool AlreadyHeardOnRadio(string speaker, string text, TimeSpan now, double withinSeconds = 1.0)
    {
        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (item.Kind != ObsKind.Radio)
                    continue;

                if ((now - item.RoundTime).Duration().TotalSeconds > withinSeconds)
                    continue;

                if (item.Speaker == speaker && item.Text == text)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The last thing the agent announced itself, kept only long enough to recognise the echo.
    ///
    /// An announcement the agent makes comes straight back at it: the brain carries the console
    /// component the tool drives, so it is both the announcer and a listener. Usually the source on
    /// the event identifies it, but a console configured to announce globally dispatches with no
    /// source at all, and then the text is the only thing left to match on.
    /// </summary>
    private string? _selfAnnounced;

    /// <summary>Called before the announcement goes out, because the echo arrives synchronously.</summary>
    public void NoteSelfAnnouncement(string text)
    {
        lock (_lock)
            _selfAnnounced = text;
    }

    public bool WasLastAnnouncedBySelf(string text)
    {
        lock (_lock)
            return _selfAnnounced != null && _selfAnnounced == text;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _selfAnnounced = null;
            _observed = 0;
            _dropped = 0;
        }
    }
}
