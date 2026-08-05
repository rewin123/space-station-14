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
    /// Ceiling on one completion.
    ///
    /// 640 is sized for a non-reasoning model, where the whole budget goes into the answer. A
    /// reasoning model spends most of it thinking first — DeepSeek measured at 215 tokens of
    /// deliberation before a one-sentence radio call — so on one of those this has to go up several
    /// times over or the tool call itself gets truncated mid-argument. The client now warns when
    /// that happens instead of leaving it to look like the model behaving oddly.
    /// </summary>
    public static readonly CVarDef<int> MaxTokens =
        CVarDef.Create("ai.max_tokens", 3000, CVar.SERVERONLY);

    /// <summary>A remote API can be slow; llama-swap may need to load the model from disk.</summary>
    public static readonly CVarDef<float> RequestTimeout =
        CVarDef.Create("ai.request_timeout", 180f, CVar.SERVERONLY);

    // ------------------------------------------------------------------ loop

    /// <summary>N — how often accumulated observations are handed to the model as one user message.</summary>
    public static readonly CVarDef<float> TickSeconds =
        CVarDef.Create("ai.tick_seconds", 8f, CVar.SERVERONLY);

    /// <summary>Back-off when nothing at all happened, so an empty station costs nothing.</summary>
    public static readonly CVarDef<float> TickSecondsIdle =
        CVarDef.Create("ai.tick_seconds_idle", 25f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MaxToolCallsPerTurn =
        CVarDef.Create("ai.max_tool_calls_per_turn", 6, CVar.SERVERONLY);

    /// <summary>Observations buffered before the oldest are dropped (and the drop is reported).</summary>
    public static readonly CVarDef<int> ObsBuffer =
        CVarDef.Create("ai.obs_buffer", 200, CVar.SERVERONLY);

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

    public static readonly CVarDef<int> CompactHigh =
        CVarDef.Create("ai.compact_high", 90000, CVar.SERVERONLY);

    public static readonly CVarDef<int> CompactKeepTail =
        CVarDef.Create("ai.compact_keep_tail", 30000, CVar.SERVERONLY);

    /// <summary>Hysteresis: compaction re-arms only after usage falls back below this.</summary>
    public static readonly CVarDef<int> CompactLow =
        CVarDef.Create("ai.compact_low", 45000, CVar.SERVERONLY);

    // ----------------------------------------------------------- diagnostics

    /// <summary>
    /// How many rows a single look may return.
    ///
    /// Generous on purpose: the model's context is large, and blindness costs far more than
    /// verbosity — an agent that lists sixty of four hundred things tells the crew "there is no
    /// SMES here" with complete confidence. Rows come out nearest-first, so a cut removes the far
    /// end of the room, and the answer says out loud that it was cut.
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
