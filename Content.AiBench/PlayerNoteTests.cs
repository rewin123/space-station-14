using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// Заметки о персонажах: один файл на человека, живут между сменами.
///
/// Здесь сосредоточен весь риск подсистемы, и он проверяется без сервера. Главный тест —
/// <see cref="Name_CannotEscapeTheDirectory"/>: имя персонажа выбирает игрок, модель подставляет
/// его в аргумент инструмента, и оно становится путём на диске. Соседний
/// <see cref="SkillStore.Normalise"/> санитизации не делает вовсе — там имя придумывает модель, и
/// худшее, что бывает, это кривой заголовок.
///
/// Второй по важности — <see cref="Overflow_IsLocalToOnePerson"/>: он кодирует, ЗАЧЕМ хранилище
/// пофайловое. На живом сервере общий котёл MEMORY.md упёрся в свой лимит и перестал принимать
/// что-либо вообще; лимит на человека делает отказ локальным.
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

    /// <summary>Каталог, в который стор реально пишет.</summary>
    private string PeopleDir => Path.Combine(_dir, "people");

    private string[] PeopleFiles() =>
        Directory.Exists(PeopleDir) ? Directory.GetFiles(PeopleDir) : System.Array.Empty<string>();

    // --------------------------------------------------------------------- формат

    [Test]
    public void Note_RoundTripsThroughDisk()
    {
        var store = NewStore();

        Assert.That(store.Add("Иван Петров", "Инженер, просил открыть атмос.", Stamp).Ok, Is.True);
        Assert.That(store.Add("Иван Петров", "Обещал вернуть карту и вернул.", Stamp).Ok, Is.True);

        // Второй стор из того же каталога обязан увидеть ровно то же.
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
        // Слаг лоссовый: он лишён регистра, пунктуации и кавычек. Показывать модели «иван-ржавый»
        // вместо «Иван "Ржавый" Петров» значило бы врать ей о том, как человека зовут.
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
        // Ровно как у скиллов (антаг-вор.md): читаемое имя файла — главный отладочный аффорданс,
        // хеш вместо него грепом не найдёшь.
        NewStore().Add("Иван Петров", "Раз.", Stamp);

        Assert.That(PeopleFiles().Select(Path.GetFileName), Does.Contain("иван-петров.md"));
    }

    // ------------------------------------------------------------------ безопасность

    [Test]
    public void Name_CannotEscapeTheDirectory()
    {
        // Белый список символов, а не чёрный список опасных последовательностей: после него «..»,
        // слэш и двоеточие невыразимы в принципе, а не «вычищены» списком, который забудут пополнить.
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

            // Ничего не появилось и не изменилось уровнем выше — там лежит SOUL.md живого агента.
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
        // Сервер на Linux, но отладочная копия хранилища на ноутбуке не должна падать на ровном месте.
        Assert.Multiple(() =>
        {
            Assert.That(PlayerNoteStore.Slugify("CON"), Is.EqualTo("con-"));
            Assert.That(PlayerNoteStore.Slugify("nul"), Is.EqualTo("nul-"));
            Assert.That(PlayerNoteStore.Slugify("com1"), Is.EqualTo("com1-"));
            Assert.That(PlayerNoteStore.Slugify("Conrad"), Is.EqualTo("conrad"), "не трогать похожие");
        });
    }

    // ----------------------------------------------------------------------- штамп

    [Test]
    public void Add_StampsEveryEntry()
    {
        // Штамп ставит стор, а не модель: модель забудет, и наполовину проштампованное хранилище
        // хуже непроштампованного — по нему нельзя отличить прошлую смену от сегодняшней.
        var store = NewStore();
        store.Add("Иван Петров", "Взломал шкаф.", Stamp);

        var entry = store.Read("Иван Петров").Entries![0];

        Assert.That(entry, Is.EqualTo($"{Stamp} Взломал шкаф."));
    }

    [Test]
    public void Replace_KeepsWhateverStampTheModelWrites()
    {
        // Правка не перештамповывается: штамп отвечает на вопрос «когда я это узнал», а
        // переформулировка знания не меняет. Модель правит запись вместе с её штампом.
        var store = NewStore();
        store.Add("Иван Петров", "Взломал шкаф.", Stamp);

        var result = store.Replace("Иван Петров", "Взломал шкаф.", $"{Stamp} Взломал шкаф ГП.");

        Assert.That(result.Ok, Is.True, result.Message);
        Assert.That(store.Read("Иван Петров").Entries![0], Is.EqualTo($"{Stamp} Взломал шкаф ГП."));
    }

    // ------------------------------------------------------------------- правки

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
        // Каталог, зарастающий пустыми заметками, врёт поиску о том, кого агент знает.
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

    // ------------------------------------------------------------------- лимиты

    [Test]
    public void Overflow_IsLocalToOnePerson()
    {
        // Это тест про то, ЗАЧЕМ хранилище пофайловое. В общем котле MEMORY.md одна многословная
        // тема заперла запись обо всём остальном; здесь переполненный Иван не мешает Петру.
        var store = NewStore(noteLimit: 120, maxEntry: 100);

        // Записи разные: на точный дубликат Add отвечает «такая запись уже есть» и Ok=true.
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
        // Урок MEMORY.md: если сокращение запрещать по вместимости, переполненную память нельзя
        // починить никогда, и она запирается навсегда. Ровно так это и случилось на mcbot, когда
        // лимит понизили под уже накопленным текстом — воспроизводим тот же сценарий.
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
        // Хрупкая запись не должна выесть ход целиком и проглотить реплику, которой ждёт экипаж.
        var store = NewStore(noteLimit: 100, maxEntry: 60);

        Assert.That(store.Add("Иван Петров", new string('а', 40), Stamp).Ok, Is.True,
            "первая запись должна помещаться");

        NoteResult last = null;
        for (var i = 0; i < 5; i++)
            last = store.Add("Иван Петров", new string((char)('б' + i), 40), Stamp);

        Assert.That(last!.Message, Does.Contain("не трать на неё этот ход"));
    }

    // --------------------------------------------------------------------- загрузка

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

    // ----------------------------------------------------------------------- поиск

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
        // У SkillStore.Nearest порога нет, и на мусорный запрос он всё равно возвращает три имени,
        // поданных модели как «похожие». Здесь пустой ответ — законный ответ.
        var store = NewStore();
        store.Add("Иван Петров", "Раз.", Stamp);

        Assert.That(store.Search("кхзщыв"), Is.Empty);
    }

    [Test]
    public void Search_IsDeterministicOnTiedDistances()
    {
        // Без tie-break порядок при равных расстояниях задаёт обход Dictionary, то есть ответ
        // инструмента плавает между перезагрузками.
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
