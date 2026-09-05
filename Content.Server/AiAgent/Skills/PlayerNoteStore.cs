using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;

namespace Content.Server.AiAgent.Skills;

/// <summary>One note: a slug (also the file name), a display name, and entries.</summary>
public sealed class PlayerNote
{
    public required string Slug { get; init; }

    /// <summary>
    /// The name as it was first spoken. Lives in the file's header, not in its name: the slug is
    /// stripped of case, spaces and everything that can't go into a path, and showing it to the
    /// model would mean showing "ivan-petrov" instead of "Ivan Petrov".
    /// </summary>
    public required string Name { get; set; }

    public required List<string> Entries { get; init; }
}

public sealed record NoteResult(
    bool Ok,
    string Message,
    IReadOnlyList<string>? Entries = null,
    string? Usage = null);

/// <summary>
/// Notes about characters: one file per person, surviving the shift.
///
/// <b>Why separate from <see cref="MemoryStore"/>.</b> A second file used to live there alongside the
/// station memory, <c>CREW.md</c>, for the people of the current shift, and it got wiped at the round
/// review. On the live server this produced the opposite of the intended result: the agent stopped
/// writing to the file that gets wiped and piled people into <c>MEMORY.md</c>, which survives
/// rounds — and that one hit its limit and stopped accepting anything at all. <c>CREW.md</c> is gone;
/// people live here entirely. The limit here is per NOTE, not per store, so one person's overflowing
/// note doesn't lock out writing about everyone else.
///
/// <b>What was copied from its neighbours in the directory, and why.</b> Fragment-only edits
/// (<see cref="MemoryStore"/>: whoever is allowed to rewrite the file wholesale will eventually
/// return a shortened version and lose everything). Matching by a short unique substring (the model
/// remembers the gist, not the bytes). Shrinking is always allowed, even past the limit, otherwise an
/// overflowing note can never be repaired. Writing through tmp + rename with an in-memory rollback on
/// disk failure, so disk and memory never diverge. <see cref="LoadFromDisk"/> does not empty the
/// library on a read error.
///
/// <b>What is deliberately NOT here.</b> An index in the system prompt. <see cref="SkillStore"/> has
/// one, and at 167 skills that is already around 20 KB of frozen prefix; the number of characters
/// will grow over months, and such an index would eat the window. A note is opened by a tool, and a
/// NOTE line in the observation is what reminds the agent it exists.
/// </summary>
public sealed class PlayerNoteStore
{
    /// <summary>The same delimiter as <see cref="MemoryStore"/>: the model already knows this format.</summary>
    public const string Delimiter = MemoryStore.Delimiter;

    private readonly string _dir;
    private readonly ISawmill _sawmill;
    private readonly Dictionary<string, PlayerNote> _notes = new();

    /// <summary>
    /// The store is read from TWO threads. The note tools run on the agent thread (they touch files,
    /// not entities, and so deliberately do not marshal), while <see cref="TryPeek"/> is called from
    /// the main thread by speech handlers, when it needs to decide whether to attach a hint. There is
    /// no real contention, but the lock costs nothing and removes a "Collection was modified" that
    /// would otherwise surface exactly under load.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Where to report edits, or null when the debug bus is off.
    ///
    /// Through <see cref="AttachSink"/> rather than the constructor, for the same reason as its
    /// neighbours: <c>ReloadAgentFiles</c> rebuilds the store wholesale, and a sink attached to the
    /// first instance would keep describing a store nobody writes to any more.
    /// </summary>
    private IAgentEventSink? _sink;

    /// <summary>
    /// Limit in CHARACTERS, like its neighbours: characters are model-independent, and the agent can
    /// count them itself when asked to shorten something.
    /// </summary>
    public int NoteLimit { get; init; } = 2000;

    /// <summary>
    /// Ceiling on the number of notes. Only blocks creating new ones — existing ones are still
    /// editable, otherwise a full store could never be worked back down.
    /// </summary>
    public int MaxNotes { get; init; } = 2000;

    /// <summary>
    /// Ceiling on a SINGLE entry. Without it, one retelling of a shift takes up the whole note and
    /// hits its limit on the first try, and a note about a person should be a few lines, not a
    /// dossier.
    /// </summary>
    public int MaxEntryLength { get; init; } = 400;

    /// <summary>Slug truncation point. Cyrillic is two bytes in UTF-8, so 64 characters is up to 128 bytes.</summary>
    private const int MaxSlugLength = 64;

