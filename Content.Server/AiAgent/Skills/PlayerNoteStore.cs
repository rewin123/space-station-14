using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;

namespace Content.Server.AiAgent.Skills;

/// <summary>Одна заметка: слаг (он же имя файла), отображаемое имя и записи.</summary>
public sealed class PlayerNote
{
    public required string Slug { get; init; }

    /// <summary>
    /// Имя в том виде, в каком оно впервые прозвучало. Живёт в заголовке файла, а не в его имени:
    /// слаг лишён регистра, пробелов и всего, что нельзя пускать в путь, и показывать его модели
    /// значило бы показывать «иван-петров» вместо «Иван Петров».
    /// </summary>
    public required string Name { get; set; }

    public required List<string> Entries { get; init; }
}

public sealed record NoteResult(
    bool Ok,
    string Message,
    IReadOnlyList<string>? Entries = null,
    string? Usage = null);

/// <summary>
/// Заметки о персонажах: один файл на человека, переживают смену.
///
/// <b>Зачем отдельно от <see cref="MemoryStore"/>.</b> Там рядом с памятью о станции жил второй
/// файл, <c>CREW.md</c>, под людей своей смены, и он стирался на разборе раунда. На живом сервере
/// это дало обратный задуманному результат: агент перестал писать в стираемый файл и сложил людей
/// в <c>MEMORY.md</c>, который переживает раунды, — а тот упёрся в свой лимит и перестал принимать
/// что-либо вообще. <c>CREW.md</c> больше нет; люди целиком живут здесь.
/// Здесь лимит на ЗАМЕТКУ, а не на хранилище, поэтому переполненная заметка об одном человеке не
/// запирает запись обо всех остальных.
///
/// <b>Что скопировано у соседей по каталогу и почему.</b> Правка только фрагментом
/// (<see cref="MemoryStore"/>: кто может переписать файл целиком, однажды вернёт укороченную
/// версию и потеряет всё). Матчинг по короткой уникальной подстроке (модель помнит суть, а не
/// байты). Сокращение разрешено всегда, даже сверх лимита, иначе переполненная заметка не чинится.
/// Запись через tmp + rename с откатом правки в памяти при отказе диска, чтобы диск и память не
/// разошлись. <see cref="LoadFromDisk"/> не опустошает библиотеку при ошибке чтения.
///
/// <b>Чего здесь намеренно НЕТ.</b> Индекса в системном промпте. У <see cref="SkillStore"/> он
/// есть, и на 167 скиллах это уже около 20 КБ замороженного префикса; персонажей за месяцы станет
/// больше, и такой индекс съел бы окно. Заметка открывается инструментом, а о её существовании
/// напоминает строка NOTE в наблюдении.
/// </summary>
public sealed class PlayerNoteStore
{
    /// <summary>Тот же разделитель, что в <see cref="MemoryStore"/>: модель уже знает этот формат.</summary>
    public const string Delimiter = MemoryStore.Delimiter;

    private readonly string _dir;
    private readonly ISawmill _sawmill;
    private readonly Dictionary<string, PlayerNote> _notes = new();

    /// <summary>
    /// Стор читают с ДВУХ потоков. Инструменты заметок работают на потоке агента (они трогают
    /// файлы, а не сущности, и потому намеренно не маршалятся), а <see cref="TryPeek"/> дёргается
    /// с главного потока из обработчиков речи, когда надо решить, вешать ли подсказку. Реальной
    /// конкуренции нет, но лок стоит ноль и снимает «Collection was modified», который иначе
    /// вылезет ровно под нагрузкой.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Куда сообщать о правках, либо null, когда отладочная шина выключена.
    ///
    /// Через <see cref="AttachSink"/>, а не через конструктор, по той же причине, что у соседей:
    /// <c>ReloadAgentFiles</c> пересоздаёт стор целиком, и сток, привязанный к первому экземпляру,
    /// продолжал бы описывать хранилище, в которое больше никто не пишет.
    /// </summary>
    private IAgentEventSink? _sink;

