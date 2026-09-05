using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Llm;

/// <summary>How a call to this profile is paid for — this changes how the router reacts to failure.</summary>
public enum LlmQuotaKind
{
    /// <summary>Our own hardware. Cannot be exhausted, nothing to pay.</summary>
    Free,

    /// <summary>Pay per token. Exhaustion means an empty balance, not a window.</summary>
    Metered,

    /// <summary>
    /// Subscription. Exhaustion is a <b>normal state</b>, not an error.
    ///
    /// Codex counts its quota in calls per window (250–2000 to Luna per five hours; a weekly ceiling
    /// exists, but OpenAI doesn't publish the numbers), Grok Build has one weekly pool shared across
    /// all products, and xAI publishes nothing at all. There is no planning ahead — the only option
    /// is to react to a 429 with a long sleep until reset and measure our own spend.
    /// </summary>
    Subscription,
}

/// <summary>What outgoing traffic for this profile goes through.</summary>
public enum LlmProxyMode
{
    /// <summary>Direct. Mandatory for loopback: a local port won't be reachable through a German exit.</summary>
    None,

    /// <summary>Through the SOCKS proxy from <c>ai.llm_socks_proxy</c>.</summary>
    Socks,
}

/// <summary>Protocol shape. Set up for future growth — see the comment on <see cref="AiLlmProfilePrototype.Transport"/>.</summary>
public enum LlmTransport
{
    /// <summary>Plain <c>POST /chat/completions</c>, non-streaming.</summary>
    ChatCompletions,
}

/// <summary>Where to learn the real context size from.</summary>
public enum LlmCtxProbe
{
    /// <summary>Don't ask, take <see cref="AiLlmProfilePrototype.CtxLimit"/>.</summary>
    None,

    /// <summary><c>GET /props?model=…</c> — only llama-server knows this (and llama-swap as a proxy).</summary>
    Props,
}

/// <summary>
/// A single model provider: where to reach it, how it's paid for, and which fields it will tolerate.
///
/// <para>
/// <b>A prototype, not a CVar string.</b> There are four profiles, each with a dozen fields; in TOML
/// that would become either dozens of flat keys like <c>ai.deepseek_ctx_limit</c>, or JSON stuffed
/// into a string. A prototype is validated on startup, survives an edit without a rebuild, and
/// repeats a pattern already established in this fork — <see cref="AiBackupPowerPrototype"/> with its
/// table in <c>Resources/Prototypes/_AiAgent/backup_power.yml</c>.
/// </para>
/// <para>
/// <b>There can be no secrets here.</b> <c>Content.Server/Acz/ContentMagicAczProvider.cs</c> hands
/// out the entire <c>Resources/</c> folder to every player who connects — a key placed in this YAML
/// would go straight to the first person who joined. That's why <see cref="KeyFile"/> stores a
/// <b>file name</b> inside <c>ai_data/</c>, not the value itself.
/// </para>
/// <para>
/// Profile order is not set here but in <c>ai.llm_chain</c>: the chain is an operational decision
/// that needs to be changeable from a live server's console, not by editing data and restarting.
/// </para>
/// </summary>
[Prototype]
public sealed partial class AiLlmProfilePrototype : IPrototype
{
    /// <summary>Short name for <c>ai.llm_chain</c> and for <c>aiagent llm</c>: <c>local</c>, <c>codex</c>.</summary>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Base URL, already including <c>/v1</c>. For example <c>http://127.0.0.1:9292/v1</c>.</summary>
    [DataField(required: true)]
    public string Endpoint = string.Empty;

    /// <summary>Model name in the exact form the endpoint expects.</summary>
    [DataField(required: true)]
    public string Model = string.Empty;

    /// <summary>Which request fields the endpoint will tolerate. See <see cref="LlmDialect"/>.</summary>
    [DataField]
    public LlmDialect Dialect = LlmDialect.OpenAiCompat;

    /// <summary>
    /// Protocol shape. Right now everyone has the same one, and that's deliberate.
    ///
    /// The field is set up in advance so that a future in-house Responses API adapter can slot in as
    /// just another value, rather than as a rewrite of the router. For now, subscriptions go through
    /// a local bridge that translates the protocol itself, and from the game's point of view they
    /// are an ordinary OpenAI-compatible endpoint on loopback.
    /// </summary>
    [DataField]
    public LlmTransport Transport = LlmTransport.ChatCompletions;

    /// <summary>What we're paying with. Changes how long to sleep after a 429 and whether to track spend.</summary>
    [DataField]
    public LlmQuotaKind Quota = LlmQuotaKind.Metered;

    /// <summary>What traffic goes through. For loopback it must be <see cref="LlmProxyMode.None"/>.</summary>
    [DataField]
    public LlmProxyMode Proxy = LlmProxyMode.None;

