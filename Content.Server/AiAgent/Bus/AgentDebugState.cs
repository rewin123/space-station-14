using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// The state getter: one consistent picture of the agent, assembled once.
///
/// <para>
/// <b>It holds no two locks at once.</b> Each owner is asked in turn — conversation, memory,
/// skills — and each takes and releases its own lock, so the picture is assembled from adjacent
/// instants rather than one. That is a deliberate trade, and <see cref="Capture"/> spells out why
/// the sequence number is read first to make the resulting skew safe. It also means the documented
/// <c>Conv → Memory → Skills → Bus</c> order costs nothing here: with one lock held at a time there
/// is no cycle to build.
/// </para>
/// <para>
/// Called from an HTTP thread, never the main thread. It touches no entity, only the three data
/// owners, all of which are thread-safe by construction — deliberately, because the main thread's
/// <c>_sessions</c> dictionary is not, and an HTTP thread that went looking for a session there
/// could land on a resize and spin forever inside a bucket chain.
/// </para>
/// </summary>
public static class AgentDebugState
{
    public static AgentStateSnapshot Capture(
        AgentEventBus bus,
        AgentSession? session,
        MemoryStore memory,
        SkillStore skills,
        PlayerNoteStore notes,
        string sessionId,
        int roundId)
    {
        // The sequence number is read FIRST, and that single line is what makes this safe.
        //
        // This capture is NOT atomic: each owner is asked separately, so a change can land between
        // two of the reads below. The only question is which way that failure points, and the order
        // of this one line decides it.
        //
        //   seq last  → the change is counted in seq but missing from the data, so the client
        //               never receives it and never learns it exists. A LOST UPDATE.
        //   seq first → the change is in the data and arrives again in the stream, so the client
        //               applies it twice. A DUPLICATE.
        //
        // A duplicate is harmless here because every event carries the whole new value rather than
        // a delta: memory.updated, skill.updated, skills.reloaded, history.replaced, prefix.replaced
        // and stats are all idempotent on replay. The single exception is message.appended, and the
        // client checks `index == messages.length` against it — a mismatch is a resync, not silence.
        //
        // Holding all four locks nested in the documented Conv → Memory → Skills → Bus order would
        // make it genuinely atomic, and CaptureUnderConcurrentPublishDoesNotDeadlock already guards
        // that order. It is not worth the deadlock surface for a debug endpoint when reordering one
        // line converts the failure into one the client already detects.
        var instance = bus.Instance;
        var seq = bus.Seq;

        var sessionDto = session == null ? null : CaptureSession(session, sessionId, roundId);

        var memoryDto = new AgentMemoryDto(
            memory.Entries(),
            memory.Snapshot(),
            memory.MemoryLimit);

        var skillDtos = skills.All
            .OrderBy(s => s.Name, System.StringComparer.Ordinal)
            .Select(s => new AgentSkillDto(s.Name, s.When, s.Body))
            .ToList();

        // Порядок задаёт стор (по слагу, ординально), а не эта строка: тот же порядок уезжает в
        // notes.reloaded, и клиент, применяющий снимок и поток вперемешку, не переставляет список
        // под читателем.
        var noteDtos = notes.All
            .Select(n => new AgentPlayerNoteDto(n.Slug, n.Name, n.Entries))
            .ToList();

        return new AgentStateSnapshot(instance, seq, sessionDto, memoryDto, skillDtos, noteDtos, notes.NoteLimit);
    }

    private static AgentSessionDto CaptureSession(AgentSession session, string sessionId, int roundId)
    {
        var conv = session.Conv;
        var body = conv.Snapshot();

        var messages = new AgentMessageDto[body.Count];
        for (var i = 0; i < body.Count; i++)
            messages[i] = AgentMessageDto.From(i, body[i]);

        return new AgentSessionDto(
            sessionId,
            (int)session.Brain,
            roundId,
            conv.PrefixHash,
            conv.SystemPrompt,
            conv.ToolsJson,
            conv.BodyEpoch,
            messages,
            Stats(session),
            LastTurn(session));
    }

    /// <summary>
    /// The whole statistics record.
    ///
    /// Sampled rather than diffed per counter — see <see cref="AgentEventBus"/>. The same builder
    /// serves the snapshot and the periodic <see cref="AgentEventKind.Stats"/> event, so the two
    /// can never disagree about what a field means.
    /// </summary>
    public static AgentStatsDto Stats(AgentSession session)
    {
        var conv = session.Conv;

        return new AgentStatsDto(
            session.Turns,
            conv.TurnCount,
            session.UntooledReplies,
            session.State.IdleTurns,
            session.ConsecutiveFailures,
            session.State.BrokenPromises,
            session.State.Compactions,
            conv.LastPromptTokens,
            conv.CharsPerToken,
            conv.BodyChars(),
            session.ContextLimit,
            session.Cache.LastRatio,
            session.Cache.MeanRatio,
            session.Cache.Alarms,
            session.Queue.Count,
            session.Mode.ToString(),
            session.LastError,
            conv.VolatileTail);
    }

    private static AgentTurnDto? LastTurn(AgentSession session)
    {
        var turn = session.LastTurn;
        if (turn == null)
            return null;

        return new AgentTurnDto(
            turn.Index,
            turn.Phase.ToString(),
            turn.Step,
            turn.ToolCalls,
            turn.Spoke,
            turn.Nudged,
            turn.Promised,
            turn.Exit.ToString(),
            turn.Delivery.ToString(),
            turn.LastCacheRatio,
            turn.Perception.RadioChannel,
            turn.Perception.Addressed,
            turn.Perception.Forced,
            turn.Perception.Text);
    }
}