    private const int MaxConsolidationFailuresPerTurn = 3;
    private int _consolidationFailures;

    public PlayerNoteStore(string dataDir, ISawmill sawmill)
    {
        _dir = Path.Combine(dataDir, "people");
        _sawmill = sawmill;
    }

    /// <summary>Start reporting edits. Called from <c>ReloadAgentFiles</c>, once per instance.</summary>
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

    public int Count
    {
        get
        {
            lock (_sync)
                return _notes.Count;
        }
    }

    /// <summary>
    /// The whole store, as COPIES, in stable order — for a state snapshot.
    /// </summary>
    /// <remarks>
    /// Copies, not live objects, unlike <c>SkillStore.All</c>: a skill is an immutable record, while
    /// <see cref="PlayerNote"/> holds a mutable <see cref="List{T}"/>, and an HTTP thread enumerating
    /// it while the agent thread appends an entry would get a "Collection was modified" in the
    /// debugger — exactly the place where other people's breakages get fixed.
    /// </remarks>
    public IReadOnlyList<PlayerNote> All
    {
        get
        {
            lock (_sync)
                return _notes.Values
                    .OrderBy(n => n.Slug, StringComparer.Ordinal)
                    .Select(n => new PlayerNote
                    {
                        Slug = n.Slug,
                        Name = n.Name,
                        Entries = n.Entries.ToList(),
                    })
                    .ToList();
        }
    }

    // ------------------------------------------------------------------ name → file

