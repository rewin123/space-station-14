using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// Wire DTOs for the OpenAI-compatible chat-completions API.
///
/// Every property here is declared in the exact order it must appear on the wire, because
/// System.Text.Json serialises in declaration order and llama.cpp reuses its KV cache only up to
/// the first divergent <em>token</em>. A reordered field is not a cosmetic difference — it moves
/// the divergence point to the top of the request and costs a full prefill on every single turn.
/// For the same reason nothing here is a <c>Dictionary&lt;string, object&gt;</c>: dictionary
/// iteration order is an implementation detail.
/// </summary>
public static class LlmJson
{
    /// <summary>
    /// The single serializer used on every path that can produce a request body. Sharing one
    /// instance is what guarantees a conversation serialises identically whether it came from
    /// memory or from a reloaded session snapshot.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // No indentation: the bytes we build are the bytes we send.
        WriteIndented = false,

        // Cyrillic must survive as UTF-8, not as \uXXXX escapes.
        //
        // The default encoder escapes every non-ASCII character, which for a Russian-speaking
        // agent means each word arrives as a run of six-character escape sequences: roughly six
        // times the bytes, tokenized as punctuation soup rather than as words. Caught by the
        // Inspect_ReportsCutAiWire benchmark, where a perfectly correct answer came back as
        // "\u043F\u0440\u043E\u0432\u043E\u0434 \u043F\u0435\u0440\u0435\u0440\u0435\u0437\u0430\u043D".
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writer options matching <see cref="Options"/>, for hand-written JSON.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };
}

public sealed class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCallDto>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    public static ChatMessageDto System(string text) => new() { Role = "system", Content = text };
    public static ChatMessageDto User(string text) => new() { Role = "user", Content = text };

    public static ChatMessageDto Tool(string toolCallId, string json) =>
        new() { Role = "tool", ToolCallId = toolCallId, Content = json };
}

public sealed class ToolCallDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionCallDto Function { get; set; } = new();
}

public sealed class FunctionCallDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw JSON, echoed back verbatim — re-serialising it would risk changing bytes.</summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

public sealed class ToolDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ToolFunctionDto Function { get; set; } = new();
}

public sealed class ToolFunctionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Parsed once from a hand-written canonical JSON schema string. A <see cref="JsonNode"/>
    /// round-trips in document order, so the schema serialises byte-identically every time.
    /// </summary>
    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; set; }
}

/// <summary>Request body. Property order is the wire order — see the note on <see cref="LlmJson"/>.</summary>
public sealed class ChatRequestDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessageDto> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    public List<ToolDto>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    /// <summary>
    /// Kept false on purpose. Several Qwen templates advertise parallel calls but emit malformed
    /// multi-call blocks, and one call per turn keeps the conversation append-only and predictable.
    /// </summary>
    [JsonPropertyName("parallel_tool_calls")]
    public bool ParallelToolCalls { get; set; }

    /// <summary>
    /// Also false on purpose: llama.cpp emits tool calls only at the end of the stream in some
    /// template/parser combinations, so a streaming client sees raw XML it cannot parse.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float TopP { get; set; }

    /// <summary>
    /// llama.cpp's own sampler, not an OpenAI parameter. That's exactly why it's nullable.
    ///
    /// <c>WhenWritingNull</c> can't drop a non-nullable value, so before profiles existed,
    /// <c>top_k</c> and <c>min_p</c> went out on every request — including the ones addressed to a
    /// strict API that answers an unknown field with a 400. Who receives them is decided by
    /// <see cref="LlmDialectRules.AllowsSamplerExtras"/>.
    /// </summary>
    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    /// <inheritdoc cref="TopK"/>
    [JsonPropertyName("min_p")]
    public float? MinP { get; set; }

    /// <summary>
    /// Null means "no ceiling", and that is the default.
    ///
    /// A cap sized for the answer truncates a reasoning model mid-thought, and a completion cut off
    /// before it produced either text or a tool call comes back empty — which used to poison the
    /// conversation outright. Letting the provider apply its own limit costs nothing here: the
    /// agent's replies are a sentence or two, and what it spends is decided by
    /// <c>ai.thinking_effort</c>, not by where the budget happens to run out.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>
    /// llama.cpp extension: reuse the slot's prefix KV cache. Null — don't send it at all, because
    /// for every other provider it's an unknown field.
    /// </summary>
    [JsonPropertyName("cache_prompt")]
    public bool? CachePrompt { get; set; }

    /// <summary>
    /// DeepSeek's thinking switch. Null leaves the model on its own default, and is serialised
    /// away — a local llama.cpp never sees a field it does not know.
    ///
    /// The default matters here: on <c>deepseek-v4-flash</c> thinking is on at effort
    /// <c>high</c> unless the request says otherwise, and that is most of the delay between the
    /// crew asking something and hearing an answer. Turning it off entirely bought the latency
    /// back and cost noticeably in answer quality, so the useful setting is the middle one — think,
    /// but briefly.
    /// </summary>
    [JsonPropertyName("thinking")]
    public ThinkingDto? Thinking { get; set; }

    /// <summary>
    /// Pin the agent to one slot. <c>--slot-prompt-similarity</c> routing is documented as
    /// behaving unpredictably, and a slot switch throws away the whole prefix cache.
    /// </summary>
    [JsonPropertyName("id_slot")]
    public int? IdSlot { get; set; }

    /// <summary>
    /// OpenAI's shape for the same thing DeepSeek accepts as the <see cref="Thinking"/> object. Sent
    /// only to a strict OpenAI-compatible endpoint — sending both fields at once would mean a double
    /// setting with an unpredictable winner.
    ///
    /// It's placed last, and so doesn't shift any existing field: as long as the value is null, the
    /// request bytes to llama.cpp and to DeepSeek stay exactly what they were before profiles
    /// existed, and the prefix cache doesn't notice.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// vLLM / Qwen chat template: <c>enable_thinking: false</c> switches off reasoning on a model
    /// where it's enabled by default. Null — the field is omitted, and other profiles' bytes don't
    /// change.
    /// </summary>
    [JsonPropertyName("chat_template_kwargs")]
    public ChatTemplateKwargsDto? ChatTemplateKwargs { get; set; }
}

