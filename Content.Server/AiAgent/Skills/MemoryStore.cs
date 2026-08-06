using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;

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
    /// This store is reached from two threads and nothing else in the fork is.
    ///
    /// The memory tools run on the agent thread (they touch files, not entities, so they
    /// deliberately do not marshal), <see cref="RefreshSnapshot"/> is called from the compaction
    /// ritual on that same thread — and <see cref="Snapshot"/> is read from the main thread when a
    /// session starts or the console prints status. Uncontended in practice; the lock costs nothing
    /// and removes a "Collection was modified" that would only ever appear under load.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Where changes are reported, or null when the debug bus is off.
    ///
    /// Set through <see cref="AttachSink"/> rather than the constructor because this store is
    /// rebuilt wholesale by <c>ReloadAgentFiles</c> — a sink attached once to the first instance
    /// would keep describing a store nobody writes to any more, and nothing would say so.
    /// </summary>
    private IAgentEventSink? _sink;

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

    /// <summary>Start reporting writes. Called from <c>ReloadAgentFiles</c>, once per instance.</summary>
    public void AttachSink(IAgentEventSink sink)
    {
        lock (_sync)
            _sink = sink;
    }

    public void ResetTurnCounters()
    {
        lock (_sync)
            _consolidationFailures = 0;
    }

    private string PathFor(MemoryTarget t) =>
        Path.Combine(_dir, t == MemoryTarget.Memory ? "MEMORY.md" : "CREW.md");

    private int LimitFor(MemoryTarget t) => t == MemoryTarget.Memory ? MemoryLimit : CrewLimit;

    /// <summary>A copy: the live list is mutated under <see cref="_sync"/> and must not escape it.</summary>
    public IReadOnlyList<string> Entries(MemoryTarget t)
    {
        lock (_sync)
            return EntriesLocked(t);
    }

    private IReadOnlyList<string> EntriesLocked(MemoryTarget t) =>
        _live.TryGetValue(t, out var list) ? list.ToList() : Array.Empty<string>();

    private List<string> Live(MemoryTarget t) => _live.TryGetValue(t, out var l) ? l : _live[t] = new List<string>();

    private static int Length(IEnumerable<string> entries) => string.Join(Delimiter, entries).Length;

    // ------------------------------------------------------------------------ io

    public void LoadFromDisk()
    {
        lock (_sync)
        {
            foreach (var target in new[] { MemoryTarget.Memory, MemoryTarget.Crew })
            {
                _live[target] = ReadFile(PathFor(target));
                _snapshot[target] = RenderBlock(target);

                // A reload replaces the live entries wholesale. Reported for the same reason a
                // compaction reports the whole body: a client holding the old list has no way to
                // discover on its own that it is now describing a different file.
                _sink?.MemoryUpdated(target, EntriesLocked(target));
            }
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

    /// <summary>
    /// Write the live entries out. Returns false — and says why — rather than swallowing the error.
    ///
    /// Swallowing it is the worst available failure mode for this particular store. The tool would
    /// answer <c>{"ok":true,"result":"записано"}</c>, the curator would take that as done and never
    /// retry, and the lesson would vanish at the next reload. A self-evolving memory that confidently
    /// loses what it learned is worse than one that admits it could not write. Every caller rolls the
    /// in-memory edit back on failure, so live state and disk never disagree.
    /// </summary>
    private bool TrySave(MemoryTarget target, out string error)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(target);
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, string.Join(Delimiter, Live(target)));
            File.Move(tmp, path, overwrite: true);

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"память не сохранена: {error}");
            return false;
        }
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>The frozen text for zone 0. Never changes between rebuilds.</summary>
    public string Snapshot(MemoryTarget t)
    {
        lock (_sync)
            return _snapshot.GetValueOrDefault(t, string.Empty);
    }

    /// <summary>
    /// Catch the snapshot up to the live state. Called only at a prefix rebuild.
    ///
    /// Reported, even though the live entries did not move: the frozen text is what the model
    /// actually reads, and the gap between it and the live list is the single most confusing thing
    /// about this store. A debugger showing the two side by side would otherwise display a frozen
    /// column that silently stopped being true at the last compaction.
    /// </summary>
    public void RefreshSnapshot()
    {
        lock (_sync)
        {
            foreach (var target in new[] { MemoryTarget.Memory, MemoryTarget.Crew })
            {
                _snapshot[target] = RenderBlock(target);
                _sink?.MemoryUpdated(target, EntriesLocked(target));
            }
        }
    }

    /// <summary>
    /// Render a block with a capacity header, so the model knows its own budget and can decide to
    /// consolidate before it hits the wall rather than after.
    /// </summary>
    private string RenderBlock(MemoryTarget target)
    {
        var entries = EntriesLocked(target);
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

        lock (_sync)
        {
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

            if (!TrySave(target, out var error))
            {
                entries.RemoveAt(entries.Count - 1);
                return NotWritten(target, error);
            }

            return Success(target, "записано");
        }
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

        lock (_sync)
        {
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

            var previous = entries[idx];
            entries[idx] = newContent;

            if (!TrySave(target, out var error))
            {
                entries[idx] = previous;
                return NotWritten(target, error);
            }

            return Success(target, "запись заменена");
        }
    }

    /// <summary>
    /// Очистить цель целиком — и на диске тоже.
    ///
    /// Нужна ровно для одного: <c>CREW.md</c> не должен переживать раунд. В SS14 каждая смена —
    /// это новая вселенная с теми же именами персонажей, поэтому запись «Иван Петров — предатель»,
    /// приехавшая из прошлого раунда, это метагейминг, наказуемый на любом публичном сервере.
    ///
    /// <c>MEMORY.md</c> при этом переживать раунды ДОЛЖЕН: там факты о станции и о самом себе
    /// («APC ядра виден в look, но недоступен для move_camera»), и ради накопления этого знания
    /// вся механика памяти и заводилась.
    /// </summary>
    public MemoryResult Clear(MemoryTarget target)
    {
        lock (_sync)
        {
            var entries = Live(target);
            if (entries.Count == 0)
                return new MemoryResult(true, "и так пусто");

            var previous = new List<string>(entries);
            entries.Clear();

            if (!TrySave(target, out var error))
            {
                entries.AddRange(previous);
                return NotWritten(target, error);
            }

            return Success(target, $"очищено записей: {previous.Count}");
        }
    }

    public MemoryResult Remove(MemoryTarget target, string oldText)
    {
        oldText = oldText.Trim();
        if (oldText.Length == 0)
            return new MemoryResult(false, "нужен фрагмент 'match'");

        lock (_sync)
        {
            var entries = Live(target);
            var matches = entries.Where(e => e.Contains(oldText, StringComparison.Ordinal)).ToList();

            if (matches.Count == 0)
                return new MemoryResult(false, $"ни одна запись не содержит '{Preview(oldText)}'",
                    entries.Select(Preview).ToList());

            if (matches.Distinct().Count() > 1)
                return new MemoryResult(false,
                    $"фрагмент '{Preview(oldText)}' встречается в нескольких разных записях — возьми подлиннее",
                    matches.Select(Preview).ToList());

            var at = entries.IndexOf(matches[0]);
            entries.RemoveAt(at);

            if (!TrySave(target, out var error))
            {
                entries.Insert(at, matches[0]);
                return NotWritten(target, error);
            }

            return Success(target, "запись удалена");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The one exit every successful write takes — and therefore the one place that reports it.
    ///
    /// Add, Replace and Remove all land here after <see cref="TrySave"/> has returned true, so a
    /// write that was rolled back because the disk refused it publishes nothing. That is the point:
    /// the event says what is now on disk, not what was attempted.
    /// </summary>
    private MemoryResult Success(MemoryTarget target, string message)
    {
        _consolidationFailures = 0;
        var entries = EntriesLocked(target);
        _sink?.MemoryUpdated(target, entries);
        return new MemoryResult(true, message, null, $"{Length(entries)}/{LimitFor(target)}");
    }

    /// <summary>
    /// The edit was rolled back because the disk refused it. Told plainly, and with
    /// <c>retry: later</c> semantics in the wording, so the model does not treat it as a rejection
    /// of the content and rewrite it into something worse.
    /// </summary>
    private MemoryResult NotWritten(MemoryTarget target, string error) =>
        new(false, $"на диск записать не удалось, память не изменилась ({error}). Попробуй позже.",
            null, $"{Length(EntriesLocked(target))}/{LimitFor(target)}");

    private MemoryResult ConsolidationFailure(MemoryTarget target, string error)
    {
        _consolidationFailures++;

        // Terminal answer after a few tries: a fragile write must not loop the turn to exhaustion
        // and swallow the reply the crew is waiting for.
        if (_consolidationFailures > MaxConsolidationFailuresPerTurn)
            return new MemoryResult(false,
                "запись пропущена — не трать на неё этот ход, ответь экипажу и попробуй позже",
                null, $"{Length(EntriesLocked(target))}/{LimitFor(target)}");

        return new MemoryResult(false, error, EntriesLocked(target).Select(Preview).ToList(),
            $"{Length(EntriesLocked(target))}/{LimitFor(target)}");
    }

    private static string Preview(string s) =>
        s.Length <= 90 ? s : s[..90] + "…";
}
