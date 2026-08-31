using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vfs;
using Content.Server.AiAgent.Vfs.Mounts;
using Robust.Shared.ContentPack;

namespace Content.Server.AiAgent;

/// <summary>
/// Файловая система агента: сборка, общий слой и три инструмента поверх.
///
/// <para>
/// Раньше здесь было семь инструментов и три хранилища в одном экземпляре на процесс. Семь стали
/// тремя не ради красоты: на этом же железе и этом же кванте померено, что 46 узких команд топят
/// модель, а около тринадцати работают, и ширина набирается объединением, а не новыми записями в
/// реестре. Три хранилища стали своими у каждого тела, потому что общими они были случайно —
/// киборг таскал в префиксе досье Станционного ИИ на экипаж.
/// </para>
/// <para>
/// Инструменты работают вне игрового потока: они трогают файлы, а не сущности, и потому, в
/// отличие от всех остальных, не маршалятся. Ни один не помечен <c>GameAction</c> — и это
/// несущее свойство, а не недосмотр: именно оно позволяет куратору писать на разборе отрезка,
/// когда игровые инструменты отвечают <c>review_mode</c>.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// Справочник — один экземпляр на процесс, общий всем телам.
    ///
    /// <para>
    /// Разделяется именно ЭКЗЕМПЛЯР, а не каталог: 226 статей весят полтора мегабайта, и копия на
    /// каждое из четырёх тел стоила бы вчетверо больше памяти и вчетверо больше обходов диска на
    /// каждой перестройке префикса — внутри ритуала компакции, где и так платится prefill.
    /// </para>
    /// </summary>
    private DocTree? _library;

    /// <summary>Вика игры. Тоже одна на процесс: её дерево строится из прототипов и от тела не зависит.</summary>
    private GuidebookMount? _guidebook;

    [Dependency] private IResourceManager _resources = default!;

    /// <summary>
    /// Перечитать общий слой. Главный поток, при старте системы и по команде консоли.
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
            Description = "вика игры по-английски: точные имена машин на экранах экипажа",
            Access = VfsAccess.Read,
            Shared = true,
        };

        _guidebook.Reload();

        // Снимок сессий привязан к каталогу; стенд, переставивший ai.data_dir, не должен писать в
        // папку предыдущего сценария.
        _sessionStore = null;
    }

    /// <summary>
    /// Собрать файловую систему одного тела.
    ///
    /// <para>
    /// Единственное место, где перечислена таблица монтирований. Порядок вызовов — это порядок
    /// строк в зоне 0, поэтому переставлять их просто так нельзя: другой порядок — другой SHA
    /// префикса, то есть полный prefill каждый ход и ни одной ошибки в логе.
    /// </para>
    /// </summary>
    public Vfs.Vfs BuildVfs(string agentId)
    {
        // Сверяемся с текущим ai.data_dir, а не только с «построен ли он».
        //
        // Стенды переставляют каталог ПОСЛЕ Initialize, и справочник, собранный на старом корне,
        // молча остался бы читать чужую папку: сессия поднялась бы, а статей в ней не было бы ни
        // одной. Ровно та поломка, которую в игре видно как «агент разучился».
        if (_library == null || _guidebook == null
            || _library.Root != System.IO.Path.Combine(DataDir(), "wiki_ru"))
        {
            ReloadSharedLibrary();
        }

        var dir = AgentDir(agentId);

        var vfs = new VfsBuilder(_sawmill)
            .AddShared(_library!, "wiki_ru", VfsAccess.Read,
                "справочник по игре: отделы, машины, процедуры")
            .AddShared(_guidebook!)
            .AddFolder(System.IO.Path.Combine(dir, "skills"), "skills", VfsAccess.Write,
                "что ты понял сам")
            .AddNotes(dir, "players", VfsAccess.Write,
                "твои заметки о людях, по файлу на человека", NoteStamp)
            .AddMemory(dir, "memory.md", VfsAccess.Write,
                "факты о станции и мире — они же в блоке ПАМЯТЬ выше")
            .AddText(RoleFile(dir, "CURATOR.md"), "curator.md", VfsAccess.Read,
                "чем ты руководствуешься на разборе отрезка")
            .Build();

        if (_bus != null)
            vfs.AttachSink(_bus.ForProcess());

        return vfs;
    }

    /// <summary>
    /// Файл, привязанный к роли: свой каталог перебивает общий <c>ai_data/</c>.
    ///
    /// Та же цепочка, что у личности, и по той же причине: идентификаторы боргов выдаёт аллокатор
    /// (<c>combat-1</c>, <c>combat-2</c>, …), и держать копию файла под каждый возможный номер
    /// бессмысленно — файл привязан к роли, а не к экземпляру.
    /// </summary>
    public string RoleFile(string agentDir, string file)
    {
        var own = System.IO.Path.Combine(agentDir, file);
        return System.IO.File.Exists(own) ? own : System.IO.Path.Combine(DataDir(), file);
    }

    /// <summary>
    /// Долгая память ЯДРА. Не «память процесса»: у каждого тела она своя.
    /// </summary>
    /// <remarks>
    /// Существует ради консоли, отладчика и тестов, которым нужен именно тот агент, ради которого
    /// их и открывают. Код, работающий с конкретным телом, обязан ходить через
    /// <c>session.Body.Vfs</c>, иначе борг будет читать и править чужое.
    /// </remarks>
    public Skills.MemoryStore Memory =>
        CoreVfs?.Memory ?? throw new InvalidOperationException("ядро ещё не запускалось, память не смонтирована");

    /// <summary>Заметки о людях ЯДРА. Та же оговорка, что у <see cref="Memory"/>.</summary>
    public Skills.PlayerNoteStore Notes =>
        CoreVfs?.Notes ?? throw new InvalidOperationException("ядро ещё не запускалось, заметки не смонтированы");

    /// <summary>
    /// Файловая система живого агента по его идентификатору — для консоли и отладчика.
    /// </summary>
    /// <remarks>
    /// Ищется среди работающих сессий, а не собирается заново: собранная копия открыла бы вторые
    /// экземпляры сторов поверх тех же файлов, и правка через консоль разошлась бы с тем, что
    /// видит сам агент.
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

    // ------------------------------------------------------------------ инструменты

    private void RegisterVfsTools(AgentSession s, AiToolRegistry r)
    {
        r.Register(new AiTool
        {
            Name = "sh",
            Description = "Ходить по своим файлам: ls, tree, cat, grep, find, mkdir, rm, mv. " +
                          "Путь всегда полный, от корня. Труб и редиректов нет.",
            SchemaJson = """
                {"type":"object","required":["cmd"],"additionalProperties":false,"properties":{
                "cmd":{"type":"string","description":"Одна команда целиком, например: grep насос /wiki_ru"}}}
                """,
            Handler = (a, ct) => Task.FromResult(RunShell(s, a)),
        });

        r.Register(new AiTool
        {
            Name = "write_file",
            Description = "Создать файл или переписать целиком. 'desc' — не длиннее 60 символов, " +
                          "это единственная строка, которую видно в ls.",
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
            Description = "Правка фрагментом: найти кусок текста и заменить. Пустой 'match' — " +
                          "дописать в конец, пустой 'replacement' — удалить фрагмент.",
            SchemaJson = """
                {"type":"object","required":["path"],"additionalProperties":false,"properties":{
                "path":{"type":"string"},
                "match":{"type":"string","description":"Дословный фрагмент. Пусто — дописать в конец."},
                "replacement":{"type":"string","description":"Чем заменить. Пусто — удалить фрагмент."}}}
                """,
            Handler = (a, ct) => Task.FromResult(EditFile(s, a)),
        });
    }

    // -------------------------------------------------------------------- обработчики

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

        _sawmill.Info($"[LLM] {s.Body.Id}: правка {path}");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["result"] = result.Message,
        });
    }

    /// <summary>Разобрать путь и найти монтирование, или вернуть готовый отказ.</summary>
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
