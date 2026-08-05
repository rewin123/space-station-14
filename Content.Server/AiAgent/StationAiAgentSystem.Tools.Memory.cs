using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent;

/// <summary>
/// Skill and memory tools. These four are what make the agent able to change itself.
///
/// They run entirely off the game thread — they touch files, not entities — so unlike every other
/// tool they do not marshal. That is deliberate: memory writes must not compete with the tick.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private MemoryStore? _memory;
    private SkillStore? _skills;

    /// <summary>
    /// Both stores are built eagerly, at <c>Initialize</c>, rather than on first touch.
    ///
    /// Lazy construction was reachable from both the agent thread and the main thread, so two racing
    /// first touches could build two stores and quietly drop whatever the loser wrote. There is no
    /// upside to deferring: the constructor is a path join and a directory read.
    /// </summary>
    public MemoryStore Memory => _memory ?? throw new InvalidOperationException("память ещё не загружена");
    public SkillStore Skills => _skills ?? throw new InvalidOperationException("скиллы ещё не загружены");

    /// <summary>(Re)build both stores against the current <c>ai.data_dir</c>. Main thread only.</summary>
    public void ReloadAgentFiles()
    {
        var dir = DataDir();

        var memory = new MemoryStore(dir, _sawmill);
        var skills = new SkillStore(dir, _sawmill);

        // Attached before the load, so the initial contents arrive on the bus like any other
        // change. Attaching here rather than once at startup is the point: this method builds new
        // stores every time, and a sink bound to the old pair would go on describing a store
        // nobody writes to, with nothing anywhere reporting the divergence.
        if (_bus != null)
        {
            var sink = _bus.ForProcess();
            memory.AttachSink(sink);
            skills.AttachSink(sink);
        }

        memory.LoadFromDisk();
        skills.LoadFromDisk();

        _memory = memory;
        _skills = skills;

        // The snapshot store is keyed off the same directory; a benchmark that repoints ai.data_dir
        // must not keep writing into the previous scenario's scratch folder.
        _sessionStore = null;
    }

    private void RegisterMemoryTools(AgentSession s, AiToolRegistry r)
    {
        r.Register(new AiTool
        {
            Name = "skill_view",
            Description = "Открыть скилл целиком по имени из индекса в системном промпте.",
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Имя скилла."}}}
                """,
            Handler = (a, ct) => Task.FromResult(SkillView(a)),
        });

        r.Register(new AiTool
        {
            Name = "skill_write",
            Description = "Создать скилл или полностью переписать существующий. 'когда' — не длиннее " +
                          "60 символов, это единственная строка, попадающая в системный промпт.",
            SchemaJson = """
                {"type":"object","required":["name","when","body"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Короткое имя на уровне класса задач, через дефис."},
                "when":{"type":"string","maxLength":60,"description":"В какой ситуации это открывать."},
                "body":{"type":"string","maxLength":5000,"description":"Как действовать, по шагам, с граблями."}}}
                """,
            Handler = (a, ct) => Task.FromResult(SkillWrite(a)),
        });

        r.Register(new AiTool
        {
            Name = "skill_edit",
            Description = "Правка скилла фрагментом: найти кусок текста и заменить. Пустой 'match' — " +
                          "дописать в конец, пустой 'replacement' — удалить фрагмент.",
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string"},
                "match":{"type":"string","description":"Дословный фрагмент тела. Пусто — дописать в конец."},
                "replacement":{"type":"string","description":"Чем заменить. Пусто — удалить фрагмент."}}}
                """,
            Handler = (a, ct) => Task.FromResult(SkillEdit(a)),
        });

        r.Register(new AiTool
        {
            Name = "memory",
            Description = "Твоя долгая память. MEMORY — факты о станции и мире, CREW — про людей. " +
                          "Правка только фрагментом; целиком файл переписать нельзя.",
            SchemaJson = """
                {"type":"object","required":["action"],"additionalProperties":false,"properties":{
                "action":{"type":"string","enum":["add","replace","remove"]},
                "file":{"type":"string","enum":["MEMORY","CREW"],"default":"MEMORY"},
                "content":{"type":"string","description":"Текст записи для add и replace."},
                "match":{"type":"string","description":"Фрагмент существующей записи для replace и remove."}}}
                """,
            Handler = (a, ct) => Task.FromResult(MemoryTool(a)),
        });
    }

    // ------------------------------------------------------------------- skills

    private ToolResult SkillView(JsonElement args)
    {
        if (!TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail(ToolError.BadArgs, "нужно имя скилла");

        if (!Skills.TryGet(name!, out var skill))
            return ToolResult.Fail(ToolError.BadArgs, $"нет скилла '{name}'",
                retry: "other_target", alternatives: Skills.Nearest(SkillStore.Normalise(name!)));

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["name"] = skill.Name,
            ["when"] = skill.When,
            ["body"] = skill.Body,
        });
    }

    private ToolResult SkillWrite(JsonElement args)
    {
        TryGetString(args, "name", out var name);
        TryGetString(args, "when", out var when);
        TryGetString(args, "body", out var body);

        var result = Skills.Write(name ?? "", when ?? "", body ?? "");

        if (!result.Ok)
            return ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Names);

        _sawmill.Info($"[LLM] скилл записан: {SkillStore.Normalise(name!)}");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["skill"] = SkillStore.Normalise(name!),
            ["result"] = result.Message,
            ["note"] = "в индекс системного промпта попадёт при следующей перестройке префикса",
        });
    }

    private ToolResult SkillEdit(JsonElement args)
    {
        TryGetString(args, "name", out var name);
        TryGetString(args, "match", out var match);
        TryGetString(args, "replacement", out var replacement);

        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail(ToolError.BadArgs, "нужно имя скилла");

        var result = Skills.Edit(name!, match ?? "", replacement ?? "");

        return result.Ok
            ? ToolResult.Success(new Dictionary<string, object?> { ["skill"] = name, ["result"] = result.Message })
            : ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Names);
    }

    // ------------------------------------------------------------------- memory

    private ToolResult MemoryTool(JsonElement args)
    {
        if (!TryGetString(args, "action", out var action) || string.IsNullOrWhiteSpace(action))
            return ToolResult.Fail(ToolError.BadArgs, "нужен 'action'",
                alternatives: new[] { "add", "replace", "remove" });

        TryGetString(args, "file", out var file);
        TryGetString(args, "content", out var content);
        TryGetString(args, "match", out var match);

        var target = string.Equals(file, "CREW", StringComparison.OrdinalIgnoreCase)
            ? MemoryTarget.Crew
            : MemoryTarget.Memory;

        var result = action!.ToLowerInvariant() switch
        {
            "add" => Memory.Add(target, content ?? ""),
            "replace" => Memory.Replace(target, match ?? "", content ?? ""),
            "remove" => Memory.Remove(target, match ?? ""),
            _ => new MemoryResult(false, $"нет действия '{action}'"),
        };

        if (!result.Ok)
        {
            // Hand back the current entries on failure: the model needs to see what is actually
            // there to consolidate, and asking it to guess wastes the retry.
            var fail = ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Entries);
            return fail;
        }

        _sawmill.Info($"[LLM] память {action} в {target}: {result.Usage}");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["result"] = result.Message,
            ["usage"] = result.Usage,
            ["note"] = "записано на диск; в системном промпте появится при следующей перестройке префикса",
        });
    }
}
