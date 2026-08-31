using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Llm;

/// <summary>Что о роутере может спросить остальная система, не зная, что он вообще есть.</summary>
public interface ILlmRouter
{
    /// <summary>Кто отвечал в последний раз, или null пока никто.</summary>
    string? CurrentProfile { get; }

    /// <summary>Порог компакции текущего профиля, или 0 — брать <c>ai.compact_high</c>.</summary>
    int CurrentCompactHigh { get; }

    /// <summary>
    /// Заявленное окно контекста текущего профиля, или 0 — неизвестно.
    ///
    /// Нужно потому, что размер контекста у сессии снимается ОДИН раз при старте, а профиль за
    /// раунд может смениться. Порог компакции, посчитанный против окна прошлого провайдера, — это
    /// отказ, у которого в журнале до самого конца виден здоровый размер промпта.
    /// </summary>
    int CurrentCtxLimit { get; }

    /// <summary>Цепочка в том порядке, в каком она обходится.</summary>
    IReadOnlyList<string> Chain { get; }

    /// <summary>Многострочный отчёт для <c>aiagent llm</c>.</summary>
    string Describe();

    /// <summary>Закрепить профиль до конца раунда. False — такого профиля в цепочке нет.</summary>
    bool TryUse(string profileId, out string reason);

    /// <summary>Снять «мёртв» и сон — после того как человек перелогинился.</summary>
    bool Revive(string profileId, out string reason);
}

/// <summary>Ручки роутера, снятые с CVar'ов один раз при сборке цепочки.</summary>
public sealed record LlmRouterOptions(
    float CooldownSeconds,
    float QuotaCooldownSeconds,
    float RecheckSeconds,
    float TotalTimeoutSeconds);

/// <summary>
/// Главная модель и цепочка фаллбеков за одним <see cref="ILlmClient"/>.
///
/// <para>
/// <b>Почему это декоратор, а не переделка.</b> <see cref="ILlmClient"/> конструируется в
/// единственном месте (<c>StationAiAgentSystem.EnsureClient</c>), а потребителей у него три — цикл
/// хода, куратор и суммаризатор компакции. Роутер встаёт в ту одну строку, и ни один из трёх не
/// узнаёт, что провайдер теперь не один.
/// </para>
/// <para>
/// <b>Почему выбор липкий.</b> Компакция, замороженный префикс и алярм префикс-кэша написаны под
/// один стабильный префикс: живой сервер держит реюз 97.9%, и каждое переключение провайдера стоит
/// полного prefill на новой стороне. Поэтому выбранный профиль держится, пока работает, а возврат
/// на главный пробуется не чаще <c>ai.llm_recheck_seconds</c> — а не «кто первый ответил на этом
/// ходу».
/// </para>
/// <para>
/// <b>Почему переключает не всякая ошибка.</b> Обрезанный по <c>max_tokens</c> ответ и кривой JSON в
/// аргументах — это наши проблемы, и у другого провайдера они воспроизведутся ровно так же; уход по
/// ним означал бы обойти всю цепочку и вернуться туда, откуда начали, потратив четыре запроса
/// вместо одного. Классификация целиком в <see cref="Classify"/>.
/// </para>
/// </summary>
public sealed class RoutingLlmClient : ILlmClient, ILlmRouter, IDisposable
{
    private readonly List<Lane> _lanes;
    private readonly LlmQuotaState _state;
    private readonly LlmRouterOptions _options;
    private readonly ISawmill _sawmill;
    private readonly Func<DateTime> _now;

    /// <summary>
    /// Индекс профиля, который ответил последним. -1 — ещё никто.
    ///
    /// Без замка сознательно: <c>ai.max_agents</c> по умолчанию 1, а если агентов всё же несколько,
    /// худшее следствие расхождения — один лишний prefill, тогда как замок пришлось бы держать через
    /// <c>await</c>. Разрыв чтения у <c>int</c> невозможен, так что читатель увидит либо старое
    /// значение, либо новое.
    /// </summary>
    private int _current = -1;

    /// <summary>Закреплённый вручную профиль. -1 — не закреплён.</summary>
    private int _pinned = -1;

    private DateTime _lastRecheck = DateTime.MinValue;

