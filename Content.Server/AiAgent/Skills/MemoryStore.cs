using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;

namespace Content.Server.AiAgent.Skills;

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
///
/// <b>Только про станцию и мир.</b> Раньше рядом жил второй файл, <c>CREW.md</c>, под людей своей
/// смены, и он стирался на разборе раунда. Замысел был против метагейминга, а вышло наоборот:
/// агент перестал писать в стираемый файл и сложил людей сюда, в тот, что переживает раунды, —
/// и этот упёрся в свой лимит и перестал принимать что-либо вообще. Люди теперь живут в
/// <see cref="PlayerNoteStore"/>, по файлу на человека, со штампом раунда у каждой записи.
/// </summary>
public sealed class MemoryStore
{
    /// <summary>Entry delimiter. Section sign on its own line; entries may be multiline.</summary>
    public const string Delimiter = "\n§\n";

    private readonly string _dir;
    private readonly ISawmill _sawmill;
    private readonly List<string> _live = new();
    private string _snapshot = string.Empty;

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

    private string PathFor() => Path.Combine(_dir, "MEMORY.md");

    /// <summary>A copy: the live list is mutated under <see cref="_sync"/> and must not escape it.</summary>
    public IReadOnlyList<string> Entries()
    {
        lock (_sync)
            return EntriesLocked();
    }

    private IReadOnlyList<string> EntriesLocked() => _live.ToList();

    private static int Length(IEnumerable<string> entries) => string.Join(Delimiter, entries).Length;

    // ------------------------------------------------------------------------ io

    public void LoadFromDisk()
    {
        lock (_sync)
        {
            _live.Clear();
            _live.AddRange(ReadFile(PathFor()));
            _snapshot = RenderBlock();

            // A reload replaces the live entries wholesale. Reported for the same reason a
            // compaction reports the whole body: a client holding the old list has no way to
            // discover on its own that it is now describing a different file.
            _sink?.MemoryUpdated(EntriesLocked());
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
    private bool TrySave(out string error)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor();
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, string.Join(Delimiter, _live));
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
    public string Snapshot()
    {
        lock (_sync)
            return _snapshot;
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
            _snapshot = RenderBlock();
            _sink?.MemoryUpdated(EntriesLocked());
        }
    }

    /// <summary>
    /// Render a block with a capacity header, so the model knows its own budget and can decide to
    /// consolidate before it hits the wall rather than after.
    /// </summary>
    private string RenderBlock()
    {
        var entries = EntriesLocked();
        if (entries.Count == 0)
            return string.Empty;

        var used = Length(entries);
        var pct = MemoryLimit > 0 ? Math.Min(100, used * 100 / MemoryLimit) : 0;

        var bar = new string('═', 46);
        return string.Create(CultureInfo.InvariantCulture,
            $"{bar}\nПАМЯТЬ (твои заметки о станции и мире) [{pct}% — {used}/{MemoryLimit} символов]\n{bar}\n{string.Join(Delimiter, entries)}");
    }

    // -------------------------------------------------------------------- writes

    public MemoryResult Add(string content)
    {
        content = content.Trim();
        if (content.Length == 0)
            return new MemoryResult(false, "пустую запись добавить нельзя");

        lock (_sync)
        {
            if (_live.Any(e => e == content))
                return new MemoryResult(true, "такая запись уже есть");

            var newTotal = Length(_live.Append(content));

            if (newTotal > MemoryLimit)
                return ConsolidationFailure(
                    $"память заполнена: {newTotal}/{MemoryLimit} символов. Сократи запись или удали устаревшие " +
                    "через memory(action='remove'), и повтори — всё в этом же ходу.");

            _live.Add(content);

            if (!TrySave(out var error))
            {
                _live.RemoveAt(_live.Count - 1);
                return NotWritten(error);
            }

            return Success("записано");
        }
    }

