using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Content.Server.AiAgent.Skills;

public enum MemoryTarget : byte
{
    /// <summary>Facts about the station and the world.</summary>
    Memory,

    /// <summary>People: names, jobs, trust, history.</summary>
    Crew,
}

public sealed record MemoryResult(bool Ok, string Message, IReadOnlyList<string>? Entries = null, string? Usage = null);

/// <summary>
/// Bounded, file-backed memory the agent curates itself. Ported from
/// <c>hermes-agent/tools/memory_tool.py</c>, keeping the mechanics that make it survive an agent
/// writing to it unsupervised for hours.
///
/// <b>The frozen snapshot.</b> Two parallel states are kept: the snapshot baked into zone 0, and
/// the live entries on disk. A write updates disk immediately (durable, and the tool response
/// reflects it, so the model sees its own edit) but does NOT touch the system prompt — which is
/// what preserves the prefix cache for the whole compaction cycle. The snapshot catches up at the
/// next rebuild.
///
/// <b>Fragment-only edits.</b> There is no "write the whole file" operation, on purpose. Whoever
/// is allowed to rewrite the file wholesale will eventually return a shortened version, and
/// everything accumulated vanishes in one turn, silently.
///
/// <b>Shrinking is always allowed</b>, even while still over the limit. Otherwise an overflowing
/// memory locks up permanently — which happened for real on the mcbot deployment when a limit was
/// lowered and the accumulated text became unfixable.
/// </summary>
public sealed class MemoryStore
{
    /// <summary>Entry delimiter. Section sign on its own line; entries may be multiline.</summary>
    public const string Delimiter = "\n§\n";

    private readonly string _dir;
    private readonly ISawmill _sawmill;
    private readonly Dictionary<MemoryTarget, List<string>> _live = new();
    private readonly Dictionary<MemoryTarget, string> _snapshot = new();

    /// <summary>
    /// Limits are in CHARACTERS, not tokens: characters are model-independent, and the agent can
    /// count them itself when asked to consolidate.
    /// </summary>
    public int MemoryLimit { get; init; } = 4000;
    public int CrewLimit { get; init; } = 2000;

    /// <summary>
    /// After this many failed at-capacity consolidations in one turn, stop telling the model to
    /// retry: a fragile write must not be able to burn the whole turn and suppress the reply.
    /// </summary>
    private const int MaxConsolidationFailuresPerTurn = 3;

    private int _consolidationFailures;

    public MemoryStore(string dataDir, ISawmill sawmill)
    {
        _dir = Path.Combine(dataDir, "memory");
        _sawmill = sawmill;
    }

    public void ResetTurnCounters() => _consolidationFailures = 0;

    private string PathFor(MemoryTarget t) =>
        Path.Combine(_dir, t == MemoryTarget.Memory ? "MEMORY.md" : "CREW.md");

    private int LimitFor(MemoryTarget t) => t == MemoryTarget.Memory ? MemoryLimit : CrewLimit;

    public IReadOnlyList<string> Entries(MemoryTarget t) =>
        _live.TryGetValue(t, out var list) ? list : Array.Empty<string>();

    private List<string> Live(MemoryTarget t) => _live.TryGetValue(t, out var l) ? l : _live[t] = new List<string>();

    private static int Length(IEnumerable<string> entries) => string.Join(Delimiter, entries).Length;

    // ------------------------------------------------------------------------ io

    public void LoadFromDisk()
    {
        foreach (var target in new[] { MemoryTarget.Memory, MemoryTarget.Crew })
        {
            _live[target] = ReadFile(PathFor(target));
            _snapshot[target] = RenderBlock(target);
        }
    }

