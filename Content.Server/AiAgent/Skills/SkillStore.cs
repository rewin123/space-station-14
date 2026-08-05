using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Content.Server.AiAgent.Skills;

/// <summary>
/// One skill: three fields and nothing else.
///
/// No YAML, no categories, no verification flags. The model breaks YAML often enough that the
/// mcbot deployment abandoned it outright, and every extra field is one more way for a write to
/// fail on a technicality instead of saving what was learned.
/// </summary>
public sealed record Skill(string Name, string When, string Body)
{
    /// <summary>The single line that lands in the frozen prefix.</summary>
    public string IndexLine => $"  {Name} — {When}";
}

public sealed record SkillResult(bool Ok, string Message, IReadOnlyList<string>? Names = null);

/// <summary>
/// The agent's skill library: plain markdown files it writes itself.
///
/// <b>Progressive disclosure.</b> Only <c>name — when</c> goes into zone 0; the body arrives on
/// demand through <c>skill_view</c>. That is what lets the library grow for months without the
/// prefix growing with it.
///
/// The <c>when</c> line is hard-capped at 60 characters because it is the only part with a budget.
/// Anything past the cap would silently never reach the index and so could never route — a failure
/// mode that looks exactly like the model ignoring its own skills.
/// </summary>
public sealed class SkillStore
{
    public const int MaxWhenLength = 60;
    public const int MaxBodyLength = 5000;

    private readonly string _dir;
    private readonly ISawmill _sawmill;
    private readonly Dictionary<string, Skill> _skills = new();

    /// <summary>
    /// <see cref="LoadFromDisk"/> clears and refills this dictionary from the agent thread (it is
    /// step 5 of the compaction ritual), while <see cref="RenderIndex"/> enumerates it from the main
    /// thread when a session starts or the console prints status. Clearing during an enumeration is
    /// a "Collection was modified" that would only ever fire on a busy live round.
    /// </summary>
    private readonly object _sync = new();

    public SkillStore(string dataDir, ISawmill sawmill)
    {
        _dir = Path.Combine(dataDir, "skills");
        _sawmill = sawmill;
    }

    public IReadOnlyCollection<Skill> All
    {
        get
        {
            lock (_sync)
                return _skills.Values.ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _skills.Count;
        }
    }

    public bool TryGet(string name, out Skill skill)
    {
        lock (_sync)
            return _skills.TryGetValue(Normalise(name), out skill!);
    }

    // ------------------------------------------------------------------- parsing

