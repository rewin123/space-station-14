using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Perception;

/// <summary>
/// Bounded buffer of perceived lines. Written from the main thread by the perception collector,
/// drained from the main thread by the agent's marshalled call — so a plain lock is both correct
/// and cheap; there is no contention to speak of.
///
/// On overflow the <em>oldest</em> entries are dropped and the count is reported to the model as
/// a "DROPPED n" line. Silently losing them would leave the agent with a blind spot it cannot
/// know about, which is far worse than a short gap it can reason around.
/// </summary>
public sealed class ObservationQueue
{
    private readonly object _lock = new();
    private readonly Queue<Observation> _items = new();
    private int _dropped;

    public int Capacity { get; set; }

    public ObservationQueue(int capacity)
    {
        Capacity = capacity;
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
            _items.Enqueue(obs);
            while (_items.Count > Capacity)
            {
                _items.Dequeue();
                _dropped++;
            }
        }

        // Outside the lock. The handler wakes the agent thread, and holding a perception lock while
        // another thread starts a turn is how a deadlock gets written.
        Arrived?.Invoke();
    }

    /// <summary>Take everything buffered and reset the drop counter.</summary>
    public (List<Observation> Items, int Dropped) Drain()
    {
        lock (_lock)
        {
            var items = _items.ToList();
            var dropped = _dropped;
            _items.Clear();
            _dropped = 0;
            return (items, dropped);
        }
    }

    /// <summary>
    /// Look at what has arrived without consuming it.
    ///
    /// Used to append an "unread" note to every tool result: while the model is working through a
    /// multi-step turn it is otherwise deaf, and a bot that answers a question it never heard
    /// reads as broken. Reporting a bare count is not enough — the agent needs the actual lines to
    /// react to "wait, not that one".
    /// </summary>
    public List<string> PeekUnread(int max)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
                return new List<string>();

            return _items
                .Skip(Math.Max(0, _items.Count - max))
                .Select(ObservationFormatter.FormatLine)
                .ToList();
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
            _dropped = 0;
        }
    }
}