    /// <summary>
    /// Replace the entry containing <paramref name="oldText"/>.
    ///
    /// Matching is by <em>short unique substring</em>, not by index or by full text: the model
    /// remembers the gist of what it wrote, not the exact bytes, and demanding an exact match
    /// makes every edit a coin flip.
    /// </summary>
    public MemoryResult Replace(string oldText, string newContent)
    {
        oldText = oldText.Trim();
        newContent = newContent.Trim();

        if (oldText.Length == 0)
            return new MemoryResult(false, "нужен фрагмент 'match' — часть текста заменяемой записи");
        if (newContent.Length == 0)
            return new MemoryResult(false, "пустая замена. Чтобы удалить запись, используй action='remove'");

        lock (_sync)
        {
            var matches = _live.Select((e, i) => (Entry: e, Index: i))
                .Where(x => x.Entry.Contains(oldText, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                return ConsolidationFailure(
                    $"ни одна запись не содержит '{Preview(oldText)}'. Ты помнишь текст неточно — " +
                    "посмотри список ниже и скопируй фрагмент дословно.");

            if (matches.Select(m => m.Entry).Distinct().Count() > 1)
                return new MemoryResult(false,
                    $"фрагмент '{Preview(oldText)}' встречается в нескольких разных записях — возьми подлиннее",
                    matches.Select(m => Preview(m.Entry)).ToList());

            var idx = matches[0].Index;

            var test = _live.ToList();
            test[idx] = newContent;
            var newTotal = Length(test);

            // Shrinking is always allowed, even while still over the limit — otherwise an overflowing
            // memory can never be repaired.
            var grew = newContent.Length > _live[idx].Length;

            if (newTotal > MemoryLimit && grew)
                return ConsolidationFailure(
                    $"после замены будет {newTotal}/{MemoryLimit} символов. Сократи текст или сначала " +
                    "выбрось устаревшее — всё в этом же ходу.");

            var previous = _live[idx];
            _live[idx] = newContent;

            if (!TrySave(out var error))
            {
                _live[idx] = previous;
                return NotWritten(error);
            }

            return Success("запись заменена");
        }
    }

    public MemoryResult Remove(string oldText)
    {
        oldText = oldText.Trim();
        if (oldText.Length == 0)
            return new MemoryResult(false, "нужен фрагмент 'match'");

        lock (_sync)
        {
            var matches = _live.Where(e => e.Contains(oldText, StringComparison.Ordinal)).ToList();

            if (matches.Count == 0)
                return new MemoryResult(false, $"ни одна запись не содержит '{Preview(oldText)}'",
                    _live.Select(Preview).ToList());

            if (matches.Distinct().Count() > 1)
                return new MemoryResult(false,
                    $"фрагмент '{Preview(oldText)}' встречается в нескольких разных записях — возьми подлиннее",
                    matches.Select(Preview).ToList());

            var at = _live.IndexOf(matches[0]);
            _live.RemoveAt(at);

            if (!TrySave(out var error))
            {
                _live.Insert(at, matches[0]);
                return NotWritten(error);
            }

            return Success("запись удалена");
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
    private MemoryResult Success(string message)
    {
        _consolidationFailures = 0;
        var entries = EntriesLocked();
        _sink?.MemoryUpdated(entries);
        return new MemoryResult(true, message, null, $"{Length(entries)}/{MemoryLimit}");
    }

    /// <summary>
    /// The edit was rolled back because the disk refused it. Told plainly, and with
    /// <c>retry: later</c> semantics in the wording, so the model does not treat it as a rejection
    /// of the content and rewrite it into something worse.
    /// </summary>
    private MemoryResult NotWritten(string error) =>
        new(false, $"на диск записать не удалось, память не изменилась ({error}). Попробуй позже.",
            null, $"{Length(EntriesLocked())}/{MemoryLimit}");

    private MemoryResult ConsolidationFailure(string error)
    {
        _consolidationFailures++;

        // Terminal answer after a few tries: a fragile write must not loop the turn to exhaustion
        // and swallow the reply the crew is waiting for.
        if (_consolidationFailures > MaxConsolidationFailuresPerTurn)
            return new MemoryResult(false,
                "запись пропущена — не трать на неё этот ход, ответь экипажу и попробуй позже",
                null, $"{Length(EntriesLocked())}/{MemoryLimit}");

        return new MemoryResult(false, error, EntriesLocked().Select(Preview).ToList(),
            $"{Length(EntriesLocked())}/{MemoryLimit}");
    }

    private static string Preview(string s) =>
        s.Length <= 90 ? s : s[..90] + "…";
}
