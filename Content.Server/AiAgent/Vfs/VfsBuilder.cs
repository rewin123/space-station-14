using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.AiAgent.Locale;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Vfs.Mounts;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Vfs;

/// <summary>
/// The one and only way to assemble the agent's filesystem.
///
/// <code>
/// var vfs = new VfsBuilder(sawmill)
///     .AddShared(library,                          "wiki_ru",   VfsAccess.Read,  "справочник по игре")
///     .AddGuidebook(proto, res,                    "wiki_en",   VfsAccess.Read,  "вика игры по-английски")
///     .AddFolder(Path.Combine(dir, "skills"),      "skills",    VfsAccess.Write, "что ты понял сам")
///     .AddNotes (Path.Combine(dir, "people"),      "players",   VfsAccess.Write, "заметки о людях", Stamp)
///     .AddMemory(Path.Combine(dir, "memory"),      "memory.md", VfsAccess.Write, "факты о станции")
///     .AddText  (RoleFile(dir, "CURATOR.md"),      "curator.md",VfsAccess.Read,  "чем ты руководствуешься на разборе")
///     .Build();
/// </code>
///
/// <para>
/// There are five verbs, and each names what stands behind it, instead of pretending all mounts
/// share one machinery. Behind <see cref="AddNotes"/> and <see cref="AddMemory"/> stand untouched
/// stores with round stamps and limits; behind <see cref="AddGuidebook"/> stand prototypes that
/// have no on-disk directory at all. One shared <c>AddFolder(path, point, access)</c> would force
/// every reader to guess what actually happens to their files.
/// </para>
/// </summary>
public sealed class VfsBuilder
{
    private readonly ISawmill _sawmill;
    private readonly List<VfsMount> _mounts = new();

    /// <summary>Contradictions in the mount table itself. Collected all at once and fail <see cref="Build"/>.</summary>
    private readonly List<string> _problems = new();

    /// <summary>
    /// Content troubles: an empty reference library, an unreadable directory.
    ///
    /// <para>
    /// Deliberately separate from <see cref="_problems"/>. A malformed mount table is a programmer
    /// error, and failing on it is correct. An empty wiki is a deployment problem, and failing on
    /// it is NOT ALLOWED: an exception while assembling the body means the agent won't appear on
    /// the station at all — a round with no AI instead of a round with an AI that doesn't know the
    /// reference library. The second is worse, but the first is catastrophic.
    /// </para>
    /// <para>
    /// Staying silent about it isn't allowed either — "the agent forgot how" gets debugged for days
    /// with not a single line in the log. So it's loud: an <c>Error</c> to the sawmill and a list on
    /// <see cref="Vfs.Complaints"/> itself, where tests and the debugger can see it.
    /// </para>
    /// </summary>
    private readonly List<string> _complaints = new();

    public VfsBuilder(ISawmill sawmill)
    {
        _sawmill = sawmill;
    }

    // --------------------------------------------------------------- mounts

    /// <summary>Tree of articles on disk. The general case: both the reference library and the agent's own notes.</summary>
    public VfsBuilder AddFolder(string diskPath, string point, VfsAccess access, string description)
    {
        var tree = new DocTree(diskPath, _sawmill);

        if (access == VfsAccess.Write)
            Ensure(diskPath);

        tree.Reload();

        if (access == VfsAccess.Read && tree.Count == 0)
            _complaints.Add($"/{point}: каталог {diskPath} пуст или не читается, а смонтирован только на чтение");

        return Add(new DocMount
        {
            Point = point,
            Description = description,
            Access = access,
            Tree = tree,
        });
    }

    /// <summary>
    /// An already-built tree, shared across all agents.
    ///
    /// <para>
    /// It's the INSTANCE that's shared, not the directory. The reference library weighs a megabyte
    /// and a half; a copy per each of the four bodies would be four times the memory and four times
    /// the disk traversals on every prefix rebuild, and that's inside the compaction ritual.
    /// </para>
    /// </summary>
    public VfsBuilder AddShared(DocTree tree, string point, VfsAccess access, string description)
    {
        if (access == VfsAccess.Read && tree.Count == 0)
            _complaints.Add($"/{point}: общее дерево {tree.Root} пусто, а смонтировано только на чтение");

        return Add(new DocMount
        {
            Point = point,
            Description = description,
            Access = access,
            Shared = true,
            Tree = tree,
        });
    }

