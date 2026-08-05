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

    public static readonly CVarDef<string> Endpoint =
        CVarDef.Create("ai.endpoint", "http://127.0.0.1:9292/v1", CVar.SERVERONLY);

    public static readonly CVarDef<string> Model =
        CVarDef.Create("ai.model", "qwen3.6-27b", CVar.SERVERONLY);

    public static readonly CVarDef<string> ApiKey =
        CVarDef.Create("ai.api_key", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>
    /// 0.3 rather than a default 0.7: measured on this exact model and quant, higher
    /// temperatures corrupt Cyrillic output (see the mcbot deployment on this box).
    /// </summary>
    public static readonly CVarDef<float> Temperature =
        CVarDef.Create("ai.temperature", 0.3f, CVar.SERVERONLY);

    public static readonly CVarDef<float> TopP =
        CVarDef.Create("ai.top_p", 0.85f, CVar.SERVERONLY);

    public static readonly CVarDef<int> TopK =
        CVarDef.Create("ai.top_k", 20, CVar.SERVERONLY);

    public static readonly CVarDef<float> MinP =
        CVarDef.Create("ai.min_p", 0.05f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MaxTokens =
        CVarDef.Create("ai.max_tokens", 640, CVar.SERVERONLY);

    /// <summary>llama-swap may need to load the model from disk on the first call.</summary>
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

    /// <summary>0 means: read the real n_ctx from the server's /props at startup.</summary>
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
}
