using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// Player notes: one file per person, persisting across shifts.
///
/// This is where all of the subsystem's risk is concentrated, and it is verified without a
/// server. The main test is <see cref="Name_CannotEscapeTheDirectory"/>: the character's name is
/// chosen by the player, the model substitutes it into a tool argument, and it becomes a path on
/// disk. The neighboring <see cref="SkillStore.Normalise"/> does no sanitization at all — there
/// the name is invented by the model, and the worst that can happen is a mangled title.
///
/// The second most important test is <see cref="Overflow_IsLocalToOnePerson"/>: it encodes WHY
/// the storage is per-file. On the live server the shared MEMORY.md pool hit its limit and
/// stopped accepting anything at all; a per-person limit makes the failure local.
/// </summary>
[TestFixture]
[Category("AiSkills")]
public sealed class PlayerNoteTests
{
    private string _dir = null;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ss14ai-notes", Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir != null && Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static ISawmill Sawmill => new Robust.Shared.Log.LogManager().GetSawmill("test");

    private const string Stamp = "[раунд 7 · 02.01]";

    private PlayerNoteStore NewStore(int noteLimit = 2000, int maxNotes = 2000, int maxEntry = 400)
    {
        var store = new PlayerNoteStore(_dir, Sawmill)
        {
            NoteLimit = noteLimit,
            MaxNotes = maxNotes,
            MaxEntryLength = maxEntry,
        };
        store.LoadFromDisk();
        return store;
    }

    /// <summary>The directory the store actually writes into.</summary>
    private string PeopleDir => Path.Combine(_dir, "people");

    private string[] PeopleFiles() =>
        Directory.Exists(PeopleDir) ? Directory.GetFiles(PeopleDir) : System.Array.Empty<string>();

    // --------------------------------------------------------------------- format

    [Test]
    public void Note_RoundTripsThroughDisk()
    {
        var store = NewStore();

        Assert.That(store.Add("Иван Петров", "Инженер, просил открыть атмос.", Stamp).Ok, Is.True);
        Assert.That(store.Add("Иван Петров", "Обещал вернуть карту и вернул.", Stamp).Ok, Is.True);

        // A second store from the same directory is required to see exactly the same thing.
        var reloaded = NewStore();
        var read = reloaded.Read("Иван Петров");

        Assert.Multiple(() =>
        {
            Assert.That(read.Ok, Is.True, read.Message);
            Assert.That(read.Message, Is.EqualTo("Иван Петров"), "имя берётся из заголовка файла");
            Assert.That(read.Entries, Has.Count.EqualTo(2));
            Assert.That(read.Entries![0], Does.StartWith(Stamp));
            Assert.That(read.Entries![1], Does.Contain("Обещал вернуть карту"));
        });
    }

    [Test]
    public void DisplayName_SurvivesASlugThatCannotHoldIt()
    {
        // The slug is lossy: it strips out case, punctuation, and quote marks. Showing the model
        // «иван-ржавый» instead of «Иван "Ржавый" Петров» would mean lying to it about the
        // person's actual name.
        var store = NewStore();
        store.Add("Иван «Ржавый» Петров-мл.", "Механик.", Stamp);

        var read = NewStore().Read("иван ржавый петров мл");

        Assert.That(read.Ok, Is.True, read.Message);
        Assert.That(read.Message, Is.EqualTo("Иван «Ржавый» Петров-мл."));
    }

    [Test]
    public void Lookup_IgnoresCaseAndSpacing()
    {
        var store = NewStore();
        store.Add("Иван  Петров", "Раз.", Stamp);

        Assert.Multiple(() =>
        {
            Assert.That(store.Read("иван петров").Ok, Is.True, "регистр не должен разводить ключи");
            Assert.That(store.Read("ИВАН-ПЕТРОВ").Ok, Is.True, "дефис и пробел — один разделитель");
        });
    }

    [Test]
    public void KeepsCyrillicInTheFileName()
    {
        // Exactly like with skills (антаг-вор.md): a readable file name is the primary debugging
        // affordance — you can't grep for a hash instead.
        NewStore().Add("Иван Петров", "Раз.", Stamp);

        Assert.That(PeopleFiles().Select(Path.GetFileName), Does.Contain("иван-петров.md"));
    }