    private List<string> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new List<string>();

            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            // Split on the full delimiter, never on a bare §: an entry may legitimately contain one.
            return raw.Split(Delimiter)
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .ToList();
        }
        catch (Exception e)
        {
            _sawmill.Warning($"память не читается из {path}: {e.Message}");
            return new List<string>();
        }
    }

    private void Save(MemoryTarget target)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(target);
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, string.Join(Delimiter, Live(target)));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e)
        {
            _sawmill.Error($"память не сохранена: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>The frozen text for zone 0. Never changes between rebuilds.</summary>
    public string Snapshot(MemoryTarget t) => _snapshot.GetValueOrDefault(t, string.Empty);

    /// <summary>Catch the snapshot up to the live state. Called only at a prefix rebuild.</summary>
    public void RefreshSnapshot()
    {
        foreach (var target in new[] { MemoryTarget.Memory, MemoryTarget.Crew })
            _snapshot[target] = RenderBlock(target);
    }

    /// <summary>
    /// Render a block with a capacity header, so the model knows its own budget and can decide to
    /// consolidate before it hits the wall rather than after.
    /// </summary>
    private string RenderBlock(MemoryTarget target)
    {
        var entries = Entries(target);
        if (entries.Count == 0)
            return string.Empty;

        var limit = LimitFor(target);
        var used = Length(entries);
        var pct = limit > 0 ? Math.Min(100, used * 100 / limit) : 0;

        var title = target == MemoryTarget.Memory
            ? "ПАМЯТЬ (твои заметки о станции и мире)"
            : "ЭКИПАЖ (что ты знаешь о людях)";

        var bar = new string('═', 46);
        return string.Create(CultureInfo.InvariantCulture,
            $"{bar}\n{title} [{pct}% — {used}/{limit} символов]\n{bar}\n{string.Join(Delimiter, entries)}");
    }

    // -------------------------------------------------------------------- writes

    public MemoryResult Add(MemoryTarget target, string content)
    {
        content = content.Trim();
        if (content.Length == 0)
            return new MemoryResult(false, "пустую запись добавить нельзя");

        var entries = Live(target);

        if (entries.Any(e => e == content))
            return new MemoryResult(true, "такая запись уже есть");

        var limit = LimitFor(target);
        var candidate = entries.Append(content);
        var newTotal = Length(candidate);

        if (newTotal > limit)
            return ConsolidationFailure(target,
                $"память заполнена: {newTotal}/{limit} символов. Сократи запись или удали устаревшие " +
                "через memory(action='remove'), и повтори — всё в этом же ходу.");

        entries.Add(content);
        Save(target);
        return Success(target, "записано");
    }

    /// <summary>
    /// Replace the entry containing <paramref name="oldText"/>.
    ///
    /// Matching is by <em>short unique substring</em>, not by index or by full text: the model
    /// remembers the gist of what it wrote, not the exact bytes, and demanding an exact match
    /// makes every edit a coin flip.
    /// </summary>
    public MemoryResult Replace(MemoryTarget target, string oldText, string newContent)
    {
        oldText = oldText.Trim();
        newContent = newContent.Trim();

        if (oldText.Length == 0)
            return new MemoryResult(false, "нужен фрагмент 'match' — часть текста заменяемой записи");
        if (newContent.Length == 0)
            return new MemoryResult(false, "пустая замена. Чтобы удалить запись, используй action='remove'");

        var entries = Live(target);
        var matches = entries.Select((e, i) => (Entry: e, Index: i))
            .Where(x => x.Entry.Contains(oldText, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
            return ConsolidationFailure(target,
                $"ни одна запись не содержит '{Preview(oldText)}'. Ты помнишь текст неточно — " +
                "посмотри список ниже и скопируй фрагмент дословно.");

        if (matches.Select(m => m.Entry).Distinct().Count() > 1)
            return new MemoryResult(false,
                $"фрагмент '{Preview(oldText)}' встречается в нескольких разных записях — возьми подлиннее",
                matches.Select(m => Preview(m.Entry)).ToList());

        var idx = matches[0].Index;
        var limit = LimitFor(target);

        var test = entries.ToList();
        test[idx] = newContent;
        var newTotal = Length(test);

        // Shrinking is always allowed, even while still over the limit — otherwise an overflowing
        // memory can never be repaired.
        var grew = newContent.Length > entries[idx].Length;

        if (newTotal > limit && grew)
            return ConsolidationFailure(target,
                $"после замены будет {newTotal}/{limit} символов. Сократи текст или сначала " +
                "выбрось устаревшее — всё в этом же ходу.");

        entries[idx] = newContent;
        Save(target);
        return Success(target, "запись заменена");
    }

    public MemoryResult Remove(MemoryTarget target, string oldText)
    {
        oldText = oldText.Trim();
        if (oldText.Length == 0)
            return new MemoryResult(false, "нужен фрагмент 'match'");

        var entries = Live(target);
        var matches = entries.Where(e => e.Contains(oldText, StringComparison.Ordinal)).ToList();

        if (matches.Count == 0)
            return new MemoryResult(false, $"ни одна запись не содержит '{Preview(oldText)}'",
                entries.Select(Preview).ToList());

        if (matches.Distinct().Count() > 1)
            return new MemoryResult(false,
                $"фрагмент '{Preview(oldText)}' встречается в нескольких разных записях — возьми подлиннее",
                matches.Select(Preview).ToList());

        entries.Remove(matches[0]);
        Save(target);
        return Success(target, "запись удалена");
    }

    // ------------------------------------------------------------------ helpers

    private MemoryResult Success(MemoryTarget target, string message)
    {
        _consolidationFailures = 0;
        var used = Length(Entries(target));
        return new MemoryResult(true, message, null, $"{used}/{LimitFor(target)}");
    }

    private MemoryResult ConsolidationFailure(MemoryTarget target, string error)
    {
        _consolidationFailures++;

        // Terminal answer after a few tries: a fragile write must not loop the turn to exhaustion
        // and swallow the reply the crew is waiting for.
        if (_consolidationFailures > MaxConsolidationFailuresPerTurn)
            return new MemoryResult(false,
                "запись пропущена — не трать на неё этот ход, ответь экипажу и попробуй позже",
                null, $"{Length(Entries(target))}/{LimitFor(target)}");

        return new MemoryResult(false, error, Entries(target).Select(Preview).ToList(),
            $"{Length(Entries(target))}/{LimitFor(target)}");
    }

    private static string Preview(string s) =>
        s.Length <= 90 ? s : s[..90] + "…";
}
