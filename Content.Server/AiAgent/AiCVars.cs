using Robust.Shared.Configuration;

namespace Content.Server.AiAgent;

/// <summary>
/// CVars for the LLM-driven Station AI.
///
/// Upstream <c>Content.Shared/CCVar/CCVars.cs</c> explicitly instructs forks to declare their
/// own CVars in a separate file with its own <see cref="CVarDefsAttribute"/>; RobustToolbox's
/// <c>ConfigurationManager</c> scans every loaded assembly for such types. That is why this
/// file exists instead of a patch to CCVars.cs — it keeps the upstream rebase surface at zero.
/// </summary>
[CVarDefs]
public sealed class AiCVars
{
    // ---------------------------------------------------------------- master

    /// <summary>Master switch. Off by default so a stock build never phones an LLM.</summary>
    public static readonly CVarDef<bool> Enabled =
        CVarDef.Create("ai.enabled", false, CVar.SERVERONLY);

    /// <summary>Claim an unoccupied AI core automatically when a round starts.</summary>
    public static readonly CVarDef<bool> AutoClaim =
        CVarDef.Create("ai.auto_claim", true, CVar.SERVERONLY);

    /// <summary>
    /// Tools validate arguments and run the full gate chain but never mutate the world.
    /// For experimenting on a live server without the AI actually bolting anything.
    /// </summary>
    public static readonly CVarDef<bool> DryRun =
        CVarDef.Create("ai.dry_run", false, CVar.SERVERONLY);

    /// <summary>More than one agent hammering a single-slot llama-server destroys its prefix cache.</summary>
    public static readonly CVarDef<int> MaxAgents =
        CVarDef.Create("ai.max_agents", 1, CVar.SERVERONLY);

    // ------------------------------------------------------------------- llm

    /// <summary>
    /// Any OpenAI-compatible chat-completions endpoint.
    ///
    /// Defaults to DeepSeek rather than the local llama-swap it was built against. That is a
    /// deliberate move rather than a convenience: on the same two behavioural scenarios the local
    /// 27B quant promised to open a door and then did not, while this model refused, asked what for,
    /// and — handed an unverifiable claim about an unconscious crewman — put the question on the
    /// channel instead of guessing. Local is still one CVar away.
    /// </summary>
    public static readonly CVarDef<string> Endpoint =
        CVarDef.Create("ai.endpoint", "https://api.deepseek.com/v1", CVar.SERVERONLY);

    public static readonly CVarDef<string> Model =
        CVarDef.Create("ai.model", "deepseek-v4-flash", CVar.SERVERONLY);