    // ------------------------------------------------------------------ security

    [Test]
    public void Name_CannotEscapeTheDirectory()
    {
        // A character allowlist, not a blocklist of dangerous sequences: after it, "..", a slash,
        // and a colon are inexpressible in principle, not "cleaned up" by a list someone will
        // forget to keep updated.
        var hostile = new[]
        {
            "../../SOUL",
            @"..\..\SOUL",
            "/etc/passwd",
            @"C:\windows\system32\config",
            "a/b/c",
            "note\u0000.md",
            "\u202Egnp.exe",
            new string('я', 500),
        };

        var before = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Length;
        var store = NewStore();

        foreach (var name in hostile)
            store.Add(name, "проверка", Stamp);

        Assert.Multiple(() =>
        {
            foreach (var path in PeopleFiles())
            {
                Assert.That(Path.GetDirectoryName(path), Is.EqualTo(PeopleDir),
                    $"файл уехал за пределы каталога заметок: {path}");
                Assert.That(Path.GetFileName(path)!.Length, Is.LessThanOrEqualTo(64 + 3),
                    $"слаг не обрезан: {path}");
            }

            // Nothing appeared or changed one level up — that's where the live agent's SOUL.md lives.
            var outside = Directory.GetFiles(_dir, "*", SearchOption.TopDirectoryOnly);
            Assert.That(outside, Is.Empty, "в корне ai_data не должно появиться ничего");
            Assert.That(before, Is.EqualTo(0));
        });
    }

    [Test]
    public void Name_ThatNormalisesToNothing_IsRefused()
    {
        var store = NewStore();

        Assert.Multiple(() =>
        {
            foreach (var junk in new[] { "...", "///", "   ", "!!!", "" })
            {
                var result = store.Add(junk, "проверка", Stamp);
                Assert.That(result.Ok, Is.False, $"«{junk}» не должно давать заметку");
            }

            Assert.That(PeopleFiles(), Is.Empty, "файл с пустым именем создавать нельзя");
        });
    }

    [Test]
    public void ReservedWindowsNames_GetASuffix()
    {
        // The server runs on Linux, but a debug copy of the store on a laptop shouldn't crash for
        // no reason.
        Assert.Multiple(() =>
        {
            Assert.That(PlayerNoteStore.Slugify("CON"), Is.EqualTo("con-"));
            Assert.That(PlayerNoteStore.Slugify("nul"), Is.EqualTo("nul-"));
            Assert.That(PlayerNoteStore.Slugify("com1"), Is.EqualTo("com1-"));
            Assert.That(PlayerNoteStore.Slugify("Conrad"), Is.EqualTo("conrad"), "не трогать похожие");
        });
    }

    // ----------------------------------------------------------------------- stamp

    [Test]
    public void Add_StampsEveryEntry()
    {
        // The store applies the stamp, not the model: the model will forget, and a half-stamped
        // store is worse than an unstamped one — you can't tell last shift's entries from today's.
        var store = NewStore();
        store.Add("Иван Петров", "Взломал шкаф.", Stamp);

        var entry = store.Read("Иван Петров").Entries![0];

        Assert.That(entry, Is.EqualTo($"{Stamp} Взломал шкаф."));
    }

    [Test]
    public void Replace_KeepsWhateverStampTheModelWrites()
    {
        // An edit does not get re-stamped: the stamp answers the question "when did I learn this,"
        // and rephrasing the knowledge doesn't change that. The model edits the entry together
        // with its stamp.
        var store = NewStore();
        store.Add("Иван Петров", "Взломал шкаф.", Stamp);

        var result = store.Replace("Иван Петров", "Взломал шкаф.", $"{Stamp} Взломал шкаф ГП.");

        Assert.That(result.Ok, Is.True, result.Message);
        Assert.That(store.Read("Иван Петров").Entries![0], Is.EqualTo($"{Stamp} Взломал шкаф ГП."));
    }

    // ------------------------------------------------------------------- edits

