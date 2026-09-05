using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Llm;

/// <summary>What the rest of the system can ask the router, without knowing it even exists.</summary>
public interface ILlmRouter
{
    /// <summary>Who answered last, or null if no one has yet.</summary>
    string? CurrentProfile { get; }

    /// <summary>The current profile's compaction threshold, or 0 — take <c>ai.compact_high</c>.</summary>
    int CurrentCompactHigh { get; }

    /// <summary>
    /// The current profile's declared context window, or 0 — unknown.
    ///
    /// Needed because a session's context size is read ONCE at startup, while the profile can
    /// change over the course of a round. A compaction threshold computed against the previous
    /// provider's window is a failure that, in the log, looks like a healthy prompt size right up
    /// to the very end.
    /// </summary>
    int CurrentCtxLimit { get; }

    /// <summary>The chain in the order it is walked.</summary>
    IReadOnlyList<string> Chain { get; }

    /// <summary>Multi-line report for <c>aiagent llm</c>.</summary>
    string Describe();

    /// <summary>Pin a profile until the end of the round. False — that profile is not in the chain.</summary>
    bool TryUse(string profileId, out string reason);

    /// <summary>Clear "dead" and sleep — after a human has re-logged in.</summary>
    bool Revive(string profileId, out string reason);
}

/// <summary>Router knobs, read from CVars once when the chain is built.</summary>
public sealed record LlmRouterOptions(
    float CooldownSeconds,
    float QuotaCooldownSeconds,
    float RecheckSeconds,
    float TotalTimeoutSeconds);

/// <summary>
/// The primary model and its fallback chain, behind a single <see cref="ILlmClient"/>.
///
/// <para>
/// <b>Why this is a decorator, not a rewrite.</b> <see cref="ILlmClient"/> is constructed in exactly
/// one place (<c>StationAiAgentSystem.EnsureClient</c>), and it has three consumers — the turn loop,
/// the curator, and the compaction summarizer. The router slots into that one line, and none of the
/// three ever learns that there is now more than one provider.
/// </para>
/// <para>
/// <b>Why the choice is sticky.</b> Compaction, the frozen prefix, and the prefix-cache alarm are
/// all written for one stable prefix: the live server holds a 97.9% reuse rate, and every provider
/// switch costs a full prefill on the new side. So the chosen profile is kept as long as it works,
/// and falling back to the primary is retried no more often than <c>ai.llm_recheck_seconds</c> —
/// not "whoever answered first on this turn".
/// </para>
/// <para>
/// <b>Why not every error triggers a switch.</b> A response truncated by <c>max_tokens</c> and
/// malformed JSON in the arguments are our own problems, and another provider would reproduce them
/// exactly the same way; switching on them would mean walking the whole chain and ending up back
/// where we started, at four requests instead of one. The full classification lives in
/// <see cref="Classify"/>.
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
    /// Index of the profile that answered last. -1 — no one yet.
    ///
    /// Deliberately without a lock: <c>ai.max_agents</c> defaults to 1, and even if there are
    /// several agents, the worst consequence of a race is one extra prefill, whereas a lock would
    /// have to be held across an <c>await</c>. A torn read is impossible for an <c>int</c>, so a
    /// reader will see either the old value or the new one.
    /// </summary>
    private int _current = -1;

    /// <summary>Manually pinned profile. -1 — not pinned.</summary>
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

        // Why the reasons are collected instead of logged as we go: when the whole chain fails, it
        // matters to see all four reasons side by side. Four separate ERRORs in the log look like
        // four independent incidents, and connecting them into one failure is left to eyeballing.
        var failures = new List<string>();
        var emptyRetried = false;

        foreach (var index in Order(recheck))
        {
            var lane = _lanes[index];
            var id = lane.Profile.Id;

            // A manually pinned profile is tried even while asleep: the whole point of a manual pin
            // is letting a human insist. But if it still doesn't answer, the chain moves on — on a
            // live server, a silent agent is worse than an agent answering from the wrong profile.
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

                    // An empty response — no text and no tool call. One retry on the spot: this
                    // happens as a one-off sampling failure. It must never be put into the history —
                    // DeepSeek, after an empty assistant message, answered HTTP 400 to every
                    // subsequent request for the rest of the round.
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
                    // Our own cancellation — the round is ending or the agent is being released.
                    // This is not a provider failure, and there is no point walking the chain.
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    // The client has already been disposed — a round restart races with agent
                    // finalization (OnRoundCleanup calls ResetLlmClient while the farewell compaction
                    // is still talking to the model). This is the death of THIS instance, not of the
                    // provider, and it must not be recorded in the shared counter: the counter
                    // survives across rounds, and five minutes of cooldown would land on the NEXT,
                    // completely fresh chain. This is exactly how, on 2026-08-25, a new round spent
                    // three minutes answering "no provider responded in 0s", quoting someone else's
                    // death. The remaining links are not tried — they were disposed by the same
                    // Dispose call.
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

    // ------------------------------------------------------------------ internals

    private async Task<LlmResponse> AttemptAsync(
        Lane lane,
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto>? tools,
        TimeSpan budget,
        CancellationToken ct)
    {
        // A per-attempt deadline on top of the profile's own HttpClient.Timeout. Without it, four
        // profiles with a 150-180s timeout each add up to ten minutes on a single turn, and an
        // agent that is "just thinking" becomes indistinguishable to the crew from a broken one.
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
    /// The order to try in. Pinned first, otherwise the last successful one, otherwise chain order.
    /// A probe to fall back to the primary (<paramref name="recheck"/>) goes strictly chain order
    /// from the start.
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
                // ERROR, and with the response body: this must not be blindly retried, and the
                // reason is always in the body — a field name the provider doesn't know, or a tool
                // schema it didn't accept. Without the body, every 400 looks the same.
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
        /// <summary>Will resolve on its own: network, 5xx, timeout. Short sleep.</summary>
        Retryable,

        /// <summary>Quota ran out. Long sleep until reset.</summary>
        Quota,

        /// <summary>Needs a human: re-login, revoked token, rejected key.</summary>
        Dead,

        /// <summary>The provider rejected the request itself. Retrying is pointless.</summary>
        Incompatible,
    }

    /// <summary>
    /// Tell apart "will recover on its own" from "needs a human" and from "do not retry".
    ///
    /// Parsing the response body here isn't for elegance: bridges to subscription APIs have no
    /// common error format, and a re-login requirement arrives sometimes as code 401, sometimes as
    /// code 400 with the text <c>invalid_grant</c>. Missing it costs more than an occasional false
    /// positive toward "needs a human": a re-login won't happen by itself, and retries will keep
    /// going into the void for the rest of the shift.
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
    /// Signs that a provider requires a human. <c>refresh_token</c> is here on purpose: on this
    /// machine, that's exactly how Codex dropped out once — a single-use refresh token had already
    /// been consumed by another client.
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
