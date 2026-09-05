using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vfs;
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
        var store = new MemoryStore(_dir, Sawmill) { MemoryLimit = limit };
        store.LoadFromDisk();
        return store;
    }

    /// <summary>
    /// A filesystem for compaction: personal notes only, no knowledge base needed here.
    ///
    /// Skill storage moved into <see cref="DocTree"/>, and its own rules — editing by fragment, the
    /// twin stopper, the description cap — are checked in <c>VfsTests</c>. What remains here is the
    /// reason this file exists at all: that compaction writes, does not dirty the game history, and
    /// sends the same tool array.
    /// </summary>
    private Vfs NewVfs() => new VfsBuilder(Sawmill)
        .AddFolder(Path.Combine(_dir, "skills"), "skills", VfsAccess.Write, "что ты понял сам")
        .AddMemory(_dir, "memory.md", VfsAccess.Write, "факты о станции")
        .AddText(Path.Combine(_dir, "CURATOR.md"), "curator.md", VfsAccess.Read, "разбор")
        .Build();

    /// <summary>Put the compaction prompt alongside, the way the live directory does.</summary>
    private void SeedCuratorPrompt(string text = "Разбери отрезок.\n{{КОРЕНЬ}}") =>
        File.WriteAllText(Path.Combine(_dir, "CURATOR.md"), text);

    // ------------------------------------------------------------------- memory

    [Test]
    public void Memory_AddAndPersist()
    {
        var m = NewMemory();
        Assert.That(m.Add("Ставни карго на том же APC, что и бар.").Ok, Is.True);

        // A second store reading the same directory must see it — the write has to be durable
        // immediately, not at shutdown.
        var reloaded = NewMemory();
        Assert.That(reloaded.Entries(), Has.Count.EqualTo(1));
    }

    [Test]
    public void Memory_FrozenSnapshotDoesNotMoveUntilRefresh()
    {
        // The single most important property in the whole phase: a write during play must be
        // visible to the tool caller and INVISIBLE to zone 0, or the prefix cache dies every time
        // the agent remembers something.
        var m = NewMemory();
        m.Add("первая запись");
        m.RefreshSnapshot();

        var before = m.Snapshot();
        m.Add("вторая запись, добавлена посреди сессии");

        Assert.That(m.Snapshot(), Is.EqualTo(before),
            "снапшот зоны 0 не должен меняться от записи посреди сессии");
        Assert.That(m.Entries(), Has.Count.EqualTo(2),
            "живое состояние обязано измениться сразу — иначе модель не увидит своей же записи");

        m.RefreshSnapshot();
        Assert.That(m.Snapshot(), Is.Not.EqualTo(before),
            "после перестройки префикса снапшот обязан догнать живое состояние");
    }

    [Test]
    public void Memory_SnapshotCarriesCapacityHeader()
    {
        var m = NewMemory(limit: 1000);
        m.Add(new string('я', 250));
        m.RefreshSnapshot();

        var snapshot = m.Snapshot();
        Assert.That(snapshot, Does.Contain("/1000 символов"),
            "модель должна видеть свой бюджет, иначе консолидировать она начнёт только упёршись в стену");
        Assert.That(snapshot, Does.Contain("%"));
    }

    [Test]
    public void Memory_RefusesToOverflow()
    {
        var m = NewMemory(limit: 100);
        m.Add(new string('a', 90));

        var result = m.Add(new string('b', 90));

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
        m.Add(new string('a', 400));
        m.Add(new string('b', 400));

        var tight = new MemoryStore(_dir, Sawmill) { MemoryLimit = 100 };
        tight.LoadFromDisk();

        var result = tight.Replace(new string('a', 20), "коротко");

        Assert.That(result.Ok, Is.True,
            "сжатие обязано проходить даже за лимитом, иначе переполненная память запирается навсегда");
    }

    [Test]
    public void Memory_ReplaceNeedsAnUnambiguousFragment()
    {
        var m = NewMemory();
        m.Add("капитан Иванов носит красную куртку");
        m.Add("капитан Петров носит синюю куртку");

        var ambiguous = m.Replace("капитан", "неважно");
        Assert.That(ambiguous.Ok, Is.False, "неоднозначный фрагмент должен отвергаться");
        Assert.That(ambiguous.Message, Does.Contain("подлиннее"));

        var exact = m.Replace("Иванов", "капитан Иванов сдал куртку в стирку");
        Assert.That(exact.Ok, Is.True);
    }

    [Test]
    public void Memory_StopsRetryingAfterRepeatedFailures()
    {
        // A fragile write must not be able to burn the whole turn and swallow the reply the crew
        // is waiting for.
        var m = NewMemory(limit: 50);
        m.Add(new string('a', 45));

        for (var i = 0; i < 4; i++)
            m.Add(new string('b', 45));

        var terminal = m.Add(new string('c', 45));
        Assert.That(terminal.Message, Does.Contain("пропущена"),
            "после нескольких провалов ответ должен стать терминальным, а не звать повторять");
    }

    // ------------------------------------------------------------------- skills

    // Tests of the library itself — editing by fragment, the twin stopper, the description cap,
    // listing stability — moved into VfsTests along with the storage. What remains here is compaction.

    /// <summary>An empty conversation with a ready-made prefix — so three lines don't repeat in every test.</summary>
    private static ConversationState Fresh()
    {
        var conv = new ConversationState();
        conv.SetPrefix("ПРОМПТ", "[]");
        conv.AppendUser("наблюдение");
        return conv;
    }

    // ------------------------------------------------------------------ curator

    [Test]
    public async Task Curator_WritesAFile_AndLeavesGameHistoryUntouched()
    {
        SeedCuratorPrompt();

        var vfs = NewVfs();
        var registry = new AiToolRegistry();

        registry.Register(new AiTool
        {
            Name = "write_file",
            Description = "тест",
            SchemaJson = "{\"type\":\"object\"}",
            Handler = (a, ct) =>
            {
                VfsPath.TryParse(a.GetProperty("path").GetString(), out var path, out _);
                vfs.TryResolve(path, out var mount, out var relative, out _);

                var r = mount.Write(relative,
                    a.GetProperty("desc").GetString() ?? "",
                    a.GetProperty("content").GetString() ?? "");

                // The stand-in handler must do the same thing the real one does: the write counter
                // lives in the tool handler, not in the mount (see Vfs.NoteWrite). Without this line,
                // compaction would report zero writes even though a file was written.
                if (r.Ok)
                    vfs.NoteWrite();

                return Task.FromResult(r.Ok ? ToolResult.Success() : ToolResult.Fail(ToolError.BadArgs, r.Message));
            },
        });

        var conv = new ConversationState();
        conv.SetPrefix("ПРОМПТ", "[]");
        conv.AppendUser("наблюдение");
        conv.AppendAssistant(new LlmResponse("ответ", System.Array.Empty<ToolCallDto>(), 10, 9, 1, 0.1));

        var bodyBefore = conv.Body.Count;

        var llm = new ScriptedLlmClient()
            .ThenCall("write_file", """{"path":"/skills/болты-при-разгерметизации","desc":"Опустить болты при разгерметизации.","content":"Сначала crew_status."}""")
            .Then("Записал про болты.");

        var curator = new Curator(llm, Sawmill);
        var verdict = await curator.ReviewAsync(conv, System.Array.Empty<ToolDto>(),
            new ToolDispatcher(registry, Sawmill), vfs, maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(vfs.Skills!.Count, Is.EqualTo(1), "куратор должен был записать файл");
            Assert.That(verdict, Does.Contain("болты"));
            Assert.That(curator.LastWrites, Is.EqualTo(1), "успешная запись должна быть посчитана");
            Assert.That(conv.Body.Count, Is.EqualTo(bodyBefore),
                "ревью идёт по КОПИИ — игровая история не должна испачкаться вопросом куратора");
        });
    }

    [Test]
    public async Task Curator_CountsNoWrites_WhenItOnlyLooked()
    {
        // The reporting condition is "wrote", not "replied". Otherwise every compaction would spend
        // a line of dialogue on "looked and decided there was nothing to write", which is a
        // legitimate outcome of compaction.
        SeedCuratorPrompt();

        var vfs = NewVfs();
        var llm = new ScriptedLlmClient().Then("Нечего сохранять.");

        var curator = new Curator(llm, Sawmill);
        await curator.ReviewAsync(conv: Fresh(), tools: System.Array.Empty<ToolDto>(),
            dispatcher: new ToolDispatcher(new AiToolRegistry(), Sawmill), vfs: vfs,
            maxSteps: 2, ct: CancellationToken.None);

        Assert.That(curator.LastWrites, Is.Zero);
    }

    [Test]
    public async Task Curator_PromptComesFromTheFile_AndTheRootIsSubstituted()
    {
        SeedCuratorPrompt("ОСОБЫЙ ТЕКСТ РАЗБОРА\n{{КОРЕНЬ}}");

        var vfs = NewVfs();
        var llm = new RecordingLlmClient();

        await new Curator(llm, Sawmill).ReviewAsync(Fresh(), System.Array.Empty<ToolDto>(),
            new ToolDispatcher(new AiToolRegistry(), Sawmill), vfs, 2, CancellationToken.None);

        var asked = llm.SeenPrompts.Last().Last().Content ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(asked, Does.Contain("ОСОБЫЙ ТЕКСТ РАЗБОРА"), "промпт разбора берётся из CURATOR.md");
            Assert.That(asked, Does.Contain("/skills"), "корень дерева обязан быть подставлен");
            Assert.That(asked, Does.Not.Contain(Curator.RootPlaceholder),
                "неподставленная скобка означает, что модель читает служебную разметку");
        });
    }

    [Test]
    public async Task Curator_StillRunsWhenThePromptFileIsMissing()
    {
        // Silently skipping compaction is not allowed: from the outside it looks like "the agent
        // stopped learning" and gives not a single line in the log. The file is deliberately absent.
        var vfs = NewVfs();
        var llm = new RecordingLlmClient();

        await new Curator(llm, Sawmill).ReviewAsync(Fresh(), System.Array.Empty<ToolDto>(),
            new ToolDispatcher(new AiToolRegistry(), Sawmill), vfs, 2, CancellationToken.None);

        var asked = llm.SeenPrompts.Last().Last().Content ?? "";

        Assert.That(asked, Does.Contain("разбираешь прошедший отрезок"),
            "должен был отработать встроенный запасной текст");
        Assert.That(asked, Does.Not.Contain(Curator.RootPlaceholder));
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

        SeedCuratorPrompt();

        await new Curator(llm, Sawmill)
            .ReviewAsync(conv, tools, new ToolDispatcher(new AiToolRegistry(), Sawmill), NewVfs(), 2,
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

        SeedCuratorPrompt();

        await new Curator(llm, Sawmill).ReviewAsync(conv, System.Array.Empty<ToolDto>(),
            new ToolDispatcher(registry, Sawmill), NewVfs(), 4, CancellationToken.None);

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

        SeedCuratorPrompt();

        var verdict = await new Curator(new ThrowingLlmClient(), Sawmill)
            .ReviewAsync(conv, System.Array.Empty<ToolDto>(),
                new ToolDispatcher(new AiToolRegistry(), Sawmill), NewVfs(), 2, CancellationToken.None);

        Assert.That(verdict, Is.Null, "падение модели не должно ронять ритуал компакции");
    }
}