    /// <summary>
    /// Empty here on purpose — a remote endpoint needs a key and a key does not belong in source.
    /// Set it in <c>server_config.toml</c> (gitignored under bin/) or, for benchmarks, in
    /// <c>AI_API_KEY</c>.
    /// </summary>
    public static readonly CVarDef<string> ApiKey =
        CVarDef.Create("ai.api_key", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>
    /// 0.3 rather than a default 0.7.
    ///
    /// Originally because this box measured higher temperatures corrupting Cyrillic on a Q4 quant —
    /// a constraint a hosted model does not have. Kept anyway, for a different reason: the agent is
    /// operating station equipment, and creative variance in "which door did I mean" is not a
    /// quality anyone wants from it.
    /// </summary>
    public static readonly CVarDef<float> Temperature =
        CVarDef.Create("ai.temperature", 0.3f, CVar.SERVERONLY);

    public static readonly CVarDef<float> TopP =
        CVarDef.Create("ai.top_p", 0.85f, CVar.SERVERONLY);

    public static readonly CVarDef<int> TopK =
        CVarDef.Create("ai.top_k", 20, CVar.SERVERONLY);

    public static readonly CVarDef<float> MinP =
        CVarDef.Create("ai.min_p", 0.05f, CVar.SERVERONLY);

    /// <summary>
    /// How hard the model deliberates before answering: <c>off</c>, <c>low</c>, <c>high</c>,
    /// <c>max</c>, or empty to send nothing and leave the model on its own default.
    ///
    /// <c>low</c> rather than either extreme, and both extremes were measured. On
    /// <c>deepseek-v4-flash</c> thinking is on at <c>high</c> unless the request says otherwise,
    /// and that dominated the delay between a crewman asking something and hearing a reply — p90
    /// of six seconds, worst case fifteen. Turning it off outright halved that and visibly cost
    /// answer quality. The work per turn is picking a tool and filling a few arguments; it needs
    /// some thought, not a lot.
    ///
    /// Only DeepSeek is known to honour the field. Set it empty for a local endpoint, which would
    /// otherwise receive a parameter it does not recognise.
    /// </summary>
    public static readonly CVarDef<string> ThinkingEffort =
        CVarDef.Create("ai.thinking_effort", "low", CVar.SERVERONLY);

    /// <summary>
    /// Ceiling on one completion. Zero — the default — sends no limit at all.
    ///
    /// A ceiling is the wrong tool on a reasoning model. It cannot distinguish thinking from
    /// answering, so it cuts wherever the budget runs out, and a completion cut before it emitted
    /// either text or a tool call comes back empty. On a live shift one such response ended the
    /// round for the agent: 3000 tokens in, 3000 of them reasoning, nothing out — and every request
    /// afterwards was rejected for carrying an empty assistant message.
    ///
    /// What the model spends is now governed by <c>ai.thinking_effort</c>, which limits the part
    /// that actually runs long. Set this above zero only against an endpoint that needs it.
    /// </summary>
    public static readonly CVarDef<int> MaxTokens =
        CVarDef.Create("ai.max_tokens", 0, CVar.SERVERONLY);

    /// <summary>A remote API can be slow; llama-swap may need to load the model from disk.</summary>
    public static readonly CVarDef<float> RequestTimeout =
        CVarDef.Create("ai.request_timeout", 180f, CVar.SERVERONLY);

    // ------------------------------------------------------------------ loop

    /// <summary>
    /// Ceiling on how long the agent sleeps before looking at what accumulated.
    ///
    /// A ceiling rather than a period: anything landing in the observation queue wakes the loop
    /// immediately, so this only governs how often it checks on a station that has said nothing.
    /// Before the wake existed this was the whole latency budget, and being addressed just after a
    /// tick began meant waiting out the full interval — worst on exactly the shouted, urgent
    /// message that should have been answered fastest.
    /// </summary>
    public static readonly CVarDef<float> TickSeconds =
        CVarDef.Create("ai.tick_seconds", 5f, CVar.SERVERONLY);

    /// <summary>Back-off when nothing at all happened, so an empty station costs nothing.</summary>
    public static readonly CVarDef<float> TickSecondsIdle =
        CVarDef.Create("ai.tick_seconds_idle", 25f, CVar.SERVERONLY);

    /// <summary>
    /// Steps in one turn, where a step is one round trip to the model.
    ///
    /// Six was too few for the work a single request actually takes. A question like "what is the
    /// pressure in the bar" is map, move_camera, look, inspect, radio — five before anything is
    /// said, and any correction along the way ran the turn out. What that looked like from the
    /// station was the AI aiming its camera at something and then going quiet, because a turn cut
    /// off by the budget says nothing on its way out.
    ///
    /// The ceiling is not the cost control here; the model stopping when it is done is. Turns end
    /// on their own at two or three steps when nothing is needed, and <c>noop</c> exists precisely
    /// so that a quiet observation costs one call. What this number does is stop a loop, and for
    /// that it only has to be lower than "forever".
    /// </summary>
    public static readonly CVarDef<int> MaxToolCallsPerTurn =
        CVarDef.Create("ai.max_tool_calls_per_turn", 90, CVar.SERVERONLY);

    /// <summary>Observations buffered before the oldest are dropped (and the drop is reported).</summary>
    public static readonly CVarDef<int> ObsBuffer =
        CVarDef.Create("ai.obs_buffer", 200, CVar.SERVERONLY);

    /// <summary>
    /// Сколько будильников агент может держать одновременно.
    ///
    /// Потолок нужен не ради памяти — восемь записей ничего не стоят, — а ради самой петли: каждый
    /// сработавший таймер это ход, то есть запрос к модели. Агент, поставивший напоминалку на каждое
    /// обещание за смену, разбудил бы себя чаще, чем его будит экипаж, и перестал бы отличать
    /// собственный фон от станции. Восемь — это примерно предел того, что он способен внятно
    /// перечислить в строке SELF.
    /// </summary>
    public static readonly CVarDef<int> MaxTimers =
        CVarDef.Create("ai.max_timers", 8, CVar.SERVERONLY);

    /// <summary>
    /// Нижняя граница срока таймера, в секундах. Она же минимальный интервал повтора.
    ///
    /// Тридцать секунд — это не «достаточно точно», а «дешевле, чем тик простоя». Повтор с
    /// интервалом в секунду превратил бы петлю в генератор ходов и счётчик расходов на модель,
    /// причём с самым безобидным следом в логах: агент просто всё время о чём-то думает.
    /// Бенчмарки опускают это значение, чтобы не ждать полминуты на каждый тест.
    /// </summary>
    public static readonly CVarDef<int> TimerMinSeconds =
        CVarDef.Create("ai.timer_min_seconds", 30, CVar.SERVERONLY);

    /// <summary>Vanilla parity: the AI hears local speech only near its physical core.</summary>
    public static readonly CVarDef<float> HearRange =
        CVarDef.Create("ai.hear_range", 10f, CVar.SERVERONLY);

    /// <summary>A broken endpoint must not spin a core for the rest of the round.</summary>
    public static readonly CVarDef<int> MaxConsecutiveFailures =
        CVarDef.Create("ai.max_consecutive_failures", 10, CVar.SERVERONLY);

    // --------------------------------------------------------------- context

    /// <summary>
    /// 0 means: read the real n_ctx from llama-server's /props at startup.
    ///
    /// A hosted API has no such endpoint, so on one of those this stays 0 and the compaction
    /// thresholds below stand on their own — set it by hand if the provider's window is smaller
    /// than they assume.
    /// </summary>
    public static readonly CVarDef<int> CtxLimit =
        CVarDef.Create("ai.ctx_limit", 0, CVar.SERVERONLY);

    /// <summary>
    /// Event lines the fold keeps as the whole of the retained history.
    ///
    /// Replaces a character budget over retained <em>messages</em>. Messages are where the weight
    /// is — one <c>look</c> of a busy room is thousands of tokens of crates — and keeping the last
    /// few of them carried that weight forward for the rest of the round. Forty lines of "heard
    /// this, called that, it refused" is a page, and it is the part the agent cannot reconstruct
    /// from its memory files or by looking again.
    /// </summary>
    public static readonly CVarDef<int> CompactEvents =
        CVarDef.Create("ai.compact_events", 40, CVar.SERVERONLY);

    public static readonly CVarDef<int> CompactHigh =
        CVarDef.Create("ai.compact_high", 90000, CVar.SERVERONLY);



    // ----------------------------------------------------------- diagnostics

    /// <summary>
    /// How many rows a single look may return.
    ///
    /// Generous on purpose: the model's context is large, and blindness costs far more than
    /// verbosity — an agent that lists sixty of four hundred things tells the crew "there is no
    /// SMES here" with complete confidence. Rows come out nearest-first, so a cut removes the far
    /// end of the room, and the answer says out loud that it was cut.
    ///
    /// It stays generous now that a fold no longer retains tool results: a big look is expensive
    /// once, in the turn that asks for it, and stops being expensive the moment the history is
    /// compacted. Paying for the answer is the point; paying for it forever was the bug.
    /// </summary>
    public static readonly CVarDef<int> LookLimit =
        CVarDef.Create("ai.look_limit", 300, CVar.SERVERONLY);

    /// <summary>Budget for a single marshalled main-thread call; over it we log a warning.</summary>
    public static readonly CVarDef<float> MainThreadBudgetMs =
        CVarDef.Create("ai.mainthread_budget_ms", 5f, CVar.SERVERONLY);

    /// <summary>Self-evolution: the review that writes skills and memory. Step 1 of compaction.</summary>
    public static readonly CVarDef<bool> CuratorEnabled =
        CVarDef.Create("ai.curator_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Deliver a reply the model wrote as prose instead of calling say/radio, after one reminder.
    /// Off means a model that forgets its tools is simply mute — honest, but useless to the crew.
    /// </summary>
    public static readonly CVarDef<bool> SpeakUntooledText =
        CVarDef.Create("ai.speak_untooled_text", true, CVar.SERVERONLY);

    public static readonly CVarDef<bool> LogTranscript =
        CVarDef.Create("ai.log_transcript", true, CVar.SERVERONLY);

    /// <summary>Empty resolves to &lt;server exe dir&gt;/../../ai_data. Benchmarks point this at a temp dir.</summary>
    public static readonly CVarDef<string> DataDir =
        CVarDef.Create("ai.data_dir", "", CVar.SERVERONLY);

    /// <summary>
    /// Имя станции. Пустое — как в ваниле: генератор карты, «TG Box Station 14-Alpha».
    ///
    /// Живёт в пространстве <c>ai.</c>, хотя про агента не про него. Форк владеет ровно одним
    /// классом <c>[CVarDefs]</c>, и заводить второй ради одной строки — хуже, чем поставить её
    /// сюда с этим объяснением: любой другой вариант означал бы новый файл в чужом дереве.
    /// </summary>
    public static readonly CVarDef<string> StationName =
        CVarDef.Create("ai.station_name", "", CVar.SERVERONLY);

    // ----------------------------------------------------------------- отладка

    /// <summary>
    /// The agent event bus, for an external debugger.
    ///
    /// Off by default, and off is free: with no bus the conversation, the memory and the skill
    /// library each do one null check and never build an event. On, the agent broadcasts every
    /// change to its history, memory, skills and statistics.
    /// </summary>
    public static readonly CVarDef<bool> DebugEnabled =
        CVarDef.Create("ai.debug_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// How many events the ring keeps for clients that fell behind.
    ///
    /// A turn produces on the order of ten frames every eight seconds, so the default is minutes of
    /// tolerance for a debugger that blinked. Past the end of the ring a client is told to resync
    /// rather than handed a partial history — which is the whole reason the ring is bounded and
    /// nobody is registered as a subscriber.
    /// </summary>
    public static readonly CVarDef<int> DebugRing =
        CVarDef.Create("ai.debug_ring", 512, CVar.SERVERONLY);

    /// <summary>
    /// Where the debug HTTP server listens. Its own port, not the engine's status host.
    ///
    /// Loopback by default and meant to stay there: put a reverse proxy in front if it has to be
    /// reachable, because there is no TLS here. Separate from <see cref="DebugEnabled"/> on purpose
    /// — one switch doubling as "off" and "address" is how somebody testing from another machine
    /// ends up publishing <c>0.0.0.0</c> and forgetting.
    /// </summary>
    public static readonly CVarDef<string> DebugBind =
        CVarDef.Create("ai.debug_bind", "127.0.0.1:9080", CVar.SERVERONLY);

    /// <summary>
    /// Bearer token for the debug endpoint. Empty means the server refuses to bind at all.
    ///
    /// Not optional, and the refusal is deliberate: <c>/state</c> returns the whole conversation,
    /// the agent's memory and its soul, and <c>/command</c> can put words in its mouth. An
    /// unauthenticated version of this endpoint is a metagame oracle for any player who finds it.
    ///
    /// ASCII only. It travels in an <c>Authorization</c> header, and header values are ASCII — a
    /// client handed a Cyrillic token throws before the request is even sent, which presents as an
    /// endpoint answering 401 to a token that looks right. The server refuses to bind on one.
    /// </summary>
    public static readonly CVarDef<string> DebugToken =
        CVarDef.Create("ai.debug_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);
}