    /// <summary>
    /// Лимит в СИМВОЛАХ, как у соседей: символы не зависят от модели, и агент может пересчитать их
    /// сам, когда его просят сократить.
    /// </summary>
    public int NoteLimit { get; init; } = 2000;

    /// <summary>
    /// Потолок на число заметок. Упирается только создание новых — существующие правятся дальше,
    /// иначе полное хранилище нельзя было бы разгрести.
    /// </summary>
    public int MaxNotes { get; init; } = 2000;

    /// <summary>
    /// Потолок на ОДНУ запись. Без него один пересказ смены занимает всю заметку и упирает её в
    /// лимит с первого раза, а заметка о человеке — это несколько строк, а не досье.
    /// </summary>
    public int MaxEntryLength { get; init; } = 400;

    /// <summary>Слаг обрезается здесь. В UTF-8 кириллица по два байта, так что 64 символа — это до 128 байт.</summary>
    private const int MaxSlugLength = 64;

    private const int MaxConsolidationFailuresPerTurn = 3;
    private int _consolidationFailures;

    public PlayerNoteStore(string dataDir, ISawmill sawmill)
    {
        _dir = Path.Combine(dataDir, "people");
        _sawmill = sawmill;
    }

    /// <summary>Начать сообщать о правках. Зовётся из <c>ReloadAgentFiles</c>, по разу на экземпляр.</summary>
    public void AttachSink(IAgentEventSink sink)
    {
        lock (_sync)
            _sink = sink;
    }

