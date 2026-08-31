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
    /// server that reports a live agent and quietly stops ticking. So the main thread публикует
    /// хендлы в витрину при захвате и снимает их при освобождении, а путь отладки читает только её.
    /// </summary>
    private readonly AgentDirectory _agents = new();

    private AgentDebugServer? _debugServer;

    /// <summary>Витрина агентов, какой её видит HTTP-поток. Пуста, пока никто не захватил тело.</summary>
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
            // Библиотека ядра. Может быть null до первой сессии — отладчик поднимается раньше
            // тела, и отдать пустой снимок честнее, чем отложить старт эндпоинта.
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
    /// Новый агент занял тело. Только главный поток.
    /// </summary>
    /// <remarks>
    /// <b>Порядок здесь — контракт.</b> Сначала хендл встаёт на витрину, и только потом уходит
    /// кадр <c>session.started</c>. Клиент реагирует на этот кадр запросом снимка агента, и при
    /// обратном порядке получил бы <c>null</c> — то есть «агент не запустился» ровно про того,
    /// кто только что запустился.
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
            // Не молча: совпадение идентификаторов означает общий каталог памяти и общий файл
            // диалога у двух агентов, и витрина — единственное место, где это вообще заметно.
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
        // Снимаем с витрины ДО публикации: после кадра session.ended запрос снимка обязан
        // вернуть null, а не картинку сессии, которую вот-вот отменят.
        //
        // Снимается только свой хендл, по ссылке. Безусловное удаление по идентификатору снесло бы
        // с витрины НОВОГО агента, если борга переклеймили в том же тике.
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
    /// Обновить живость на витрине и подмести хендлы без сессий. Только главный поток.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Раз в секунду, а не каждый тик: <c>Alive</c> у ядра — это <c>IsPlayable</c>, то есть
    /// обращение к миру, и делать его тридцать раз в секунду ради индикатора не за что.
    /// <b>Отсюда и ограничение: значение годится только для индикатора.</b> Логику на нём строить
    /// нельзя — первую секунду после смерти оно врёт.
    /// </para>
    /// <para>
    /// Подметание — страховка от утечки. Появись когда-нибудь путь, убирающий сессию мимо
    /// <c>Release</c>, хендл остался бы жить со ссылкой на закрытую петлю, и снимок агента упёрся
    /// бы в отменённый токен.
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

    // ------------------------------------------------------------ входящие команды

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

    /// <summary>Положить сообщение в ящик конкретной сессии. Любой поток: у ящика свой замок.</summary>
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
    /// Правка записи агента снаружи. Та же оговорка про диск против префикса.
    /// </summary>
    /// <remarks>
    /// <paramref name="name"/> — путь внутри <c>/skills</c>, например <c>питание/смес</c>. Это
    /// изменение формата: раньше имена были плоскими. Старое плоское имя по-прежнему работает и
    /// означает файл в корне <c>/skills</c>.
    /// </remarks>
    public SkillResult ChangeSkill(string name, string? when, string? body, string? match, string? replacement)
    {
        if (CoreVfs?.Skills is not { } skills)
            return new SkillResult(false, "агент ещё не запускался, библиотека не смонтирована");

        // Две формы одной команды: (name, when, body) пишет файл целиком, (name, match,
        // replacement) правит фрагмент. Повторяет два инструмента, которые есть у самой модели.
        var result = match != null || replacement != null
            ? skills.Edit(name, match ?? "", replacement ?? "")
            : skills.Write(name, name, when ?? "", body ?? "");

        return new SkillResult(result.Ok, result.Message, result.Hints);
    }
}
