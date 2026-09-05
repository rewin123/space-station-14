using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Vfs;

/// <summary>One article: path within the mount, title, the "when" line, and the body.</summary>
public sealed record Doc(string Path, string Title, string When, string Body, DateTime Modified)
{
    /// <summary>Name without the directory — what <c>ls</c> shows.</summary>
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
}

/// <summary>
/// Tree of articles on disk. The format is the same one the skill library used, plus nesting.
///
/// <para>
/// The format is <c>#&#160;name</c>, then <c>when:&#160;…</c>, then the body. It wasn't chosen out of
/// love for simplicity: a YAML header kept breaking on the model at a quote or an indent often enough
/// that it was dropped on the previous deployment. There is nothing here left to break.
/// </para>
/// <para>
/// A folder's description lives in its <c>_index.md</c>: the same "when" line, and the body is the
/// section overview. This isn't an invention — it's what the <c>reference-*.md</c> files already were;
/// the migration just renames them.
/// </para>
/// <para>
/// Edits are fragment-only. Whoever is allowed to rewrite a file wholesale will eventually return a
/// shortened version, and everything accumulated disappears in one silent move. <see cref="Write"/>
/// exists for creation and deliberate replacement, <see cref="Edit"/> for everything else.
/// </para>
/// </summary>
public sealed class DocTree
{
    public const int MaxWhen = 60;
    public const int MaxBody = 5000;

    private readonly string _root;
    private readonly ISawmill _sawmill;

    /// <summary>Path within the mount ("atmosphere/pumps") → article.</summary>
    private readonly Dictionary<string, Doc> _docs = new(StringComparer.Ordinal);

    /// <summary>Directories, including empty ones: without this, <c>mkdir</c> would leave no trace.</summary>
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    /// <summary>
    /// The tree is read from two threads: <see cref="Reload"/> — the agent thread during the prefix
    /// rebuild step — and the listing thread — the main thread, when the console or debugger prints
    /// state. Swapping the dictionary mid-enumeration gives a "Collection was modified" that only
    /// shows up on a live change.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Where to report edits, or <c>null</c> when the debug bus is off.
    ///
    /// <para>
    /// The event format stayed the same — <c>skill.updated</c> and <c>skills.reloaded</c> with
    /// <c>name/when/body</c> fields — even though storage changed completely. Changing the wire
    /// format along with storage would mean fixing two things at once with no way to tell which one
    /// broke. The <c>name</c> field now carries the path within the mount.
    /// </para>
    /// </summary>
    private IAgentEventSink? _sink;

    public DocTree(string root, ISawmill sawmill)
    {
        _root = root;
        _sawmill = sawmill;
    }

    public string Root => _root;

    /// <summary>Start reporting edits. Called once, while assembling the filesystem.</summary>
    public void AttachSink(IAgentEventSink sink)
    {
        lock (_sync)
            _sink = sink;
    }

    private static Skill AsSkill(Doc doc) => new(doc.Path, doc.When, doc.Body);

