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

    /// <summary>
    /// Ceiling on the number of simultaneously live agents — the core and the borgs together.
    ///
    /// <para>
    /// Checked in <c>StationAiAgentSystem.StartSession</c>, that is, in one place for both bodies.
    /// It used to sit only on core claiming and count the borgs there too — which meant a robot
    /// that claimed a body first stole the core slot from the brain.
    /// </para>
    /// <para>
    /// Eight, and the number is not arbitrary: that is exactly how many the open rogue AI mode
    /// needs — the core plus seven bodies, six combat and one engineering (<c>supportBorgs</c> in
    /// <c>rogue_ai.yml</c>).
    ///
    /// <para>
    /// The default has to accommodate the mode the fork ships with. It stood at four, and the squad
    /// grew to seven on 01.09.2026, and on the old default that produced quiet half-measures: either
    /// some hulls went without an agent and stood there like statues, or the bodies claimed the
    /// whole limit and the brain could not sit in the CORE — rogue AI mode with no rogue AI, and not
    /// a single error in the log. When changing <c>supportBorgs</c>, change this number too.
    /// </para>
    /// <para>
    /// The previous default of one protected a single-slot llama-server, where two agents evict
    /// each other's prefix cache — but that protection does not live here, it lives in the profile
    /// chain (<c>AgentBody.LlmChain</c>): an agent with a different model does not contend for
    /// somebody else's slot.
    /// </para>
    /// </summary>
    public static readonly CVarDef<int> MaxAgents =
        CVarDef.Create("ai.max_agents", 8, CVar.SERVERONLY);

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

    // ------------------------------------------------------------ model chain

    /// <summary>
    /// Primary model and fallback order: comma-separated ids of <c>aiLlmProfile</c> prototypes,
    /// e.g. <c>codex,grok,deepseek,local</c>. The first one is primary.
    ///
    /// <para>
    /// Empty — behave as before: a single endpoint from <c>ai.endpoint</c> / <c>ai.model</c>. That
    /// is not a stopgap but a working mode: while profiles are not yet laid out, or one of them
    /// broke the round, the rollback has to be one line in the console, not a rebuild that kicks
    /// every player.
    /// </para>
    /// <para>
    /// The order lives here rather than in the profile YAML for exactly the same reason: the set of
    /// providers itself is data, but which of them is currently primary is an operational decision,
    /// and it has to be changed on a live server. The local profile is worth keeping last: a chain
    /// that has entirely moved to the internet ends together with the internet.
    /// </para>
    /// </summary>
    public static readonly CVarDef<string> LlmChain =
        CVarDef.Create("ai.llm_chain", "", CVar.SERVERONLY);

    /// <summary>
    /// SOCKS proxy for profiles with <c>proxy: Socks</c>. Empty — such profiles go direct.
    ///
    /// <c>SocketsHttpHandler</c> understands <c>socks4://</c>, <c>socks4a://</c> and <c>socks5://</c>
    /// since .NET 6, so no separate library is needed. Local profiles must be set to
    /// <c>proxy: None</c>: a request to loopback sent out through a remote proxy simply hangs.
    /// </summary>
    public static readonly CVarDef<string> LlmSocksProxy =
        CVarDef.Create("ai.llm_socks_proxy", "socks5://127.0.0.1:10808", CVar.SERVERONLY);

    /// <summary>How long a profile sleeps after an ordinary failure — network, 5xx, timeout. Seconds.</summary>
    public static readonly CVarDef<float> LlmCooldownSeconds =
        CVarDef.Create("ai.llm_cooldown_seconds", 300f, CVar.SERVERONLY);

    /// <summary>
    /// How long to sleep after quota exhaustion, when the provider did not say when it resets. Seconds.
    ///
    /// An hour, not five minutes, and this is not caution. A subscription quota is a window with a
    /// known end: five hours for Codex, a weekly pool for Grok Build. Probes into an exhausted
    /// window return nothing, but each one is still a request — that is, they spend exactly the
    /// thing that is already gone. If the provider sent <c>Retry-After</c>, this value is not used
    /// at all — the deadline it named is used instead.
    /// </summary>
    public static readonly CVarDef<float> LlmQuotaCooldownSeconds =
        CVarDef.Create("ai.llm_quota_cooldown_seconds", 3600f, CVar.SERVERONLY);

    /// <summary>
    /// How often to try returning to the primary profile after falling back. Seconds.
    ///
    /// A probe is not free: switching providers invalidates the prefix cache on both sides, and the
    /// live server holds a 97.9% reuse rate, saving tens of thousands of tokens on every turn.
    /// Five minutes is a compromise between "sit on the fallback for the whole shift" and "flap
    /// every turn".
    /// </summary>
    public static readonly CVarDef<float> LlmRecheckSeconds =
        CVarDef.Create("ai.llm_recheck_seconds", 300f, CVar.SERVERONLY);

    /// <summary>
    /// Overall ceiling on one turn, on top of the individual profiles' timeouts. Seconds.
    ///
    /// Four profiles at 150-180s each add up to ten minutes on a single turn, and an agent that is
    /// "just thinking" is indistinguishable from a broken one to the crew. The router does not start
    /// a new attempt once the budget is spent, and trims the current one to whatever remains.
    /// </summary>
    public static readonly CVarDef<float> LlmTotalTimeout =
        CVarDef.Create("ai.llm_total_timeout", 240f, CVar.SERVERONLY);

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

    /// <summary>
    /// Observations buffered before the oldest are dropped (and the drop is reported).
    ///
    /// Raised from 200 because of the stream of OBSERVED lines: observation sees every action of
    /// every person in frame, and at two hundred lines for everything, a chatty compartment would
    /// exhaust the whole queue in a single agent turn. The model's context is 256k, a hundred lines
    /// in an observation message breaks nothing; losing a line of speech would, and that is what
    /// <see cref="ObserveBuffer"/> guards against.
    /// </summary>
    public static readonly CVarDef<int> ObsBuffer =
        CVarDef.Create("ai.obs_buffer", 600, CVar.SERVERONLY);

    /// <summary>
    /// How many alarms the agent may hold at once.
    ///
    /// The ceiling is not about memory — eight records cost nothing — but about the loop itself:
    /// every timer that fires is a turn, that is, a request to the model. An agent that set a
    /// reminder for every promise made during the shift would wake itself more often than the crew
    /// wakes it, and would stop being able to tell its own background noise from the station. Eight
    /// is roughly the limit of what it can meaningfully list in the SELF line.
    /// </summary>
    public static readonly CVarDef<int> MaxTimers =
        CVarDef.Create("ai.max_timers", 8, CVar.SERVERONLY);

    /// <summary>
    /// Lower bound on a timer's duration, in seconds. Also the minimum repeat interval.
    ///
    /// Thirty seconds is not "accurate enough" but "cheaper than an idle tick". A repeat with a
    /// one-second interval would turn the loop into a turn generator and a model-spend counter,
    /// with the most innocent-looking trace in the logs: the agent is simply thinking about
    /// something all the time. Benchmarks lower this value so as not to wait half a minute on every
    /// test.
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

    /// <summary>
    /// Warning threshold for a SINGLE slice of work in the world. Limits nothing — only writes to
    /// the log and to <c>aiagent cost</c>. What actually limits it is <see cref="FrameBudgetMs"/>.
    /// </summary>
    public static readonly CVarDef<float> MainThreadBudgetMs =
        CVarDef.Create("ai.mainthread_budget_ms", 5f, CVar.SERVERONLY);

    /// <summary>
    /// How many milliseconds of frame time the world bus may spend on agent requests.
    ///
    /// Three at a tickrate of 30 is 9% of a frame. A healthy tick on this server spends 21-26ms of
    /// 33.3 on <c>EntitySystems</c>, so the three milliseconds come out of the slack, not out of
    /// somebody else's work.
    ///
    /// Zero does not disable the bus: one slice always runs before the first deadline check,
    /// otherwise an overloaded server would freeze the agent forever, and it would quietly die in
    /// the middle of a round.
    /// </summary>
    public static readonly CVarDef<float> FrameBudgetMs =
        CVarDef.Create("ai.frame_budget_ms", 3f, CVar.SERVERONLY);

    /// <summary>
    /// Age after which an ordinary request is served ahead of urgent ones, ms.
    ///
    /// A starvation guard: without it a stream of urgent requests could keep an overview stuck in
    /// the queue indefinitely. At the current queue depth (tools are called strictly sequentially,
    /// one request in flight) it never fires, and that is fine — it is insurance for the future.
    /// </summary>
    public static readonly CVarDef<float> WorldPromoteMs =
        CVarDef.Create("ai.world_promote_ms", 500f, CVar.SERVERONLY);

    /// <summary>
    /// Ceiling on the world queue's depth. It must never trigger — if it does, some concurrency has
    /// crept in that the module does not have. The failure is loud: the request comes back with an
    /// error rather than being silently dropped.
    /// </summary>
    public static readonly CVarDef<int> WorldQueueMax =
        CVarDef.Create("ai.world_queue_max", 256, CVar.SERVERONLY);

    /// <summary>
    /// Bus switch. <c>false</c> — requests go straight into the engine's own queue, as before the
    /// bus existed.
    ///
    /// The same trick as <see cref="LookFast"/>, for the same reason: the server is public, and a
    /// rebuild kicks everyone playing on it. A rollback has to be a command, not a deployment.
    /// </summary>
    public static readonly CVarDef<bool> WorldBusEnabled =
        CVarDef.Create("ai.world_bus", true, CVar.SERVERONLY);

    /// <summary>
    /// Collect visible entities with one tree walk instead of a query per tile.
    ///
    /// A switch, not an experiment. The slow path is kept around in the tree for two reasons, and
    /// both are worth the fifteen lines.
    ///
    /// The first is proof: an equivalence test runs both paths over the same station and requires
    /// the fast one to lose nothing the slow one saw. The claim "we broke nothing" is either
    /// checkable or it is a promise.
    ///
    /// The second is rollback. The server is public, and a rebuild-and-restart kicks everyone
    /// playing on it. <c>cvar ai.look_fast false</c> from the admin console costs a second and zero
    /// kicks, and the investigation can happen afterwards.
    /// </summary>
    public static readonly CVarDef<bool> LookFast =
        CVarDef.Create("ai.look_fast", true, CVar.SERVERONLY);

    // ------------------------------------------------------------- observation

    /// <summary>
    /// Whether the agent sees what happens near its eye.
    ///
    /// The overall switch above every observation subscription. The server is public, and if the
    /// stream of lines turns out more harmful than useful, <c>cvar ai.observe false</c> from the
    /// admin console costs a second and zero kicks — unlike a rebuild-and-restart.
    /// </summary>
    public static readonly CVarDef<bool> Observe =
        CVarDef.Create("ai.observe", true, CVar.SERVERONLY);

    /// <summary>
    /// Half-extent of the eye's field of view, in tiles.
    ///
    /// Deliberately matches <c>look {"expand":0}</c>: it is the same field, and a mismatch would
    /// mean the agent sees an event somewhere the overview would show nothing — or the other way
    /// around.
    /// </summary>
    public static readonly CVarDef<float> ObserveRange =
        CVarDef.Create("ai.observe_range", 8.5f, CVar.SERVERONLY);

    /// <summary>
    /// Which observation labels are enabled. Empty — all of them.
    ///
    /// A comma-separated list (<c>урон,выстрел,вложил</c> or <c>damage,shot,inserted</c>), a knob
    /// for when combat reveals that some kind of event produces a stream without any benefit.
    /// Names match the agent's prompt language; the other language of the same label is accepted
    /// too. Narrowed by a console command rather than a deployment.
    /// </summary>
    public static readonly CVarDef<string> ObserveKinds =
        CVarDef.Create("ai.observe_kinds", string.Empty, CVar.SERVERONLY);

    /// <summary>
    /// Whether to account for walls during observation.
    ///
    /// Off, and that is a deliberate concession. The strict check is
    /// <c>StationAiVisionSystem.IsAccessible</c>, and it unrolls three hundred tiles and makes a
    /// broadphase query for each. On a rare call (one door, one click) that is unnoticeable; on a
    /// stream of events it is a return to what once made <c>look</c> hold up a tick for a second.
    ///
    /// The price of leaving it off is named plainly: within <see cref="ObserveRange"/> the agent
    /// will notice what happens behind a wall, whereas a human in its place would see the wall. On
    /// adds a third gate stage with a per-tile memo for one tick and a cap on checks per tick.
    /// </summary>
    public static readonly CVarDef<bool> ObserveOcclusion =
        CVarDef.Create("ai.observe_occlusion", false, CVar.SERVERONLY);

    /// <summary>
    /// How many observation lines the queue holds at once.
    ///
    /// Not about economy but about eviction order: the overall queue ceiling drops the oldest entry
    /// regardless of kind, and a stream of OBSERVED would push a radio call out of it. This ceiling
    /// trims only the oldest OBSERVED entry, so the agent cannot be silenced by commotion in frame.
    /// </summary>
    public static readonly CVarDef<int> ObserveBuffer =
        CVarDef.Create("ai.observe_buffer", 400, CVar.SERVERONLY);

    /// <summary>
    /// Ceiling on strict visibility checks per tick; only applies when
    /// <see cref="ObserveOcclusion"/> is on.
    ///
    /// Insurance, not a tuning knob. The per-tile memo already collapses a fight on one tile into a
    /// single check, but it costs nothing to imagine a load with dozens of events in frame per tick
    /// — and each check is hundreds of broadphase queries. Beyond the ceiling, events are skipped,
    /// and the number skipped goes to the log: silently losing observations is worse than losing
    /// them loudly.
    /// </summary>
    public static readonly CVarDef<int> ObserveMaxChecksPerTick =
        CVarDef.Create("ai.observe_max_checks_per_tick", 4, CVar.SERVERONLY);

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
    /// Whether to read the prototype overlay from <c>&lt;ai.data_dir&gt;/config.d/*.yml</c>.
    ///
    /// <para>
    /// On, because an empty or missing directory is exactly the same behaviour as before the
    /// overlay existed: the build runs on the prototypes from <c>Resources/</c>. Parsing is per
    /// file, and a broken file does not cancel the rest
    /// (<see cref="Content.Server.AiAgent.Config.AiConfigOverlay"/>).
    /// </para>
    /// <para>
    /// It is worth turning off in exactly one case: when the server behaves differently from what
    /// is written in <c>Resources/</c>, and you need to DISTINGUISH an overlay edit from a code
    /// edit. <c>false</c> plus a restart returns the build to what is in the repository, without
    /// touching the files in <c>ai_data/</c>. Toggling it live is useless — the prototypes are
    /// already loaded; for a live reload there is <c>aiagent config reload</c>.
    /// </para>
    /// </summary>
    public static readonly CVarDef<bool> ConfigOverlay =
        CVarDef.Create("ai.config_overlay", true, CVar.SERVERONLY);

    // ------------------------------------------------- backup power without engineers
    //
    // Placed here for the same reason as ai.station_name below: the fork owns exactly one
    // [CVarDefs] class, and starting a second one for three lines would mean dropping a new file
    // into someone else's tree.

    /// <summary>
    /// Whether to deploy a backup generator when there are no engineers on shift.
    ///
    /// A switch for the same reason as <see cref="LookFast"/>: the server is public, a rebuild kicks
    /// everyone, so a rollback has to be a console command, not a deployment.
    /// </summary>
    public static readonly CVarDef<bool> BackupPower =
        CVarDef.Create("ai.backup_power", true, CVar.SERVERONLY);

    /// <summary>
    /// Backup circuit power, watts.
    ///
    /// <para>
    /// Sixty kilowatts is a figure from BEFORE measurement, not after, and that matters to know. All
    /// the reference numbers in the upstream guidebook ("batteries will last 5-10 minutes", hence
    /// 340-670kW of consumption) are written for a full crew. How much an empty station draws is
    /// unknown: a lamp takes 5W, and on a one-person shift almost all the equipment is asleep. It
    /// needs to be measured with the <c>powerstat</c> console command in a live round and set with
    /// a x1.5 margin.
    /// </para>
    /// <para>
    /// For comparison: the solar array on the Packed map is 98kW nominal (and zero on the main grid,
    /// because it is not connected to it), PACMAN is 30kW, SuperPACMAN is 50, and the upstream
    /// DebugGenerator is 300.
    /// </para>
    /// </summary>
    public static readonly CVarDef<int> BackupPowerWatts =
        CVarDef.Create("ai.backup_power_watts", 60000, CVar.SERVERONLY);

    /// <summary>
    /// Multiplier on top of the backup circuit's power.
    ///
    /// <para>
    /// Added because the very first live round exposed a hole: known stations' power lives in a
    /// prototype (<c>backup_power.yml</c>), and prototypes are read at process start — meaning there
    /// was no way to tweak the number on a live server, and it would have taken a rebuild and
    /// kicking everyone playing. The multiplier applies to ANY source — both the table and
    /// <see cref="BackupPowerWatts"/> — and is read at job assignment, so it takes effect starting
    /// with the next round.
    /// </para>
    /// <para>
    /// Proportions between stations are preserved: the table remains a relative scale, and the
    /// multiplier shifts it as a whole. If it turns out that the "APC x 1200" proxy underestimates
    /// everywhere by the same amount, it is fixed with one command instead of thirteen edits.
    /// </para>
    /// </summary>
    public static readonly CVarDef<float> BackupPowerScale =
        CVarDef.Create("ai.backup_power_scale", 1f, CVar.SERVERONLY);

    /// <summary>
    /// Which department counts as the "engineering shift".
    ///
    /// Read from the department prototype rather than a role-by-role list in code: a fork that adds
    /// its own engineering job gets counted automatically. This also lays the groundwork for
    /// porting the addon to a different fork — there the department's roster is almost certainly
    /// different.
    /// </summary>
    public static readonly CVarDef<string> BackupPowerDepartment =
        CVarDef.Create("ai.backup_power_department", "Engineering", CVar.SERVERONLY);

    /// <summary>
    /// Station name. Empty — as in vanilla: the map's generator, "TG Box Station 14-Alpha".
    ///
    /// Lives under the <c>ai.</c> namespace even though it has nothing to do with the agent. The
    /// fork owns exactly one <c>[CVarDefs]</c> class, and starting a second one for a single line
    /// would be worse than putting it here with this explanation: any other option would mean a new
    /// file in someone else's tree.
    /// </summary>
    public static readonly CVarDef<string> StationName =
        CVarDef.Create("ai.station_name", "", CVar.SERVERONLY);

    // ----------------------------------------------------------------- debug

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
    ///
    /// <para>
    /// 2048, not the previous 512, because there can now be up to four agents: four times as many
    /// frames, and a compaction burst is four <c>history.replaced</c> events with full bodies, which
    /// blows through the ring in one pass. The overflow symptom is misleading: the client does not
    /// lose data, it re-fetches it, and it looks like "the debugger keeps blinking for no reason".
    /// </para>
    /// </summary>
    public static readonly CVarDef<int> DebugRing =
        CVarDef.Create("ai.debug_ring", 2048, CVar.SERVERONLY);

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

    // ------------------------------------------------------------- rogue AI mode

    /// <summary>
    /// Whether to grant the AI doors it does not touch by default: blast doors, shutters, some
    /// external airlocks.
    ///
    /// All three knobs below <b>override the rule prototype</b> rather than add to it: what is on
    /// in the prototype can be turned off from here, but not the other way around. That is by
    /// design — this is an emergency brake on a live server, not a second set of mode settings.
    /// Read at job assignment, so they take effect starting with the next round.
    /// </summary>
    public static readonly CVarDef<bool> RogueGrantDoors =
        CVarDef.Create("ai.rogue_grant_doors", true, CVar.SERVERONLY);

    /// <summary>Whether to grant access to everything with an interface: consoles, valves, panels.</summary>
    public static readonly CVarDef<bool> RogueGrantConsoles =
        CVarDef.Create("ai.rogue_grant_consoles", true, CVar.SERVERONLY);

    /// <summary>Whether to grant turrets and their control panels.</summary>
    public static readonly CVarDef<bool> RogueGrantTurrets =
        CVarDef.Create("ai.rogue_grant_turrets", true, CVar.SERVERONLY);

    /// <summary>
    /// Whether to deploy the support borgs listed in the rule prototype onto the station.
    ///
    /// The same emergency brake: what is listed in the prototype can be turned off from here,
    /// turning on what is not enabled cannot. A separate knob from access granting because they
    /// fail differently too: access can be left in place while the robots are removed, if the
    /// evening turns out too hard for the crew.
    /// </summary>
    public static readonly CVarDef<bool> RogueSupportBorgs =
        CVarDef.Create("ai.rogue_support_borgs", true, CVar.SERVERONLY);

    /// <summary>
    /// In the open mode, also grant Assistant to those who asked to "stay in the lobby if the job is
    /// taken".
    ///
    /// Without this, closing jobs selectively locks out part of the playerbase — with no reason
    /// visible from their point of view. Turned off if it turns out the cure is worse than the
    /// disease: then such players simply stay in the lobby, as they asked.
    /// </summary>
    public static readonly CVarDef<bool> RogueForceOverflow =
        CVarDef.Create("ai.rogue_force_overflow", true, CVar.SERVERONLY);

    // ------------------------------------------------------------------ script mode

    /// <summary>
    /// Tools go through a Lua wrapper instead of separate model calls.
    ///
    /// <para>
    /// Why. A measurement of a live borg run: 661 model calls for 680 tool calls — exactly one
    /// round trip through the LLM per elementary action, at 14 seconds and 41k prompt tokens for
    /// "step onto the tile". Assembling an AME does not fit into a round under that arithmetic. In
    /// script mode the model writes a program, and the "walk there, pick up, carry, unpack" loop
    /// costs one call.
    /// </para>
    /// <para>
    /// The mode is a property of the agent, and there is exactly one of it: the tool set is either
    /// classic or scripted. Toggled per body with the <c>AgentBody.ScriptMode</c> field, so the core
    /// can be kept on the classic set while a borg runs on scripts, for comparison.
    /// </para>
    /// <para>
    /// <b>Since 20.08.2026 the default is enabled.</b> An owner's decision. The previous
    /// <c>false</c> was new-feature caution, not a conclusion from measurements: the measurements
    /// actually argue against it — one round trip through the model per tile step costs 14 seconds
    /// and 41k tokens, and across four agents at once that is no longer "more expensive" but
    /// "does not fit in a round". The rollback is <c>cvar ai.script_mode false</c> on a live server,
    /// no rebuild; it takes effect starting with the next claimed session, because the mode is
    /// decided once when the body is assembled (see <c>AiBorgSystem.Prompt</c>).
    /// </para>
    /// </summary>
    public static readonly CVarDef<bool> ScriptMode =
        CVarDef.Create("ai.script_mode", true, CVar.SERVERONLY);

    /// <summary>
    /// Language of the agent's prompt, observations and tool replies: <c>ru</c> or <c>en</c>.
    ///
    /// Frozen on the body at session start, same as <see cref="ScriptMode"/>: the frozen prefix,
    /// the tool schemas and the JSON keys the Lua prelude reads must stay one language for the
    /// whole session. Flip it on a live server with <c>cvar ai.language en</c>; it takes effect
    /// on the next claimed session, no rebuild. The agent speaks the language of its prompt.
    /// SOUL.md, CURATOR.md and the skill library are files — write them in the same language.
    /// </summary>
    public static readonly CVarDef<string> Language =
        CVarDef.Create("ai.language", "ru", CVar.SERVERONLY);

    /// <summary>
    /// How long to wait for a script before letting it go into the background.
    ///
    /// A short script must reply within the same call — otherwise the model pays an extra round
    /// trip for "look around". A long one goes to the background and delivers its result later as
    /// an observation.
    /// </summary>
    public static readonly CVarDef<int> ScriptForegroundMs =
        CVarDef.Create("ai.script_foreground_ms", 1000, CVar.SERVERONLY);

    /// <summary>
    /// How many scripts may run at once.
    ///
    /// The agent has one body, and two scripts both moving it is not concurrency but a fight over
    /// its legs. Two leaves room for a watching script alongside a working one.
    /// </summary>
    public static readonly CVarDef<int> ScriptMaxProcesses =
        CVarDef.Create("ai.script_max_processes", 2, CVar.SERVERONLY);

    /// <summary>
    /// Ceiling on a script's lifetime in real seconds — unlike alarms, which live in round time.
    /// Round time stops on pause, and a round-time ceiling would never arrive exactly when it is
    /// needed.
    /// </summary>
    public static readonly CVarDef<int> ScriptMaxSeconds =
        CVarDef.Create("ai.script_max_seconds", 300, CVar.SERVERONLY);

    /// <summary>How many tools a single script may call. A fuse, not a pacing knob.</summary>
    public static readonly CVarDef<int> ScriptMaxCalls =
        CVarDef.Create("ai.script_max_calls", 400, CVar.SERVERONLY);

    /// <summary>Ceiling on Lua instructions: the only defence against <c>while true do end</c>.</summary>
    public static readonly CVarDef<int> ScriptMaxSteps =
        CVarDef.Create("ai.script_max_steps", 5_000_000, CVar.SERVERONLY);

    /// <summary>How many output lines to keep per process; older ones are evicted with a loss marker.</summary>
    public static readonly CVarDef<int> ScriptOutputLines =
        CVarDef.Create("ai.script_output_lines", 200, CVar.SERVERONLY);

    /// <summary>
    /// End the round when the last player has left the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, an empty server stays in the same round forever, and the first person to join
    /// a day later does not get a fresh shift but the cold corpse of the previous one: the station
    /// depressurized, the crew dead, the rogue AI already victorious. Formally the round is running;
    /// there is nothing to play in it.
    /// </para>
    /// <para>
    /// Tokens are not burned regardless: <c>game.auto_pause_empty</c> halts the simulation, and
    /// building an observation while paused returns "nothing to do" (see
    /// <c>BuildObservationAsync</c>). So this setting is not about spend, but about the next player
    /// getting a clean shift.
    /// </para>
    /// </remarks>
    public static readonly CVarDef<bool> EndRoundWhenEmpty =
        CVarDef.Create("ai.end_round_when_empty", true, CVar.SERVERONLY);

    /// <summary>
    /// End the round when the station AI is killed. Only applies in rogue AI modes.
    /// </summary>
    /// <remarks>
    /// The exact same reasoning as the nuclear bomb in the upstream operative mode: there is a
    /// single antagonist, and once it is gone, there is nothing left to play for. A crew with no
    /// clearances, a dead AI and three inert robots would simply wait for the shuttle. Outside rogue
    /// AI modes the check does not apply — in an ordinary shift, the AI dying is an incident, not
    /// the end of the story.
    /// </remarks>
    public static readonly CVarDef<bool> EndRoundOnAiDeath =
        CVarDef.Create("ai.end_round_on_ai_death", true, CVar.SERVERONLY);

    /// <summary>
    /// Keep robot bodies outside the usual replication range limits (PVS).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>OFF, AND THIS IS A MEASUREMENT, NOT CAUTION.</b> The change was born on 20.08.2026 as a
    /// cure for a full-resync loop and did exactly the opposite. The same player, two rounds in a
    /// row: 11 resyncs over 10,100 ticks without it (1.1 per thousand) against 89 over 5,100 with it
    /// (17.4 per thousand) — <b>sixteen times worse</b>. Median client lag grew from 10 ticks to 36.
    /// In round 162's log the first resync lands eighteen lines after body claim and does not stop
    /// after that, even though the robots did not take a single step for the whole round.
    /// </para>
    /// <para>
    /// <b>Why it got worse.</b> Permanent replication does not remove the entity's entry into the
    /// visibility zone — it makes that entry eternal. Reading <c>PvsSystem.Overrides.cs</c>: the
    /// list of permanent entities is expanded together with its WHOLE subtree (26 entities for an
    /// engineering body — the modules and the items in their hands), iterated for every session,
    /// processed BEFORE ordinary chunks (<c>PvsSystem.cs:321</c> versus <c>:324</c>) and — unlike
    /// <c>AddPvsChunk</c> — has neither a root check nor a budget-exhaustion exit. That means the
    /// robots' subtrees eat the entry budget first, pushing the rest of the station out of state.
    /// </para>
    /// <para>
    /// The field is left in place deliberately: the bench needs a negative control. A reproduction
    /// that cannot show a regression from a change known to be harmful proves nothing about a
    /// beneficial one either.
    /// </para>
    /// </remarks>
    public static readonly CVarDef<bool> BorgPvsOverride =
        CVarDef.Create("ai.borg_pvs_override", false, CVar.SERVERONLY);

    /// <summary>
    /// Print robot movement steps to the log as <c>NET TRACE kind=borg_move</c> lines. Zero — stay
    /// silent.
    /// </summary>
    /// <remarks>
    /// Used to read the engine's own <c>net.pvs_trace</c> from the fork's PVS patch. The engine
    /// patches were removed on 30.08.2026 for a measurement against clean upstream, and that cvar
    /// disappeared with them — but the movement trace is needed regardless of whether the engine is
    /// patched.
    /// </remarks>
    public static readonly CVarDef<int> BorgMoveTrace =
        CVarDef.Create("ai.borg_move_trace", 0, CVar.SERVERONLY);
}
