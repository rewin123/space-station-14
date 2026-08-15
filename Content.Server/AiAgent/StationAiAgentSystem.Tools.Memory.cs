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
    private PlayerNoteStore? _notes;

    /// <summary>
    /// Both stores are built eagerly, at <c>Initialize</c>, rather than on first touch.
    ///
    /// Lazy construction was reachable from both the agent thread and the main thread, so two racing
    /// first touches could build two stores and quietly drop whatever the loser wrote. There is no
    /// upside to deferring: the constructor is a path join and a directory read.
    /// </summary>
    public MemoryStore Memory => _memory ?? throw new InvalidOperationException("память ещё не загружена");
    public SkillStore Skills => _skills ?? throw new InvalidOperationException("скиллы ещё не загружены");
    public PlayerNoteStore Notes => _notes ?? throw new InvalidOperationException("заметки ещё не загружены");

    /// <summary>(Re)build the stores against the current <c>ai.data_dir</c>. Main thread only.</summary>
    public void ReloadAgentFiles()
    {
        var dir = DataDir();

        var memory = new MemoryStore(dir, _sawmill);
        var skills = new SkillStore(dir, _sawmill);
        var notes = new PlayerNoteStore(dir, _sawmill);

        // Attached before the load, so the initial contents arrive on the bus like any other
        // change. Attaching here rather than once at startup is the point: this method builds new
        // stores every time, and a sink bound to the old pair would go on describing a store
        // nobody writes to, with nothing anywhere reporting the divergence.
        if (_bus != null)
        {
            var sink = _bus.ForProcess();
            memory.AttachSink(sink);
            skills.AttachSink(sink);
            notes.AttachSink(sink);
        }

        memory.LoadFromDisk();
        skills.LoadFromDisk();
        notes.LoadFromDisk();

        _memory = memory;
        _skills = skills;
        _notes = notes;

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
            Description = "Твоя долгая память о станции и мире. Про людей сюда не пиши — для них " +
                          "есть заметки по человеку. Правка только фрагментом; целиком файл " +
                          "переписать нельзя.",
            SchemaJson = """
                {"type":"object","required":["action"],"additionalProperties":false,"properties":{
                "action":{"type":"string","enum":["add","replace","remove"]},
                "content":{"type":"string","description":"Текст записи для add и replace."},
                "match":{"type":"string","description":"Фрагмент существующей записи для replace и remove."}}}
                """,
            Handler = (a, ct) => Task.FromResult(MemoryTool(a)),
        });

        // Заметки о людях. Не GameAction — по той же причине, что память и скиллы: это работа с
        // файлами, она обязана работать и из интелликарты, и на разборе отрезка, иначе куратор,
        // которому и поручено записывать людей, получал бы review_mode.
        r.Register(new AiTool
        {
            Name = "read_player_related_memory",
            Description = "Открыть свои заметки о человеке. Заметки живут между сменами; у каждой " +
                          "записи спереди стоит номер раунда — прошлораундовое за сегодняшнее не выдавай.",
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Имя человека так, как оно звучит в ARRIVAL, на рации и в манифесте."}}}
                """,
            Handler = (a, ct) => Task.FromResult(ReadPlayerNote(a)),
        });

        r.Register(new AiTool
        {
            Name = "edit_player_related_memory",
            Description = "Записать или поправить заметку о человеке. Пустой 'old' — дописать новую " +
                          "запись, номер раунда и дату проставлю я. Пустой 'new' — удалить запись. " +
                          "Правка только фрагментом; заметку целиком переписать нельзя.",
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Имя человека."},
                "old":{"type":"string","description":"Дословный фрагмент существующей записи. Пусто — добавить новую."},
                "new":{"type":"string","description":"Новый текст записи. Пусто — удалить найденную."}}}
                """,
            Handler = (a, ct) => Task.FromResult(EditPlayerNote(a)),
        });

        r.Register(new AiTool
        {
            Name = "search_player_related_notes",
            Description = "Найти, про кого у тебя есть заметки, если имя расслышал неточно. " +
                          "Пустой запрос перечислит всех.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "approx_name":{"type":"string","description":"Имя примерно: часть, фамилия, услышанное на слух."}}}
                """,
            Handler = (a, ct) => Task.FromResult(SearchPlayerNotes(a)),
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

        TryGetString(args, "content", out var content);
        TryGetString(args, "match", out var match);

        var result = action!.ToLowerInvariant() switch
        {
            "add" => Memory.Add(content ?? ""),
            "replace" => Memory.Replace(match ?? "", content ?? ""),
            "remove" => Memory.Remove(match ?? ""),
            _ => new MemoryResult(false, $"нет действия '{action}'"),
        };

        if (!result.Ok)
        {
            // Hand back the current entries on failure: the model needs to see what is actually
            // there to consolidate, and asking it to guess wastes the retry.
            var fail = ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Entries);
            return fail;
        }

        _sawmill.Info($"[LLM] память {action}: {result.Usage}");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["result"] = result.Message,
            ["usage"] = result.Usage,
            ["note"] = "записано на диск; в системном промпте появится при следующей перестройке префикса",
        });
    }

    // ------------------------------------------------------------ заметки о людях

    private ToolResult ReadPlayerNote(JsonElement args)
    {
        if (!TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail(ToolError.BadArgs,
                "нужно имя человека — то, как он звучит в ARRIVAL и на рации");

        var result = Notes.Read(name);

        if (!result.Ok)
        {
            var near = Notes.Search(name).Select(r => r.Name).Take(3).ToList();

            // retry:"none", когда похожих нет вовсе. С other_target модель начнёт перебирать
            // написания имени и сожжёт ход на человека, о котором ей просто нечего вспомнить.
            return ToolResult.Fail(ToolError.BadArgs,
                near.Count > 0
                    ? result.Message
                    : $"{result.Message}, и похожих имён тоже нет — это новый для тебя человек",
                retry: near.Count > 0 ? "other_target" : "none",
                alternatives: near.Count > 0 ? near : null);
        }

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["человек"] = result.Message,
            ["записей"] = result.Entries!.Count,
            ["заметки"] = result.Entries,
            ["usage"] = result.Usage,
        });
    }

    private ToolResult EditPlayerNote(JsonElement args)
    {
        if (!TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail(ToolError.BadArgs, "нужно имя человека");

        TryGetString(args, "old", out var old);
        TryGetString(args, "new", out var fresh);

        var hasOld = !string.IsNullOrWhiteSpace(old);
        var hasNew = !string.IsNullOrWhiteSpace(fresh);

        if (!hasOld && !hasNew)
            return ToolResult.Fail(ToolError.BadArgs,
                "нечего записывать: заполни 'new', чтобы дописать, или укажи 'old', чтобы поправить");

        // Пустой 'old' — дописать: тот же контракт, что у skill_edit, который модель уже знает.
        // Отличие одно: здесь это ещё и заводит заметку, что снимает танец «создай, потом правь»
        // на самой первой встрече с человеком.
        var result = (hasOld, hasNew) switch
        {
            (false, true) => Notes.Add(name, fresh!, NoteStamp()),
            (true, true) => Notes.Replace(name, old!, fresh!),
            (true, false) => Notes.Remove(name, old!),
            _ => new NoteResult(false, "нечего записывать"),
        };

        if (!result.Ok)
            return ToolResult.Fail(ToolError.BadArgs, result.Message, alternatives: result.Entries);

        _sawmill.Info($"[LLM] заметка о «{name}»: {result.Message} ({result.Usage})");

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["человек"] = name,
            ["result"] = result.Message,
            ["usage"] = result.Usage,
        });
    }

    private ToolResult SearchPlayerNotes(JsonElement args)
    {
        TryGetString(args, "approx_name", out var approx);

        var found = Notes.Search(approx);

        var d = new Dictionary<string, object?>();

        // Пустой результат — успех, а не отказ: «этот человек мне незнаком» полноценный ответ,
        // и отказ научил бы модель, что искать было ошибкой.
        AddRows(d, "найдено",
            found.Select(r => $"{r.Name} — записей {r.Entries} — {r.Preview}").ToList(),
            10,
            "слишком много похожих — уточни имя");

        if (found.Count == 0)
            d["note"] = "ни на одно похожее имя заметок нет";

        return ToolResult.Success(d);
    }
}
