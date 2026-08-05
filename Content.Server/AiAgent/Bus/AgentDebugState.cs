using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// The state getter: one consistent picture of the agent, assembled once.
///
/// <para>
/// <b>This is the only place that holds more than two locks.</b> The order is
/// <c>Conv → Memory → Skills → Bus</c> and it is total: every publisher takes at most its own
/// domain lock and then the bus lock, which is a prefix of that order, so no cycle can be built.
/// Capture walks the whole chain in the same direction. That is the entire deadlock argument.
/// </para>
/// <para>
/// The bus lock is taken <em>last</em> and read for the sequence number, which is what pairs the
/// snapshot with the stream: a client that fetches state at seq N and then asks for events after N
/// sees every change exactly once. Read the seq first and a change landing in between would be
/// applied twice; read it without the data locks held and it could be applied zero times.
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
        string sessionId)
    {
        // Conv first. Everything read here comes out of one acquisition, so the messages, the
        // prefix and the counters describe the same instant rather than three adjacent ones.
        var sessionDto = session == null ? null : CaptureSession(session, sessionId);

        var memoryDto = new AgentMemoryDto(
            memory.Entries(MemoryTarget.Memory),
            memory.Snapshot(MemoryTarget.Memory),
            memory.Entries(MemoryTarget.Crew),
            memory.Snapshot(MemoryTarget.Crew));

        var skillDtos = skills.All
            .OrderBy(s => s.Name, System.StringComparer.Ordinal)
            .Select(s => new AgentSkillDto(s.Name, s.When, s.Body))
            .ToList();

        return new AgentStateSnapshot(bus.Instance, bus.Seq, sessionDto, memoryDto, skillDtos);
    }

    private static AgentSessionDto CaptureSession(AgentSession session, string sessionId)
    {
        var conv = session.Conv;
        var body = conv.Snapshot();

        var messages = new AgentMessageDto[body.Count];
        for (var i = 0; i < body.Count; i++)
            messages[i] = AgentMessageDto.From(i, body[i]);

        return new AgentSessionDto(
            sessionId,
            (int)session.Brain,
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
            session.ConsecutiveFailures,
            session.State.BrokenPromises,
            session.State.Compactions,
            session.State.CompactionArmed,
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
