using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Log;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// A live check of one profile: reach the provider using the same fields the agent uses.
///
/// <para>
/// <b>Why it exists.</b> Endpoint configuration breaks silently, and that's its main property. The
/// model isn't the one listed in <c>/v1/models</c>; <c>ctxLimit</c> is set twice as high as the real
/// <c>n_ctx</c>; <c>reportsCache: true</c> on a provider that doesn't report cache; <c>reasoningEffort:
/// off</c> on an endpoint that silently ignores that field; and finally <c>timeoutSeconds</c> not
/// smaller than <c>ai.llm_total_timeout</c> — in which case the fallback is NEVER tried. None of
/// these five breakages produces an error at startup. The first four surface a day later through
/// bills and the log, the fifth only on the evening the head of the chain goes down.
/// </para>
/// <para>
/// So the check doesn't ask "does the address respond" — it compares what the profile CLAIMS against
/// what the provider actually does, and prints the discrepancies. The request goes through
/// <see cref="LlamaClient"/> and <see cref="LlamaClient.CreateHttp"/>, i.e. with the same proxy, the
/// same key, and the same dialect as a live turn: a check that takes a different path checks
/// something other than what actually plays.
/// </para>
/// <para>
/// <b>It costs money.</b> This is a real call to the model: tokens for a paid profile, a slice of the
/// window for a subscription. One request per profile, a prompt of a few words, <c>max_tokens</c>
/// untouched (the profile has its own) — but it can't be called free, and it shouldn't be called in
/// a loop.
/// </para>
/// </summary>
public static class LlmProbe
{
    /// <summary>A short prompt: the answer's substance doesn't matter, only the fact of a reply and its metrics.</summary>
    private const string Ping = "Ответь одним словом: работает.";

