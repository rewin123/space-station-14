using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Guidebook;
using Robust.Shared.ContentPack;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// The game's built-in wiki — the one the player opens with a key press.
///
/// <para>
/// Mounted from PROTOTYPES, not a directory, and that matters more than it looks. The identifier,
/// localized name, list of children, and text path already live in
/// <c>Resources/Prototypes/Guidebook/*.yml</c>. That is, the tree, names, and order come from the
/// game itself: sections match what the player sees, there are no orphan articles by construction,
/// and when upstream updates, the agent's wiki doesn't diverge from the real one. Walking a
/// directory would give a flat pile of files with machine names and no hierarchy.
/// </para>
/// <para>
/// Content is served as raw markup. It's readable — text dominates over tags in it — and a
/// converter would become one more place that silently loses a paragraph whenever upstream changes
/// its format. Why this is needed at all: the agent says "check the pump", while the player looks
/// at a panel labeled <c>Gas Volume Pump</c>. English machine names exist only here.
/// </para>
/// </summary>
public sealed class GuidebookMount : VfsMount
{
    private readonly IPrototypeManager _proto;
    private readonly IResourceManager _res;
    private readonly ISawmill _sawmill;

    /// <summary>
    /// Path within the mount → article. Built ONLY on the main thread, in <see cref="Reload"/>.
    ///
    /// <para>
    /// Building it lazily doesn't work, and that was found on a live server, not in a test.
    /// Filesystem tools are deliberately not marshaled — they touch files, not entities — so the
    /// first access comes from the agent thread. And an article's name is resolved via
    /// <c>Loc.GetString</c>, which reaches into <c>IoCManager.Resolve</c>, which throws "IoC has no
    /// context on this thread" on a foreign thread. The whole call would fail: <c>ls /</c> and a
    /// pathless <c>grep</c> walk every mount, including this one.
    /// </para>
    /// </summary>
    private Dictionary<string, Article>? _index;

    /// <summary>A wiki article: the prototype plus its ALREADY-resolved name.</summary>
    /// <remarks>
    /// The name is resolved while building the index, on the main thread, and only read afterward.
    /// Keeping just the prototype here would mean calling localization from a listing call, i.e.
    /// from the agent thread.
    /// </remarks>
    private sealed record Article(GuideEntryPrototype Entry, string Title);

    /// <summary>Path → article body. Filled lazily: nobody needs a megabyte and a half all at once.</summary>
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    private readonly object _sync = new();

    public GuidebookMount(IPrototypeManager proto, IResourceManager res, ISawmill sawmill)
    {
        _proto = proto;
        _res = res;
        _sawmill = sawmill;
    }

    // ------------------------------------------------------------------- index

    /// <summary>
    /// The ready index. If it hasn't been built yet, empty rather than built on the spot.
    /// </summary>
    /// <remarks>
    /// An empty response means "the wiki isn't mounted", and that's more honest than building the
    /// index from the agent thread and having the whole tool call fail on localization.
    /// </remarks>
    private Dictionary<string, Article> Index()
    {
        lock (_sync)
            return _index ?? Empty;
    }

    private static readonly Dictionary<string, Article> Empty = new(StringComparer.Ordinal);