    /// <summary>
    /// Character name → safe key.
    ///
    /// This is the only place in the whole subsystem where a string outside the player's control
    /// turns into a path on disk, and therefore the only one where sanitisation is mandatory.
    /// <see cref="SkillStore.Normalise"/> doesn't do it — there the model makes up the name, and the
    /// worst that happens is a crooked title. Here the player picks the name in the character editor
    /// and the model substitutes it into a tool argument; a character named "../../SOUL" would write
    /// outside the directory without this function.
    ///
    /// The method doesn't filter a blacklist of dangerous sequences, it keeps a whitelist of allowed
    /// characters: letters, digits, hyphen. After that, neither "..", nor a slash, nor a colon, nor
    /// an absolute path is expressible at all — there's nothing to write them with, rather than "they
    /// got cleaned out". <c>char.IsLetterOrDigit</c> passes Unicode through, so Cyrillic works, same
    /// as for skills.
    ///
    /// An empty string as output is a legitimate result for a name like "...", and the caller must
    /// refuse rather than silently write to a file with an empty name.
    /// </summary>
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sb = new StringBuilder(name.Length);

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_')
                sb.Append('-');
        }

        // Collapse hyphens: "Ivan   Petrov" and "Ivan-Petrov" should give one key.
        var slug = sb.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        slug = slug.Trim('-');

        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength].Trim('-');

        // The server runs on Linux, but two bytes of insurance: on Windows a file with such a name
        // cannot be created, and a debug copy of the store on a laptop would fail for no reason.
        return Reserved.Contains(slug) ? slug + "-" : slug;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private string PathFor(string slug) => Path.Combine(_dir, $"{slug}.md");

    // ---------------------------------------------------------------- file format

    /// <summary>
    /// A header with the display name, then entries joined by the delimiter. Not YAML — for the same
    /// reason as skills: the model breaks YAML often enough that it's worth accounting for.
    /// </summary>
    public static string Render(PlayerNote note) =>
        $"# {note.Name}\n{string.Join(Delimiter, note.Entries)}\n";

    /// <summary>Parse a file. <c>null</c> means the file isn't ours or is corrupted; the caller will skip it.</summary>
    public static PlayerNote? Parse(string raw, string slug)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal);

        var brk = text.IndexOf('\n');
        var header = (brk < 0 ? text : text[..brk]).Trim();

        if (!header.StartsWith("# ", StringComparison.Ordinal))
            return null;

        var name = header[2..].Trim();
        if (name.Length == 0)
            return null;

        var body = brk < 0 ? string.Empty : text[(brk + 1)..];

        // Split on the FULL delimiter, never on a bare §: an entry may legitimately contain one.
        var entries = body.Split(Delimiter)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToList();

        return new PlayerNote { Slug = slug, Name = name, Entries = entries };
    }

    // -------------------------------------------------------------------------- io

    public void LoadFromDisk()
    {
        var loaded = new Dictionary<string, PlayerNote>();

        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var path in Directory.EnumerateFiles(_dir, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var slug = Path.GetFileNameWithoutExtension(path);

                    PlayerNote? note;
                    try
                    {
                        note = Parse(File.ReadAllText(path), slug);
                    }
                    catch (Exception e)
                    {
                        _sawmill.Warning($"заметка не читается: {path} ({e.Message})");
                        continue;
                    }

                    if (note == null)
                    {
                        _sawmill.Warning($"заметка не разобралась: {path}");
                        continue;
                    }

                    loaded[slug] = note;
                }
            }
        }
        catch (Exception e)
        {
            // Early exit WITHOUT clearing: a directory read failure is transient, and emptying the
            // library because of it means losing everything accumulated where waiting would do.
            _sawmill.Error($"каталог заметок не читается: {e.Message}");
            return;
        }

        lock (_sync)
        {
            _notes.Clear();
            foreach (var (slug, note) in loaded)
                _notes[slug] = note;

            // One event for the whole store, not one per note. A reload is the only way a note
            // DISAPPEARS without its own event: the file was deleted by hand, or it stopped parsing.
            // A client folding note.updated into a map would otherwise keep ghosts around.
            _sink?.PlayerNotesReloaded(_notes.Values.ToList());
        }

        _sawmill.Info($"заметок о людях загружено: {loaded.Count}");
    }

    /// <summary>
    /// Write a note to disk. Returns false and explains why, rather than swallowing the error.
    ///
    /// Swallowing it is the worst available failure mode: the tool would answer "written", the
    /// curator would consider the matter done and not retry, and the lesson would vanish by the next
    /// load. Every caller rolls the in-memory edit back, so memory and disk never diverge.
    /// </summary>
    private bool TrySave(PlayerNote note, out string error)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(note.Slug);
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, Render(note));
            File.Move(tmp, path, overwrite: true);

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"заметка не сохранена: {error}");
            return false;
        }
    }

    private bool TryDeleteFile(string slug, out string error)
    {
        try
        {
            File.Delete(PathFor(slug));
            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"заметка не удалена: {error}");
            return false;
        }
    }

    // ----------------------------------------------------------------------- reading

    /// <summary>
    /// Whether a note exists, and how many entries it has. A cheap check for the NOTE hint — it gets
    /// called from the main thread on every first line spoken, so this is a dictionary lookup only, no disk.
    /// </summary>
    public bool TryPeek(string? name, out string display, out int entries)
    {
        display = string.Empty;
        entries = 0;

        var slug = Slugify(name);
        if (slug.Length == 0)
            return false;

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return false;

            display = note.Name;
            entries = note.Entries.Count;
            return true;
        }
    }

    public NoteResult Read(string? name)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false, "нужно имя персонажа");

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return new NoteResult(false, $"заметок о «{name}» нет");

            return new NoteResult(true, note.Name, note.Entries.ToList(),
                $"{Length(note.Entries)}/{NoteLimit}");
        }
    }

    /// <summary>
    /// Search by an approximate name.
    ///
    /// Substring first, then Levenshtein — and, unlike <see cref="SkillStore.Nearest"/>, with a
    /// threshold and a stable order. Without the threshold, a garbage query would still return three
    /// random names presented to the model as "similar"; without a tie-break, the order among equal
    /// distances would be determined by dictionary enumeration order, i.e. it would change between
    /// restarts.
    /// </summary>
    public IReadOnlyList<(string Name, int Entries, string Preview)> Search(string? approx)
    {
        var needle = (approx ?? string.Empty).Trim();

        lock (_sync)
        {
            var all = _notes.Values.ToList();

            if (needle.Length == 0)
            {
                return all
                    .OrderBy(n => n.Name, StringComparer.Ordinal)
                    .Select(Row)
                    .ToList();
            }

            var slug = Slugify(needle);

            var scored = all
                .Select(n =>
                {
                    var substring = n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                    || (slug.Length > 0 && n.Slug.Contains(slug, StringComparison.Ordinal));

                    var distance = Tools.AiToolRegistry.Distance(n.Slug, slug);
                    return (Note: n, Substring: substring, Distance: distance);
                })
                // Threshold: a typo in a couple of letters counts as similar, a "third of it matches" does not.
                .Where(x => x.Substring || x.Distance <= Math.Max(2, slug.Length / 3))
                .OrderByDescending(x => x.Substring)
                .ThenBy(x => x.Distance)
                .ThenBy(x => x.Note.Name, StringComparer.Ordinal)
                .Select(x => Row(x.Note))
                .ToList();

            return scored;
        }
    }

    private static (string Name, int Entries, string Preview) Row(PlayerNote n) =>
        (n.Name, n.Entries.Count, n.Entries.Count > 0 ? Preview(n.Entries[0], 60) : "(пусто)");

    // ------------------------------------------------------------------------ writing

    /// <summary>
    /// Add an entry, creating the note if it doesn't exist yet.
    ///
    /// The stamp is set HERE, not by the model: the model will forget, and after a round the note
    /// "broke into a locker" will stop being distinguishable from today's report. It comes in as a
    /// parameter rather than being taken from <c>DateTime.Now</c> internally, so format tests stay
    /// deterministic.
    /// </summary>
    public NoteResult Add(string? name, string content, string stamp)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false,
                $"из имени «{name}» не выходит ключа — назови персонажа так, как он звучит в эфире");

        content = content.Trim();
        if (content.Length == 0)
            return new NoteResult(false, "пустую запись добавить нельзя");

        if (content.Length > MaxEntryLength)
            return new NoteResult(false,
                $"запись длиннее {MaxEntryLength} символов ({content.Length}) — заметка о человеке " +
                "это несколько строк, а не пересказ смены");

        var stamped = string.IsNullOrWhiteSpace(stamp) ? content : $"{stamp.Trim()} {content}";

        lock (_sync)
        {
            var existed = _notes.TryGetValue(slug, out var note);

            if (!existed && _notes.Count >= MaxNotes)
                return new NoteResult(false,
                    $"заметок уже {_notes.Count} из {MaxNotes} — новую завести некуда. " +
                    "Существующие правятся по-прежнему.");

            note ??= new PlayerNote
            {
                Slug = slug,
                Name = (name ?? slug).Trim(),
                Entries = new List<string>(),
            };

            if (note.Entries.Any(e => e == stamped))
                return new NoteResult(true, "такая запись уже есть");

            var newTotal = Length(note.Entries.Append(stamped));
            if (newTotal > NoteLimit)
                return ConsolidationFailure(note,
                    $"заметка о «{note.Name}» заполнена: {newTotal}/{NoteLimit} символов. Сократи " +
                    "запись или выброси устаревшее через edit_player_related_memory с пустым 'new', " +
                    "и повтори — всё в этом же ходу.");

            note.Entries.Add(stamped);

            if (!TrySave(note, out var error))
            {
                note.Entries.RemoveAt(note.Entries.Count - 1);
                return NotWritten(note, error);
            }

            _notes[slug] = note;
            return Success(note, existed ? "записано" : "заметка заведена");
        }
    }

    /// <summary>Replace the entry containing <paramref name="oldText"/>.</summary>
    public NoteResult Replace(string? name, string oldText, string newContent)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false, "нужно имя персонажа");

        oldText = oldText.Trim();
        newContent = newContent.Trim();

        if (oldText.Length == 0)
            return new NoteResult(false, "нужен фрагмент 'old' — часть текста заменяемой записи");
        if (newContent.Length == 0)
            return new NoteResult(false, "пустой 'new' — это удаление; так и задумано, но тогда 'old' обязателен");

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return new NoteResult(false, $"заметок о «{name}» нет — сначала заведи запись с пустым 'old'");

            var matches = note.Entries.Select((e, i) => (Entry: e, Index: i))
                .Where(x => x.Entry.Contains(oldText, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                return ConsolidationFailure(note,
                    $"ни одна запись о «{note.Name}» не содержит '{Preview(oldText, 90)}'. Ты помнишь " +
                    "текст неточно — посмотри список ниже и скопируй фрагмент дословно.");

            if (matches.Select(m => m.Entry).Distinct().Count() > 1)
                return new NoteResult(false,
                    $"фрагмент '{Preview(oldText, 90)}' встречается в нескольких записях — возьми подлиннее",
                    matches.Select(m => Preview(m.Entry, 90)).ToList());

            var idx = matches[0].Index;

            var test = note.Entries.ToList();
            test[idx] = newContent;

            // Shrinking is always allowed, even past the limit, otherwise an overflowing note can never be repaired.
            var grew = newContent.Length > note.Entries[idx].Length;

            if (newContent.Length > MaxEntryLength && grew)
                return new NoteResult(false,
                    $"запись длиннее {MaxEntryLength} символов ({newContent.Length}) — сократи");

            if (Length(test) > NoteLimit && grew)
                return ConsolidationFailure(note,
                    $"после замены будет {Length(test)}/{NoteLimit} символов. Сократи текст или " +
                    "сначала выбрось устаревшее — всё в этом же ходу.");

            var previous = note.Entries[idx];
            note.Entries[idx] = newContent;

            if (!TrySave(note, out var error))
            {
                note.Entries[idx] = previous;
                return NotWritten(note, error);
            }

            return Success(note, "запись заменена");
        }
    }

    /// <summary>
    /// Remove an entry. If it was the last one, remove the file too: a directory overgrown with empty
    /// notes lies to search about who the agent knows.
    /// </summary>
    public NoteResult Remove(string? name, string oldText)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false, "нужно имя персонажа");

        oldText = oldText.Trim();
        if (oldText.Length == 0)
            return new NoteResult(false, "нужен фрагмент 'old'");

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return new NoteResult(false, $"заметок о «{name}» нет");

            var matches = note.Entries.Where(e => e.Contains(oldText, StringComparison.Ordinal)).ToList();

            if (matches.Count == 0)
                return new NoteResult(false,
                    $"ни одна запись о «{note.Name}» не содержит '{Preview(oldText, 90)}'",
                    note.Entries.Select(e => Preview(e, 90)).ToList());

            if (matches.Distinct().Count() > 1)
                return new NoteResult(false,
                    $"фрагмент '{Preview(oldText, 90)}' встречается в нескольких записях — возьми подлиннее",
                    matches.Select(e => Preview(e, 90)).ToList());

            var at = note.Entries.IndexOf(matches[0]);
            note.Entries.RemoveAt(at);

            if (note.Entries.Count == 0)
            {
                if (!TryDeleteFile(slug, out var delError))
                {
                    note.Entries.Insert(at, matches[0]);
                    return NotWritten(note, delError);
                }

                _notes.Remove(slug);
                _consolidationFailures = 0;

                // A tombstone: the note left along with the file, and it now has zero entries. There
                // is deliberately no separate event kind for this — "the new whole value for the key"
                // is empty here, and the client deletes the key on an empty list.
                _sink?.PlayerNoteUpdated(note);

                return new NoteResult(true, $"последняя запись удалена, заметка о «{note.Name}» закрыта");
            }

            if (!TrySave(note, out var error))
            {
                note.Entries.Insert(at, matches[0]);
                return NotWritten(note, error);
            }

            return Success(note, "запись удалена");
        }
    }

    // ----------------------------------------------------------------------- helpers

    private static int Length(IEnumerable<string> entries) => string.Join(Delimiter, entries).Length;

    /// <summary>
    /// The single successful exit of all three edits, and therefore the single place that reports
    /// them.
    ///
    /// Right here, not in <c>Add</c>/<c>Replace</c>/<c>Remove</c> separately: reporting must only
    /// happen after <see cref="TrySave"/> has confirmed the write, and all three roll the in-memory
    /// edit back on a disk failure and exit through <see cref="NotWritten"/>. An event announcing a
    /// write the agent will not see after a reload is worse than no event at all — it looks credible.
    /// </summary>
    private NoteResult Success(PlayerNote note, string message)
    {
        _consolidationFailures = 0;
        _sink?.PlayerNoteUpdated(note);

        return new NoteResult(true, message, null,
            string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));
    }

    private NoteResult NotWritten(PlayerNote note, string error) =>
        new(false, $"на диск записать не удалось, заметка не изменилась ({error}). Попробуй позже.",
            null, string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));

    private NoteResult ConsolidationFailure(PlayerNote note, string error)
    {
        _consolidationFailures++;

        // A terminal answer after a few attempts: a fragile write must not eat up the whole turn and
        // swallow the reply the crew is waiting for.
        if (_consolidationFailures > MaxConsolidationFailuresPerTurn)
            return new NoteResult(false,
                "запись пропущена — не трать на неё этот ход, ответь экипажу и попробуй позже",
                null, string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));

        return new NoteResult(false, error,
            note.Entries.Select(e => Preview(e, 90)).ToList(),
            string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));
    }

    private static string Preview(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
