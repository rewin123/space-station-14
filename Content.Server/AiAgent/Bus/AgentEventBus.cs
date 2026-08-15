using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// The outbound event bus: a ring of the last N frames, a monotonic cursor, and a signal for
/// long-poll readers.
///
/// <para>
/// <b>A ring instead of per-subscriber queues.</b> The reference implementation fans out by
/// awaiting a send to each subscriber in turn, with no per-subscriber queue and no timeout, so one
/// slow socket head-of-line-blocks every reader behind it. Here nobody is registered at all:
/// readers ask "what happened after N", and a reader that fell too far behind is told to resync
/// rather than being kept alive at the publisher's expense. Publication is therefore O(1), never
/// blocks, and cannot be made slow by a client.
/// </para>
/// <para>
/// <b>The cursor is (instance, seq), not seq.</b> A bare sequence number is not enough: after a
/// process restart it starts again at zero while a client still holds 50 000, and that client
/// believes it is permanently caught up. <see cref="Instance"/> is minted per bus, so a restart is
/// visible as a mismatch rather than as silence. The same check catches a client that invented a
/// cursor from the future.
/// </para>
/// <para>
/// <b>Statistics are sampled, not diffed.</b> The counters that make up <see cref="AgentStatsDto"/>
/// are <c>++</c> on auto-properties across four files. Publishing each one would mean six
/// field-plus-setter conversions and six new chances to forget, to feed a stream nobody consumes as
/// a delta. One whole record per turn boundary is idempotent, cheap and answers the same question.
/// </para>
/// </summary>
public sealed class AgentEventBus
{
    /// <summary>
    /// Guards the ring, the cursor and the waiter. Last in the lock order
    /// <c>Conv → Memory → Skills → Bus</c>: publishers hold their own domain lock when they get
    /// here, and nothing that holds this ever reaches back for a domain lock.
    /// </summary>
    private readonly object _sync = new();

    private readonly AgentEvent[] _ring;
    private int _next;
    private int _count;
    private long _seq;

    /// <summary>Completed and replaced on every publish; long-poll readers await the live one.</summary>
    private TaskCompletionSource _signal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Identifies this process's bus. Part of every cursor a client holds.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..12];

    public AgentEventBus(int ringSize)
    {
        // A turn produces on the order of ten frames every eight seconds, so 512 is minutes of
        // tolerance for a client that blinked. Small enough that the whole ring is a rounding error.
        _ring = new AgentEvent[Math.Clamp(ringSize, 16, 65536)];
    }

    /// <summary>Highest sequence number published so far. Zero means nothing has been.</summary>
    public long Seq
    {
        get { lock (_sync) return _seq; }
    }

    /// <summary>How many frames the ring currently holds.</summary>
    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public int Capacity => _ring.Length;

    /// <summary>A sink bound to one session id — what a conversation is handed.</summary>
    public IAgentEventSink ForSession(string sessionId) => new BoundSink(this, sessionId);

    /// <summary>
    /// A sink for the process-wide stores. Memory and skills outlive any one session — they are
    /// owned by the system, not by the agent — so their events carry no session id.
    /// </summary>
    public IAgentEventSink ForProcess() => new BoundSink(this, "");

    /// <summary>
    /// Publish one frame. Never blocks, never throws, never rejects: a ring overwrites.
    /// </summary>
    public long Publish(AgentEventKind kind, string sessionId, string payloadJson)
    {
        TaskCompletionSource signal;
        long seq;

        lock (_sync)
        {
            seq = ++_seq;
            _ring[_next] = new AgentEvent(seq, kind, sessionId, payloadJson);
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length)
                _count++;

            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Outside the lock: continuations must not run while a publisher holds it, and with
        // RunContinuationsAsynchronously this is a queue-and-return anyway.
        signal.TrySetResult();
        return seq;
    }

    /// <summary>
    /// Everything after <paramref name="since"/>, or a resync verdict.
    ///
    /// Resync means the client's cursor cannot be honoured: it belongs to another process, it names
    /// a frame the ring has already overwritten, or it is ahead of what was ever published. In all
    /// three the only correct answer is "refetch the snapshot", and saying so beats handing back a
    /// plausible-looking partial history.
    /// </summary>
    public AgentEventRead Read(string? instance, long since)
    {
        lock (_sync)
        {
            if (instance != null && instance != Instance)
                return new AgentEventRead(Instance, _seq, Resync: true, Array.Empty<AgentEvent>());

            if (since > _seq)
                return new AgentEventRead(Instance, _seq, Resync: true, Array.Empty<AgentEvent>());

            var oldest = _seq - _count;
            if (since < oldest)
                return new AgentEventRead(Instance, _seq, Resync: true, Array.Empty<AgentEvent>());

            var wanted = (int)(_seq - since);
            if (wanted <= 0)
                return new AgentEventRead(Instance, _seq, Resync: false, Array.Empty<AgentEvent>());

            var events = new AgentEvent[wanted];
            // The ring is a circular buffer; walk back from the newest so order comes out ascending.
            for (var i = 0; i < wanted; i++)
            {
                var slot = (_next - wanted + i + _ring.Length) % _ring.Length;
                events[i] = _ring[slot];
            }

            return new AgentEventRead(Instance, _seq, Resync: false, events);
        }
    }

