using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Locale;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vfs;
using Content.Server.AiAgent.Vfs.Mounts;
using Robust.Shared.ContentPack;

namespace Content.Server.AiAgent;

/// <summary>
/// The agent's filesystem: assembly, the shared layer, and three tools on top of it.
///
/// <para>
/// This used to be seven tools and three stores in one instance per process. Seven became three
/// not for tidiness: measured on this same hardware and this same quantum, 46 narrow commands
/// swamp the model while around thirteen work, and breadth is gained by merging tools, not by
/// adding new registry entries. The three stores became per-body because being shared was an
/// accident — the borg was carrying the Station AI's crew dossier in its prefix.
/// </para>
/// <para>
/// The tools run off the game thread: they touch files, not entities, and so, unlike everything
/// else, they aren't marshalled. None of them is marked <c>GameAction</c> — and that's a load-bearing
/// property, not an oversight: it's exactly what lets the curator write during a segment review,
/// when game tools answer with <c>review_mode</c>.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// The reference library — one instance per process, shared by every body.
    ///
    /// <para>
    /// What's shared is specifically the INSTANCE, not the folder: 226 articles weigh a megabyte and
    /// a half, and a copy for each of four bodies would cost four times the memory and four times the
    /// disk traversals on every prefix rebuild — inside the compaction ritual, which already pays for
    /// the prefill.
    /// </para>
    /// </summary>
    private DocTree? _library;

    /// <summary>The in-game guidebook. Also one per process: its tree is built from prototypes and doesn't depend on the body.</summary>
    private GuidebookMount? _guidebook;

    [Dependency] private IResourceManager _resources = default!;

    /// <summary>
    /// Reload the shared layer. Main thread, at system start and on a console command.
    /// </summary>
    public void ReloadSharedLibrary()
    {
        var root = System.IO.Path.Combine(DataDir(), "wiki_ru");

        if (_library == null || _library.Root != root)
            _library = new DocTree(root, _sawmill);

        _library.Reload();

        _guidebook ??= new GuidebookMount(_protoMan, _resources, _sawmill)
        {
            Point = "wiki_en",
            Description = AgentLocale.Ru.WikiEnDesc,
            Access = VfsAccess.Read,
            Shared = true,
        };

        _guidebook.Reload();

        // The session store is tied to the directory; a bench that repointed ai.data_dir must not
        // write into the previous scenario's folder.
        _sessionStore = null;
    }

    /// <summary>
    /// Assemble the filesystem for a single body.
    ///
    /// <para>
    /// The one place where the mount table is enumerated. The order of calls is the order of lines
    /// in zone 0, so they cannot be reordered casually: a different order means a different prefix
    /// SHA, i.e. a full prefill on every turn and not a single error in the log.
    /// </para>
    /// </summary>
    public Vfs.Vfs BuildVfs(string agentId, AgentLang lang = AgentLang.Ru)
    {
        // Checked against the current ai.data_dir, not just against "has it been built at all".
        //
        // Benches repoint the directory AFTER Initialize, and a library built against the old root
        // would silently keep reading the wrong folder: the session would come up, but with no
        // articles in it at all. Exactly the kind of breakage that shows up in-game as "the agent
        // forgot how".
        if (_library == null || _guidebook == null
            || _library.Root != System.IO.Path.Combine(DataDir(), "wiki_ru"))
        {
            ReloadSharedLibrary();
        }

        var loc = AgentLocale.Of(lang);
        _guidebook!.Description = loc.WikiEnDesc;

        var dir = AgentDir(agentId);

        var vfs = new VfsBuilder(_sawmill)
            .AddShared(_library!, "wiki_ru", VfsAccess.Read, loc.WikiRuDesc)
            .AddShared(_guidebook!)
            .AddFolder(System.IO.Path.Combine(dir, "skills"), "skills", VfsAccess.Write,
                loc.SkillsDesc)
            .AddNotes(dir, "players", VfsAccess.Write,
                loc.PlayersDesc, NoteStamp)
            .AddMemory(dir, "memory.md", VfsAccess.Write,
                loc.MemoryDesc)
            .AddText(RoleFile(dir, "CURATOR.md"), "curator.md", VfsAccess.Read,
                loc.CuratorDesc)
            .Build(lang);

        if (_bus != null)
            vfs.AttachSink(_bus.ForProcess());

        return vfs;
    }

    /// <summary>
    /// A file tied to a role: its own folder takes precedence over the shared <c>ai_data/</c>.
    ///
    /// Same chain as for the persona, and for the same reason: borg identifiers are handed out by
    /// the allocator (<c>combat-1</c>, <c>combat-2</c>, …), and keeping a copy of the file under
    /// every possible number would be pointless — the file is tied to the role, not the instance.
    /// </summary>
    public string RoleFile(string agentDir, string file)
    {
        var own = System.IO.Path.Combine(agentDir, file);
        return System.IO.File.Exists(own) ? own : System.IO.Path.Combine(DataDir(), file);
    }

    /// <summary>
    /// The CORE's long-term memory. Not "process memory": each body has its own.
    /// </summary>
    /// <remarks>
    /// Exists for the console, the debugger, and tests, which need exactly the agent they were
    /// opened for. Code that works with a specific body must go through <c>session.Body.Vfs</c>,
    /// otherwise a borg would end up reading and editing someone else's.
    /// </remarks>
    public Skills.MemoryStore Memory =>
        CoreVfs?.Memory ?? throw new InvalidOperationException("ядро ещё не запускалось, память не смонтирована");

    /// <summary>The CORE's notes on people. Same caveat as <see cref="Memory"/>.</summary>
    public Skills.PlayerNoteStore Notes =>
        CoreVfs?.Notes ?? throw new InvalidOperationException("ядро ещё не запускалось, заметки не смонтированы");

    /// <summary>
    /// The filesystem of a live agent by its identifier — for the console and the debugger.
    /// </summary>
    /// <remarks>
    /// Looked up among running sessions rather than assembled anew: an assembled copy would open a
    /// second set of store instances over the same files, and an edit through the console would
    /// diverge from what the agent itself sees.
    /// </remarks>
    public bool TryGetVfs(string agentId, out Vfs.Vfs vfs)
    {
        foreach (var session in _sessions.Values)
        {
            if (!string.Equals(session.Body.Id, agentId, System.StringComparison.OrdinalIgnoreCase))
                continue;

            vfs = session.Body.Vfs;
            return true;
        }

        vfs = null!;
        return false;
    }

    // ------------------------------------------------------------------ tools

    private void RegisterVfsTools(AgentSession s, AiToolRegistry r)
    {
        var L = s.Locale;

        r.Register(new AiTool
        {
            Name = "sh",
            Description = L.T(
                "Ходить по своим файлам: ls, tree, cat, grep, find, mkdir, rm, mv. " +
                "Путь всегда полный, от корня. Труб и редиректов нет.",
                "Walk your files: ls, tree, cat, grep, find, mkdir, rm, mv. " +
                "The path is always full, from the root. No pipes or redirects."),
            SchemaJson = """
                {"type":"object","required":["cmd"],"additionalProperties":false,"properties":{
                "cmd":{"type":"string","description":"Одна команда целиком, например: grep насос /wiki_ru"}}}
                """,
            Handler = (a, ct) => Task.FromResult(RunShell(s, a)),
        });

        r.Register(new AiTool
        {
            Name = "write_file",
            Description = L.T(
                "Создать файл или переписать целиком. 'desc' — не длиннее 60 символов, " +
                "это единственная строка, которую видно в ls.",
                "Create a file or overwrite it entirely. 'desc' is at most 60 characters; " +
                "that is the only line visible in ls."),
            SchemaJson = """
                {"type":"object","required":["path","desc","content"],"additionalProperties":false,"properties":{
                "path":{"type":"string","description":"Полный путь, например /skills/питание/смес."},
                "desc":{"type":"string","maxLength":60,"description":"В какой ситуации это открывать."},
                "content":{"type":"string","maxLength":5000,"description":"Само содержимое, по шагам, с граблями."}}}
                """,
            Handler = (a, ct) => Task.FromResult(WriteFile(s, a)),
        });

        r.Register(new AiTool
        {
            Name = "edit_file",
            Description = L.T(
                "Правка фрагментом: найти кусок текста и заменить. Пустой 'match' — " +
                "дописать в конец, пустой 'replacement' — удалить фрагмент.",
                "Patch a fragment: find a piece of text and replace it. Empty 'match' " +
                "appends to the end, empty 'replacement' deletes the fragment."),
            SchemaJson = """
                {"type":"object","required":["path"],"additionalProperties":false,"properties":{
                "path":{"type":"string"},
                "match":{"type":"string","description":"Дословный фрагмент. Пусто — дописать в конец."},
                "replacement":{"type":"string","description":"Чем заменить. Пусто — удалить фрагмент."}}}
                """,
            Handler = (a, ct) => Task.FromResult(EditFile(s, a)),
        });
    }

    // -------------------------------------------------------------------- handlers

    private ToolResult RunShell(AgentSession s, JsonElement args)
    {
        if (!TryGetString(args, "cmd", out var cmd) || string.IsNullOrWhiteSpace(cmd))
            return ToolResult.Fail(ToolError.BadArgs, "нужна команда, например: ls /wiki_ru");

        var result = new Shell(s.Body.Vfs).Run(cmd);

        if (!result.Ok)
        {
            return ToolResult.Fail(ToolError.BadArgs, result.Text,
                retry: "other_target", alternatives: result.Hints);
        }

        if (result.Mutated)
            _sawmill.Info($"[LLM] {s.Body.Id}: sh {cmd}");

        return ToolResult.Success(new Dictionary<string, object?> { ["out"] = result.Text });
    }

    private ToolResult WriteFile(AgentSession s, JsonElement args)
    {
        if (!TryGetString(args, "path", out var path) || string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail(ToolError.BadArgs, "нужен путь файла");

        TryGetString(args, "desc", out var desc);
        TryGetString(args, "content", out var content);

        if (!TryTarget(s, path!, out var mount, out var relative, out var failure))
            return failure!;

        var result = mount.Write(relative, desc ?? "", content ?? "");

        if (!result.Ok)
            return ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Hints);

        // Below both paths: a wire call and a Lua function both land here. This is exactly the
        // counter the curator uses to tell that the review wrote something.
        s.Body.Vfs.NoteWrite();

        _sawmill.Info($"[LLM] {s.Body.Id}: записан {path}");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["result"] = result.Message,
        });
    }

    private ToolResult EditFile(AgentSession s, JsonElement args)
    {
        if (!TryGetString(args, "path", out var path) || string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail(ToolError.BadArgs, "нужен путь файла");

        TryGetString(args, "match", out var match);
        TryGetString(args, "replacement", out var replacement);

        if (!TryTarget(s, path!, out var mount, out var relative, out var failure))
            return failure!;

        var result = mount.Edit(relative, match ?? "", replacement ?? "");

        if (!result.Ok)
            return ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Hints);

        s.Body.Vfs.NoteWrite();

        _sawmill.Info($"[LLM] {s.Body.Id}: правка {path}");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["result"] = result.Message,
        });
    }

    /// <summary>Parse the path and locate the mount, or return a ready-made failure.</summary>
    private static bool TryTarget(
        AgentSession s,
        string raw,
        out VfsMount mount,
        out VfsPath relative,
        out ToolResult? failure)
    {
        mount = null!;
        relative = VfsPath.Root;
        failure = null;

        if (!VfsPath.TryParse(raw, out var path, out var parseError))
        {
            failure = ToolResult.Fail(ToolError.BadArgs, parseError, retry: "other_target");
            return false;
        }

        if (!s.Body.Vfs.TryResolve(path, out mount, out relative, out var error))
        {
            failure = ToolResult.Fail(ToolError.BadArgs, error,
                retry: "other_target", alternatives: s.Body.Vfs.MountPoints());
            return false;
        }

        return true;
    }
}
