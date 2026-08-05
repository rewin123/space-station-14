using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// OpenAI-compatible client aimed at llama-swap / llama-server.
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
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly LlmSampling _sampling;

    /// <param name="baseUrl">Such as <c>http://127.0.0.1:9292/v1</c>.</param>
    public LlamaClient(string baseUrl, string model, string apiKey, LlmSampling sampling, TimeSpan timeout, ISawmill sawmill)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _sampling = sampling;
        _sawmill = sawmill;

        // Explicitly proxy-free. This box exports HTTP_PROXY=http://127.0.0.1:10809 and
        // ALL_PROXY=socks5h://127.0.0.1:10808 globally, which swallows requests to localhost and
        // hangs them. Relying on NO_PROXY is not enough: HttpClient.DefaultProxy is read from the
        // environment once at process start and NO_PROXY wildcard semantics vary between runtimes.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        _http = new HttpClient(handler) { Timeout = timeout };

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto>? tools,
        CancellationToken ct)
    {
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
            TopK = _sampling.TopK,
            MinP = _sampling.MinP,
            MaxTokens = _sampling.MaxTokens,
            CachePrompt = true,
            IdSlot = _sampling.IdSlot,
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
            throw new LlmException($"HTTP {(int)response.StatusCode} from {_baseUrl}: {Truncate(raw, 600)}");
        }

        return Parse(raw, (DateTime.UtcNow - started).TotalSeconds);
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

            var message = choices[0].GetProperty("message");

            if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                text = contentEl.GetString();

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var fn = tc.GetProperty("function");
                    calls.Add(new ToolCallDto
                    {
                        Id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                        Type = "function",
                        Function = new FunctionCallDto
                        {
                            Name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                            Arguments = fn.TryGetProperty("arguments", out var argEl)
                                ? (argEl.ValueKind == JsonValueKind.String ? argEl.GetString() ?? "{}" : argEl.GetRawText())
                                : "{}",
                        },
                    });
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
        if (root.TryGetProperty("usage", out var usage))
        {
            prompt = GetInt(usage, "prompt_tokens");
            completion = GetInt(usage, "completion_tokens");

            if (usage.TryGetProperty("prompt_tokens_details", out var details))
                cached = GetInt(details, "cached_tokens");

            // Reasoning models spend part of the completion budget thinking before they write, and
            // that share does not appear anywhere else. Without it, "out 300т" looks like a verbose
            // answer when it was in fact 215 tokens of deliberation and a sentence that got cut off.
            if (usage.TryGetProperty("completion_tokens_details", out var completionDetails))
                reasoning = GetInt(completionDetails, "reasoning_tokens");

            // DeepSeek reports its cache split under its own names.
            if (cached == 0)
                cached = GetInt(usage, "prompt_cache_hit_tokens");
        }

        if (root.TryGetProperty("timings", out var timings))
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

        return new LlmResponse(text, calls, prompt, cached, completion, seconds, finishReason, reasoning);
    }

    public async Task<int?> GetContextSizeAsync(CancellationToken ct)
    {
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
                return null;

            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.TryGetProperty("default_generation_settings", out var gen))
            {
                var n = GetInt(gen, "n_ctx");
                if (n > 0)
                    return n;
            }

            var direct = GetInt(doc.RootElement, "n_ctx");
            return direct > 0 ? direct : null;
        }
        catch (Exception e)
        {
            _sawmill.Debug($"could not read /props: {e.Message}");
            return null;
        }
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
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

public sealed record LlmSampling(
    float Temperature,
    float TopP,
    int TopK,
    float MinP,
    int MaxTokens,
    int? IdSlot);

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message)
    {
    }
}
