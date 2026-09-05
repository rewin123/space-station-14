using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Analyzers;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// Everything the client needs to know about one endpoint.
///
/// A separate record rather than a set of constructor arguments: there are now several providers,
/// each with its own proxy, its own dialect and its own timeout, and seven positional parameters in
/// a row is an invitation to swap two of them by mistake.
/// </summary>
public sealed record LlmEndpoint(
    string Id,
    string BaseUrl,
    string Model,
    string ApiKey,
    LlmDialect Dialect,
    TimeSpan Timeout,
    LlmProxyMode Proxy = LlmProxyMode.None,
    string SocksProxy = "",
    LlmCtxProbe CtxProbe = LlmCtxProbe.None,
    int CtxLimit = 0,
    bool ReportsCache = true);

/// <summary>
/// OpenAI-compatible client aimed at llama-swap / llama-server, DeepSeek and any strict
/// OpenAI-shaped endpoint. Which of the three it is talking to is decided by
/// <see cref="LlmEndpoint.Dialect"/>.
///
/// <c>Content.Server</c> is not sandbox-checked (<c>ServerOptions.Sandboxing = false</c>, and
/// upstream's own <c>SandboxTest</c> only inspects Content.Client and Content.Shared), so
/// <c>System.Net.Http</c> is legal here — and only here. Putting any of this in Content.Shared
/// would fail the build.
/// </summary>
public sealed class LlamaClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ISawmill _sawmill;
    private readonly LlmEndpoint _endpoint;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly LlmSampling _sampling;

    public LlmEndpoint Endpoint => _endpoint;

    public LlamaClient(LlmEndpoint endpoint, LlmSampling sampling, ISawmill sawmill)
    {
        _endpoint = endpoint;
        _baseUrl = endpoint.BaseUrl.TrimEnd('/');
        _model = endpoint.Model;
        _sampling = sampling;
        _sawmill = sawmill;

        _http = CreateHttp(endpoint, endpoint.Timeout);
    }

    /// <summary>
    /// An HTTP client for a single profile: proxy, timeout, key.
    ///
    /// <para>
    /// Public and static because this assembly has two consumers: the client itself and the endpoint
    /// check (<see cref="LlmProbe"/>). The check must go over the wire EXACTLY the way a live turn
    /// does — otherwise it checks something other than what actually plays: "curl works fine for me
    /// but the server is silent" is almost always about the proxy.
    /// </para>
    /// <para>
    /// The proxy is set per profile, and "no proxy" is not a default — it's a requirement. This
    /// machine exports HTTP_PROXY=http://127.0.0.1:10809 and ALL_PROXY=socks5h://127.0.0.1:10808
    /// globally, and a request to loopback that goes through the proxy simply hangs. Relying on
    /// NO_PROXY is not an option: HttpClient.DefaultProxy is read from the environment once at
    /// process startup, and wildcard semantics in NO_PROXY differ between runtimes. So local profiles
    /// run with the proxy forcibly disabled, and cloud profiles with an explicitly specified SOCKS
    /// proxy, and neither case depends on whatever happens to be in the service's environment.
    /// </para>
    /// </summary>
    public static HttpClient CreateHttp(LlmEndpoint endpoint, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        if (endpoint.Proxy == LlmProxyMode.Socks && !string.IsNullOrWhiteSpace(endpoint.SocksProxy))
        {
            // .NET 6+ understands socks4://, socks4a:// and socks5:// directly in WebProxy — no
            // separate SOCKS library is required.
            handler.Proxy = new WebProxy(endpoint.SocksProxy);
            handler.UseProxy = true;
        }
        else
        {
            handler.Proxy = null;
            handler.UseProxy = false;
        }

        var http = new HttpClient(handler) { Timeout = timeout };

        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        return http;
    }

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto>? tools,
        CancellationToken ct)
    {
        var dialect = _endpoint.Dialect;

        var request = new ChatRequestDto
        {
            Model = _model,
            Messages = new List<ChatMessageDto>(messages),
            Tools = tools is { Count: > 0 } ? new List<ToolDto>(tools) : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            ParallelToolCalls = false,
            Stream = false,
            Temperature = _sampling.Temperature,
            TopP = _sampling.TopP,

            // Below are fields OpenAI doesn't have. Each one is sent only to whoever understands it:
            // llama-server silently ignores an unfamiliar field, a strict API answers with 400, and
            // without this filter "the provider is down" was indistinguishable from "the provider
            // didn't understand the fourth field".
            TopK = LlmDialectRules.AllowsSamplerExtras(dialect) ? _sampling.TopK : null,
            MinP = LlmDialectRules.AllowsSamplerExtras(dialect) ? _sampling.MinP : null,

            MaxTokens = _sampling.MaxTokens > 0 ? _sampling.MaxTokens : null,
            CachePrompt = LlmDialectRules.AllowsCachePrompt(dialect) ? true : null,
            IdSlot = LlmDialectRules.AllowsIdSlot(dialect) ? _sampling.IdSlot : null,

            Thinking = LlmDialectRules.AllowsThinking(dialect)
                ? ThinkingRequest.Build(_sampling.ThinkingEffort)
                : null,

            ReasoningEffort = LlmDialectRules.AllowsReasoningEffort(dialect)
                ? ReasoningEffortRequest.Build(_sampling.ThinkingEffort)
                : null,

            // Qwen3 on vLLM thinks by default. reasoning_effort: off doesn't send the field at all —
            // that's not enough, the template will still open <think> on its own. An explicit
            // enable_thinking:false suppresses it. This field doesn't go out to other OpenAI-shaped
            // profiles (grok, codex): their effort isn't off, and WhenWritingNull drops the null
            // entirely.
            ChatTemplateKwargs = LlmDialectRules.AllowsReasoningEffort(dialect)
                ? ChatTemplateKwargsRequest.Build(_sampling.ThinkingEffort)
                : null,
        };

        var body = JsonSerializer.Serialize(request, LlmJson.Options);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var started = DateTime.UtcNow;
        using var response = await _http.PostAsync($"{_baseUrl}/chat/completions", content, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Include the body: llama-server puts the actual reason (bad template, context
            // overflow, unknown model alias) there, and without it every failure looks the same.
            throw new LlmHttpException(
                (int) response.StatusCode,
                Truncate(raw, 600),
                RetryAfterOf(response),
                $"HTTP {(int) response.StatusCode} from {_baseUrl}: {Truncate(raw, 600)}");
        }

        return Parse(raw, (DateTime.UtcNow - started).TotalSeconds);
    }

    /// <summary>
    /// When the provider allowed a retry, if it said so.
    ///
    /// For a subscription this is the most valuable field in the whole response: an exhausted quota
    /// is a state with a known end, and knowing it precisely means not spending the remainder on
    /// probing attempts. <c>Retry-After</c> per the standard arrives either as seconds or as an HTTP
    /// date; rate-limit-reset headers are written differently by everyone, so we take the first one
    /// we manage to parse.
    /// </summary>
    private static DateTime? RetryAfterOf(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry != null)
        {
            if (retry.Delta is { } delta)
                return DateTime.UtcNow + delta;
            if (retry.Date is { } date)
                return date.UtcDateTime;
        }

        foreach (var name in RateLimitResetHeaders)
        {
            if (!response.Headers.TryGetValues(name, out var values))
                continue;

            foreach (var value in values)
            {
                if (TryParseReset(value, out var when))
                    return when;
            }
        }

        return null;
    }

    private static readonly string[] RateLimitResetHeaders =
    {
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens",
        "x-ratelimit-reset",
        "ratelimit-reset",
    };

    /// <summary>
    /// Forms in which the reset time appears: "12s", "1m30s", "45" (seconds), and unix seconds.
    /// Anything that fails to parse is honestly reported as "unknown" rather than as zero.
    /// </summary>
    private static bool TryParseReset(string value, out DateTime when)
    {
        when = default;
        value = value.Trim();
        if (value.Length == 0)
            return false;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            // More than a year in seconds means this is a unix timestamp, not a delay.
            when = plain > 31_536_000
                ? DateTimeOffset.FromUnixTimeSeconds((long) plain).UtcDateTime
                : DateTime.UtcNow.AddSeconds(plain);
            return true;
        }

        double seconds = 0;
        var number = 0d;
        var seen = false;
        var hasNumber = false;

        foreach (var c in value)
        {
            if (char.IsDigit(c))
            {
                number = number * 10 + (c - '0');
                hasNumber = true;
                continue;
            }

            if (!hasNumber)
                return false;

            switch (char.ToLowerInvariant(c))
            {
                case 'h': seconds += number * 3600; break;
                case 'm': seconds += number * 60; break;
                case 's': seconds += number; break;
                default: return false;
            }

            seen = true;
            number = 0;
            hasNumber = false;
        }

        if (!seen)
            return false;

        when = DateTime.UtcNow.AddSeconds(seconds);
        return true;
    }

    private LlmResponse Parse(string raw, double seconds)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        string? text = null;
        var calls = new List<ToolCallDto>();

        string? finishReason = null;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            if (choices[0].TryGetProperty("finish_reason", out var finishEl)
                && finishEl.ValueKind == JsonValueKind.String)
            {
                finishReason = finishEl.GetString();
            }

            if (TryGetObject(choices[0], "message", out var message))
            {
                if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    text = contentEl.GetString();

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        if (!TryGetObject(tc, "function", out var fn))
                            continue;

                        calls.Add(new ToolCallDto
                        {
                            Id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                            Type = "function",
                            Function = new FunctionCallDto
                            {
                                Name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                                Arguments = fn.TryGetProperty("arguments", out var argEl) && argEl.ValueKind != JsonValueKind.Null
                                    ? (argEl.ValueKind == JsonValueKind.String ? argEl.GetString() ?? "{}" : argEl.GetRawText())
                                    : "{}",
                            },
                        });
                    }
                }
            }
        }

        var prompt = 0;
        var cached = 0;
        var completion = 0;
        var reasoning = 0;

        // Cache accounting, in order of preference. llama-server's non-standard `timings` block is
        // the most direct signal; `usage.prompt_tokens_details.cached_tokens` is the OpenAI-shaped
        // fallback; if neither exists the ratio is simply unavailable and the caller says so
        // rather than reporting a confident zero.
        if (TryGetObject(root, "usage", out var usage))
        {
            prompt = GetInt(usage, "prompt_tokens");
            completion = GetInt(usage, "completion_tokens");

            if (TryGetObject(usage, "prompt_tokens_details", out var details))
                cached = GetInt(details, "cached_tokens");

            // Reasoning models spend part of the completion budget thinking before they write, and
            // that share does not appear anywhere else. Without it, "out 300t" looks like a verbose
            // answer when it was in fact 215 tokens of deliberation and a sentence that got cut off.
            if (TryGetObject(usage, "completion_tokens_details", out var completionDetails))
                reasoning = GetInt(completionDetails, "reasoning_tokens");

            // DeepSeek reports its cache split under its own names.
            if (cached == 0)
                cached = GetInt(usage, "prompt_cache_hit_tokens");
        }

        if (TryGetObject(root, "timings", out var timings))
        {
            var cacheN = GetInt(timings, "cache_n");
            var promptN = GetInt(timings, "prompt_n");
            if (cacheN > 0 || promptN > 0)
            {
                cached = cacheN;
                if (prompt <= 0)
                    prompt = cacheN + promptN;
            }
        }

        if (finishReason == "length")
        {
            _sawmill.Warning(
                $"ответ обрезан по max_tokens ({completion}т, из них {reasoning}т размышлений) — " +
                "вызов инструмента мог не дописаться; подними ai.max_tokens");
        }

        return new LlmResponse(
            text, calls, prompt, cached, completion, seconds, finishReason, reasoning,
            _endpoint.Id, _endpoint.ReportsCache);
    }

    public async Task<int?> GetContextSizeAsync(CancellationToken ct)
    {
        // Only llama-server knows how to answer /props. For everyone else it's a 404, and previously
        // it would arrive, get logged as a warning, and leave the compaction threshold at the
        // printed ai.compact_high — meaning on a model with a four-hundred-thousand-token context
        // the agent would compact just as often as on the local one.
        if (_endpoint.CtxProbe != LlmCtxProbe.Props)
            return _endpoint.CtxLimit > 0 ? _endpoint.CtxLimit : null;

        // /props lives on llama-server itself, one level above the /v1 prefix. Through llama-swap
        // it needs the model in the query string so the proxy knows which upstream to ask.
        var root = _baseUrl.EndsWith("/v1", StringComparison.Ordinal)
            ? _baseUrl[..^3]
            : _baseUrl;

        try
        {
            using var response = await _http.GetAsync($"{root}/props?model={Uri.EscapeDataString(_model)}", ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return _endpoint.CtxLimit > 0 ? _endpoint.CtxLimit : null;

            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.TryGetProperty("default_generation_settings", out var gen))
            {
                var n = GetInt(gen, "n_ctx");
                if (n > 0)
                    return n;
            }

            var direct = GetInt(doc.RootElement, "n_ctx");
            if (direct > 0)
                return direct;
        }
        catch (Exception e)
        {
            _sawmill.Debug($"could not read /props: {e.Message}");
        }

        return _endpoint.CtxLimit > 0 ? _endpoint.CtxLimit : null;
    }

    /// <summary>
    /// Like <c>TryGetProperty</c>, but only takes the field if it is an object.
    /// </summary>
    /// <remarks>
    /// vLLM serializes unfilled protocol fields as <c>null</c> instead of omitting them:
    /// <c>"usage": {..., "prompt_tokens_details": null}</c> shows up in EVERY response, with or
    /// without a cache. A plain <c>TryGetProperty</c> finds such a field, returns true with a Null
    /// element, and the very next call on that element throws an InvalidOperationException —
    /// "requires 'Object', but ... has 'Null'". This exact issue is what kept the AI silent for a
    /// full day (Aug 24-25, 2026) on a single endpoint running awq: vLLM responded 200, and parsing
    /// dropped every turn. llama.cpp and the cloud APIs don't send null fields, so the hole never
    /// showed up on them.
    /// </remarks>
    private static bool TryGetObject(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.Object
               && el.TryGetProperty(name, out value)
               && value.ValueKind == JsonValueKind.Object;
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int)v.GetDouble(),
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Translate the configured effort into the object DeepSeek expects.
///
/// An empty setting sends no field at all, which is what a local llama.cpp needs: it would see a
/// parameter it does not know. Everything else is explicit, because "the model's own default" is
/// <c>high</c> and that is the one setting this agent should not be silently left on.
/// </summary>
public static partial class ThinkingRequest
{
    public static ThinkingDto? Build(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "" => null,
        "off" or "disabled" or "none" => new ThinkingDto { Type = "disabled" },
        var e => new ThinkingDto { Type = "enabled", ReasoningEffort = e },
    };
}

