using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// What the router knows about each profile and what it must remember across rounds.
///
/// <para>
/// <b>Why this survives a restart.</b> <c>ResetLlmClient()</c> discards the client on every round
/// restart — deliberately, so that an edit to <c>ai.endpoint</c> takes effect without restarting the
/// server. There are dozens of rounds per day. If the state lived inside the client, every restart
/// would knock on an exhausted subscription again and finish off the rest of the weekly pool — right
/// when it's already gone. So cooldowns, reset times, and the "needs re-login" flag live in the
/// file, not in the client's memory.
/// </para>
/// <para>
/// <b>Why the counters.</b> Neither OpenAI nor xAI publish the actual ceiling: for Codex only the
/// window is known (250-2000 calls to Luna over five hours) and the fact that a weekly limit exists;
/// for Grok Build, only that the pool is weekly and shared across all Grok products. There's only one
/// way to find the ceiling: count your own spend in the same windows and see where the failures start.
/// </para>
/// </summary>
public sealed class LlmQuotaState
{
    /// <summary>How often the file is allowed to be rewritten for counters alone.</summary>
    private static readonly TimeSpan CounterSaveInterval = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly ISawmill _sawmill;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();

    private readonly Dictionary<string, ProfileState> _profiles = new();
    private DateTime _lastSave = DateTime.MinValue;

    public LlmQuotaState(string dataDir, ISawmill sawmill, Func<DateTime>? now = null)
    {
        _path = Path.Combine(dataDir, "llm_state.json");
        _sawmill = sawmill;
        _now = now ?? (() => DateTime.UtcNow);

        Load();
    }

    /// <summary>Whether this profile can be tried right now, and if not — why.</summary>
    public bool IsAvailable(string id, out string why)
    {
        lock (_lock)
        {
            var p = Get(id);

            if (p.DeadReason is { Length: > 0 })
            {
                why = p.DeadReason;
                return false;
            }

            if (p.CooldownUntilUtc is { } until && until > _now())
            {
                var left = until - _now();
                why = $"{p.CooldownReason} (ещё {Describe(left)})";
                return false;
            }

            why = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Defer a profile for a duration. The farther deadline wins: a retry after a 5xx must not
    /// shorten the sleep assigned by an exhausted quota.
    /// </summary>
    public void Cooldown(string id, TimeSpan duration, string reason)
    {
        CooldownUntil(id, _now() + duration, reason);
    }

    /// <inheritdoc cref="Cooldown"/>
    public void CooldownUntil(string id, DateTime untilUtc, string reason)
    {
        lock (_lock)
        {
            var p = Get(id);
            if (p.CooldownUntilUtc is { } existing && existing > untilUtc)
                return;

            p.CooldownUntilUtc = untilUtc;
            p.CooldownReason = reason;
            Persist(force: true);
        }
    }

    /// <summary>
    /// The profile is disabled until a human intervenes: a revoked token, a required re-login,
    /// a rejected key.
    ///
    /// Retrying that is pointless — it won't fix itself. Silently degrading is also not an option:
    /// that's exactly how the agent once stood idle for a whole round, and in the log it looked
    /// like it had been carded.
    /// </summary>
    public void MarkDead(string id, string reason)
    {
        lock (_lock)
        {
            var p = Get(id);
            if (p.DeadReason == reason)
                return;

            p.DeadReason = reason;
            _sawmill.Error($"профиль {id} выключен до вмешательства человека: {reason}");
            Persist(force: true);
        }
    }

    /// <summary>Lift both the death and the sleep — the <c>aiagent llm revive</c> command and a successful turn.</summary>
    public void Revive(string id)
    {
        lock (_lock)
        {
            var p = Get(id);
            if (p.DeadReason is null or "" && p.CooldownUntilUtc is null)
                return;

            p.DeadReason = null;
            p.CooldownUntilUtc = null;
            p.CooldownReason = null;
            Persist(force: true);
        }
    }

    /// <summary>Record a successful turn: calls, tokens, and, for paid profiles, money.</summary>
    public void NoteSuccess(LlmProfileConfig profile, LlmResponse response)
    {
        lock (_lock)
        {
            var p = Get(profile.Id);
            var now = _now();

            p.DeadReason = null;
            p.CooldownUntilUtc = null;
            p.CooldownReason = null;

            RollWindows(p, profile, now);

            var tokens = response.PromptTokens + response.CompletionTokens;

            p.WindowCalls++;
            p.WindowTokens += tokens;
            p.WeekCalls++;
            p.WeekTokens += tokens;
            p.TotalCalls++;

            if (profile.Quota == LlmQuotaKind.Metered)
            {
                var miss = Math.Max(0, response.PromptTokens - response.CachedTokens);
                var spend =
                    miss / 1_000_000.0 * profile.PriceInPer1M
                    + response.CachedTokens / 1_000_000.0 * profile.PriceCachedInPer1M
                    + response.CompletionTokens / 1_000_000.0 * profile.PriceOutPer1M;

                p.DaySpendUsd += spend;
                p.TotalSpendUsd += spend;
            }

            Persist(force: false);
        }
    }

    /// <summary>A snapshot for <c>aiagent llm</c>. A copy so the reader doesn't need the lock.</summary>
    public ProfileSnapshot Snapshot(string id)
    {
        lock (_lock)
        {
            var p = Get(id);
            return new ProfileSnapshot(
                id,
                p.DeadReason,
                p.CooldownUntilUtc,
                p.CooldownReason,
                p.WindowCalls,
                p.WindowTokens,
                p.WindowStartUtc,
                p.WeekCalls,
                p.WeekTokens,
                p.WeekStartUtc,
                p.TotalCalls,
                p.DaySpendUsd,
                p.TotalSpendUsd);
        }
    }

    /// <summary>Flush the file immediately — on server shutdown.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            Persist(force: true);
        }
    }