    public void ResetTurnCounters()
    {
        lock (_sync)
            _consolidationFailures = 0;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _notes.Count;
        }
    }

    /// <summary>
    /// Всё хранилище КОПИЯМИ, в устойчивом порядке — для снимка состояния.
    /// </summary>
    /// <remarks>
    /// Копиями, а не живыми объектами, в отличие от <c>SkillStore.All</c>: скилл — неизменяемая
    /// запись, а <see cref="PlayerNote"/> держит изменяемый <see cref="List{T}"/>, и HTTP-поток,
    /// обходящий его, пока поток агента дописывает запись, получил бы «Collection was modified»
    /// в отладчике, то есть ровно там, где чинят чужие поломки.
    /// </remarks>
    public IReadOnlyList<PlayerNote> All
    {
        get
        {
            lock (_sync)
                return _notes.Values
                    .OrderBy(n => n.Slug, StringComparer.Ordinal)
                    .Select(n => new PlayerNote
                    {
                        Slug = n.Slug,
                        Name = n.Name,
                        Entries = n.Entries.ToList(),
                    })
                    .ToList();
        }
    }

    // ------------------------------------------------------------------ имя → файл

    /// <summary>
    /// Имя персонажа → безопасный ключ.
    ///
    /// Это единственное место во всей подсистеме, где строка из-под контроля игрока превращается в
    /// путь на диске, и потому единственное, где санитизация обязательна.
    /// <see cref="SkillStore.Normalise"/> её не делает — там имя придумывает модель, и худшее, что
    /// бывает, это кривой заголовок. Здесь имя выбирает игрок в редакторе персонажа, а модель
    /// подставляет его в аргумент инструмента; персонаж по имени «../../SOUL» без этой функции
    /// писал бы мимо каталога.
    ///
    /// Метод не фильтрует чёрный список опасных последовательностей, а оставляет белый список
    /// разрешённых символов: буквы, цифры, дефис. После этого ни «..», ни слэш, ни двоеточие, ни
    /// абсолютный путь невыразимы в принципе — их нечем записать, а не «их вычистили».
    /// <c>char.IsLetterOrDigit</c> пропускает Unicode, поэтому кириллица работает, как и у скиллов.
    ///
    /// Пустая строка на выходе — законный результат для имени вроде «...», и вызывающий обязан
    /// отказать, а не молча писать в файл с пустым именем.
    /// </summary>
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sb = new StringBuilder(name.Length);

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_')
                sb.Append('-');
        }

        // Схлопнуть дефисы: «Иван   Петров» и «Иван-Петров» должны дать один ключ.
        var slug = sb.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        slug = slug.Trim('-');

        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength].Trim('-');

        // Сервер на Linux, но два байта страховки: на Windows файл с таким именем не создать,
        // и отладочная копия хранилища на ноутбуке падала бы на ровном месте.
        return Reserved.Contains(slug) ? slug + "-" : slug;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private string PathFor(string slug) => Path.Combine(_dir, $"{slug}.md");

    // ---------------------------------------------------------------- формат файла

    /// <summary>
    /// Заголовок с отображаемым именем, затем записи через разделитель. Не YAML — по той же
    /// причине, что и у скиллов: модель ломает YAML достаточно часто, чтобы это стоило учитывать.
    /// </summary>
    public static string Render(PlayerNote note) =>
        $"# {note.Name}\n{string.Join(Delimiter, note.Entries)}\n";

    /// <summary>Разобрать файл. <c>null</c> — файл не наш или испорчен; вызывающий его пропустит.</summary>
    public static PlayerNote? Parse(string raw, string slug)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal);

        var brk = text.IndexOf('\n');
        var header = (brk < 0 ? text : text[..brk]).Trim();

        if (!header.StartsWith("# ", StringComparison.Ordinal))
            return null;

        var name = header[2..].Trim();
        if (name.Length == 0)
            return null;

        var body = brk < 0 ? string.Empty : text[(brk + 1)..];

        // Сплит по ПОЛНОМУ разделителю, никогда по голому §: запись может законно его содержать.
        var entries = body.Split(Delimiter)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToList();

        return new PlayerNote { Slug = slug, Name = name, Entries = entries };
    }

    // -------------------------------------------------------------------------- io

    public void LoadFromDisk()
    {
        var loaded = new Dictionary<string, PlayerNote>();

        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var path in Directory.EnumerateFiles(_dir, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var slug = Path.GetFileNameWithoutExtension(path);

                    PlayerNote? note;
                    try
                    {
                        note = Parse(File.ReadAllText(path), slug);
                    }
                    catch (Exception e)
                    {
                        _sawmill.Warning($"заметка не читается: {path} ({e.Message})");
                        continue;
                    }

                    if (note == null)
                    {
                        _sawmill.Warning($"заметка не разобралась: {path}");
                        continue;
                    }

                    loaded[slug] = note;
                }
            }
        }
        catch (Exception e)
        {
            // Ранний выход БЕЗ очистки: сбой чтения каталога транзиентен, а опустошить библиотеку
            // из-за него значит потерять всё накопленное там, где надо было просто подождать.
            _sawmill.Error($"каталог заметок не читается: {e.Message}");
            return;
        }

        lock (_sync)
        {
            _notes.Clear();
            foreach (var (slug, note) in loaded)
                _notes[slug] = note;

            // Кадром на всё хранилище, а не по кадру на заметку. Перечитывание — единственный путь,
            // которым заметка ИСЧЕЗАЕТ без собственного события: файл удалили руками или он перестал
            // разбираться. Клиент, складывающий note.updated в карту, иначе держал бы призраков.
            _sink?.PlayerNotesReloaded(_notes.Values.ToList());
        }

        _sawmill.Info($"заметок о людях загружено: {loaded.Count}");
    }

    /// <summary>
    /// Записать заметку на диск. Возвращает false и объясняет причину, а не проглатывает ошибку.
    ///
    /// Проглотить — худший из доступных отказов: инструмент ответил бы «записано», куратор счёл бы
    /// дело сделанным и не повторил, а урок исчез бы к следующей загрузке. Все вызывающие
    /// откатывают правку в памяти, поэтому память и диск не расходятся.
    /// </summary>
    private bool TrySave(PlayerNote note, out string error)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(note.Slug);
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, Render(note));
            File.Move(tmp, path, overwrite: true);

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"заметка не сохранена: {error}");
            return false;
        }
    }

    private bool TryDeleteFile(string slug, out string error)
    {
        try
        {
            File.Delete(PathFor(slug));
            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"заметка не удалена: {error}");
            return false;
        }
    }

    // ----------------------------------------------------------------------- чтение

    /// <summary>
    /// Есть ли заметка и сколько в ней записей. Дешёвая проверка для подсказки NOTE — её дёргают с
    /// главного потока на каждую первую реплику, поэтому здесь только словарь и никакого диска.
    /// </summary>
    public bool TryPeek(string? name, out string display, out int entries)
    {
        display = string.Empty;
        entries = 0;

        var slug = Slugify(name);
        if (slug.Length == 0)
            return false;

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return false;

            display = note.Name;
            entries = note.Entries.Count;
            return true;
        }
    }

    public NoteResult Read(string? name)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false, "нужно имя персонажа");

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return new NoteResult(false, $"заметок о «{name}» нет");

            return new NoteResult(true, note.Name, note.Entries.ToList(),
                $"{Length(note.Entries)}/{NoteLimit}");
        }
    }

    /// <summary>
    /// Поиск по неточному имени.
    ///
    /// Сначала подстрока, потом Левенштейн — и, в отличие от <see cref="SkillStore.Nearest"/>, с
    /// порогом и с устойчивым порядком. Без порога на мусорный запрос всё равно возвращались бы
    /// три случайных имени, поданных модели как «похожие»; без tie-break порядок при равных
    /// расстояниях определялся бы обходом словаря, то есть менялся бы между перезагрузками.
    /// </summary>
    public IReadOnlyList<(string Name, int Entries, string Preview)> Search(string? approx)
    {
        var needle = (approx ?? string.Empty).Trim();

        lock (_sync)
        {
            var all = _notes.Values.ToList();

            if (needle.Length == 0)
            {
                return all
                    .OrderBy(n => n.Name, StringComparer.Ordinal)
                    .Select(Row)
                    .ToList();
            }

            var slug = Slugify(needle);

            var scored = all
                .Select(n =>
                {
                    var substring = n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                    || (slug.Length > 0 && n.Slug.Contains(slug, StringComparison.Ordinal));

                    var distance = Tools.AiToolRegistry.Distance(n.Slug, slug);
                    return (Note: n, Substring: substring, Distance: distance);
                })
                // Порог: опечатка в паре букв — это похоже, а совпадение «на треть» уже нет.
                .Where(x => x.Substring || x.Distance <= Math.Max(2, slug.Length / 3))
                .OrderByDescending(x => x.Substring)
                .ThenBy(x => x.Distance)
                .ThenBy(x => x.Note.Name, StringComparer.Ordinal)
                .Select(x => Row(x.Note))
                .ToList();

            return scored;
        }
    }

    private static (string Name, int Entries, string Preview) Row(PlayerNote n) =>
        (n.Name, n.Entries.Count, n.Entries.Count > 0 ? Preview(n.Entries[0], 60) : "(пусто)");

    // ------------------------------------------------------------------------ запись

    /// <summary>
    /// Добавить запись, создав заметку, если её ещё нет.
    ///
    /// Штамп ставится ЗДЕСЬ, а не моделью: модель забудет, и через раунд заметка «взломал шкаф»
    /// перестанет отличаться от сегодняшнего доклада. Приходит он параметром, а не берётся из
    /// <c>DateTime.Now</c> внутри, чтобы тесты формата остались детерминированными.
    /// </summary>
    public NoteResult Add(string? name, string content, string stamp)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false,
                $"из имени «{name}» не выходит ключа — назови персонажа так, как он звучит в эфире");

        content = content.Trim();
        if (content.Length == 0)
            return new NoteResult(false, "пустую запись добавить нельзя");

        if (content.Length > MaxEntryLength)
            return new NoteResult(false,
                $"запись длиннее {MaxEntryLength} символов ({content.Length}) — заметка о человеке " +
                "это несколько строк, а не пересказ смены");

        var stamped = string.IsNullOrWhiteSpace(stamp) ? content : $"{stamp.Trim()} {content}";

        lock (_sync)
        {
            var existed = _notes.TryGetValue(slug, out var note);

            if (!existed && _notes.Count >= MaxNotes)
                return new NoteResult(false,
                    $"заметок уже {_notes.Count} из {MaxNotes} — новую завести некуда. " +
                    "Существующие правятся по-прежнему.");

            note ??= new PlayerNote
            {
                Slug = slug,
                Name = (name ?? slug).Trim(),
                Entries = new List<string>(),
            };

            if (note.Entries.Any(e => e == stamped))
                return new NoteResult(true, "такая запись уже есть");

            var newTotal = Length(note.Entries.Append(stamped));
            if (newTotal > NoteLimit)
                return ConsolidationFailure(note,
                    $"заметка о «{note.Name}» заполнена: {newTotal}/{NoteLimit} символов. Сократи " +
                    "запись или выброси устаревшее через edit_player_related_memory с пустым 'new', " +
                    "и повтори — всё в этом же ходу.");

            note.Entries.Add(stamped);

            if (!TrySave(note, out var error))
            {
                note.Entries.RemoveAt(note.Entries.Count - 1);
                return NotWritten(note, error);
            }

            _notes[slug] = note;
            return Success(note, existed ? "записано" : "заметка заведена");
        }
    }

    /// <summary>Заменить запись, содержащую <paramref name="oldText"/>.</summary>
    public NoteResult Replace(string? name, string oldText, string newContent)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false, "нужно имя персонажа");

        oldText = oldText.Trim();
        newContent = newContent.Trim();

        if (oldText.Length == 0)
            return new NoteResult(false, "нужен фрагмент 'old' — часть текста заменяемой записи");
        if (newContent.Length == 0)
            return new NoteResult(false, "пустой 'new' — это удаление; так и задумано, но тогда 'old' обязателен");

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return new NoteResult(false, $"заметок о «{name}» нет — сначала заведи запись с пустым 'old'");

            var matches = note.Entries.Select((e, i) => (Entry: e, Index: i))
                .Where(x => x.Entry.Contains(oldText, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                return ConsolidationFailure(note,
                    $"ни одна запись о «{note.Name}» не содержит '{Preview(oldText, 90)}'. Ты помнишь " +
                    "текст неточно — посмотри список ниже и скопируй фрагмент дословно.");

            if (matches.Select(m => m.Entry).Distinct().Count() > 1)
                return new NoteResult(false,
                    $"фрагмент '{Preview(oldText, 90)}' встречается в нескольких записях — возьми подлиннее",
                    matches.Select(m => Preview(m.Entry, 90)).ToList());

            var idx = matches[0].Index;

            var test = note.Entries.ToList();
            test[idx] = newContent;

            // Сокращение разрешено всегда, даже сверх лимита, иначе переполненную заметку не починить.
            var grew = newContent.Length > note.Entries[idx].Length;

            if (newContent.Length > MaxEntryLength && grew)
                return new NoteResult(false,
                    $"запись длиннее {MaxEntryLength} символов ({newContent.Length}) — сократи");

            if (Length(test) > NoteLimit && grew)
                return ConsolidationFailure(note,
                    $"после замены будет {Length(test)}/{NoteLimit} символов. Сократи текст или " +
                    "сначала выбрось устаревшее — всё в этом же ходу.");

            var previous = note.Entries[idx];
            note.Entries[idx] = newContent;

            if (!TrySave(note, out var error))
            {
                note.Entries[idx] = previous;
                return NotWritten(note, error);
            }

            return Success(note, "запись заменена");
        }
    }

    /// <summary>
    /// Удалить запись. Если она была последней — удалить и файл: каталог, зарастающий пустыми
    /// заметками, врёт поиску о том, кого агент знает.
    /// </summary>
    public NoteResult Remove(string? name, string oldText)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            return new NoteResult(false, "нужно имя персонажа");

        oldText = oldText.Trim();
        if (oldText.Length == 0)
            return new NoteResult(false, "нужен фрагмент 'old'");

        lock (_sync)
        {
            if (!_notes.TryGetValue(slug, out var note))
                return new NoteResult(false, $"заметок о «{name}» нет");

            var matches = note.Entries.Where(e => e.Contains(oldText, StringComparison.Ordinal)).ToList();

            if (matches.Count == 0)
                return new NoteResult(false,
                    $"ни одна запись о «{note.Name}» не содержит '{Preview(oldText, 90)}'",
                    note.Entries.Select(e => Preview(e, 90)).ToList());

            if (matches.Distinct().Count() > 1)
                return new NoteResult(false,
                    $"фрагмент '{Preview(oldText, 90)}' встречается в нескольких записях — возьми подлиннее",
                    matches.Select(e => Preview(e, 90)).ToList());

            var at = note.Entries.IndexOf(matches[0]);
            note.Entries.RemoveAt(at);

            if (note.Entries.Count == 0)
            {
                if (!TryDeleteFile(slug, out var delError))
                {
                    note.Entries.Insert(at, matches[0]);
                    return NotWritten(note, delError);
                }

                _notes.Remove(slug);
                _consolidationFailures = 0;

                // Надгробие: заметка ушла вместе с файлом, и записей в ней теперь ноль. Отдельного
                // вида события на это нет намеренно — «новое целое значение ключа» здесь пусто,
                // и клиент на пустом списке ключ удаляет.
                _sink?.PlayerNoteUpdated(note);

                return new NoteResult(true, $"последняя запись удалена, заметка о «{note.Name}» закрыта");
            }

            if (!TrySave(note, out var error))
            {
                note.Entries.Insert(at, matches[0]);
                return NotWritten(note, error);
            }

            return Success(note, "запись удалена");
        }
    }

    // ----------------------------------------------------------------------- хелперы

    private static int Length(IEnumerable<string> entries) => string.Join(Delimiter, entries).Length;

    /// <summary>
    /// Единственный успешный выход у всех трёх правок, и потому единственное место, где о них
    /// сообщается.
    ///
    /// Именно здесь, а не в <c>Add</c>/<c>Replace</c>/<c>Remove</c> по отдельности: сообщать надо
    /// только после того, как <see cref="TrySave"/> подтвердил запись, а все они на отказе диска
    /// откатывают правку в памяти и уходят через <see cref="NotWritten"/>. Событие, объявившее
    /// запись, которой агент не увидит после перезагрузки, хуже отсутствия события — оно выглядит
    /// достоверным.
    /// </summary>
    private NoteResult Success(PlayerNote note, string message)
    {
        _consolidationFailures = 0;
        _sink?.PlayerNoteUpdated(note);

        return new NoteResult(true, message, null,
            string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));
    }

    private NoteResult NotWritten(PlayerNote note, string error) =>
        new(false, $"на диск записать не удалось, заметка не изменилась ({error}). Попробуй позже.",
            null, string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));

    private NoteResult ConsolidationFailure(PlayerNote note, string error)
    {
        _consolidationFailures++;

        // Терминальный ответ после нескольких попыток: хрупкая запись не должна выесть ход целиком
        // и проглотить реплику, которой ждёт экипаж.
        if (_consolidationFailures > MaxConsolidationFailuresPerTurn)
            return new NoteResult(false,
                "запись пропущена — не трать на неё этот ход, ответь экипажу и попробуй позже",
                null, string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));

        return new NoteResult(false, error,
            note.Entries.Select(e => Preview(e, 90)).ToList(),
            string.Create(CultureInfo.InvariantCulture, $"{Length(note.Entries)}/{NoteLimit}"));
    }

    private static string Preview(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