/// <summary>
/// The same effort, but in the strict OpenAI form — a flat <c>reasoning_effort</c> field.
///
/// Separate from <see cref="ThinkingRequest"/> because the value sets don't match. For DeepSeek,
/// "disable" is expressed as the object <c>{"type":"disabled"}</c>; OpenAI has no such value at
/// all, and <c>reasoning_effort: "off"</c> is an HTTP 400. The shared <c>ai.thinking_effort</c>
/// setting is one for everyone, so the translation has to happen here rather than in the config:
/// otherwise "off" set for the local model would break the subscription profile, and the router
/// would honestly consider it incompatible.
/// </summary>
public static class ReasoningEffortRequest
{
    public static string? Build(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "" or "off" or "disabled" or "none" => null,
        "minimal" or "low" or "medium" or "high" => effort.Trim().ToLowerInvariant(),

        // An unknown level is not sent at all: the provider keeps its own default, which is clearly
        // better than failing on every turn because of a typo in a CVar.
        _ => null,
    };
}

/// <summary>
/// <c>chat_template_kwargs.enable_thinking</c> for vLLM. The field is sent only when thinking needs
/// to be turned off: otherwise the Qwen3 template opens &lt;think&gt; on its own, and an empty
/// reasoning_effort doesn't cancel that.
/// </summary>
public static class ChatTemplateKwargsRequest
{
    public static ChatTemplateKwargsDto? Build(string effort) =>
        effort.Trim().ToLowerInvariant() is "off" or "disabled" or "none"
            ? new ChatTemplateKwargsDto { EnableThinking = false }
            : null;
}