    public RoutingLlmClient(
        IReadOnlyList<(LlmProfileConfig Profile, LlmEndpoint Endpoint, LlmSampling Sampling)> chain,
        LlmQuotaState state,
        LlmRouterOptions options,
        ISawmill sawmill,
        Func<DateTime>? now = null,
        Func<LlmEndpoint, LlmSampling, ILlmClient>? clientFactory = null)
    {
        if (chain.Count == 0)
            throw new ArgumentException("цепочка провайдеров пуста", nameof(chain));

        _state = state;
        _options = options;
        _sawmill = sawmill;
        _now = now ?? (() => DateTime.UtcNow);
        _lastRecheck = _now();

        var make = clientFactory ?? ((e, s) => new LlamaClient(e, s, sawmill));

        _lanes = new List<Lane>(chain.Count);
        foreach (var (profile, endpoint, sampling) in chain)
            _lanes.Add(new Lane(profile, make(endpoint, sampling), endpoint.CtxLimit));

        _sawmill.Info($"цепочка провайдеров: {string.Join(" → ", Chain)}");
    }

    public string? CurrentProfile => _current >= 0 ? _lanes[_current].Profile.Id : null;

    public int CurrentCompactHigh => _current >= 0 ? _lanes[_current].Profile.CompactHigh : 0;

    public int CurrentCtxLimit => _current >= 0 ? _lanes[_current].CtxLimit : 0;

