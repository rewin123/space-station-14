using System.Globalization;

namespace Content.Server.AiAgent.Context;

/// <summary>
/// Watches the prefix cache and screams when it breaks.
///
/// This exists because a broken prefix cache is <em>silent</em>. A single interpolated timestamp
/// in the system prompt costs a full prefill on every single turn — tens of thousands of tokens —
/// and presents as "the AI got slow", with no error anywhere and nothing in any log to point at.
/// The only reliable signal is the ratio the server itself reports, so we watch it and alarm on it.
///
/// Steady state is 98–100%. The only legitimate misses are the first turn of a session and the one
/// turn immediately after a compaction.
/// </summary>
public sealed class CacheMetrics
{
    private readonly ISawmill _sawmill;

    /// <summary>Hash of zone 0 as it was when the session started or was last rebuilt.</summary>
    public string ExpectedPrefixHash { get; private set; } = string.Empty;

    public int Turns { get; private set; }
    public double LastRatio { get; private set; }
    public double MeanRatio => Turns == 0 ? 0.0 : _ratioSum / Turns;
    public int Alarms { get; private set; }

    /// <summary>Set for the turn immediately following a compaction, where a miss is expected.</summary>
    public bool ExpectMiss { get; set; }

    private double _ratioSum;
    private int _consecutiveLow;

    public CacheMetrics(ISawmill sawmill)
    {
        _sawmill = sawmill;
    }

    public void SetExpectedPrefix(string hash)
    {
        ExpectedPrefixHash = hash;
        _consecutiveLow = 0;
    }

    /// <summary>Record one completion. Returns false when the alarm fired.</summary>
    public bool Record(int promptTokens, int cachedTokens, string currentPrefixHash, string? systemPreview = null)
    {
        Turns++;

        var ratio = promptTokens <= 0 ? 0.0 : (double)cachedTokens / promptTokens;
        LastRatio = ratio;
        _ratioSum += ratio;

        // A changed hash outside a rebuild is a bug by definition — the prefix is supposed to be
        // immutable between compactions.
        if (ExpectedPrefixHash.Length > 0 && currentPrefixHash != ExpectedPrefixHash)
        {
            Alarms++;
            _sawmill.Error(
                $"ПРЕФИКС ИЗМЕНИЛСЯ ВНЕ КОМПАКЦИИ: было {ExpectedPrefixHash}, стало {currentPrefixHash}. " +
                $"Начало system-промпта: {Truncate(systemPreview, 200)}");
            ExpectedPrefixHash = currentPrefixHash;
            return false;
        }

        if (ExpectMiss || Turns <= 1)
        {
            ExpectMiss = false;
            _consecutiveLow = 0;
            return true;
        }

        if (ratio >= 0.90)
        {
            _consecutiveLow = 0;
            return true;
        }

        _consecutiveLow++;

        // Two in a row, not one: a single low turn happens legitimately when an unusually large
        // observation lands, and alarming on it would train everyone to ignore the alarm.
        if (_consecutiveLow < 2)
            return true;

        Alarms++;
        _sawmill.Error(string.Create(CultureInfo.InvariantCulture,
            $"ПРЕФИКС-КЭШ СЛОМАН: {ratio * 100:F1}% два хода подряд (промпт {promptTokens}т, из кэша {cachedTokens}т), " +
            $"хэш зоны 0 {currentPrefixHash} не менялся — ищи волатильные данные в теле диалога"));

        _consecutiveLow = 0;
        return false;
    }

    public string Format(int promptTokens, int cachedTokens, int outTokens, double seconds, int tools, string mode) =>
        string.Create(CultureInfo.InvariantCulture,
            $"prompt {promptTokens}т (cache {cachedTokens}, {LastRatio * 100:F1}%)  " +
            $"out {outTokens}т  {seconds:F1}с  tools={tools}  mode={mode}");

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "—" : s.Length <= max ? s : s[..max] + "…";
}
