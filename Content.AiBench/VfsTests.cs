using System;
using System.IO;
using System.Linq;
using Content.Server.AiAgent.Vfs;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// The agent filesystem (VFS): routing, permissions, output caps, and library separation.
///
/// <para>
/// Four things are guarded here, each of which breaks silently. Directory traversal is the one
/// place where a string from the model becomes a path. Read permissions are the one thing keeping
/// the agent from editing a reference article it just read. Output caps are the one thing that
/// separates a single <c>grep</c> from blowing out the context window. And root stability: the
/// string landing in zone 0 must not depend on how many files sit in the tree, otherwise every
/// agent write costs a full prefill.
/// </para>
/// </summary>
[TestFixture]
[Category("AiVfs")]
public sealed class VfsTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ss14ai-vfs", Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir != null && Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static ISawmill Sawmill => new LogManager().GetSawmill("test");

    /// <summary>A reference library with two sections: one with an index, one without.</summary>
    private string SeedWiki()
    {
        var wiki = Path.Combine(_dir, "wiki_ru");
        Directory.CreateDirectory(Path.Combine(wiki, "атмосфера"));
        Directory.CreateDirectory(Path.Combine(wiki, "питание"));

        File.WriteAllText(Path.Combine(wiki, "_index.md"),
            "# справочник\nкогда: Вопрос про устройство станции\nОглавление справочника.\n");

        File.WriteAllText(Path.Combine(wiki, "атмосфера", "_index.md"),
            "# атмосфера\nкогда: Газы, трубы, разгерметизация\nОбзор раздела про атмосферу.\n");

        File.WriteAllText(Path.Combine(wiki, "атмосфера", "насосы.md"),
            "# насосы\nкогда: Насосы, вентили, давление в трубах\nGas Volume Pump качает объём.\nВторая строка про вентиль.\n");

        File.WriteAllText(Path.Combine(wiki, "питание", "апц.md"),
            "# апц\nкогда: APC, местное питание отдела\nAPC питает отдел и держит заряд.\n");

        return wiki;
    }

    private Vfs Build(VfsAccess wikiAccess = VfsAccess.Read)
    {
        var wiki = SeedWiki();

        return new VfsBuilder(Sawmill)
            .AddFolder(wiki, "wiki_ru", wikiAccess, "справочник по игре")
            .AddFolder(Path.Combine(_dir, "skills"), "skills", VfsAccess.Write, "что ты понял сам")
            .Build();
    }

    private Shell NewShell(VfsAccess wikiAccess = VfsAccess.Read) => new(Build(wikiAccess));

    // ------------------------------------------------------------------- reading

    [Test]
    public void Ls_RootListsMountsInDeclarationOrder()
    {
        var result = NewShell().Run("ls /");

        Assert.That(result.Ok, Is.True);
        Assert.That(result.Text, Does.Contain("wiki_ru"));
        Assert.That(result.Text, Does.Contain("skills"));
        Assert.That(result.Text.IndexOf("wiki_ru", StringComparison.Ordinal),
            Is.LessThan(result.Text.IndexOf("skills", StringComparison.Ordinal)),
            "порядок корня — это порядок объявления, иначе зона 0 переставляется между запусками");
    }

    [Test]
    public void Ls_ShowsFoldersWithTheirIndexDescription()
    {
        var result = NewShell().Run("ls /wiki_ru");

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("атмосфера/"));
            Assert.That(result.Text, Does.Contain("Газы, трубы, разгерметизация"),
                "описание папки берётся из её _index");
            Assert.That(result.Text, Does.Not.Contain("_index"),
                "оглавление описывает саму папку и в её листинге не показывается");
        });
    }

    [Test]
    public void Cat_ReadsAnArticle_ExtensionOptional()
    {
        // A path behaves like it would on a real filesystem: letter for letter and case-sensitive.
        // One concession — the extension can be left off, because ".md" is a storage detail, not
        // part of the article's name.
        var shell = NewShell();

        var bare = shell.Run("cat /wiki_ru/атмосфера/насосы");
        var full = shell.Run("cat /wiki_ru/атмосфера/насосы.md");

        Assert.Multiple(() =>
        {
            Assert.That(bare.Ok, Is.True);
            Assert.That(bare.Text, Does.Contain("Gas Volume Pump"));
            Assert.That(full.Text, Is.EqualTo(bare.Text));

            Assert.That(shell.Run("cat /wiki_ru/Атмосфера/Насосы").Ok, Is.False,
                "регистр значим: имена списываются из ls, а не сочиняются");
        });
    }

    [Test]
    public void Cat_OnAFolder_GivesItsOverview()
    {
        var result = NewShell().Run("cat /wiki_ru/атмосфера");

        Assert.That(result.Ok, Is.True);
        Assert.That(result.Text, Does.Contain("Обзор раздела"),
            "с обзора раздела правильно начинать, а не с угадывания имени файла внутри");
    }

    [Test]
    public void Grep_FindsByWord_AndReportsWhereItIs()
    {
        var result = NewShell().Run("grep вентиль /wiki_ru");

        Assert.That(result.Ok, Is.True);
        Assert.That(result.Text, Does.Contain("/wiki_ru/атмосфера/насосы"));
        Assert.That(result.Text, Does.Contain(":"), "у совпадения должен быть номер строки");
    }

    [Test]
    public void Grep_MissIsSuccess_NotFailure()
    {
        var result = NewShell().Run("grep сингулярность /wiki_ru");

        Assert.That(result.Ok, Is.True,
            "«такого слова нет» — полноценный ответ; отказ научил бы модель, что искать было ошибкой");
        Assert.That(result.Text, Does.Contain("не встречается"));
    }

    [Test]
    public void Find_MatchesByName()
    {
        var result = NewShell().Run("find апц");

        Assert.That(result.Ok, Is.True);
        Assert.That(result.Text, Does.Contain("/wiki_ru/питание/апц"));
    }

    // --------------------------------------------------------------------- permissions

    [Test]
    public void ReadOnlyMount_RefusesEveryMutation()
    {
        var shell = NewShell();

        Assert.Multiple(() =>
        {
            foreach (var command in new[]
                     {
                         "mkdir /wiki_ru/новое",
                         "rm /wiki_ru/питание/апц",
                         "mv /wiki_ru/питание/апц /wiki_ru/питание/апц2",
                     })
            {
                var result = shell.Run(command);
                Assert.That(result.Ok, Is.False, $"«{command}» обязана отказать");
                Assert.That(result.Mutated, Is.False, $"«{command}» не должна ничего менять");
            }
        });
    }

    [Test]
    public void ReadOnlyMount_StillReadable()
    {
        Assert.That(NewShell().Run("cat /wiki_ru/питание/апц").Ok, Is.True);
    }

    // ------------------------------------------------------- path safety

    [Test]
    public void Path_RefusesEscapes()
    {
        var shell = NewShell();

        Assert.Multiple(() =>
        {
            foreach (var bad in new[]
                     {
                         "cat /wiki_ru/../../etc/passwd",
                         "cat /wiki_ru/атмосфера/../../../secret",
                         "cat ../secret",
                         "cat etc/passwd",
                         "cat ",
                     })
            {
                Assert.That(shell.Run(bad).Ok, Is.False, $"«{bad}» обязана отказать");
            }
        });
    }

    [Test]
    public void Path_EscapeDoesNotReachDiskEvenWhenTheFileExists()
    {
        // The file is placed RIGHT NEXT TO the library root: if normalization is leaky, ".." will
        // reach it.
        File.WriteAllText(Path.Combine(_dir, "secret.md"), "# secret\nкогда: нельзя\nтайна\n");

        var result = NewShell().Run("cat /wiki_ru/../secret");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Text, Does.Not.Contain("тайна"));
    }

    // ------------------------------------------------------------------- writing

    [Test]
    public void Write_ThenRead_RoundTrips()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        VfsPath.TryParse("/skills/дверь-заклинило", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);

        var written = mount.Write(relative, "дверь не отвечает на команду", "Проверить питание APC, потом болты.");

        Assert.That(written.Ok, Is.True, written.Message);

        var read = shell.Run("cat /skills/дверь-заклинило");

        Assert.Multiple(() =>
        {
            Assert.That(read.Ok, Is.True);
            Assert.That(read.Text, Does.Contain("болты"));
            Assert.That(shell.Run("ls /skills").Text, Does.Contain("дверь не отвечает"),
                "описание — это то, по чему файл находят в листинге");
            Assert.That(File.Exists(Path.Combine(_dir, "skills", "дверь-заклинило.md")), Is.True,
                "на диске обязано лежать «.md», иначе следующее перечитывание файл не увидит");
        });
    }

    [Test]
    public void Mkdir_ThenWriteInside_Works()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        Assert.That(shell.Run("mkdir /skills/питание").Ok, Is.True);
        Assert.That(shell.Run("mkdir /skills/питание").Mutated, Is.True, "повторный mkdir не ошибка");

        VfsPath.TryParse("/skills/питание/смес", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);

        Assert.That(mount.Write(relative, "SMES не отдаёт заряд", "Проверить входной терминал.").Ok, Is.True);
        Assert.That(shell.Run("ls /skills/питание").Text, Does.Contain("смес"));
        Assert.That(shell.Run("cat /skills/питание/смес").Ok, Is.True);
    }

    [Test]
    public void Rm_RefusesToDropANonEmptyFolder()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        shell.Run("mkdir /skills/питание");
        VfsPath.TryParse("/skills/питание/смес", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);
        mount.Write(relative, "SMES не отдаёт заряд", "тело");

        var result = shell.Run("rm /skills/питание");

        Assert.That(result.Ok, Is.False,
            "рекурсивное удаление одной строкой — способ потерять раздел опечаткой, а обратной операции нет");
    }

    [Test]
    public void Edit_AppendsAndReplacesByFragment()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        VfsPath.TryParse("/skills/шлюз", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);
        mount.Write(relative, "шлюз не открывается", "Первая строка.");

        Assert.Multiple(() =>
        {
            Assert.That(mount.Edit(relative, "", "Дописанное.").Ok, Is.True);
            Assert.That(mount.Edit(relative, "Первая строка.", "Исправленная строка.").Ok, Is.True);

            var text = shell.Run("cat /skills/шлюз").Text;
            Assert.That(text, Does.Contain("Исправленная строка."));
            Assert.That(text, Does.Contain("Дописанное."));
            Assert.That(text, Does.Not.Contain("Первая строка."));

            Assert.That(mount.Edit(relative, "которого нет", "x").Ok, Is.False,
                "неточный фрагмент — отказ, а не запись мимо");
        });
    }

    [Test]
    public void Write_StopsNearDuplicates_WithinTheSameFolder()
    {
        var vfs = Build();

        VfsPath.TryParse("/skills/добыть-руду", out var first, out _);
        vfs.TryResolve(first, out var mount, out var relFirst, out _);
        mount.Write(relFirst, "нужна руда", "тело");

        VfsPath.TryParse("/skills/безопасно-добыть-руду", out var second, out _);
        vfs.TryResolve(second, out _, out var relSecond, out _);

        var result = mount.Write(relSecond, "нужна руда осторожно", "тело");

        Assert.That(result.Ok, Is.False, "то же имя с довеском — это тот же самый файл");
        Assert.That(result.Hints, Is.Not.Null.And.Contains("добыть-руду.md"),
            "подсказка называет файл так, как он лежит на диске");
    }

    [Test]
    public void Write_AllowsTheSameNameInAnotherFolder()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        shell.Run("mkdir /skills/атмосфера");
        shell.Run("mkdir /skills/питание");

        VfsPath.TryParse("/skills/атмосфера/насосы", out var a, out _);
        vfs.TryResolve(a, out var mount, out var relA, out _);
        Assert.That(mount.Write(relA, "насосы атмоса", "тело").Ok, Is.True);

        VfsPath.TryParse("/skills/питание/насосы", out var b, out _);
        vfs.TryResolve(b, out _, out var relB, out _);

        Assert.That(mount.Write(relB, "насосы питания", "тело").Ok, Is.True,
            "в плоской библиотеке это было одно имя, в дереве — две законные разные статьи");
    }

    [Test]
    public void Write_RefusesAnOverlongDescription()
    {
        var vfs = Build();

        VfsPath.TryParse("/skills/длинное", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);

        var result = mount.Write(relative, new string('я', DocTree.MaxWhen + 1), "тело");

        Assert.That(result.Ok, Is.False,
            "описание — единственная строка, которую видно в ls; всё за пределом не доехало бы до листинга");
    }

    // ------------------------------------------------------------------ output caps

    [Test]
    public void Grep_TruncatesLoudly()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        VfsPath.TryParse("/skills/много", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);

        var body = string.Join('\n', Enumerable.Range(0, Shell.MaxHits * 3).Select(i => $"строка {i} со словом иголка"));
        mount.Write(relative, "много одинаковых строк", body);

        var result = shell.Run("grep иголка /skills");

        Assert.Multiple(() =>
        {
            Assert.That(result.Text.Split('\n').Length, Is.LessThanOrEqualTo(Shell.MaxHits + 1));
            Assert.That(result.Text, Does.Contain("сузь"),
                "молча обрезанный вывод читается как «больше ничего нет» — то есть врёт");
        });
    }

    [Test]
    public void Cat_TruncatesLoudly_AndOffersTheRange()
    {
        var vfs = Build();
        var shell = new Shell(vfs);

        VfsPath.TryParse("/skills/длинный", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);

        // Bypassing DocTree: its body cap is 5000, whereas what's being checked here is the OUTPUT
        // cap, which exists for the reference library and the game wiki, where articles are known
        // to run longer.
        var wiki = Path.Combine(_dir, "wiki_ru");
        File.WriteAllText(Path.Combine(wiki, "огромная.md"),
            "# огромная\nкогда: длинная статья\n" + string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"строка {i}")));

        var fresh = new Shell(Build());
        var result = fresh.Run("cat /wiki_ru/огромная");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Text.Length, Is.LessThan(Shell.MaxCat + 400));
            Assert.That(result.Text, Does.Contain("показано"));
            Assert.That(fresh.Run("cat /wiki_ru/огромная:10-12").Text.Split('\n').Length, Is.EqualTo(3),
                "дочитывать надо диапазоном, а не повторным cat целиком");
        });
    }

    // ------------------------------------------------------------------ parsing

    [Test]
    public void UnknownCommand_NamesTheOnesThatExist()
    {
        var result = NewShell().Run("chmod 777 /skills");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Text, Does.Contain("ls").And.Contains("grep"));
    }

    [Test]
    public void Pipes_AreRefusedRatherThanHalfUnderstood()
    {
        var result = NewShell().Run("grep насос /wiki_ru | head -5");

        Assert.That(result.Ok, Is.False,
            "частично исполненный конвейер вернул бы правдоподобный, но неверный ответ");
    }

    [Test]
    public void Tokenize_KeepsQuotedPhrasesTogether()
    {
        var argv = Shell.Tokenize("grep \"воздушная тревога\" /wiki_ru");

        Assert.That(argv, Is.EqualTo(new[] { "grep", "воздушная тревога", "/wiki_ru" }));
    }

    // ------------------------------------------------------- building and isolation

    [Test]
    public void Build_RefusesADuplicateMountPoint()
    {
        var wiki = SeedWiki();

        Assert.Throws<InvalidOperationException>(() => new VfsBuilder(Sawmill)
            .AddFolder(wiki, "wiki_ru", VfsAccess.Read, "справочник")
            .AddFolder(wiki, "wiki_ru", VfsAccess.Read, "он же ещё раз")
            .Build());
    }

    [Test]
    public void Build_ShoutsAboutAnEmptyReadOnlyMount_ButStillBuilds()
    {
        // A silently empty wiki looks in-game like "the agent forgot how" and doesn't produce a
        // single line in the log — hence the loud complaint. But NOT a crash: an exception while
        // building a body means a round with no agent at all, which is worse than an agent that
        // doesn't know the reference library.
        var vfs = new VfsBuilder(Sawmill)
            .AddFolder(Path.Combine(_dir, "пусто"), "wiki_ru", VfsAccess.Read, "справочник")
            .AddFolder(Path.Combine(_dir, "skills"), "skills", VfsAccess.Write, "своё")
            .Build();

        Assert.Multiple(() =>
        {
            Assert.That(vfs.Complaints, Has.Count.EqualTo(1));
            Assert.That(vfs.Complaints[0], Does.Contain("wiki_ru"));
            Assert.That(new Shell(vfs).Run("ls /").Ok, Is.True, "агент обязан продолжать работать");
        });
    }

    [Test]
    public void TwoAgents_DoNotSeeEachOthersLibraries_ButShareTheWiki()
    {
        var wiki = SeedWiki();
        var shared = new DocTree(wiki, Sawmill);
        shared.Reload();

        var core = new VfsBuilder(Sawmill)
            .AddShared(shared, "wiki_ru", VfsAccess.Read, "справочник")
            .AddFolder(Path.Combine(_dir, "agents", "core", "skills"), "skills", VfsAccess.Write, "своё")
            .Build();

        var borg = new VfsBuilder(Sawmill)
            .AddShared(shared, "wiki_ru", VfsAccess.Read, "справочник")
            .AddFolder(Path.Combine(_dir, "agents", "combat-1", "skills"), "skills", VfsAccess.Write, "своё")
            .Build();

        VfsPath.TryParse("/skills/тайна-ядра", out var path, out _);
        core.TryResolve(path, out var coreSkills, out var relative, out _);
        Assert.That(coreSkills.Write(relative, "что знает только ядро", "тело").Ok, Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(new Shell(core).Run("ls /skills").Text, Does.Contain("тайна-ядра"));
            Assert.That(new Shell(borg).Run("ls /skills").Text, Does.Not.Contain("тайна-ядра"),
                "борг не должен видеть библиотеку Станционного ИИ — там в том числе досье на экипаж");

            Assert.That(new Shell(borg).Run("cat /wiki_ru/атмосфера/насосы").Ok, Is.True,
                "а справочник общий, и общий одним экземпляром");
        });
    }

    [Test]
    public void Mount_WithAnExtension_IsReachableBothWays()
    {
        // Path parsing strips ".md" so that "pumps" and "pumps.md" are the same file. Because of
        // that, the "memory.md" mount point was arriving in the table as "memory" and wasn't being
        // found at all. Caught on a live server: the fixture had no mount points with an extension.
        var vfs = new VfsBuilder(Sawmill)
            .AddFolder(Path.Combine(_dir, "skills"), "skills", VfsAccess.Write, "своё")
            .AddMemory(_dir, "memory.md", VfsAccess.Write, "факты о станции")
            .Build();

        var shell = new Shell(vfs);

        Assert.Multiple(() =>
        {
            Assert.That(shell.Run("cat /memory.md").Ok, Is.True, "как написано в корневом листинге");
            Assert.That(shell.Run("cat /memory").Ok, Is.True, "и как модель напишет по привычке");
            Assert.That(shell.Run("cat /skills").Ok, Is.False, "а несуществующее по-прежнему промах");
            Assert.That(vfs.RenderRoot(), Does.Contain("/memory.md"),
                "а показывать надо полное имя, иначе листинг расходится с тем, что работает");
        });
    }

    // ------------------------------------------------------ the real reference library

    /// <summary>
    /// The real library from <c>ai_data/wiki_ru</c>, if it's present.
    ///
    /// <para>
    /// Checks exactly what a fixture can't: that the migration laid out 226 articles the way the
    /// production parser then reads them. The migration script is written in Python and knows
    /// nothing about the C# parser — a format mismatch would only show up in the game.
    /// </para>
    /// <para>
    /// The directory isn't in git, so on a clean checkout the test is skipped rather than failed.
    /// </para>
    /// </summary>
    [Test]
    public void RealLibrary_ParsesAndIsNavigable()
    {
        // Tests run from bin/Content.AiBench, so the repository root is two levels up.
        var root = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "ai_data", "wiki_ru"));

        if (!Directory.Exists(root))
            Assert.Ignore("ai_data/wiki_ru нет — тест для машины, где агент уже работал");

        var vfs = new VfsBuilder(Sawmill)
            .AddFolder(root, "wiki_ru", VfsAccess.Read, "справочник по игре")
            .Build();

        var shell = new Shell(vfs);

        Assert.Multiple(() =>
        {
            Assert.That(vfs.Complaints, Is.Empty, "справочник не должен быть пустым");

            var sections = shell.Run("ls /wiki_ru");
            Assert.That(sections.Ok, Is.True);
            Assert.That(sections.Text, Does.Contain("атмосфера/").And.Contains("питание/"));
            Assert.That(sections.Text, Does.Contain("давление"), "у разделов обязано быть описание из _index");

            var article = shell.Run("cat /wiki_ru/атмосфера/насосы");
            Assert.That(article.Ok, Is.True);

            var found = shell.Run("grep APC /wiki_ru/питание");
            Assert.That(found.Ok, Is.True);
            Assert.That(found.Text, Does.Contain("/wiki_ru/питание/"));

            // Not a single live reference to retired tools: the reference library must not teach the
            // agent to call something that no longer exists.
            var stale = shell.Run("grep skill_view /wiki_ru");
            Assert.That(stale.Text, Does.Contain("не встречается"));
        });
    }

    // -------------------------------------------------------------- zone-0 guard

    [Test]
    public void RenderRoot_DoesNotMoveWhenTheTreeChanges()
    {
        var vfs = Build();
        var before = vfs.RenderRoot();

        VfsPath.TryParse("/skills/новый", out var path, out _);
        vfs.TryResolve(path, out var mount, out var relative, out _);
        mount.Write(relative, "что-то новое", "тело");
        mount.Remove(relative);

        Assert.That(vfs.RenderRoot(), Is.EqualTo(before),
            "прежний индекс менялся от каждой записи и тянул за собой полный prefill; этот блок обязан быть постоянным");
    }

    /// <summary>
    /// The zone-0 root must stay small — that's the whole reason this was all rewritten.
    ///
    /// <para>
    /// The previous skills index took up 16,425 characters of frozen prefix, grew with every agent
    /// write, and got repeated in full again inside the parsing prompt. The cap here is generous: it
    /// isn't meant to catch one stray character, but the return of the same disease — a counter, a
    /// list of articles, a "helpful" digest pulled from the tree.
    /// </para>
    /// </summary>
    [Test]
    public void RenderRoot_StaysSmall()
    {
        // Exactly the table StationAiAgentSystem.BuildVfs assembles, long descriptions included.
        var vfs = new VfsBuilder(Sawmill)
            .AddFolder(SeedWiki(), "wiki_ru", VfsAccess.Read, "справочник по игре: отделы, машины, процедуры")
            .AddFolder(Path.Combine(_dir, "skills"), "skills", VfsAccess.Write, "что ты понял сам")
            .AddNotes(_dir, "players", VfsAccess.Write, "твои заметки о людях, по файлу на человека", () => "[раунд 1 · 01.01]")
            .AddMemory(_dir, "memory.md", VfsAccess.Write, "факты о станции и мире — они же в блоке ПАМЯТЬ выше")
            .AddText(Path.Combine(_dir, "CURATOR.md"), "curator.md", VfsAccess.Read, "чем ты руководствуешься на разборе отрезка")
            .Build();

        var root = vfs.RenderRoot();

        Assert.That(root.Length, Is.LessThan(1200),
            $"корень зоны 0 разросся до {root.Length} символов — прежний индекс начинался так же");
    }

    [Test]
    public void RenderRoot_CarriesEveryMountWithItsAccess()
    {
        var root = Build().RenderRoot();

        Assert.Multiple(() =>
        {
            Assert.That(root, Does.Contain("/wiki_ru").And.Contains("r--"));
            Assert.That(root, Does.Contain("/skills").And.Contains("rw-"));
            Assert.That(root, Does.Not.Contain("229"), "счётчиков в зоне 0 быть не должно");
        });
    }
}
