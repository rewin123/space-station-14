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

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _dropped = 0;
        }
    }
}