    /// <summary>Build the index. MAIN THREAD ONLY: resolves names via localization.</summary>
    private void BuildIndex()
    {
        lock (_sync)
        {
            var index = new Dictionary<string, Article>(StringComparer.Ordinal);
            var all = _proto.EnumeratePrototypes<GuideEntryPrototype>().ToDictionary(p => p.ID, StringComparer.Ordinal);

            // Roots are whoever nobody named as a child. Determined by fact, not by a flag: a
            // prototype has no separate "top-level" marker, and tagging one by hand would mean
            // maintaining a second list that would drift from the first.
            var children = all.Values.SelectMany(p => p.Children).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var root in all.Values
                         .Where(p => !children.Contains(p.ID))
                         .OrderByDescending(p => p.Priority)
                         .ThenBy(p => p.ID, StringComparer.Ordinal))
            {
                Walk(root, string.Empty, all, index, new HashSet<string>(StringComparer.Ordinal));
            }

            _index = index;
        }
    }

    private void Walk(
        GuideEntryPrototype entry,
        string prefix,
        IReadOnlyDictionary<string, GuideEntryPrototype> all,
        Dictionary<string, Article> index,
        HashSet<string> seen)
    {
        // Upstream's wiki is a graph, not a tree: the same article is legitimately hung under two
        // sections. Without this check, such a crossing would turn into an infinite traversal.
        if (!seen.Add(entry.ID))
            return;

        // The prototype's identifier as is: this is the article's real name in the wiki, and
        // adapting it to our own conventions would mean diverging from what the player sees.
        var path = prefix.Length == 0 ? entry.ID : prefix + "/" + entry.ID;

        index[path] = new Article(entry, Title(entry));

        foreach (var child in entry.Children)
        {
            if (all.TryGetValue(child.Id, out var proto))
                Walk(proto, path, all, index, seen);
        }

        seen.Remove(entry.ID);
    }

    /// <summary>Resolve the name. Main thread only — called from <see cref="BuildIndex"/>.</summary>
    private static string Title(GuideEntryPrototype entry)
    {
        // The name is a localization key. If there's no translation, Loc returns the key itself,
        // and showing "guide-entry-apc" makes less sense than the identifier.
        var name = Loc.GetString(entry.Name);
        return name == entry.Name ? entry.ID : name;
    }

    // ------------------------------------------------------------------- reading

    public override IReadOnlyList<VfsEntry> List(VfsPath relative, out string error)
    {
        error = string.Empty;

        var index = Index();
        var prefix = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments) + "/";

        if (!relative.IsRoot && !index.ContainsKey(string.Join('/', relative.Segments)))
        {
            error = $"нет раздела «/{Point}/{string.Join('/', relative.Segments)}»";
            return Array.Empty<VfsEntry>();
        }

        var result = new List<VfsEntry>();

        foreach (var (path, article) in index)
        {
            if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = prefix.Length == 0 ? path : path[prefix.Length..];

            if (rest.Length == 0 || rest.Contains('/'))
                continue;

            var kids = index.Keys.Count(k => k.StartsWith(path + "/", StringComparison.Ordinal));
            result.Add(new VfsEntry(rest, kids > 0, article.Title, kids, null));
        }

        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public override bool TryRead(VfsPath relative, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        var key = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments);
        var index = Index();

        if (key.Length == 0 || !index.TryGetValue(key, out var article))
        {
            error = $"нет статьи «/{Point}/{key}»";
            return false;
        }

        content = Body(key, article.Entry);

        if (content.Length == 0)
        {
            error = $"статья «/{Point}/{key}» не читается";
            return false;
        }

        content = $"# {article.Title}\n{content}";
        return true;
    }

    private string Body(string key, GuideEntryPrototype entry)
    {
        lock (_sync)
        {
            if (_text.TryGetValue(key, out var cached))
                return cached;
        }

        var text = string.Empty;

        try
        {
            using var reader = _res.ContentFileReadText(entry.Text);
            text = reader.ReadToEnd().Trim();
        }
        catch (Exception e)
        {
            _sawmill.Warning($"статья вики {entry.Text} не читается: {e.Message}");
        }

        lock (_sync)
            _text[key] = text;

        return text;
    }

    public override IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit)
    {
        var hits = new List<VfsHit>();
        var index = Index();
        var prefix = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments);

        foreach (var (key, article) in index.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (prefix.Length > 0
                && !key.StartsWith(prefix + "/", StringComparison.Ordinal)
                && key != prefix)
            {
                continue;
            }

            var line = 0;

            foreach (var text in Body(key, article.Entry).Split('\n'))
            {
                line++;

                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new VfsHit($"/{Point}/{key}", line, text.Trim()));

                if (hits.Count >= limit)
                    return hits;
            }
        }

        return hits;
    }

    /// <summary>Rebuild the index and clear the body cache. MAIN THREAD ONLY.</summary>
    public override void Reload()
    {
        lock (_sync)
            _text.Clear();

        BuildIndex();
    }

    /// <summary>How many articles are in the wiki. For checking at startup that the mount isn't empty.</summary>
    public int Count => Index().Count;
}
