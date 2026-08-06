using System.Text.Json;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent;

/// <summary>
/// The debug bus, as the system sees it.
///
/// One bus per process, not per session. Sessions come and go with rounds; memory and skills
/// outlive them, and a debugger watching a restart wants one continuous stream with the restart
/// visible in it rather than two disconnected ones. That is also why the sequence number and the
/// instance id belong here and not on <see cref="AgentSession"/>.
///
/// Null until the debug server is switched on, which is what makes the whole feature cost nothing
/// when it is off: every owner does one null check and skips building the arguments.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private AgentEventBus? _bus;

    /// <summary>
    /// The session a debug thread may look at, handed over by the main thread.
    ///
    /// <b>An HTTP thread must never look a session up itself.</b> <c>_sessions</c> is a plain
    /// <c>Dictionary</c> mutated from the main thread, and a <c>TryGetValue</c> that lands on a
    /// resize does not throw — it can spin forever inside the bucket chain, and the symptom is a
    /// server that reports a live agent and quietly stops ticking. So the main thread publishes the
    /// reference here at claim and clears it at release, and the debug path only ever reads this
    /// field.
    /// </summary>
    private volatile AgentSession? _debugSession;

    private AgentDebugServer? _debugServer;

    /// <summary>
    /// One agent at a time (<c>ai.max_agents</c> is 1), so the id is a constant — the same one the
    /// session snapshot on disk uses. It stays in the envelope anyway: a uniform frame shape costs
    /// nothing now and is what a second agent would need later.
    /// </summary>
    private const string DebugSessionId = "current";

    /// <summary>The live bus, or null when <c>ai.debug_enabled</c> is off.</summary>
    public AgentEventBus? DebugBus => _bus;

    /// <summary>Where the debug endpoint is listening, or null when it is not.</summary>
    public string? DebugEndpoint => _debugServer?.Prefix;

    /// <summary>
    /// Bring the bus up if the operator asked for it. Main thread, from <c>Initialize</c>.
    ///
    /// Sampled once rather than watched: the owners each capture a sink, and flipping the CVar
    /// mid-round would leave half of them reporting and half silent. Off is the default, and off
    /// means every owner's null check is the entire cost of the feature.
    /// </summary>
    private void StartDebugBus()
    {
        if (!_cfg.GetCVar(AiCVars.DebugEnabled))
            return;

        _bus = new AgentEventBus(_cfg.GetCVar(AiCVars.DebugRing));
        _sawmill.Info($"шина отладки поднята, кольцо {_bus.Capacity}, instance {_bus.Instance}");

        var router = new AgentDebugRouter(
            _bus,
            _cfg.GetCVar(AiCVars.DebugToken),
            DebugSessionId,
            () => _debugSession,
            () => Memory,
            () => Skills,
            CurrentRoundId,
            text => (SendUserMessage(text, out var reason), reason),
            ChangeMemory,
            ChangeSkill);

        // May legitimately fail — a taken port must degrade to "no debug endpoint", never abort a
        // round start. The bus stays up either way, so the console command can still read it.
        _debugServer = AgentDebugServer.TryStart(
            _cfg.GetCVar(AiCVars.DebugBind), _cfg.GetCVar(AiCVars.DebugToken), router, _sawmill);
    }

    /// <summary>
    /// Take the debug server down. Called from the system's <c>Shutdown</c>.
    ///
    /// <c>Shutdown()</c> is never called on a dedicated server — <c>BaseServer.Cleanup</c> goes
    /// through <c>EntitySystemManager.Clear()</c>, which does not call it — so on the real deployment
    /// the socket is reclaimed by the OS at process exit and this costs nothing. It does run on the
    /// client and in the integration harness, and there it is what stops a listener from outliving
    /// the test that started it and failing the next one's bind.
    /// </summary>
    private void StopDebugServer()
    {
        _debugServer?.Dispose();
        _debugServer = null;
    }

    /// <summary>A new agent took a core. Main thread only.</summary>
    private void AttachDebugSession(AgentSession session)
    {
        _debugSession = session;

        _bus?.Publish(AgentEventKind.SessionStarted, SessionIdFor(session.Brain), JsonSerializer.Serialize(new
        {
            brain = (int)session.Brain,
            round = CurrentRoundId(),
            prefix_hash = session.Conv.PrefixHash,
        }, LlmJson.Options));
    }

    /// <summary>
    /// The agent is going away. Main thread only, and before the CTS is cancelled.
    ///
    /// The event matters more than it looks: a client has been accumulating a conversation for a
    /// whole round, and nothing in a message frame would ever tell it that the history it holds
    /// belongs to an agent that no longer exists.
    /// </summary>
    private void DetachDebugSession(AgentSession session, string why)
    {
        _debugSession = null;

        _bus?.Publish(AgentEventKind.SessionEnded, SessionIdFor(session.Brain), JsonSerializer.Serialize(new
        {
            brain = (int)session.Brain,
            reason = why,
        }, LlmJson.Options));
    }

    // ------------------------------------------------------------ входящие команды

    /// <summary>
    /// Queue a user message for the agent's next turn.
    ///
    /// Refused outright when nobody holds a core, rather than queued: text that survived a round
    /// restart would be delivered into a fresh conversation, out of context and unattributable.
    /// Callable from any thread — the inbox has its own lock and the loop claims from it.
    /// </summary>
    public bool SendUserMessage(string text, out string reason)
    {
        var session = _debugSession;

        if (session == null)
        {
            reason = "нет активного агента";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "пустое сообщение";
            return false;
        }

        session.Inbox.Enqueue(text);
        reason = "доставлено следующим ходом";
        return true;
    }

    /// <summary>
    /// Edit memory from outside the agent.
    ///
    /// Goes straight to the store rather than through the loop: the stores have their own locks,
    /// are file-backed, and are not part of the conversation the loop owns. But the caller must be
    /// told that the edit is only on disk — the model keeps reading the frozen zone-0 text until
    /// the next prefix rebuild, and an operator who is not told that watches the agent behave
    /// identically and concludes the endpoint is broken.
    /// </summary>
    public MemoryResult ChangeMemory(MemoryTarget target, string action, string match, string content) =>
        action switch
        {
            "add" => Memory.Add(target, content),
            "replace" => Memory.Replace(target, match, content),
            "remove" => Memory.Remove(target, match),
            _ => new MemoryResult(false, $"неизвестное действие '{action}' — ожидалось add, replace или remove"),
        };

    /// <summary>Write or edit a skill from outside the agent. Same disk-versus-prefix caveat.</summary>
    public SkillResult ChangeSkill(string name, string? when, string? body, string? match, string? replacement)
    {
        // Two shapes on one command: (name, when, body) writes the skill whole, (name, match,
        // replacement) edits a fragment. Mirrors the two tools the model itself has.
        if (match != null || replacement != null)
            return Skills.Edit(name, match ?? "", replacement ?? "");

        return Skills.Write(name, when ?? "", body ?? "");
    }
}