public sealed record LlmSampling(
    float Temperature,
    float TopP,
    int TopK,
    float MinP,
    int MaxTokens,
    int? IdSlot,
    string ThinkingEffort = "");

/// <summary>
/// A model failure. <c>[Virtual]</c> rather than sealed so that <see cref="LlmHttpException"/> can
/// refine it: RobustToolbox forbids implicit inheritance via the RA0003 analyzer.
/// </summary>
[Virtual]
public class LlmException : Exception
{
    public LlmException(string message) : base(message)
    {
    }
}

/// <summary>
/// A failure that has a code and, if we're lucky, the time of the next retry.
///
/// Previously every failure was the same <see cref="LlmException"/> with a string inside, and the
/// router would have had to parse the text to tell "the subscription quota ran out until evening"
/// apart from "the token was revoked, a human is needed" and from "the provider didn't understand
/// a field". The distinction here is fundamental: the first is cured by a long sleep, the second
/// only by hand, and the third can't be retried at all.
/// </summary>
public sealed class LlmHttpException : LlmException
{
    public int StatusCode { get; }

    /// <summary>The response body, truncated. For llama-server it holds the real reason.</summary>
    public string Body { get; }

    /// <summary>When the provider allowed a retry, if it said so.</summary>
    public DateTime? RetryAfterUtc { get; }

    public LlmHttpException(int statusCode, string body, DateTime? retryAfterUtc, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
        RetryAfterUtc = retryAfterUtc;
    }
}