    /// <summary>
    /// The only place an article enters memory — and hence the only place that reports it. Called
    /// only AFTER a successful disk write, so a disk failure publishes nothing. The caller holds the
    /// lock.
    /// </summary>
    private void Commit(string relative, Doc doc)
    {
        _docs[relative] = doc;
        _sink?.SkillUpdated(AsSkill(doc));
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _docs.Count;
        }
    }

    /// <summary>The whole tree as bus records. For a state snapshot in the debugger.</summary>
    public IReadOnlyList<Skill> All
    {
        get
        {
            lock (_sync)
                return _docs.Values.OrderBy(d => d.Path, StringComparer.Ordinal).Select(AsSkill).ToList();
        }
    }

    // ------------------------------------------------------------------- parsing

    public static Doc? Parse(string path, string text, DateTime modified)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        if (lines.Length == 0)
            return null;

        var first = lines[0].Trim();

        if (!first.StartsWith('#'))
            return null;

        var title = first.TrimStart('#').Trim();
        var when = string.Empty;
        var bodyStart = 1;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith("когда:", StringComparison.OrdinalIgnoreCase))
            {
                when = line["когда:".Length..].Trim();
                bodyStart = i + 1;
            }

            break;
        }

        var body = string.Join('\n', lines.Skip(bodyStart)).Trim();
        return new Doc(path, title, when, body, modified);
    }

    public static string Render(Doc doc) => $"# {doc.Title}\nкогда: {doc.When}\n{doc.Body}\n";

    // ---------------------------------------------------------------------- disk

    public void Reload()
    {
        // The directory is read in full before taking the lock: holding the lock over file I/O
        // would mean putting disk latency ahead of whatever the main thread is doing.
        var docs = new Dictionary<string, Doc>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                    dirs.Add(RelativeOf(dir));

                foreach (var file in Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories))
                {
                    // The key is the path as it is on disk, with the extension. This is a real
                    // filesystem, just mounted; there's no reason to diverge from it in naming.
                    var rel = RelativeOf(file);

                    var doc = Parse(rel, File.ReadAllText(file), File.GetLastWriteTime(file));

                    if (doc == null)
                    {
                        _sawmill.Warning($"не разобрался как статья: {file}");
                        continue;
                    }

                    docs[rel] = doc;
                }
            }
        }
        catch (Exception e)
        {
            // Keep what's already in memory: a one-off read failure shouldn't wipe out a library
            // the agent has been building for months.
            _sawmill.Warning($"дерево {_root} не читается: {e.Message}");
            return;
        }

        lock (_sync)
        {
            _docs.Clear();
            foreach (var (key, doc) in docs)
                _docs[key] = doc;

            _dirs.Clear();
            foreach (var dir in dirs)
                _dirs.Add(dir);

            // One frame for the whole tree, not one frame per survivor. Reload is the only way an
            // article can disappear, and an event about survivors says nothing about the vanished.
            _sink?.SkillsReloaded(_docs.Values.Select(AsSkill).ToList());
        }
    }

    private string RelativeOf(string absolute) =>
        Path.GetRelativePath(_root, absolute).Replace('\\', '/');

    private string DiskPath(string relative) =>
        Path.Combine(_root, Path.Combine(relative.Split('/')));

    /// <summary>
    /// An article's key from the requested path: as written, or with <c>.md</c> appended if that
    /// doesn't exist.
    /// </summary>
    /// <remarks>
    /// The one concession in addressing. The extension is a storage detail, and requiring it in
    /// every path would mean catching mistakes where there aren't any. The exact match is checked
    /// first: a file named without an extension stays reachable under its real name.
    /// </remarks>
    private string? KeyOf(string relative)
    {
        if (relative.Length == 0)
            return null;

        if (_docs.ContainsKey(relative))
            return relative;

        var withExtension = VfsPath.WithExtension(relative);
        return _docs.ContainsKey(withExtension) ? withExtension : null;
    }

    // ------------------------------------------------------------------- reading

    public bool TryGet(string relative, out Doc doc)
    {
        lock (_sync)
        {
            var key = KeyOf(relative);

            if (key != null)
                return _docs.TryGetValue(key, out doc!);

            doc = null!;
            return false;
        }
    }

    public bool HasDir(string relative)
    {
        if (relative.Length == 0)
            return true;

        lock (_sync)
            return _dirs.Contains(relative) || _docs.Keys.Any(k => k.StartsWith(relative + "/", StringComparison.Ordinal));
    }

    /// <summary>Direct children of a directory: folders first, then articles, both groups alphabetical.</summary>
    public IReadOnlyList<VfsEntry> Children(string relative)
    {
        lock (_sync)
        {
            var prefix = relative.Length == 0 ? string.Empty : relative + "/";
            var dirs = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var files = new List<VfsEntry>();

            foreach (var dir in _dirs)
            {
                if (!Under(dir, prefix, out var rest) || rest.Contains('/'))
                    continue;

                dirs.TryAdd(rest, 0);
            }

            foreach (var (key, doc) in _docs)
            {
                if (!Under(key, prefix, out var rest))
                    continue;

                var slash = rest.IndexOf('/');

                if (slash >= 0)
                {
                    var head = rest[..slash];
                    dirs.TryGetValue(head, out var n);
                    dirs[head] = n + 1;
                    continue;
                }

                // _index describes the folder itself and doesn't show up in that folder's own
                // listing: otherwise every folder would contain a file named after its own index.
                if (rest == VfsPath.IndexFile + VfsPath.Extension)
                    continue;

                files.Add(new VfsEntry(rest, false, doc.When, doc.Body.Length, doc.Modified));
            }

            var result = new List<VfsEntry>();

            foreach (var (name, count) in dirs)
            {
                var indexKey = prefix + name + "/" + VfsPath.IndexFile + VfsPath.Extension;
                var desc = _docs.TryGetValue(indexKey, out var index) ? index.When : string.Empty;
                result.Add(new VfsEntry(name, true, desc, count, null));
            }

            result.AddRange(files.OrderBy(f => f.Name, StringComparer.Ordinal));
            return result;
        }
    }

    private static bool Under(string key, string prefix, out string rest)
    {
        rest = string.Empty;

        if (prefix.Length == 0)
        {
            rest = key;
            return true;
        }

        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        rest = key[prefix.Length..];
        return rest.Length > 0;
    }

    /// <summary>Names closest by Levenshtein distance — for a clear rejection instead of "no such thing".</summary>
    public IReadOnlyList<string> Nearest(string relative, int count = 3)
    {
        lock (_sync)
            return _docs.Keys
                .OrderBy(k => Tools.AiToolRegistry.Distance(k, relative))
                .ThenBy(k => k, StringComparer.Ordinal)
                .Take(count)
                .ToList();
    }

    public IReadOnlyList<VfsHit> Grep(string needle, string relative, string mountPoint, int limit)
    {
        var hits = new List<VfsHit>();
        var prefix = relative.Length == 0 ? string.Empty : relative + "/";

        lock (_sync)
        {
            foreach (var (key, doc) in _docs.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal) && key != relative)
                    continue;

                var line = 0;

                foreach (var text in doc.Body.Split('\n'))
                {
                    line++;

                    if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new VfsHit($"/{mountPoint}/{key}", line, text.Trim()));

                    if (hits.Count >= limit)
                        return hits;
                }

                // The "when" line is searched too: it's usually where the word the crew calls the
                // thing by lives, while the body speaks in specifics.
                if (doc.When.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new VfsHit($"/{mountPoint}/{key}", 0, "когда: " + doc.When));

                if (hits.Count >= limit)
                    return hits;
            }
        }

        return hits;
    }

    // -------------------------------------------------------------------- writing

    public VfsWrite Write(string relative, string title, string when, string body)
    {
        when = when.Trim();
        body = body.Trim();

        if (relative.Length == 0)
            return VfsWrite.No("нужен путь файла");

        // Always write with the extension: otherwise the next directory reload, which globs
        // "*.md", simply won't see the file, and the write would look successful right up until
        // the next restart.
        relative = VfsPath.WithExtension(relative);

        if (when.Length == 0)
            return VfsWrite.No("нужно описание — по нему файл и находят в листинге");

        if (when.Length > MaxWhen)
            return VfsWrite.No(
                $"описание длиннее {MaxWhen} символов ({when.Length}). Это единственная строка, " +
                "которую видно в ls, — всё за пределом просто не доедет до листинга. Сократи.");

        if (body.Length > MaxBody)
            return VfsWrite.No($"тело длиннее {MaxBody} символов ({body.Length})");

        lock (_sync)
        {
            if (!_docs.ContainsKey(relative))
            {
                var clash = FindOverlapping(relative);

                if (clash != null)
                    return VfsWrite.No(
                        $"«{clash}» — то же имя с довеском или без него, то есть тот же самый файл. " +
                        "Не плоди близнецов: дополни его через edit_file. Если имя осмысленно только " +
                        "для сегодняшней задачи, оно неправильное. Соседнее имя в той же папке " +
                        "(другое слово, а не уточнение к этому) записать можно.",
                        new[] { clash });
            }

            var existed = _docs.ContainsKey(relative);
            var doc = new Doc(relative, title.Trim().Length > 0 ? title.Trim() : NameOf(relative), when, body, DateTime.Now);

            if (!TrySave(doc, out var error))
                return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({error})");

            Commit(relative, doc);
            EnsureDirsFor(relative);

            return VfsWrite.Fine(existed ? "файл перезаписан" : "файл создан");
        }
    }

    public VfsWrite Edit(string relative, string match, string replacement)
    {
        match = match.Trim();

        lock (_sync)
        {
            var key = KeyOf(relative);

            if (key == null || !_docs.TryGetValue(key, out var doc))
                return VfsWrite.No($"нет файла «{relative}»", NearestLocked(relative));

            relative = key;

            if (match.Length == 0)
            {
                var appended = doc with { Body = (doc.Body + "\n" + replacement.Trim()).Trim(), Modified = DateTime.Now };

                if (appended.Body.Length > MaxBody)
                    return VfsWrite.No($"тело превысит {MaxBody} символов — сначала выбрось лишнее");

                if (!TrySave(appended, out var appendError))
                    return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({appendError})");

                Commit(relative, appended);
                return VfsWrite.Fine("дописано в конец");
            }

            var count = Occurrences(doc.Body, match);

            if (count == 0)
                return VfsWrite.No(
                    "такого фрагмента в файле нет — ты помнишь текст неточно, открой его через " +
                    "cat и скопируй дословно");

            if (count > 1)
                return VfsWrite.No("фрагмент встречается несколько раз — возьми подлиннее");

            var body = doc.Body.Replace(match, replacement.Trim(), StringComparison.Ordinal).Trim();

            if (body.Length > MaxBody)
                return VfsWrite.No($"тело превысит {MaxBody} символов");

            var edited = doc with { Body = body, Modified = DateTime.Now };

            if (!TrySave(edited, out var saveError))
                return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({saveError})");

            Commit(relative, edited);
            return VfsWrite.Fine(replacement.Trim().Length == 0 ? "фрагмент удалён" : "фрагмент заменён");
        }
    }

    public VfsWrite MakeDir(string relative)
    {
        if (relative.Length == 0)
            return VfsWrite.No("нужен путь папки");

        lock (_sync)
        {
            if (_docs.ContainsKey(relative))
                return VfsWrite.No($"«{relative}» — это файл, а не папка");

            if (_dirs.Contains(relative))
                return VfsWrite.Fine("папка уже была");

            try
            {
                Directory.CreateDirectory(DiskPath(relative));
            }
            catch (Exception e)
            {
                return VfsWrite.No($"папку создать не удалось ({e.GetType().Name}: {e.Message})");
            }

            _dirs.Add(relative);
            EnsureDirsFor(relative);

            return VfsWrite.Fine("папка создана");
        }
    }

    public VfsWrite Remove(string relative)
    {
        if (relative.Length == 0)
            return VfsWrite.No("корень удалить нельзя");

        lock (_sync)
        {
            if (KeyOf(relative) is { } key)
            {
                relative = key;

                try
                {
                    File.Delete(DiskPath(relative));
                }
                catch (Exception e)
                {
                    return VfsWrite.No($"удалить не удалось ({e.GetType().Name}: {e.Message})");
                }

                _docs.Remove(relative);

                // A disappearance can't be described by a single event: skill.updated has no form
                // for "this no longer exists", and a client folding events into a map would keep a
                // ghost forever. So removal is published as the whole set — the same as a reload.
                _sink?.SkillsReloaded(_docs.Values.Select(AsSkill).ToList());

                return VfsWrite.Fine("файл удалён");
            }

            if (!_dirs.Contains(relative))
                return VfsWrite.No($"нет ни файла, ни папки «{relative}»", NearestLocked(relative));

            // Refuse to tear down a non-empty folder. A one-line recursive delete is a way to lose
            // a reference-library section to a typo in the path, and we have no undo for that.
            var children = _docs.Keys.Count(k => k.StartsWith(relative + "/", StringComparison.Ordinal));

            if (children > 0)
                return VfsWrite.No($"в папке ещё {children} файлов — удали их сначала");

            try
            {
                Directory.Delete(DiskPath(relative));
            }
            catch (Exception e)
            {
                return VfsWrite.No($"папку удалить не удалось ({e.GetType().Name}: {e.Message})");
            }

            _dirs.Remove(relative);
            return VfsWrite.Fine("папка удалена");
        }
    }

    public VfsWrite Move(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0)
            return VfsWrite.No("нужны оба пути");

        lock (_sync)
        {
            if (KeyOf(from) is not { } fromKey || !_docs.TryGetValue(fromKey, out var doc))
                return VfsWrite.No($"нет файла «{from}»", NearestLocked(from));

            from = fromKey;
            to = VfsPath.WithExtension(to);

            if (_docs.ContainsKey(to))
                return VfsWrite.No($"«{to}» уже занято");

            var moved = doc with { Path = to, Modified = DateTime.Now };

            if (!TrySave(moved, out var error))
                return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({error})");

            try
            {
                File.Delete(DiskPath(from));
            }
            catch (Exception e)
            {
                // The copy has already landed: we report the truth instead of "renamed", or else
                // both files would silently remain on disk and the next Reload would show a twin.
                _sawmill.Warning($"старый файл {from} не удалён: {e.Message}");
                return VfsWrite.No($"скопировано в «{to}», но старый файл удалить не удалось ({e.GetType().Name})");
            }

            _docs.Remove(from);
            Commit(to, moved);
            EnsureDirsFor(to);

            return VfsWrite.Fine($"переименовано в «{to}»");
        }
    }

    // ------------------------------------------------------------- internals

    private bool TrySave(Doc doc, out string error)
    {
        try
        {
            var path = DiskPath(doc.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Render(doc));
            File.Move(tmp, path, overwrite: true);

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"файл «{doc.Path}» не сохранён: {error}");
            return false;
        }
    }

    private void EnsureDirsFor(string relative)
    {
        var parts = relative.Split('/');

        for (var i = 1; i < parts.Length; i++)
            _dirs.Add(string.Join('/', parts[..i]));
    }

    /// <summary>File name without the directory and without the extension — for comparing names.</summary>
    private static string NameOf(string relative)
    {
        var name = relative.Contains('/') ? relative[(relative.LastIndexOf('/') + 1)..] : relative;

        return name.EndsWith(VfsPath.Extension, StringComparison.Ordinal)
            ? name[..^VfsPath.Extension.Length]
            : name;
    }

    private IReadOnlyList<string> NearestLocked(string relative, int count = 3) =>
        _docs.Keys
            .OrderBy(k => Tools.AiToolRegistry.Distance(k, relative))
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(count)
            .ToList();

    /// <summary>
    /// A file that is the same name with an add-on — within a SINGLE folder.
    ///
    /// <para>
    /// A shared word isn't grounds by itself: "power/apc" and "power/smes" are different things,
    /// and rejecting the second because of the first would leave the agent with no permitted name
    /// at all. What's caught is exactly what was caught on the previous deployment:
    /// <c>safe_mine_ore</c> next to <c>mine_ore</c>, i.e. a subset of words.
    /// </para>
    /// <para>
    /// The search scope is neighbors within the same folder, not the whole tree. In a flat library
    /// this was the same thing; in a tree, "atmosphere/pumps" and "power/pumps" are legitimately
    /// different articles.
    /// </para>
    /// </summary>
    private string? FindOverlapping(string relative)
    {
        var dir = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : string.Empty;
        var words = Words(NameOf(relative));

        if (words.Count == 0)
            return null;

        foreach (var existing in _docs.Keys)
        {
            var otherDir = existing.Contains('/') ? existing[..existing.LastIndexOf('/')] : string.Empty;

            if (!string.Equals(dir, otherDir, StringComparison.Ordinal))
                continue;

            var other = Words(NameOf(existing));

            if (other.Count == 0)
                continue;

            if (words.IsSubsetOf(other) || other.IsSubsetOf(words))
                return existing;
        }

        return null;

        static HashSet<string> Words(string name) =>
            name.Split('-', '_', ' ')
                .Where(w => w.Length > 2)
                .ToHashSet(StringComparer.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;

        var count = 0;
        var i = 0;

        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }
}
