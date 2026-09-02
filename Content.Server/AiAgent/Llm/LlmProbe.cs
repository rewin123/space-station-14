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
/// Живая проверка одного профиля: дойти до провайдера теми же полями, какими ходит агент.
///
/// <para>
/// <b>Зачем она есть.</b> Настройка эндпоинта ломается тихо, и это её главное свойство. Модель не
/// та, что в <c>/v1/models</c>; <c>ctxLimit</c> завышен вдвое против настоящего <c>n_ctx</c>;
/// <c>reportsCache: true</c> у провайдера, который про кэш не сообщает; <c>reasoningEffort: off</c>
/// у эндпоинта, который это поле молча игнорирует; наконец, <c>timeoutSeconds</c> не меньше
/// <c>ai.llm_total_timeout</c> — и тогда фаллбек не пробуется НИКОГДА. Ни одна из пяти поломок не
/// даёт ошибки при старте. Первые четыре видны через сутки по счетам и по журналу, пятая — только
/// в тот вечер, когда голова цепочки ляжет.
/// </para>
/// <para>
/// Поэтому проверка не спрашивает «отвечает ли адрес», а сверяет ЗАЯВЛЕННОЕ в профиле с тем, что
/// провайдер делает на самом деле, и печатает расхождения. Запрос идёт через
/// <see cref="LlamaClient"/> и <see cref="LlamaClient.CreateHttp"/>, то есть с тем же прокси, тем
/// же ключом и тем же диалектом, что боевой ход: проверка, ходящая другим путём, проверяет не то,
/// что играет.
/// </para>
/// <para>
/// <b>Стоит денег.</b> Это настоящее обращение к модели: у платного профиля — токены, у подписки —
/// доля окна. Один запрос на профиль, промпт в несколько слов, <c>max_tokens</c> не трогается
/// (у профиля он свой) — но нулём это не назвать, и вызывать её в цикле не стоит.
/// </para>
/// </summary>
public static class LlmProbe
{
    /// <summary>Короткий промпт: ответ по существу не нужен, нужен факт ответа и его метрики.</summary>
    private const string Ping = "Ответь одним словом: работает.";

    /// <summary>
    /// Проверить профиль и вернуть строки отчёта — по одной на факт.
    /// </summary>
    /// <param name="keyNote">
    /// Откуда взялся ключ, уже в печатном виде: «ai_data/deepseek.key, 35 символов» или «ФАЙЛА
    /// НЕТ». Собирается снаружи, потому что путь к <c>ai_data/</c> знает система, а не клиент.
    /// </param>
    /// <param name="compactHighDefault">Печатное <c>ai.compact_high</c> — на случай, если у профиля своего нет.</param>
    /// <param name="totalTimeout"><c>ai.llm_total_timeout</c>: бюджет всего хода на всю цепочку.</param>
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

        // ------------------------------------------------------------ сверка чисел, до сети
        //
        // Эти три расхождения видны без единого запроса, и печатаются они ПЕРВЫМИ намеренно: если
        // провайдер вдобавок лежит, отчёт всё равно скажет то, что можно починить прямо сейчас.

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
            // Не ошибка: /v1/models есть не у всех, и наш мост к подписке — как раз такой случай.
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

        // ------------------------------------------------------------------- контекст

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

        // ----------------------------------------------------------------- живой запрос

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

            // Кэш. Первый запрос по холодному префиксу законно даёт ноль, поэтому обратная сторона
            // — обещали кэш, а его нет — здесь НЕ ошибка, а замечание: отличить холодный старт от
            // вранья в профиле одним запросом нельзя.
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

            // Размышление. Поле уходит молча и молча игнорируется — это ровно та поломка, которую
            // на Grok 4.6 удалось поймать только замером, и повторять замер руками больше не надо.
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
    /// <c>GET /v1/models</c> → список идентификаторов, или null, если эндпоинт так не умеет.
    /// </summary>
    /// <remarks>
    /// Разница между null и пустым списком несущая. Мост к подписке (<c>Tools/grokbridge</c>) на
    /// этот путь не отвечает вовсе, и печатать из-за этого «моделей нет» значило бы обвинить
    /// исправный профиль.
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