    public IReadOnlyList<string> Chain
    {
        get
        {
            var ids = new List<string>(_lanes.Count);
            foreach (var lane in _lanes)
                ids.Add(lane.Profile.Id);
            return ids;
        }
    }

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto>? tools,
        CancellationToken ct)
    {
        var started = _now();
        var deadline = started + TimeSpan.FromSeconds(Math.Max(5f, _options.TotalTimeoutSeconds));

        var recheck = DueForRecheck();
        if (recheck)
            _lastRecheck = started;

        // Почему причины собираются, а не логируются по ходу: когда падает вся цепочка, важно
        // увидеть все четыре причины рядом. Четыре отдельных ERROR'а в журнале выглядят как четыре
        // независимых инцидента, и связать их в один отказ приходится глазами.
        var failures = new List<string>();
        var emptyRetried = false;

        foreach (var index in Order(recheck))
        {
            var lane = _lanes[index];
            var id = lane.Profile.Id;

            // Закреплённый вручную профиль пробуется даже из сна: смысл ручного закрепления в том,
            // чтобы человек мог настоять. Но если он всё же не отвечает, цепочка продолжается — на
            // живом сервере молчащий агент хуже, чем агент, ответивший не с того профиля.
            if (index != _pinned && !_state.IsAvailable(id, out var why))
            {
                failures.Add($"{id}: {why}");
                continue;
            }

            var remaining = deadline - _now();
            if (remaining < TimeSpan.FromSeconds(2))
            {
                failures.Add($"{id}: не пробовали, общий бюджет {_options.TotalTimeoutSeconds:F0}с исчерпан");
                break;
            }

            while (true)
            {
                try
                {
                    var response = await AttemptAsync(lane, messages, tools, remaining, ct).ConfigureAwait(false);

                    // Пустой ответ — ни текста, ни вызова инструмента. Один повтор на месте: это
                    // бывает разовой неудачей семплирования. Ставить его в историю нельзя ни в
                    // каком случае — DeepSeek после пустого assistant-сообщения отвечал HTTP 400
                    // на все последующие запросы до конца раунда.
                    if (IsEmpty(response))
                    {
                        if (!emptyRetried)
                        {
                            emptyRetried = true;
                            _sawmill.Warning($"{id}: пустой ответ, повторяю на том же профиле");
                            remaining = deadline - _now();
                            if (remaining >= TimeSpan.FromSeconds(2))
                                continue;
                        }

                        failures.Add($"{id}: пустой ответ");
                        break;
                    }

                    Settle(index);
                    _state.NoteSuccess(lane.Profile, response);
                    return response;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Наша собственная отмена — раунд кончается или агента отпускают. Это не отказ
                    // провайдера, и по цепочке идти незачем.
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    // Клиент уже разобран — рестарт раунда гонится с финализацией агента
                    // (OnRoundCleanup зовёт ResetLlmClient, пока прощальная компакция ещё ходит в
                    // модель). Это смерть ЭТОГО экземпляра, а не провайдера, и записывать её в
                    // общий счётчик нельзя: счётчик переживает раунды, и пять минут кулдауна
                    // достаются СЛЕДУЮЩЕЙ, совершенно свежей цепочке. Ровно так 25.08.2026 новый
                    // раунд три минуты отвечал «ни один провайдер не ответил за 0с», цитируя
                    // чужую смерть. Остальные звенья не пробуем — они разобраны тем же Dispose.
                    throw;
                }
                catch (Exception e)
                {
                    var verdict = Classify(e, out var reason);
                    Apply(lane, verdict, reason, e);
                    failures.Add($"{id}: {reason}");
                    break;
                }
            }
        }

        throw new LlmException(
            $"ни один провайдер не ответил за {(_now() - started).TotalSeconds:F0}с — " + string.Join("; ", failures));
    }

    public Task<int?> GetContextSizeAsync(CancellationToken ct)
    {
        var lane = PreferredLane();
        return lane.Client.GetContextSizeAsync(ct);
    }

    public bool TryUse(string profileId, out string reason)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (!string.Equals(_lanes[i].Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                continue;

            _pinned = i;
            _current = i;
            reason = $"закреплён {_lanes[i].Profile.Id} до конца раунда";
            _sawmill.Info(reason);
            return true;
        }

        reason = $"профиля {profileId} в цепочке нет: {string.Join(", ", Chain)}";
        return false;
    }

    public bool Revive(string profileId, out string reason)
    {
        foreach (var lane in _lanes)
        {
            if (!string.Equals(lane.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                continue;

            _state.Revive(lane.Profile.Id);
            reason = $"{lane.Profile.Id}: сон и метка «мёртв» сняты";
            return true;
        }

        reason = $"профиля {profileId} в цепочке нет: {string.Join(", ", Chain)}";
        return false;
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("цепочка: ").AppendLine(string.Join(" → ", Chain));
        sb.Append("сейчас: ").Append(CurrentProfile ?? "—");
        if (_pinned >= 0)
            sb.Append("  (закреплён вручную)");
        sb.AppendLine();

        foreach (var lane in _lanes)
        {
            var p = lane.Profile;
            var s = _state.Snapshot(p.Id);

            sb.Append("  ").Append(p.Id.PadRight(10))
              .Append(p.Quota.ToString().PadRight(13))
              .Append(p.Model);

            if (s.DeadReason is { Length: > 0 })
                sb.Append("  МЁРТВ: ").Append(s.DeadReason);
            else if (s.CooldownUntilUtc is { } until && until > _now())
                sb.Append("  спит ").Append(LlmQuotaState.Describe(until - _now())).Append(": ").Append(s.CooldownReason);

            sb.AppendLine();

            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"             окно {p.QuotaWindowHours:F0}ч: {s.WindowCalls} обращений, {s.WindowTokens / 1000} тыс. токенов; " +
                $"неделя: {s.WeekCalls} обращений, {s.WeekTokens / 1000} тыс."));

            if (p.Quota == LlmQuotaKind.Metered)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"; за сутки ${s.DaySpendUsd:F3}, всего ${s.TotalSpendUsd:F2}"));
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        foreach (var lane in _lanes)
            (lane.Client as IDisposable)?.Dispose();

        _state.Flush();
    }

    // ------------------------------------------------------------------ внутри

    private async Task<LlmResponse> AttemptAsync(
        Lane lane,
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto>? tools,
        TimeSpan budget,
        CancellationToken ct)
    {
        // Свой срок на попытку помимо HttpClient.Timeout профиля. Без него четыре профиля с
        // таймаутом по 150-180 с складываются в десять минут на одном ходу, и агент, который
        // «просто думает», выглядит для экипажа неотличимо от сломанного.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        return await lane.Client.ChatAsync(messages, tools, cts.Token).ConfigureAwait(false);
    }

    private static bool IsEmpty(LlmResponse response) =>
        string.IsNullOrWhiteSpace(response.Content) && response.ToolCalls.Count == 0;

    private void Settle(int index)
    {
        if (_current == index)
            return;

        var from = _current >= 0 ? _lanes[_current].Profile.Id : "—";
        _current = index;
        _sawmill.Info($"провайдер: {from} → {_lanes[index].Profile.Id}");
    }

    private Lane PreferredLane()
    {
        if (_pinned >= 0)
            return _lanes[_pinned];

        foreach (var index in Order(recheck: false))
        {
            if (_state.IsAvailable(_lanes[index].Profile.Id, out _))
                return _lanes[index];
        }

        return _lanes[0];
    }

    private bool DueForRecheck() =>
        _pinned < 0 && _current > 0 && _now() - _lastRecheck >= TimeSpan.FromSeconds(_options.RecheckSeconds);

    /// <summary>
    /// В каком порядке пробовать. Закреплённый первым, иначе прошлый удачный, иначе — как в цепочке.
    /// Проба возврата на главный (<paramref name="recheck"/>) идёт строго по цепочке с начала.
    /// </summary>
    private IEnumerable<int> Order(bool recheck)
    {
        if (_pinned >= 0)
        {
            yield return _pinned;

            for (var i = 0; i < _lanes.Count; i++)
            {
                if (i != _pinned)
                    yield return i;
            }

            yield break;
        }

        if (!recheck && _current >= 0)
        {
            yield return _current;

            for (var i = 0; i < _lanes.Count; i++)
            {
                if (i != _current)
                    yield return i;
            }

            yield break;
        }

        for (var i = 0; i < _lanes.Count; i++)
            yield return i;
    }

    private void Apply(Lane lane, Verdict verdict, string reason, Exception e)
    {
        var id = lane.Profile.Id;

        switch (verdict)
        {
            case Verdict.Dead:
                _state.MarkDead(id, reason);
                break;

            case Verdict.Quota:
                var until = (e as LlmHttpException)?.RetryAfterUtc
                            ?? _now() + TimeSpan.FromSeconds(QuotaCooldownFor(lane.Profile));
                _state.CooldownUntil(id, until, reason);
                _sawmill.Warning($"{id}: квота исчерпана, сплю до {until:HH:mm:ss} UTC ({reason})");
                break;

            case Verdict.Incompatible:
                // ERROR и с телом ответа: слепо повторять такое нельзя, а причина всегда в теле —
                // имя поля, которого провайдер не знает, или схема инструмента, которую он не
                // принял. Без тела все 400 выглядят одинаково.
                _sawmill.Error($"{id}: запрос отвергнут как некорректный, профиль несовместим — {reason}");
                _state.Cooldown(id, TimeSpan.FromSeconds(_options.CooldownSeconds), "несовместимый запрос");
                break;

            default:
                _sawmill.Warning($"{id}: {reason}");
                _state.Cooldown(id, TimeSpan.FromSeconds(_options.CooldownSeconds), reason);
                break;
        }
    }

    private float QuotaCooldownFor(LlmProfileConfig profile) =>
        profile.QuotaCooldownSeconds > 0 ? profile.QuotaCooldownSeconds : _options.QuotaCooldownSeconds;

    private enum Verdict
    {
        /// <summary>Само пройдёт: сеть, 5xx, таймаут. Короткий сон.</summary>
        Retryable,

        /// <summary>Квота кончилась. Длинный сон до сброса.</summary>
        Quota,

        /// <summary>Нужен человек: перелогин, отозванный токен, отклонённый ключ.</summary>
        Dead,

        /// <summary>Провайдер не принял сам запрос. Повторять бессмысленно.</summary>
        Incompatible,
    }

    /// <summary>
    /// Отличить «полежит и встанет» от «нужен человек» и от «не повторять».
    ///
    /// Разбор тела ответа здесь не от изящества: у мостов к подписочным API нет общего формата
    /// ошибок, и требование перелогина приходит то кодом 401, то кодом 400 с текстом
    /// <c>invalid_grant</c>. Пропустить его дороже, чем лишний раз ошибиться в сторону «нужен
    /// человек»: перелогин сам не случится, и повторы будут идти в пустоту до конца смены.
    /// </summary>
    private static Verdict Classify(Exception e, out string reason)
    {
        switch (e)
        {
            case LlmHttpException http:
            {
                var body = http.Body.ToLowerInvariant();
                reason = $"HTTP {http.StatusCode}: {Trim(http.Body, 200)}";

                foreach (var hint in ReloginHints)
                {
                    if (body.Contains(hint, StringComparison.Ordinal))
                        return Verdict.Dead;
                }

                return http.StatusCode switch
                {
                    401 or 403 => Verdict.Dead,
                    402 => Verdict.Dead,
                    429 => Verdict.Quota,
                    400 or 404 or 422 => Verdict.Incompatible,
                    _ => Verdict.Retryable,
                };
            }

            case OperationCanceledException:
                reason = "таймаут";
                return Verdict.Retryable;

            case HttpRequestException http:
                reason = $"нет связи: {http.Message}";
                return Verdict.Retryable;

            case SocketException socket:
                reason = $"сеть: {socket.Message}";
                return Verdict.Retryable;

            case IOException io:
                reason = $"обрыв: {io.Message}";
                return Verdict.Retryable;

            default:
                reason = $"{e.GetType().Name}: {e.Message}";
                return Verdict.Retryable;
        }
    }

    /// <summary>
    /// Признаки того, что провайдер требует человека. <c>refresh_token</c> здесь не случайно: на
    /// этой машине именно так однажды отвалился Codex — одноразовый refresh-токен успел
    /// использовать другой клиент.
    /// </summary>
    private static readonly string[] ReloginHints =
    {
        "relogin",
        "re-login",
        "re-authenticate",
        "reauthenticate",
        "invalid_grant",
        "refresh_token",
        "invalid api key",
        "incorrect api key",
        "unauthorized",
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record Lane(LlmProfileConfig Profile, ILlmClient Client, int CtxLimit);
}