    /// <summary>
    /// Parse the mcbot format: <c># name</c>, then <c>когда: …</c>, then the body.
    ///
    /// Chosen because it survived production use where YAML frontmatter did not — a model that
    /// mangles a quote or an indent still gets a parseable file here.
    /// </summary>
    public static Skill? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2)
            return null;

        var first = lines[0].Trim();
        if (!first.StartsWith('#'))
            return null;

        var name = Normalise(first.TrimStart('#').Trim());
        if (name.Length == 0)
            return null;

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
        return new Skill(name, when, body);
    }

    public static string Render(Skill s) => $"# {s.Name}\nкогда: {s.When}\n{s.Body}\n";

    /// <summary>Lowercase, spaces to hyphens — so "Открыть Дверь" and "открыть-дверь" are one skill.</summary>
    public static string Normalise(string name) =>
        name.Trim().ToLowerInvariant().Replace(' ', '-');

    // ------------------------------------------------------------------------ io

    public void LoadFromDisk()
    {
        // Read the directory first and swap the dictionary under the lock only once: holding the
        // lock across file IO would put disk latency in front of whatever the main thread is doing.
        var loaded = new Dictionary<string, Skill>();

        try
        {
            if (Directory.Exists(_dir))
            {
                // Archived skills live in .archive/ and are deliberately not loaded — they are out
                // of the index but recoverable by hand.
                foreach (var path in Directory.EnumerateFiles(_dir, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var skill = Parse(File.ReadAllText(path));
                    if (skill == null)
                    {
                        _sawmill.Warning($"скилл не разобрался: {path}");
                        continue;
                    }

                    loaded[skill.Name] = skill;
                }
            }
        }
        catch (Exception e)
        {
            // Keep whatever is already in memory: a transient read error must not silently empty
            // the library the agent spent the round building.
            _sawmill.Warning($"библиотека скиллов не читается: {e.Message}");
            return;
        }

        lock (_sync)
        {
            _skills.Clear();
            foreach (var (name, skill) in loaded)
                _skills[name] = skill;
        }
    }

    private string PathFor(string name) => Path.Combine(_dir, $"{name}.md");

    /// <summary>
    /// Write one skill out. Same policy as <c>MemoryStore.TrySave</c>: report the failure rather
    /// than let the in-memory copy diverge from disk, because "saved" that was not saved is the one
    /// answer the curator has no way to detect.
    /// </summary>
    private bool TrySave(Skill skill, out string error)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(skill.Name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Render(skill));
            File.Move(tmp, path, overwrite: true);

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"скилл '{skill.Name}' не сохранён: {error}");
            return false;
        }
    }

    // -------------------------------------------------------------------- writes

    public SkillResult Write(string name, string when, string body)
    {
        name = Normalise(name);
        when = when.Trim();
        body = body.Trim();

        if (name.Length == 0)
            return new SkillResult(false, "нужно имя скилла");

        if (when.Length == 0)
            return new SkillResult(false, "нужна строка 'когда' — по ней скилл и находится");

        if (when.Length > MaxWhenLength)
            return new SkillResult(false,
                $"'когда' длиннее {MaxWhenLength} символов ({when.Length}). Это единственная строка, " +
                "попадающая в системный промпт: всё за пределом просто не доедет до индекса и " +
                "скилл никогда не сработает. Сократи.");

        if (body.Length > MaxBodyLength)
            return new SkillResult(false, $"тело длиннее {MaxBodyLength} символов ({body.Length})");

        lock (_sync)
        {
            // Duplicate stopper. Prompt-level pleading did not work on the mcbot deployment — the
            // model kept creating safe_mine_ore next to mine_ore — so this is mechanical.
            if (!_skills.ContainsKey(name))
            {
                var clash = FindOverlapping(name);
                if (clash != null)
                    return new SkillResult(false,
                        $"уже есть похожий скилл '{clash}'. Не плоди близнецов — дополни его через skill_edit. " +
                        "Если имя осмысленно только для сегодняшней задачи, оно неправильное.",
                        new[] { clash });
            }

            var skill = new Skill(name, when, body);

            if (!TrySave(skill, out var error))
                return new SkillResult(false, $"на диск записать не удалось, скилл не сохранён ({error})");

            var existed = _skills.ContainsKey(name);
            _skills[name] = skill;

            return new SkillResult(true, existed ? "скилл обновлён" : "скилл создан");
        }
    }

    /// <summary>A skill whose name shares a meaningful word with the proposed one.</summary>
    private string? FindOverlapping(string name)
    {
        var words = Words(name);

        foreach (var existing in _skills.Keys)
        {
            if (Words(existing).Any(w => words.Contains(w)))
                return existing;
        }

        return null;

        static HashSet<string> Words(string n) =>
            n.Split('-', '_', ' ')
                .Where(w => w.Length > 2)
                .ToHashSet();
    }

    /// <summary>
    /// Edit by fragment, never wholesale — same reason as memory: whoever may rewrite the file
    /// entirely will eventually return a shortened version and lose everything else in it.
    /// </summary>
    public SkillResult Edit(string name, string oldText, string newText)
    {
        name = Normalise(name);
        oldText = oldText.Trim();

        lock (_sync)
        {
            if (!_skills.TryGetValue(name, out var skill))
                return new SkillResult(false, $"нет скилла '{name}'", NearestLocked(name));

            if (oldText.Length == 0)
            {
                // Empty match means append — the common case of "I learned one more gotcha".
                var appended = skill with { Body = (skill.Body + "\n" + newText.Trim()).Trim() };
                if (appended.Body.Length > MaxBodyLength)
                    return new SkillResult(false, $"тело превысит {MaxBodyLength} символов — сначала выбрось лишнее");

                if (!TrySave(appended, out var appendError))
                    return new SkillResult(false, $"на диск записать не удалось, скилл не изменён ({appendError})");

                _skills[name] = appended;
                return new SkillResult(true, "дописано в конец");
            }

            var count = CountOccurrences(skill.Body, oldText);

            if (count == 0)
                return new SkillResult(false,
                    "такого фрагмента в теле скилла нет — ты помнишь текст неточно, открой его через " +
                    "skill_view и скопируй дословно");

            if (count > 1)
                return new SkillResult(false, "фрагмент встречается несколько раз — возьми подлиннее");

            var body = skill.Body.Replace(oldText, newText.Trim(), StringComparison.Ordinal).Trim();

            if (body.Length > MaxBodyLength)
                return new SkillResult(false, $"тело превысит {MaxBodyLength} символов");

            var edited = skill with { Body = body };

            if (!TrySave(edited, out var error))
                return new SkillResult(false, $"на диск записать не удалось, скилл не изменён ({error})");

            _skills[name] = edited;

            return new SkillResult(true, newText.Trim().Length == 0 ? "фрагмент удалён" : "фрагмент заменён");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
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

    public IReadOnlyList<string> Nearest(string name, int count = 3)
    {
        lock (_sync)
            return NearestLocked(name, count);
    }

    private IReadOnlyList<string> NearestLocked(string name, int count = 3) =>
        _skills.Keys
            .OrderBy(k => Tools.AiToolRegistry.Distance(k, name))
            .Take(count)
            .ToList();

    // -------------------------------------------------------------------- index

    /// <summary>The block that goes into zone 0: one line per skill, nothing else.</summary>
    public string RenderIndex()
    {
        lock (_sync)
        {
            if (_skills.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append("СКИЛЛЫ (открывай через skill_view, когда ситуация совпадает):\n");

            // Sorted so the block is a deterministic function of the library — an unstable order
            // here would change zone 0 on every rebuild for no reason.
            foreach (var skill in _skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
                sb.Append(skill.IndexLine).Append('\n');

            return sb.ToString();
        }
    }
}
