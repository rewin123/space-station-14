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
/// Всё, что клиенту нужно знать про один эндпоинт.
///
/// Отдельной записью, а не набором аргументов конструктора: провайдеров теперь несколько, у каждого
/// свой прокси, свой диалект и свой таймаут, и семь позиционных параметров подряд — приглашение
/// перепутать два из них местами.
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

        // Прокси задаётся по профилю, и «никакого прокси» — не значение по умолчанию, а требование.
        //
        // Эта машина экспортирует HTTP_PROXY=http://127.0.0.1:10809 и
        // ALL_PROXY=socks5h://127.0.0.1:10808 глобально, и запрос на loopback, ушедший в прокси,
        // просто зависает. Полагаться на NO_PROXY нельзя: HttpClient.DefaultProxy читается из
        // окружения один раз при старте процесса, а семантика подстановочных знаков в NO_PROXY
        // отличается между рантаймами. Поэтому локальные профили ходят с выключенным прокси
        // принудительно, а облачные — с явно указанным SOCKS, и ни один из двух случаев не зависит
        // от того, что оказалось в окружении сервиса.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        if (endpoint.Proxy == LlmProxyMode.Socks && !string.IsNullOrWhiteSpace(endpoint.SocksProxy))
        {
            // .NET 6+ понимает socks4://, socks4a:// и socks5:// прямо в WebProxy — своей
            // библиотеки для SOCKS не требуется.
            handler.Proxy = new WebProxy(endpoint.SocksProxy);
            handler.UseProxy = true;
        }
        else
        {
            handler.Proxy = null;
            handler.UseProxy = false;
        }

        _http = new HttpClient(handler) { Timeout = endpoint.Timeout };

        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
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

            // Ниже — поля, которых у OpenAI нет. Каждое отправляется только тому, кто его понимает:
            // llama-server незнакомое поле игнорирует молча, строгая API отвечает 400, и без этого
            // фильтра «провайдер лежит» было не отличить от «провайдер не понял четвёртое поле».
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
    /// Когда провайдер разрешил повторить попытку, если он это сказал.
    ///
    /// Для подписки это самое ценное поле во всём ответе: исчерпанная квота — состояние с известным
    /// концом, и знать его точно означает не тратить остаток пробами. <c>Retry-After</c> по
    /// стандарту приходит либо секундами, либо HTTP-датой; заголовки сброса лимитов пишут все
    /// по-своему, поэтому берём первый, который удаётся понять.
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
    /// Формы, в которых встречается время сброса: «12s», «1m30s», «45» (секунды) и unix-секунды.
    /// Всё, что не разобралось, честно отдаётся как «не знаю», а не как ноль.
    /// </summary>
    private static bool TryParseReset(string value, out DateTime when)
    {
        when = default;
        value = value.Trim();
        if (value.Length == 0)
            return false;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            // Больше года в секундах — значит это unix-время, а не задержка.
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

        return new LlmResponse(
            text, calls, prompt, cached, completion, seconds, finishReason, reasoning,
            _endpoint.Id, _endpoint.ReportsCache);
    }

    public async Task<int?> GetContextSizeAsync(CancellationToken ct)
    {
        // Спрашивать /props умеет только llama-server. У всех остальных это 404, и раньше он
        // приходил, логировался предупреждением и оставлял порог компакции на печатном
        // ai.compact_high — то есть на модели с контекстом в четыреста тысяч токенов агент
        // компактился так же часто, как на локальной.
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
/// То же усилие, но в форме строгого OpenAI — плоским полем <c>reasoning_effort</c>.
///
/// Отдельно от <see cref="ThinkingRequest"/>, потому что наборы значений не совпадают. У DeepSeek
/// «выключить» выражается объектом <c>{"type":"disabled"}</c>; у OpenAI такого значения нет вовсе,
/// и <c>reasoning_effort: "off"</c> — это HTTP 400. Общая настройка <c>ai.thinking_effort</c> одна
/// на всех, так что перевод обязан быть здесь, а не в конфиге: иначе выставленное для локальной
/// модели «off» роняло бы подписочный профиль, и роутер честно счёл бы его несовместимым.
/// </summary>
public static class ReasoningEffortRequest
{
    public static string? Build(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "" or "off" or "disabled" or "none" => null,
        "minimal" or "low" or "medium" or "high" => effort.Trim().ToLowerInvariant(),

        // Неизвестный уровень не посылаем вовсе: у провайдера останется его собственный
        // умолчательный, что заведомо лучше отказа на каждом ходу из-за опечатки в CVar.
        _ => null,
    };
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
/// Отказ модели. <c>[Virtual]</c>, а не sealed, чтобы <see cref="LlmHttpException"/> мог его
/// уточнить: RobustToolbox запрещает неявное наследование анализатором RA0003.
/// </summary>
[Virtual]
public class LlmException : Exception
{
    public LlmException(string message) : base(message)
    {
    }
}

/// <summary>
/// Отказ, у которого есть код и, если повезло, время следующей попытки.
///
/// Раньше всякая неудача была одной и той же <see cref="LlmException"/> со строкой внутри, и
/// роутеру пришлось бы разбирать текст, чтобы отличить «квота на подписке кончилась до вечера» от
/// «токен отозван, нужен человек» и от «провайдер не понял поле». Разница здесь принципиальная:
/// первое лечится длинным сном, второе — только руками, третье вообще нельзя повторять.
/// </summary>
public sealed class LlmHttpException : LlmException
{
    public int StatusCode { get; }

    /// <summary>Тело ответа, обрезанное. У llama-server там настоящая причина.</summary>
    public string Body { get; }

    /// <summary>Когда провайдер разрешил повторить, если сказал.</summary>
    public DateTime? RetryAfterUtc { get; }

    public LlmHttpException(int statusCode, string body, DateTime? retryAfterUtc, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
        RetryAfterUtc = retryAfterUtc;
    }
}
