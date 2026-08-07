using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Tools;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// Phase 4: the pieces that let the agent change itself.
///
/// These guard the rules that only look like fussiness until an agent has been writing to its own
/// memory unsupervised for a few hours — at which point each of them is the difference between a
/// library that accumulates and one that quietly empties itself.
/// </summary>
[TestFixture]
[Category("AiSkills")]
public sealed class SkillMemoryTests
{
    private string _dir = null;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ss14ai-skills", Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir != null && Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static ISawmill Sawmill => new Robust.Shared.Log.LogManager().GetSawmill("test");

    private MemoryStore NewMemory(int limit = 300)
    {
        var store = new MemoryStore(_dir, Sawmill) { MemoryLimit = limit, CrewLimit = limit };
        store.LoadFromDisk();
        return store;
    }

    private SkillStore NewSkills()
    {
        var store = new SkillStore(_dir, Sawmill);
        store.LoadFromDisk();
        return store;
    }

    // ------------------------------------------------------------------- memory

    [Test]
    public void Memory_AddAndPersist()
    {
        var m = NewMemory();
        Assert.That(m.Add(MemoryTarget.Memory, "Ставни карго на том же APC, что и бар.").Ok, Is.True);

        // A second store reading the same directory must see it — the write has to be durable
        // immediately, not at shutdown.
        var reloaded = NewMemory();
        Assert.That(reloaded.Entries(MemoryTarget.Memory), Has.Count.EqualTo(1));
    }

    [Test]
    public void Memory_FrozenSnapshotDoesNotMoveUntilRefresh()
    {
        // The single most important property in the whole phase: a write during play must be
        // visible to the tool caller and INVISIBLE to zone 0, or the prefix cache dies every time
        // the agent remembers something.
        var m = NewMemory();
        m.Add(MemoryTarget.Memory, "первая запись");
        m.RefreshSnapshot();

        var before = m.Snapshot(MemoryTarget.Memory);
        m.Add(MemoryTarget.Memory, "вторая запись, добавлена посреди сессии");

        Assert.That(m.Snapshot(MemoryTarget.Memory), Is.EqualTo(before),
            "снапшот зоны 0 не должен меняться от записи посреди сессии");
        Assert.That(m.Entries(MemoryTarget.Memory), Has.Count.EqualTo(2),
            "живое состояние обязано измениться сразу — иначе модель не увидит своей же записи");

        m.RefreshSnapshot();
        Assert.That(m.Snapshot(MemoryTarget.Memory), Is.Not.EqualTo(before),
            "после перестройки префикса снапшот обязан догнать живое состояние");
    }

    [Test]
    public void Memory_SnapshotCarriesCapacityHeader()
    {
        var m = NewMemory(limit: 1000);
        m.Add(MemoryTarget.Memory, new string('я', 250));
        m.RefreshSnapshot();

        var snapshot = m.Snapshot(MemoryTarget.Memory);
        Assert.That(snapshot, Does.Contain("/1000 символов"),
            "модель должна видеть свой бюджет, иначе консолидировать она начнёт только упёршись в стену");
        Assert.That(snapshot, Does.Contain("%"));
    }

