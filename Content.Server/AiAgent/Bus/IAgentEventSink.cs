using System.Collections.Generic;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// What a data owner sees of the bus.
///
/// Deliberately narrow and typed: <see cref="ConversationState"/>, <see cref="MemoryStore"/> and
/// <see cref="SkillStore"/> get a sink, not a bus. They cannot read the ring, cannot mint a seq,
/// and cannot learn what a session id is — the sink they are handed is already bound to one. That
/// keeps <c>ConversationState</c>'s standing invariant intact: it knows nothing about entities,
/// rounds or the game.
///
/// Every method is called <em>inside</em> the caller's own lock, so implementations must be quick
/// and must never call back into the caller.
/// </summary>
public interface IAgentEventSink
{
    /// <summary>One message appended at <paramref name="index"/> of the current body epoch.</summary>
    void MessageAppended(int bodyEpoch, int index, ChatMessageDto message);

    /// <summary>The body was replaced wholesale. Indices restart within the new epoch.</summary>
    void HistoryReplaced(int bodyEpoch, IReadOnlyList<ChatMessageDto> body);

    /// <summary>Zone 0 changed.</summary>
    void PrefixReplaced(string prefixHash, string systemPrompt, string toolsJson);

    /// <summary>Live memory entries, as they now stand on disk.</summary>
    void MemoryUpdated(IReadOnlyList<string> entries);

    /// <summary>One skill written or edited.</summary>
    void SkillUpdated(Skill skill);

    /// <summary>The library was re-read from disk; these are the skills that survived.</summary>
    void SkillsReloaded(IReadOnlyCollection<Skill> skills);

    /// <summary>
    /// Одна заметка о человеке целиком. Пустой <see cref="PlayerNote.Entries"/> значит, что заметки
    /// больше нет, — см. <see cref="AgentEventKind.PlayerNoteUpdated"/>.
    /// </summary>
    void PlayerNoteUpdated(PlayerNote note);

    /// <summary>Хранилище заметок перечитано с диска; вот те, что уцелели.</summary>
    void PlayerNotesReloaded(IReadOnlyCollection<PlayerNote> notes);

    /// <summary>
    /// The whole statistics record, sampled at a turn boundary.
    ///
    /// Unlike the others this is not raised by the owner of the data — the counters live across
    /// four files — but by the loop, once per turn, from the one place every turn passes through.
    /// </summary>
    void Stats(AgentStatsDto stats);
}

/// <summary>
/// The sink installed when the bus is off: every call returns immediately.
///
/// Kept as a real object rather than a null check at each call site so the owners have one code
/// path. The <em>enabled</em> check still happens before the call — see the owners' <c>_sink</c>
/// null test — because building the arguments (a defensive copy of the body, for instance) costs
/// more than the call does.
/// </summary>
public sealed class NullAgentEventSink : IAgentEventSink
{
    public static NullAgentEventSink Instance { get; } = new();

    private NullAgentEventSink() { }

    public void MessageAppended(int bodyEpoch, int index, ChatMessageDto message) { }
    public void HistoryReplaced(int bodyEpoch, IReadOnlyList<ChatMessageDto> body) { }
    public void PrefixReplaced(string prefixHash, string systemPrompt, string toolsJson) { }
    public void MemoryUpdated(IReadOnlyList<string> entries) { }
    public void SkillUpdated(Skill skill) { }
    public void SkillsReloaded(IReadOnlyCollection<Skill> skills) { }
    public void PlayerNoteUpdated(PlayerNote note) { }
    public void PlayerNotesReloaded(IReadOnlyCollection<PlayerNote> notes) { }
    public void Stats(AgentStatsDto stats) { }
}