    [Test]
    public void Edit_CreatesAppendsReplacesAndDeletes()
    {
        var store = NewStore();

        Assert.Multiple(() =>
        {
            Assert.That(store.Add("Иван Петров", "Первая.", Stamp).Message, Is.EqualTo("заметка заведена"));
            Assert.That(store.Add("Иван Петров", "Вторая.", Stamp).Message, Is.EqualTo("записано"));
            Assert.That(store.Replace("Иван Петров", "Первая.", "Первая, уточнённая.").Ok, Is.True);
            Assert.That(store.Remove("Иван Петров", "Вторая.").Ok, Is.True);
        });

        var left = store.Read("Иван Петров").Entries;
        Assert.That(left, Has.Count.EqualTo(1));
        Assert.That(left![0], Does.Contain("уточнённая"));
    }

    [Test]
    public void Edit_RefusesAnInexactFragment()
    {
        var store = NewStore();
        store.Add("Иван Петров", "Взломал шкаф.", Stamp);

        var result = store.Replace("Иван Петров", "взломал сейф", "что-то");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("дословно"));
            Assert.That(result.Entries, Is.Not.Null.And.Not.Empty,
                "отказ обязан показать, что там на самом деле лежит");
        });
    }

    [Test]
    public void Edit_RefusesAnAmbiguousFragment()
    {
        var store = NewStore();
        store.Add("Иван Петров", "Просил открыть атмос.", Stamp);
        store.Add("Иван Петров", "Просил открыть оружейную.", Stamp);

        var result = store.Replace("Иван Петров", "Просил открыть", "что-то");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Message, Does.Contain("подлиннее"));
    }

    [Test]
    public void RemovingTheLastEntry_DeletesTheFile()
    {
        // A directory cluttered with empty notes lies to search about who the agent actually knows.
        var store = NewStore();
        store.Add("Иван Петров", "Единственная.", Stamp);

        var result = store.Remove("Иван Петров", "Единственная.");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(store.Read("Иван Петров").Ok, Is.False);
            Assert.That(PeopleFiles(), Is.Empty);
            Assert.That(store.Count, Is.Zero);
        });
    }

    // ------------------------------------------------------------------- limits

    [Test]
    public void Overflow_IsLocalToOnePerson()
    {
        // This test is about WHY the storage is per-file. In the shared MEMORY.md pool, one
        // verbose topic locked out recording anything else; here an overflowing Ivan doesn't
        // block Petr.
        var store = NewStore(noteLimit: 120, maxEntry: 100);

        // The entries are different: on an exact duplicate, Add responds "this entry already
        // exists" with Ok=true.
        var filled = false;
        for (var i = 0; i < 10 && !filled; i++)
            filled = !store.Add("Иван Петров", $"запись номер {i} " + new string('а', 40), Stamp).Ok;

        Assert.That(filled, Is.True, "заметка должна была упереться в лимит");
        Assert.That(store.Add("Пётр Иванов", "запись про другого человека", Stamp).Ok, Is.True,
            "переполнение одной заметки не должно запирать другие");
    }

    [Test]
    public void ShrinkingIsAllowed_EvenWhenOverTheLimit()
    {
        // Lesson from MEMORY.md: if shrinking is blocked by capacity, an overflowing memory can
        // never be fixed and locks up forever. That's exactly what happened on mcbot, when the
        // limit was lowered below already accumulated text — we reproduce that same scenario.
        NewStore(noteLimit: 500, maxEntry: 200).Add("Иван Петров", new string('а', 150), Stamp);

        var tightened = NewStore(noteLimit: 50, maxEntry: 200);

        Assert.Multiple(() =>
        {
            Assert.That(tightened.Read("Иван Петров").Entries![0].Length, Is.GreaterThan(50),
                "заметка должна быть уже сверх нового лимита");
            Assert.That(tightened.Replace("Иван Петров", "ааа", "коротко").Ok, Is.True,
                "сокращение обязано проходить даже сверх лимита, иначе заметку не починить");
        });
    }

    [Test]
    public void Entry_LongerThanTheCap_IsRefused()
    {
        var store = NewStore(maxEntry: 50);

        var result = store.Add("Иван Петров", new string('а', 51), Stamp);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Message, Does.Contain("50"));
    }

    [Test]
    public void FileCap_StopsNewNotes_ButNotEdits()
    {
        var store = NewStore(maxNotes: 2);
        store.Add("Первый", "раз", Stamp);
        store.Add("Второй", "раз", Stamp);

        Assert.Multiple(() =>
        {
            Assert.That(store.Add("Третий", "раз", Stamp).Ok, Is.False, "новую заводить некуда");
            Assert.That(store.Add("Первый", "ещё", Stamp).Ok, Is.True,
                "существующие обязаны правиться, иначе полное хранилище не разгрести");
        });
    }

    [Test]
    public void StopsRetrying_AfterRepeatedFailures()
    {
        // A fragile write must not eat up the whole turn and swallow the line the crew is waiting for.
        var store = NewStore(noteLimit: 100, maxEntry: 60);

        Assert.That(store.Add("Иван Петров", new string('а', 40), Stamp).Ok, Is.True,
            "первая запись должна помещаться");

        NoteResult last = null;
        for (var i = 0; i < 5; i++)
            last = store.Add("Иван Петров", new string((char)('б' + i), 40), Stamp);

        Assert.That(last!.Message, Does.Contain("не трать на неё этот ход"));
    }

    // --------------------------------------------------------------------- loading

    [Test]
    public void LoadFromDisk_SurvivesAGarbageFile()
    {
        NewStore().Add("Иван Петров", "Раз.", Stamp);
        File.WriteAllText(Path.Combine(PeopleDir, "мусор.md"), "тут нет заголовка");

        var store = NewStore();

        Assert.Multiple(() =>
        {
            Assert.That(store.Read("Иван Петров").Ok, Is.True, "один битый файл не роняет библиотеку");
            Assert.That(store.Count, Is.EqualTo(1), "битый файл в библиотеку не попадает");
        });
    }

    [Test]
    public void Write_LeavesNoTempFileBehind()
    {
        NewStore().Add("Иван Петров", "Раз.", Stamp);

        Assert.That(PeopleFiles().Where(f => f.EndsWith(".tmp", System.StringComparison.Ordinal)),
            Is.Empty);
    }

    // ----------------------------------------------------------------------- search

    [Test]
    public void Search_FindsBySubstringAndByMisspelling()
    {
        var store = NewStore();
        store.Add("Иван Петров", "Раз.", Stamp);
        store.Add("Мира Восс", "Раз.", Stamp);

        Assert.Multiple(() =>
        {
            Assert.That(Names(store.Search("петров")), Does.Contain("Иван Петров"));
            Assert.That(Names(store.Search("иван-птров")), Does.Contain("Иван Петров"),
                "опечатка в одну букву — это всё ещё похоже");
        });
    }

    [Test]
    public void Search_RefusesToGuessOnGarbage()
    {
        // SkillStore.Nearest has no threshold, and for a garbage query it still returns three
        // names, served to the model as "similar." Here an empty result is a legitimate result.
        var store = NewStore();
        store.Add("Иван Петров", "Раз.", Stamp);

        Assert.That(store.Search("кхзщыв"), Is.Empty);
    }

    [Test]
    public void Search_IsDeterministicOnTiedDistances()
    {
        // Without a tie-break, the order at equal distances is set by Dictionary iteration order,
        // meaning the tool's answer drifts between reloads.
        var store = NewStore();
        store.Add("Аня Иванова", "Раз.", Stamp);
        store.Add("Оля Иванова", "Раз.", Stamp);

        var first = Names(NewStore().Search("иванова"));
        var second = Names(NewStore().Search("иванова"));

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Is.EqualTo(new List<string> { "Аня Иванова", "Оля Иванова" }));
    }

    [Test]
    public void Search_WithoutAQuery_ListsEveryone()
    {
        var store = NewStore();
        store.Add("Иван Петров", "Раз.", Stamp);
        store.Add("Мира Восс", "Раз.", Stamp);

        Assert.That(store.Search(""), Has.Count.EqualTo(2));
    }

    private static List<string> Names(IReadOnlyList<(string Name, int Entries, string Preview)> rows) =>
        rows.Select(r => r.Name).ToList();
}