    /// <summary>
    /// Name of the key file inside <c>ai.data_dir</c> — for example <c>deepseek.key</c>. Not the value.
    ///
    /// Empty — falls back to <c>ai.api_key</c>, so a single "endpoint from TOML" setup keeps working
    /// without any YAML at all.
    /// </summary>
    [DataField]
    public string KeyFile = string.Empty;

    /// <summary>Where to learn the context size from.</summary>
    [DataField]
    public LlmCtxProbe CtxProbe = LlmCtxProbe.None;

    /// <summary>
    /// Context size to use when there's no one to ask. 0 — unknown.
    ///
    /// Mandatory to set for everything except llama-server: without it,
    /// <c>EffectiveCompactHigh</c> silently falls back to the printed <c>ai.compact_high</c>, and on
    /// a model with a four-hundred-thousand-token context the agent compacts just as often as on the
    /// local one.
    /// </summary>
    [DataField]
    public int CtxLimit;

    /// <summary>Own compaction threshold. 0 — take <c>ai.compact_high</c>.</summary>
    [DataField]
    public int CompactHigh;

    /// <summary>Own request timeout, seconds. 0 — take <c>ai.request_timeout</c>.</summary>
    [DataField]
    public float TimeoutSeconds;

    /// <summary>
    /// Whether the provider reports how much of the prompt came from cache.
    ///
    /// Lying here is expensive in both directions. Setting <c>true</c> for a provider that reports
    /// nothing makes <see cref="Content.Server.AiAgent.Context.CacheMetrics"/> continuously log an
    /// ERROR of "prefix cache broken" — and devalues the alarm that exists to catch a real breakage.
    /// </summary>
    [DataField]
    public bool ReportsCache = true;

    /// <summary>
    /// Effort level for <c>thinking.reasoning_effort</c> (DeepSeek) or for top-level
    /// <c>reasoning_effort</c> (strict OpenAI). Empty — take <c>ai.thinking_effort</c>.
    ///
    /// Which of these actually goes on the wire is decided by the dialect: sending both fields at
    /// once is not allowed.
    /// </summary>
    [DataField]
    public string ReasoningEffort = string.Empty;

    // ------------------------------------------------------------------ money

    /// <summary>Price of a million input tokens on a cache miss, USD. 0 — don't track.</summary>
    [DataField]
    public float PriceInPer1M;

    /// <summary>Price of a million input tokens on a cache hit, USD.</summary>
    [DataField]
    public float PriceCachedInPer1M;

    /// <summary>Price of a million output tokens, USD.</summary>
    [DataField]
    public float PriceOutPer1M;

    // ------------------------------------------------------------------- quota

    /// <summary>
    /// Length of the quota window in hours — for accounting only, not for limiting.
    ///
    /// Five hours, because that's how long Codex's window is. We have no way to learn the vendor's
    /// ceiling, but we can compute our own spend over the same window and finally see whether our
    /// ~148 calls fit within the claimed 250–2000.
    /// </summary>
    [DataField]
    public float QuotaWindowHours = 5f;

    /// <summary>
    /// How long to sleep after a 429 when the provider didn't say when it resets. Seconds. 0 — take
    /// <c>ai.llm_quota_cooldown_seconds</c>.
    /// </summary>
    [DataField]
    public float QuotaCooldownSeconds;
}

/// <summary>
/// The same thing as <see cref="AiLlmProfilePrototype"/>, but without the prototype wrapping.
///
/// Exists for the sake of testability, and the reason is concrete: the prototype's <c>ID</c> has a
/// private setter, because the serializer fills it in — which means a profile can't be built from a
/// test, and verifying the failure chain would only be possible by starting a server with the full
/// set of prototypes. For logic where sleeps, quotas, and traversal order matter, that's an
/// unaffordable cost: such tests are slow, which is exactly why they don't get written.
///
/// It also happens to be the right boundary: the router needs ten fields, not a type from the data layer.
/// </summary>
public sealed record LlmProfileConfig(
    string Id,
    string Model,
    LlmQuotaKind Quota,
    int CompactHigh = 0,
    float QuotaWindowHours = 5f,
    float QuotaCooldownSeconds = 0f,
    float PriceInPer1M = 0f,
    float PriceCachedInPer1M = 0f,
    float PriceOutPer1M = 0f)
{
    public static LlmProfileConfig From(AiLlmProfilePrototype p) => new(
        p.ID,
        p.Model,
        p.Quota,
        p.CompactHigh,
        p.QuotaWindowHours,
        p.QuotaCooldownSeconds,
        p.PriceInPer1M,
        p.PriceCachedInPer1M,
        p.PriceOutPer1M);
}
