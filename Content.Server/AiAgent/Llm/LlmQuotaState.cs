using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// Что роутер знает про каждый профиль и что обязан помнить между раундами.
///
/// <para>
/// <b>Зачем это переживает рестарт.</b> <c>ResetLlmClient()</c> выбрасывает клиента на каждом
/// рестарте раунда — сознательно, чтобы правка <c>ai.endpoint</c> подхватывалась без перезапуска
/// сервера. Раундов за сутки десятки. Если состояние жить внутри клиента, то каждый рестарт будет
/// заново стучаться в исчерпанную подписку и добивать остаток недельного пула — причём именно
/// тогда, когда его и так нет. Поэтому cooldown'ы, время сброса и флаг «нужен перелогин» лежат в
/// файле, а не в памяти клиента.
/// </para>
/// <para>
/// <b>Зачем счётчики.</b> Ни OpenAI, ни xAI не публикуют настоящий потолок: у Codex известно только
/// окно (250–2000 обращений к Luna за пять часов) и факт существования недельного лимита, у Grok
/// Build — что пул недельный и общий на все продукты Grok. Узнать потолок можно единственным
/// способом: считать свой расход в тех же окнах и посмотреть, где начнутся отказы.
/// </para>
/// </summary>
public sealed class LlmQuotaState
{
    /// <summary>Как часто разрешено переписывать файл из-за одних счётчиков.</summary>
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

    /// <summary>Можно ли сейчас пробовать этот профиль, и если нет — почему.</summary>
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
    /// Отложить профиль на время. Более далёкий срок побеждает: попытка после 5xx не должна
    /// сокращать сон, назначенный исчерпанной квотой.
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
    /// Профиль выключен до вмешательства человека: отозванный токен, требование перелогина,
    /// отклонённый ключ.
    ///
    /// Повторять такое бессмысленно — само оно не починится. Тихо деградировать тоже нельзя: ровно
    /// так однажды агент простоял весь раунд, и в журнале это выглядело как будто его закардили.
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

    /// <summary>Снять и смерть, и сон — команда <c>aiagent llm revive</c> и успешный ход.</summary>
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

    /// <summary>Записать удачный ход: обращения, токены и, для платных, деньги.</summary>
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

    /// <summary>Снимок для <c>aiagent llm</c>. Копия, чтобы читателю не понадобился замок.</summary>
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

    /// <summary>Дописать файл немедленно — на остановке сервера.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            Persist(force: true);
        }
    }

    // ------------------------------------------------------------------ внутри

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

            // Через временный файл: файл читается на старте, и обрезанный на середине записи он
            // сбросил бы все cooldown'ы разом — то есть ровно в тот момент, когда они нужнее всего.
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
            // Порченый файл не должен мешать серверу подняться: худшее, что случится, — забытые
            // cooldown'ы, а это одна лишняя проба на профиль.
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

/// <summary>Состояние одного профиля для вывода в консоль.</summary>
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
