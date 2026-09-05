using System.Linq;
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
    /// server that reports a live agent and quietly stops ticking. So the main thread publishes
    /// handles into the directory when a body is claimed and removes them when it is released, and
    /// the debug path only ever reads that directory.
    /// </summary>
    private readonly AgentDirectory _agents = new();

    private AgentDebugServer? _debugServer;

    /// <summary>The agent directory as the HTTP thread sees it. Empty until someone claims a body.</summary>
    public AgentDirectory DebugAgents => _agents;

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
            _agents,
            // The core's library. Can be null until the first session — the debugger comes up before
            // the body does, and handing back an empty snapshot is more honest than delaying the
            // endpoint's start.
            () => CoreVfs,
            CurrentRoundId,
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

    /// <summary>
    /// A new agent has taken over a body. Main thread only.
    /// </summary>
    /// <remarks>
    /// <b>The order here is a contract.</b> The handle goes into the directory first, and only then
    /// does the <c>session.started</c> frame go out. The client reacts to that frame by requesting an
    /// agent snapshot, and in the reverse order it would get <c>null</c> — i.e. "the agent didn't
    /// start" about the very agent that just started.
    /// </remarks>
    private void AttachDebugSession(AgentSession session)
    {
        var id = session.Body.Id;
        var round = CurrentRoundId();
        var startedSeq = _bus?.Seq ?? 0;

        var handle = new AgentHandle
        {
            Id = id,
            Name = session.Body.Name,
            Brain = (int) session.Brain,
            Round = round,
            StartedSeq = startedSeq,
            Alive = true,
            Capture = () => AgentDebugState.CaptureSession(session, id, round),
            Roster = () => AgentDebugState.Roster(session, id, session.Body.Name, round, startedSeq),
            Send = text => (Deliver(session, text, out var why), why),
        };

        if (!_agents.Add(handle))
        {
            // Not silently: a matching identifier means two agents share a memory directory and a
            // conversation file, and the directory is the only place where that is even noticeable.
            _sawmill.Warning($"агент {id} уже на витрине отладки — второй с тем же идентификатором не показан");
        }

        _bus?.Publish(AgentEventKind.SessionStarted, session.Body.Id, JsonSerializer.Serialize(new
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
        // Removed from the directory BEFORE publishing: after the session.ended frame, a snapshot
        // request must return null, not a picture of a session that's about to be cancelled.
        //
        // Only this handle is removed, by reference. An unconditional removal by id would knock the
        // NEW agent off the directory if the borg got reclaimed in the same tick.
        foreach (var handle in _agents.All)
        {
            if (handle.Id == session.Body.Id)
                _agents.Remove(handle.Id, handle);
        }

        _bus?.Publish(AgentEventKind.SessionEnded, session.Body.Id, JsonSerializer.Serialize(new
        {
            brain = (int)session.Brain,
            reason = why,
        }, LlmJson.Options));
    }

    private float _sinceRoster;

    /// <summary>
    /// Refresh liveness in the directory and sweep out handles without a session. Main thread only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once a second, not every tick: <c>Alive</c> for the core is <c>IsPlayable</c>, i.e. a call
    /// into the world, and there's no reason to make it thirty times a second for an indicator.
    /// <b>Hence the limitation: the value is only good as an indicator.</b> Logic must not be built
    /// on it — it lies for the first second after death.
    /// </para>
    /// <para>
    /// The sweep is insurance against a leak. Should a path ever appear that removes a session
    /// without going through <c>Release</c>, the handle would keep living with a reference to a
    /// closed loop, and an agent snapshot would run into a cancelled token.
    /// </para>
    /// </remarks>
    private void RefreshAgentDirectory(float frameTime)
    {
        if (_bus == null)
            return;

        _sinceRoster += frameTime;

        if (_sinceRoster < 1f)
            return;

        _sinceRoster = 0f;

        foreach (var session in _sessions.Values)
        {
            if (_agents.Find(session.Body.Id) is { } handle)
                handle.Alive = session.Body.Alive();
        }

        _agents.RetainOnly(_sessions.Values.Select(s => s.Body.Id).ToList());
    }

    // ------------------------------------------------------------ inbound commands

    /// <summary>
    /// Queue a user message for the agent's next turn.
    ///
    /// Refused outright when nobody holds a core, rather than queued: text that survived a round
    /// restart would be delivered into a fresh conversation, out of context and unattributable.
    /// Callable from any thread — the inbox has its own lock and the loop claims from it.
    /// </summary>
    public bool SendUserMessage(string agentId, string text, out string reason)
    {
        var handle = _agents.Find(agentId);

        if (handle == null)
        {
            reason = $"нет агента '{agentId}'";
            return false;
        }

        (var ok, reason) = handle.Send(text);
        return ok;
    }

    /// <summary>Put a message into a specific session's inbox. Any thread: the inbox has its own lock.</summary>
    private static bool Deliver(AgentSession session, string text, out string reason)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "пустое сообщение";
            return false;
        }

        session.Inbox.Enqueue(text);

        // An operator typing into the debugger is the least patient audience there is, and unlike a
        // radio line this never passes through the observation queue — so without waking the loop
        // here it would sit out the tick, or on an idle station the twenty-five second one.
        session.Wake();

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
    public MemoryResult ChangeMemory(string action, string match, string content)
    {
        if (CoreVfs?.Memory is not { } memory)
            return new MemoryResult(false, "агент ещё не запускался, память не смонтирована");

        return action switch
        {
            "add" => memory.Add(content),
            "replace" => memory.Replace(match, content),
            "remove" => memory.Remove(match),
            _ => new MemoryResult(false, $"неизвестное действие '{action}' — ожидалось add, replace или remove"),
        };
    }

    /// <summary>
    /// Edit an agent's skill entry from outside. Same caveat about disk versus prefix.
    /// </summary>
    /// <remarks>
    /// <paramref name="name"/> is a path inside <c>/skills</c>, e.g. <c>питание/смес</c>. This is a
    /// format change: names used to be flat. An old flat name still works and refers to a file at
    /// the root of <c>/skills</c>.
    /// </remarks>
    public SkillResult ChangeSkill(string name, string? when, string? body, string? match, string? replacement)
    {
        if (CoreVfs?.Skills is not { } skills)
            return new SkillResult(false, "агент ещё не запускался, библиотека не смонтирована");

        // Two forms of the same command: (name, when, body) writes the whole file, (name, match,
        // replacement) edits a fragment. Mirrors the two tools the model itself has.
        var result = match != null || replacement != null
            ? skills.Edit(name, match ?? "", replacement ?? "")
            : skills.Write(name, name, when ?? "", body ?? "");

        return new SkillResult(result.Ok, result.Message, result.Hints);
    }
}