    /// <summary>
    /// Notes about people: an untouched <see cref="PlayerNoteStore"/> underneath the mount.
    /// </summary>
    /// <param name="agentDir">
    /// The AGENT's directory, not the notes folder: the store appends "people" to it itself, and
    /// passing an already-built path would mean introducing a second way to compute the same thing.
    /// </param>
    public VfsBuilder AddNotes(
        string agentDir,
        string point,
        VfsAccess access,
        string description,
        Func<string> stamp)
    {
        Ensure(Path.Combine(agentDir, "people"));

        var store = new PlayerNoteStore(agentDir, _sawmill);
        store.LoadFromDisk();

        return Add(new NotesMount
        {
            Point = point,
            Description = description,
            Access = access,
            Store = store,
            Stamp = stamp,
        });
    }

    /// <summary>Long-term memory: an untouched <see cref="MemoryStore"/> underneath the mount.</summary>
    /// <param name="agentDir">The agent's directory: the store appends "memory" to it itself.</param>
    /// <param name="limit">
    /// The memory ceiling in characters. Taken as a parameter, not a property set after assembly:
    /// the store declares it <c>init</c>, and rightly so — a ceiling that can be moved on the fly
    /// isn't a ceiling.
    /// </param>
    public VfsBuilder AddMemory(
        string agentDir,
        string point,
        VfsAccess access,
        string description,
        int limit = 4000)
    {
        Ensure(Path.Combine(agentDir, "memory"));

        var store = new MemoryStore(agentDir, _sawmill) { MemoryLimit = limit };
        store.LoadFromDisk();
        store.RefreshSnapshot();

        return Add(new MemoryMount
        {
            Point = point,
            Description = description,
            Access = access,
            Store = store,
        });
    }

    /// <summary>
    /// An already-assembled shared mount — the game's wiki, for example.
    ///
    /// The instance is shared per process: <see cref="Mounts.GuidebookMount"/> builds its tree from
    /// prototypes and doesn't depend on the body, and rebuilding it for each of the four agents
    /// would mean four traversals of all the wiki prototypes instead of one.
    /// </summary>
    public VfsBuilder AddShared(VfsMount mount)
    {
        if (!mount.Shared)
            _problems.Add($"/{mount.Point}: передано в AddShared, но не помечено общим");

        return Add(mount);
    }

    /// <summary>The game's wiki: the tree and names come from prototypes, it has no disk.</summary>
    public VfsBuilder AddGuidebook(
        IPrototypeManager proto,
        IResourceManager res,
        string point,
        VfsAccess access,
        string description)
    {
        var mount = new GuidebookMount(proto, res, _sawmill)
        {
            Point = point,
            Description = description,
            Access = access,
            Shared = true,
        };

        return Add(mount);
    }

    /// <summary>A single text file with no split into "when" and body.</summary>
    public VfsBuilder AddText(string file, string point, VfsAccess access, string description) =>
        Add(new TextMount
        {
            Point = point,
            Description = description,
            Access = access,
            File = file,
        });

    // -------------------------------------------------------------------- assembly

    /// <summary>
    /// Assemble. Fails on a contradictory table rather than adapting to it.
    ///
    /// <para>
    /// Errors are collected all at once and reported as a single exception. Failing on the first
    /// one would mean fixing the table one line per server restart.
    /// </para>
    /// </summary>
    public Vfs Build(AgentLang lang = AgentLang.Ru)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mount in _mounts)
        {
            if (mount.Point.Length == 0 || mount.Point.Contains('/'))
                _problems.Add($"точка монтирования «{mount.Point}» должна быть одним сегментом без слэшей");

            if (!seen.Add(mount.Point))
                _problems.Add($"точка монтирования «/{mount.Point}» объявлена дважды");

            if (mount.Description.Length == 0)
                _problems.Add($"/{mount.Point}: нет описания, а оно едет в системный промпт");
        }

        if (_mounts.Count == 0)
            _problems.Add("не объявлено ни одного монтирования");

        if (_problems.Count > 0)
            throw new InvalidOperationException(
                "файловая система агента не собирается:\n  " + string.Join("\n  ", _problems));

        foreach (var complaint in _complaints)
            _sawmill.Error($"файловая система: {complaint}");

        return new Vfs(_mounts, _complaints, lang);
    }

    private VfsBuilder Add(VfsMount mount)
    {
        _mounts.Add(mount);
        return this;
    }

    /// <summary>
    /// Create a directory ahead of time, for writing.
    ///
    /// <para>
    /// The first borg arrives on the station with an empty directory, and its very first write
    /// would otherwise run into a missing folder — that is, into a failure that looks like "the
    /// agent can't write". Creating an empty directory is cheaper than explaining this in the prompt.
    /// </para>
    /// </summary>
    private void Ensure(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e)
        {
            _problems.Add($"каталог {path} не создаётся: {e.GetType().Name}: {e.Message}");
        }
    }
}
