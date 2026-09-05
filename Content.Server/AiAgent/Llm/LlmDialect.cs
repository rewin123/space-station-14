namespace Content.Server.AiAgent.Llm;

/// <summary>
/// Which request fields a provider is even capable of accepting.
///
/// <para>
/// One <see cref="ChatRequestDto"/> serves everyone, and historically it sent the union of
/// llama.cpp's and DeepSeek's extensions at once: <c>top_k</c> and <c>min_p</c> (not OpenAI
/// parameters), <c>cache_prompt</c> and <c>id_slot</c> (llama-server only), <c>thinking</c>
/// (DeepSeek only). While there was only one endpoint, this worked: llama-server silently ignores
/// unfamiliar fields. A strict API doesn't behave that way — it responds with 400, and before this
/// table there was no way to tell "the provider is down" apart from "the provider didn't understand
/// the fourth field".
/// </para>
/// <para>
/// The table describes <b>what can be sent</b>, not what the model is capable of. Whether a
/// particular model can think is decided by its own config; here the only question is whether the
/// endpoint survives the field in the body.
/// </para>
/// </summary>
public enum LlmDialect
{
    /// <summary>llama.cpp / llama-server directly or through llama-swap.</summary>
    LlamaCpp,

    /// <summary>api.deepseek.com — OpenAI plus its own <c>thinking</c> object.</summary>
    DeepSeek,

    /// <summary>A strict OpenAI-compatible endpoint: only what OpenAI documents.</summary>
    OpenAiCompat,
}

/// <summary>
/// The rules from <see cref="LlmDialect"/> in executable form.
///
/// A separate type rather than properties on the prototype, for exactly one reason: the
/// <c>LlmRouterTests</c> test checks them against the serialized request body, and the rule must
/// live in one place, not be duplicated between the prototype and the client.
/// </summary>
public static class LlmDialectRules
{
    /// <summary><c>top_k</c> and <c>min_p</c> — llama.cpp samplers, OpenAI has no such parameters.</summary>
    public static bool AllowsSamplerExtras(LlmDialect dialect) => dialect == LlmDialect.LlamaCpp;

    /// <summary><c>cache_prompt</c> — a llama.cpp extension.</summary>
    public static bool AllowsCachePrompt(LlmDialect dialect) => dialect == LlmDialect.LlamaCpp;

    /// <summary><c>id_slot</c> — pinning to a llama-server slot.</summary>
    public static bool AllowsIdSlot(LlmDialect dialect) => dialect == LlmDialect.LlamaCpp;

    /// <summary>The <c>thinking</c> object — a DeepSeek extension; their own SDK hides it inside <c>extra_body</c>.</summary>
    public static bool AllowsThinking(LlmDialect dialect) => dialect == LlmDialect.DeepSeek;

    /// <summary>
    /// Top-level <c>reasoning_effort</c> — the OpenAI form.
    ///
    /// Sending it to a local llama.cpp is pointless and harmful: llama-server accepts the field in
    /// the body and silently ignores it, because the effort level there is set by the
    /// <c>--chat-template-kwargs</c> launch flag. In other words, the value set here would look like
    /// it works and do nothing — the worst kind of setting.
    /// </summary>
    public static bool AllowsReasoningEffort(LlmDialect dialect) => dialect == LlmDialect.OpenAiCompat;
}