/// <summary>Chat template arguments understood by vLLM with Qwen3.</summary>
public sealed class ChatTemplateKwargsDto
{
    [JsonPropertyName("enable_thinking")]
    public bool EnableThinking { get; set; }
}

/// <summary>
/// The <c>thinking</c> object DeepSeek expects. Its own SDK hides this under <c>extra_body</c>;
/// over plain HTTP it is a top-level field like any other.
/// </summary>
public sealed class ThinkingDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "disabled";

    /// <summary>
    /// <c>low</c>, <c>high</c> or <c>max</c> on <c>deepseek-v4-flash</c>; null when thinking is off.
    /// <c>medium</c> and <c>xhigh</c> are accepted but fold into <c>high</c>.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

/// <summary>What the agent loop actually consumes from a completion.</summary>
/// <param name="Profile">
/// Who answered. Needed in the log: with a fallback chain, there is otherwise no way to figure out
/// after the fact whose output this was — and it's exactly on bad turns that you want to know
/// whether it was the local model or not.
/// </param>
/// <param name="ReportsCache">
/// Whether this provider reports the share of the prompt served from cache. For one that doesn't
/// report it, a zero <see cref="CachedTokens"/> means "unknown", not "cache broken", and the
/// <see cref="Content.Server.AiAgent.Context.CacheMetrics"/> alarm must stay silent.
/// </param>
public sealed record LlmResponse(
    string? Content,
    IReadOnlyList<ToolCallDto> ToolCalls,
    int PromptTokens,
    int CachedTokens,
    int CompletionTokens,
    double DurationSeconds,
    string? FinishReason = null,
    int ReasoningTokens = 0,
    string? Profile = null,
    bool ReportsCache = true)
{
    /// <summary>
    /// The completion was cut off mid-sentence by max_tokens.
    ///
    /// Invisible until a reasoning model made it common: DeepSeek spends its budget thinking before
    /// it writes, so a limit tuned for a non-reasoning model truncates the tool call itself — and a
    /// half-written call reads downstream as a model that simply behaved strangely.
    /// </summary>
    public bool Truncated => FinishReason == "length";

    /// <summary>
    /// Share of the prompt served from cache. Below ~0.9 on a turn that did not just compact
    /// means the prefix drifted — the canary for an accidental timestamp in the system prompt.
    /// </summary>
    public double CacheRatio => PromptTokens <= 0 ? 0.0 : (double)CachedTokens / PromptTokens;

    public static LlmResponse Empty { get; } =
        new(null, Array.Empty<ToolCallDto>(), 0, 0, 0, 0.0);
}