    /// <summary>
    /// Wait until something is published past <paramref name="since"/>, or until the timeout.
    ///
    /// This is what makes a plain-HTTP reader cheap: without it the caller must choose between a
    /// second of latency and a busy loop. Returns as soon as the read is non-empty, and an empty
    /// result on timeout is a normal answer, not an error.
    /// </summary>
    public async Task<AgentEventRead> ReadAsync(
        string? instance,
        long since,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var read = Read(instance, since);
        if (read.Resync || read.Events.Count > 0)
            return read;

        Task signal;
        lock (_sync)
        {
            // Re-read under the lock: a publish between the check above and here would otherwise be
            // missed, and the reader would wait out the full timeout for news it already had.
            if (_seq > since)
                return Read(instance, since);

            signal = _signal.Task;
        }

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);

        try
        {
            await signal.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timed out or the server is going down — an empty read is the honest answer.
        }

        return Read(instance, since);
    }

    // ------------------------------------------------------------------ sink

    /// <summary>
    /// Serialises each change and drops it in the ring.
    ///
    /// Serialisation happens here, on the caller's thread and inside the caller's lock, so that the
    /// ring never holds a reference to an object the agent thread is still editing. All the wire
    /// DTOs in this codebase are mutable classes whose property order is load-bearing for the KV
    /// cache; handing one to an HTTP thread would be a race, and copying it here would be a second
    /// model to keep true.
    /// </summary>
    private sealed class BoundSink(AgentEventBus bus, string sessionId) : IAgentEventSink
    {
        public void MessageAppended(int bodyEpoch, int index, ChatMessageDto message)
        {
            bus.Publish(AgentEventKind.MessageAppended, sessionId, JsonSerializer.Serialize(new
            {
                body_epoch = bodyEpoch,
                index,
                message = AgentMessageDto.From(index, message),
            }, AgentDebugJson.Options));
        }

        public void HistoryReplaced(int bodyEpoch, IReadOnlyList<ChatMessageDto> body)
        {
            var messages = new AgentMessageDto[body.Count];
            for (var i = 0; i < body.Count; i++)
                messages[i] = AgentMessageDto.From(i, body[i]);

            bus.Publish(AgentEventKind.HistoryReplaced, sessionId, JsonSerializer.Serialize(new
            {
                body_epoch = bodyEpoch,
                messages,
            }, AgentDebugJson.Options));
        }

        public void PrefixReplaced(string prefixHash, string systemPrompt, string toolsJson)
        {
            bus.Publish(AgentEventKind.PrefixReplaced, sessionId, JsonSerializer.Serialize(new
            {
                prefix_hash = prefixHash,
                system_prompt = systemPrompt,
                tools_json = toolsJson,
            }, AgentDebugJson.Options));
        }

        public void MemoryUpdated(IReadOnlyList<string> entries)
        {
            bus.Publish(AgentEventKind.MemoryUpdated, sessionId, JsonSerializer.Serialize(new
            {
                entries,
            }, AgentDebugJson.Options));
        }

        public void SkillUpdated(Skill skill)
        {
            bus.Publish(AgentEventKind.SkillUpdated, sessionId, JsonSerializer.Serialize(new
            {
                name = skill.Name,
                when = skill.When,
                body = skill.Body,
            }, AgentDebugJson.Options));
        }

        public void SkillsReloaded(IReadOnlyCollection<Skill> skills)
        {
            var all = new List<object>(skills.Count);
            foreach (var s in skills)
                all.Add(new { name = s.Name, when = s.When, body = s.Body });

            bus.Publish(AgentEventKind.SkillsReloaded, sessionId, JsonSerializer.Serialize(new
            {
                skills = all,
            }, AgentDebugJson.Options));
        }

        public void PlayerNoteUpdated(PlayerNote note)
        {
            bus.Publish(AgentEventKind.PlayerNoteUpdated, sessionId, JsonSerializer.Serialize(new
            {
                slug = note.Slug,
                name = note.Name,
                entries = note.Entries,
            }, AgentDebugJson.Options));
        }

        public void PlayerNotesReloaded(IReadOnlyCollection<PlayerNote> notes)
        {
            // Кадр несёт хранилище целиком, как и у скиллов. Это осознанный предел: одна заметка
            // ограничена 2000 символами, но самих заметок может накопиться до 2000, и тогда кадр
            // вырастет до мегабайтов, а перечитывание случается на каждой компакции. Когда каталог
            // перестанет быть десятками файлов, здесь надо оставить только индекс (слаг, имя, число
            // записей) и добавить маршрут за одной заметкой — сейчас это была бы сложность впрок.
            var all = new List<object>(notes.Count);
            foreach (var n in notes)
                all.Add(new { slug = n.Slug, name = n.Name, entries = n.Entries });

            bus.Publish(AgentEventKind.PlayerNotesReloaded, sessionId, JsonSerializer.Serialize(new
            {
                notes = all,
            }, AgentDebugJson.Options));
        }

        public void Stats(AgentStatsDto stats)
        {
            bus.Publish(AgentEventKind.Stats, sessionId, JsonSerializer.Serialize(stats, AgentDebugJson.Options));
        }
    }
}

/// <summary>The answer to "what happened after N".</summary>
public sealed record AgentEventRead(
    string Instance,
    long Seq,
    bool Resync,
    IReadOnlyList<AgentEvent> Events);