    // ------------------------------------------------------------------ internal

    private ProfileState Get(string id)
    {
        if (_profiles.TryGetValue(id, out var p))
            return p;

        p = new ProfileState { WindowStartUtc = _now(), WeekStartUtc = _now(), DayStartUtc = _now() };
        _profiles[id] = p;
        return p;
    }

    private static void RollWindows(ProfileState p, LlmProfileConfig profile, DateTime now)
    {
        var window = TimeSpan.FromHours(Math.Max(0.25, profile.QuotaWindowHours));

        if (now - p.WindowStartUtc >= window)
        {
            p.WindowStartUtc = now;
            p.WindowCalls = 0;
            p.WindowTokens = 0;
        }

        if (now - p.WeekStartUtc >= TimeSpan.FromDays(7))
        {
            p.WeekStartUtc = now;
            p.WeekCalls = 0;
            p.WeekTokens = 0;
        }

        if (now - p.DayStartUtc >= TimeSpan.FromDays(1))
        {
            p.DayStartUtc = now;
            p.DaySpendUsd = 0;
        }
    }

    private void Persist(bool force)
    {
        if (!force && _now() - _lastSave < CounterSaveInterval)
            return;

        _lastSave = _now();

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new FileDto { Profiles = _profiles }, FileJson);

            // Via a temp file: the file is read at startup, and if it were truncated mid-write it
            // would reset all cooldowns at once — right at the moment they're needed most.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не удалось сохранить {_path}: {e.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(_path), FileJson);
            if (dto?.Profiles == null)
                return;

            foreach (var (id, p) in dto.Profiles)
                _profiles[id] = p;

            _sawmill.Debug($"состояние провайдеров прочитано: {_profiles.Count} профилей из {_path}");
        }
        catch (Exception e)
        {
            // A corrupted file must not stop the server from starting: the worst that happens is
            // forgotten cooldowns, which is just one extra probe per profile.
            _sawmill.Warning($"не удалось прочитать {_path}, начинаю с чистого состояния: {e.Message}");
            _profiles.Clear();
        }
    }

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Describe(TimeSpan span) => span switch
    {
        _ when span <= TimeSpan.Zero => "0с",
        _ when span < TimeSpan.FromMinutes(1) => $"{span.TotalSeconds:F0}с",
        _ when span < TimeSpan.FromHours(1) => $"{span.TotalMinutes:F0}м",
        _ => string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:F1}ч"),
    };

    private sealed class FileDto
    {
        [JsonPropertyName("profiles")]
        public Dictionary<string, ProfileState> Profiles { get; set; } = new();
    }

    private sealed class ProfileState
    {
        [JsonPropertyName("dead_reason")]
        public string? DeadReason { get; set; }

        [JsonPropertyName("cooldown_until")]
        public DateTime? CooldownUntilUtc { get; set; }

        [JsonPropertyName("cooldown_reason")]
        public string? CooldownReason { get; set; }

        [JsonPropertyName("window_start")]
        public DateTime WindowStartUtc { get; set; }

        [JsonPropertyName("window_calls")]
        public int WindowCalls { get; set; }

        [JsonPropertyName("window_tokens")]
        public long WindowTokens { get; set; }

        [JsonPropertyName("week_start")]
        public DateTime WeekStartUtc { get; set; }

        [JsonPropertyName("week_calls")]
        public int WeekCalls { get; set; }

        [JsonPropertyName("week_tokens")]
        public long WeekTokens { get; set; }

        [JsonPropertyName("day_start")]
        public DateTime DayStartUtc { get; set; }

        [JsonPropertyName("day_spend_usd")]
        public double DaySpendUsd { get; set; }

        [JsonPropertyName("total_calls")]
        public long TotalCalls { get; set; }

        [JsonPropertyName("total_spend_usd")]
        public double TotalSpendUsd { get; set; }
    }
}

/// <summary>The state of one profile for console output.</summary>
public sealed record ProfileSnapshot(
    string Id,
    string? DeadReason,
    DateTime? CooldownUntilUtc,
    string? CooldownReason,
    int WindowCalls,
    long WindowTokens,
    DateTime WindowStartUtc,
    int WeekCalls,
    long WeekTokens,
    DateTime WeekStartUtc,
    long TotalCalls,
    double DaySpendUsd,
    double TotalSpendUsd);