    [Test]
    public void Memory_RefusesToOverflow()
    {
        var m = NewMemory(limit: 100);
        m.Add(MemoryTarget.Memory, new string('a', 90));

        var result = m.Add(MemoryTarget.Memory, new string('b', 90));

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Entries, Is.Not.Null.And.Not.Empty,
            "при отказе надо показать, что лежит, иначе консолидировать модель будет вслепую");
    }

    [Test]
    public void Memory_ShrinkingIsAlwaysAllowedEvenWhenOverLimit()
    {
        // The lock-up this prevents happened for real: a limit was lowered, the accumulated text
        // was over it, and every repair was refused for "would exceed the limit" — including the
        // repairs that made it smaller.
        var m = NewMemory(limit: 1000);
        m.Add(MemoryTarget.Memory, new string('a', 400));
        m.Add(MemoryTarget.Memory, new string('b', 400));

        var tight = new MemoryStore(_dir, Sawmill) { MemoryLimit = 100, CrewLimit = 100 };
        tight.LoadFromDisk();

        var result = tight.Replace(MemoryTarget.Memory, new string('a', 20), "коротко");

        Assert.That(result.Ok, Is.True,
            "сжатие обязано проходить даже за лимитом, иначе переполненная память запирается навсегда");
    }

    [Test]
    public void Memory_ReplaceNeedsAnUnambiguousFragment()
    {
        var m = NewMemory();
        m.Add(MemoryTarget.Memory, "капитан Иванов носит красную куртку");
        m.Add(MemoryTarget.Memory, "капитан Петров носит синюю куртку");

        var ambiguous = m.Replace(MemoryTarget.Memory, "капитан", "неважно");
        Assert.That(ambiguous.Ok, Is.False, "неоднозначный фрагмент должен отвергаться");
        Assert.That(ambiguous.Message, Does.Contain("подлиннее"));

        var exact = m.Replace(MemoryTarget.Memory, "Иванов", "капитан Иванов сдал куртку в стирку");
        Assert.That(exact.Ok, Is.True);
    }

    [Test]
    public void Memory_StopsRetryingAfterRepeatedFailures()
    {
        // A fragile write must not be able to burn the whole turn and swallow the reply the crew
        // is waiting for.
        var m = NewMemory(limit: 50);
        m.Add(MemoryTarget.Memory, new string('a', 45));

        for (var i = 0; i < 4; i++)
            m.Add(MemoryTarget.Memory, new string('b', 45));

        var terminal = m.Add(MemoryTarget.Memory, new string('c', 45));
        Assert.That(terminal.Message, Does.Contain("пропущена"),
            "после нескольких провалов ответ должен стать терминальным, а не звать повторять");
    }

    // ------------------------------------------------------------------- skills

    [Test]
    public void Skill_RoundTripsThroughThePlainTextFormat()
    {
        var text = "# закрыть-отдел\nкогда: Загерметизировать отдел, не заперев экипаж.\nШаг один.\nШаг два.";
        var skill = SkillStore.Parse(text);

        Assert.That(skill, Is.Not.Null);
        Assert.That(skill!.Name, Is.EqualTo("закрыть-отдел"));
        Assert.That(skill.When, Is.EqualTo("Загерметизировать отдел, не заперев экипаж."));
        Assert.That(skill.Body, Does.Contain("Шаг два"));

        var reparsed = SkillStore.Parse(SkillStore.Render(skill));
        Assert.That(reparsed, Is.EqualTo(skill), "формат обязан пережить круг записи и чтения");
    }

    [Test]
    public void Skill_RejectsOverlongWhen()
    {
        var s = NewSkills();
        var result = s.Write("тест", new string('я', 61), "тело");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Message, Does.Contain("не доедет до индекса"),
            "отказ должен объяснять ПОЧЕМУ лимит есть, иначе модель будет считать его придиркой");
    }

    [Test]
    public void Skill_StopsNearDuplicates()
    {
        // Pleading in the prompt did not work on the mcbot deployment — the model kept creating
        // safe_mine_ore next to mine_ore — so the stopper is mechanical.
        var s = NewSkills();
        Assert.That(s.Write("открыть-дверь", "Открыть дверь по просьбе экипажа.", "тело").Ok, Is.True);

        var dupe = s.Write("быстро-открыть-дверь", "То же самое, но быстрее.", "тело");

        Assert.That(dupe.Ok, Is.False);
        Assert.That(dupe.Names, Does.Contain("открыть-дверь"));
    }

    [Test]
    public void Skill_AllowsSiblingsInTheSameArea()
    {
        // The counterweight to the test above, and the reason the stopper looks for a subset
        // rather than a shared word.
        //
        // A library that covers whole domains reuses the domain word by design: питание-apc and
        // питание-smes are two subjects, not one written twice. Refusing the second because the
        // first exists does not prevent a twin — it leaves the agent with no name it is allowed to
        // write, and the lesson goes unrecorded.
        var s = NewSkills();
        Assert.That(s.Write("питание-apc", "Вопрос про APC отдела.", "тело").Ok, Is.True);

        var sibling = s.Write("питание-smes", "Вопрос про СМЭС и накопители.", "тело");
        Assert.That(sibling.Ok, Is.True, sibling.Message);

        // And the twin is still caught inside that same area.
        var twin = s.Write("питание-apc-подробно", "То же про APC, но длиннее.", "тело");
        Assert.That(twin.Ok, Is.False);
        Assert.That(twin.Names, Does.Contain("питание-apc"));
    }

    [Test]
    public void Skill_EditByFragment_AppendsAndReplaces()
    {
        var s = NewSkills();
        s.Write("проверка-питания", "Проверить питание перед управлением дверью.", "Сначала inspect.");

        Assert.That(s.Edit("проверка-питания", "", "Грабли: обесточенная дверь не отвечает.").Ok, Is.True);
        s.TryGet("проверка-питания", out var appended);
        Assert.That(appended.Body, Does.Contain("Грабли"));

        Assert.That(s.Edit("проверка-питания", "Сначала inspect.", "Сначала inspect, потом device_action.").Ok, Is.True);
        s.TryGet("проверка-питания", out var replaced);
        Assert.That(replaced.Body, Does.Contain("потом device_action"));
    }

    [Test]
    public void Skill_EditRefusesAnInexactFragment()
    {
        var s = NewSkills();
        s.Write("тест-скилл", "Проверка.", "Точный текст тела.");

        var result = s.Edit("тест-скилл", "приблизительный текст", "неважно");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Message, Does.Contain("дословно"));
    }

    [Test]
    public void Skill_IndexIsDeterministicAndOnlyCarriesTheWhenLine()
    {
        var s = NewSkills();
        s.Write("яблоко", "Первое.", new string('x', 500));
        s.Write("банан", "Второе.", new string('y', 500));

        var index = s.RenderIndex();

        Assert.That(index, Does.Contain("Первое."));
        Assert.That(index, Does.Not.Contain("xxxxx"),
            "тело в индекс попадать не должно — весь смысл прогрессивного раскрытия в этом");
        Assert.That(index.IndexOf("банан", System.StringComparison.Ordinal),
            Is.LessThan(index.IndexOf("яблоко", System.StringComparison.Ordinal)),
            "порядок обязан быть детерминированным, иначе зона 0 меняется на ровном месте");
    }

    // ------------------------------------------------------------------ curator

    [Test]
    public async Task Curator_WritesASkill_AndLeavesGameHistoryUntouched()
    {
        var skills = NewSkills();
        var registry = new AiToolRegistry();

        registry.Register(new AiTool
        {
            Name = "skill_write",
            Description = "тест",
            SchemaJson = "{\"type\":\"object\"}",
            Handler = (a, ct) =>
            {
                var r = skills.Write(
                    a.GetProperty("name").GetString(),
                    a.GetProperty("when").GetString(),
                    a.GetProperty("body").GetString());
                return Task.FromResult(r.Ok ? ToolResult.Success() : ToolResult.Fail(ToolError.BadArgs, r.Message));
            },
        });

        var conv = new ConversationState();
        conv.SetPrefix("ПРОМПТ", "[]");
        conv.AppendUser("наблюдение");
        conv.AppendAssistant(new LlmResponse("ответ", System.Array.Empty<ToolCallDto>(), 10, 9, 1, 0.1));

        var bodyBefore = conv.Body.Count;

        var llm = new ScriptedLlmClient()
            .ThenCall("skill_write", """{"name":"болты-при-разгерметизации","when":"Опустить болты при разгерметизации.","body":"Сначала crew_status."}""")
            .Then("Записал скилл про болты.");

        var curator = new Curator(llm, Sawmill);
        var verdict = await curator.ReviewAsync(conv, System.Array.Empty<ToolDto>(),
            new ToolDispatcher(registry, Sawmill), skills.RenderIndex(), maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(skills.Count, Is.EqualTo(1), "куратор должен был записать скилл");
            Assert.That(verdict, Does.Contain("болты"));
            Assert.That(conv.Body.Count, Is.EqualTo(bodyBefore),
                "ревью идёт по КОПИИ — игровая история не должна испачкаться вопросом куратора");
        });
    }

    [Test]
    public async Task Curator_SendsTheSameToolArray()
    {
        // Same invariant the compactor learned the hard way: a different tool array diverges the
        // prompt at token zero and zeroes the cache.
        var llm = new RecordingLlmClient();
        var conv = new ConversationState();
        conv.SetPrefix("ПРОМПТ", "[]");
        conv.AppendUser("наблюдение");

        var tools = new[] { new ToolDto { Function = new ToolFunctionDto { Name = "look" } } };

        await new Curator(llm, Sawmill)
            .ReviewAsync(conv, tools, new ToolDispatcher(new AiToolRegistry(), Sawmill), "", 2,
                CancellationToken.None);

        Assert.That(llm.LastTools, Is.Not.Null);
        Assert.That(llm.LastTools!.Single().Function.Name, Is.EqualTo("look"));
    }

    [Test]
    public async Task Curator_RefusesGameActions()
    {
        // The whole point of AgentMode.Review, and the one path it never covered. The gate lived
        // only in the agent loop's private dispatcher; the curator had its own copy that called the
        // handler directly, so a review that decided to announce simply announced — mid-round, on a
        // path with no repeat suppression and no counter. Both class comments claimed otherwise.
        var acted = false;

        var registry = new AiToolRegistry();
        registry.Register(new AiTool
        {
            Name = "announce",
            Description = "тест",
            SchemaJson = "{\"type\":\"object\"}",
            GameAction = true,
            Handler = (_, _) =>
            {
                acted = true;
                return Task.FromResult(ToolResult.Success());
            },
        });

        var conv = new ConversationState();
        conv.SetPrefix("ПРОМПТ", "[]");
        conv.AppendUser("наблюдение");

        var llm = new ScriptedLlmClient()
            .ThenCall("announce", """{"text":"внимание"}""")
            .Then("готово");

        await new Curator(llm, Sawmill).ReviewAsync(conv, System.Array.Empty<ToolDto>(),
            new ToolDispatcher(registry, Sawmill), "", 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(acted, Is.False, "игровой инструмент не должен был отработать во время ревью");
            Assert.That(llm.SeenPrompts.Last().Any(m => m.Content?.Contains("review_mode") == true),
                Is.True, "и модель обязана увидеть именно review_mode, а не молчаливый успех");
        });
    }

    [Test]
    public async Task Curator_SurvivesAFailingModel()
    {
        var conv = new ConversationState();
        conv.SetPrefix("ПРОМПТ", "[]");
        conv.AppendUser("наблюдение");

        var verdict = await new Curator(new ThrowingLlmClient(), Sawmill)
            .ReviewAsync(conv, System.Array.Empty<ToolDto>(),
                new ToolDispatcher(new AiToolRegistry(), Sawmill), "", 2, CancellationToken.None);

        Assert.That(verdict, Is.Null, "падение модели не должно ронять ритуал компакции");
    }
}