    /// <summary>
    /// Check a profile and return report lines — one per fact.
    /// </summary>
    /// <param name="keyNote">
    /// Where the key came from, already in printable form: "ai_data/deepseek.key, 35 characters" or
    /// "NO FILE". Assembled outside, because the path to <c>ai_data/</c> is known by the system, not
    /// by the client.
    /// </param>
    /// <param name="compactHighDefault">The printed <c>ai.compact_high</c> — in case the profile has no value of its own.</param>
    /// <param name="totalTimeout"><c>ai.llm_total_timeout</c>: the budget for the whole turn across the entire chain.</param>
    public static async Task<List<string>> RunAsync(
        AiLlmProfilePrototype profile,
        LlmEndpoint endpoint,
        LlmSampling sampling,
        string keyNote,
        int compactHighDefault,
        float totalTimeout,
        ISawmill sawmill,
        CancellationToken ct)
    {
        var lines = new List<string>
        {
            $"{profile.ID}: {endpoint.BaseUrl} | модель {endpoint.Model} | диалект {endpoint.Dialect} | " +
            $"прокси {endpoint.Proxy} | оплата {profile.Quota}",
            $"  ключ: {keyNote}",
        };

        // ------------------------------------------------------------ number checks, before the network
        //
        // These three discrepancies are visible without a single request, and they're printed
        // FIRST on purpose: if the provider is also down, the report will still say what can be
        // fixed right now.

        var compactHigh = profile.CompactHigh > 0 ? profile.CompactHigh : compactHighDefault;

        if (profile.CtxLimit > 0 && compactHigh >= profile.CtxLimit)
        {
            lines.Add($"  ПОРОГ СВЁРТКИ НЕ СРАБОТАЕТ: compactHigh {compactHigh} >= ctxLimit {profile.CtxLimit} — " +
                      "диалог дорастёт до края окна и ход кончится отказом провайдера, а не свёрткой");
        }

        var ownTimeout = profile.TimeoutSeconds > 0f ? profile.TimeoutSeconds : (float)endpoint.Timeout.TotalSeconds;

        if (totalTimeout > 0f && ownTimeout >= totalTimeout)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  ФАЛЛБЕК НЕ ПРОБУЕТСЯ: timeoutSeconds {ownTimeout:0.#} >= ai.llm_total_timeout {totalTimeout:0.#} — " +
                $"зависнув, профиль съест бюджет хода целиком и следующее звено цепочки не попробуется"));
        }

        // --------------------------------------------------------------- /v1/models

        using var http = LlamaClient.CreateHttp(endpoint, TimeSpan.FromSeconds(20));
        var listed = await ListModelsAsync(http, endpoint.BaseUrl, ct).ConfigureAwait(false);

        if (listed == null)
        {
            // Not an error: not everyone has /v1/models, and our bridge to the subscription is
            // exactly such a case.
            lines.Add("  /v1/models: не ответил — сверить имя модели нечем, это нормально для мостов");
        }
        else if (listed.Count == 0)
        {
            lines.Add("  /v1/models: пустой список");
        }
        else if (listed.Contains(endpoint.Model, StringComparer.Ordinal))
        {
            lines.Add($"  /v1/models: модель на месте (всего {listed.Count})");
        }
        else
        {
            lines.Add($"  МОДЕЛИ «{endpoint.Model}» У ПРОВАЙДЕРА НЕТ. Есть: {string.Join(", ", listed.Take(8))}" +
                      (listed.Count > 8 ? $" и ещё {listed.Count - 8}" : ""));
        }

        // ------------------------------------------------------------------- context

        using var client = new LlamaClient(endpoint, sampling, sawmill);

        if (profile.CtxProbe == LlmCtxProbe.Props)
        {
            var real = await client.GetContextSizeAsync(ct).ConfigureAwait(false);

            if (real is not { } n)
                lines.Add("  /props: не ответил — окно берётся из ctxLimit");
            else if (profile.CtxLimit > 0 && n < profile.CtxLimit)
                lines.Add($"  CTXLIMIT ЗАВЫШЕН: сервер поднят с n_ctx {n}, в профиле {profile.CtxLimit}");
            else
                lines.Add($"  /props: n_ctx {n}" + (compactHigh >= n ? "  — и он НЕ БОЛЬШЕ порога свёртки" : ""));
        }
        else if (profile.CtxLimit > 0)
        {
            lines.Add($"  окно: {profile.CtxLimit} из профиля, спросить некого (ctxProbe: None)");
        }
        else
        {
            lines.Add("  ОКНО НЕИЗВЕСТНО: ctxLimit не задан и ctxProbe: None — порог свёртки сядет " +
                      $"на печатное ai.compact_high ({compactHighDefault})");
        }

        // ----------------------------------------------------------------- live request

        try
        {
            var started = DateTime.UtcNow;

            var answer = await client
                .ChatAsync(new[] { ChatMessageDto.User(Ping) }, tools: null, ct)
                .ConfigureAwait(false);

            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            var text = (answer.Content ?? "").Replace('\n', ' ').Trim();

            if (text.Length > 60)
                text = text[..60] + "…";

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  ответ за {seconds:F1}с: «{text}»"));

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  токены: промпт {answer.PromptTokens} (из кэша {answer.CachedTokens}), " +
                $"выдача {answer.CompletionTokens}, размышление {answer.ReasoningTokens}"));

            // Cache. The first request on a cold prefix legitimately gives zero, so the opposite
            // side — cache was promised but there isn't one — is NOT an error here, just a remark:
            // one request can't tell a cold start apart from a lie in the profile.
            if (answer.CachedTokens > 0 && !profile.ReportsCache)
            {
                lines.Add("  reportsCache: false, а кэш провайдер сообщает — поставьте true, " +
                          "иначе экономия на кэше не попадёт ни в счётчик, ни в разбор раунда");
            }
            else if (answer.CachedTokens == 0 && profile.ReportsCache && answer.PromptTokens > 0)
            {
                lines.Add("  кэш не сообщён — на коротком промпте это нормально; если тревога " +
                          "«префикс-кэш сломан» идёт каждый ход, значит reportsCache: true здесь враньё");
            }

            // Thinking. The field is sent silently and silently ignored — this is exactly the
            // breakage that on Grok 4.6 could only be caught by measuring, and the measurement no
            // longer needs to be repeated by hand.
            var effort = string.IsNullOrWhiteSpace(profile.ReasoningEffort)
                ? sampling.ThinkingEffort
                : profile.ReasoningEffort;

            if (answer.ReasoningTokens > 0 && effort.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"  РАЗМЫШЛЕНИЕ НЕ ВЫКЛЮЧИЛОСЬ: заявлено off, пришло {answer.ReasoningTokens} токенов — " +
                          "эндпоинт принял поле и проигнорировал его");
            }

            if (answer.Truncated)
                lines.Add("  ответ обрезан по max_tokens — на боевом ходу так теряется вызов инструмента");
        }
        catch (LlmHttpException e)
        {
            var body = e.Body.Replace('\n', ' ').Trim();
            lines.Add($"  ОТКАЗ HTTP {e.StatusCode}: {(body.Length > 200 ? body[..200] + "…" : body)}");
        }
        catch (OperationCanceledException)
        {
            lines.Add($"  ТАЙМАУТ: не ответил за {endpoint.Timeout.TotalSeconds:0.#}с");
        }
        catch (Exception e)
        {
            lines.Add($"  НЕ ДОШЛИ: {e.Message.Split('\n')[0].Trim()}");
        }

        return lines;
    }

    /// <summary>
    /// <c>GET /v1/models</c> → a list of identifiers, or null if the endpoint doesn't support this.
    /// </summary>
    /// <remarks>
    /// The distinction between null and an empty list is load-bearing. The bridge to the
    /// subscription (<c>Tools/grokbridge</c>) doesn't respond on this path at all, and printing
    /// "no models" because of that would mean blaming a healthy profile.
    /// </remarks>
    private static async Task<List<string>?> ListModelsAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            var ids = new List<string>();

            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
            }

            return ids;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
