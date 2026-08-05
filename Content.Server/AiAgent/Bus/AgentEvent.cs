namespace Content.Server.AiAgent.Bus;

/// <summary>
/// Everything the agent broadcasts about itself.
///
/// An enum rather than strings at the call sites, deliberately. The reference implementation this
/// design is taken from (hermes-agent) names its events with string literals wherever they are
/// raised, keeps an advisory list in a different language, and carries the scar: it emits both
/// <c>tool.start</c> and <c>tool.started</c>, from two layers, meaning two different things, and
/// has a comment in the dispatcher explaining which one to throw away. With the name derived from
/// an enum member there is no call site that <em>can</em> pass a string, so the collision is not
/// expressible.
/// </summary>
public enum AgentEventKind : byte
{
    /// <summary>An agent claimed a core. Everything a client accumulated before this is void.</summary>
    SessionStarted,

    /// <summary>The agent was carded, killed, or the round restarted.</summary>
    SessionEnded,

    /// <summary>One message was appended to the body. Payload carries the message and its index.</summary>
    MessageAppended,

    /// <summary>
    /// The body was replaced wholesale — a compaction folded it, or a snapshot restored it.
    /// Payload carries the whole new history; indices from before this are meaningless.
    /// </summary>
    HistoryReplaced,

    /// <summary>Zone 0 changed: new system prompt, new tool schemas, new prefix hash.</summary>
    PrefixReplaced,

    /// <summary>Live memory entries for one target, after the write settled on disk.</summary>
    MemoryUpdated,

    /// <summary>One skill was written or edited.</summary>
    SkillUpdated,

    /// <summary>
    /// The whole statistics record, sampled at a turn boundary rather than diffed per counter.
    /// See <see cref="AgentEventBus"/> for why this one is a sample and not a diff.
    /// </summary>
    Stats,
}

/// <summary>Wire names. Dotted, like the reference, so a client can prefix-match a family.</summary>
public static class AgentEventNames
{
    public static string Of(AgentEventKind kind) => kind switch
    {
        AgentEventKind.SessionStarted => "session.started",
        AgentEventKind.SessionEnded => "session.ended",
        AgentEventKind.MessageAppended => "message.appended",
        AgentEventKind.HistoryReplaced => "history.replaced",
        AgentEventKind.PrefixReplaced => "prefix.replaced",
        AgentEventKind.MemoryUpdated => "memory.updated",
        AgentEventKind.SkillUpdated => "skill.updated",
        AgentEventKind.Stats => "stats",

        // Not a default case: a new enum member must fail loudly here rather than travel the wire
        // as an empty string that a client silently ignores.
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind, "нет имени для вида события"),
    };
}

/// <summary>
/// One frame in the ring.
///
/// <paramref name="PayloadJson"/> is already serialised, on the thread that made the change and
/// inside the lock that guarded it. That is the point: the ring holds no references to live mutable
/// objects, so an HTTP thread reading a frame cannot race the agent thread editing the
/// <c>ChatMessageDto</c> it came from. The cost is about a microsecond per message.
/// </summary>
public readonly record struct AgentEvent(long Seq, AgentEventKind Kind, string SessionId, string PayloadJson);
