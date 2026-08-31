using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Vfs;

/// <summary>Одна статья: путь внутри монтирования, заголовок, строка «когда» и тело.</summary>
public sealed record Doc(string Path, string Title, string When, string Body, DateTime Modified)
{
    /// <summary>Имя без каталога — то, что показывает <c>ls</c>.</summary>
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
}

/// <summary>
/// Дерево статей на диске. Формат тот же, что был у библиотеки скиллов, плюс вложенность.
///
/// <para>
/// Формат — <c>#&#160;имя</c>, затем <c>когда:&#160;…</c>, затем тело. Он выбран не из любви к
/// простоте: YAML-заголовок ломался у модели на кавычке и отступе достаточно часто, чтобы от него
/// отказались на прежнем развёртывании. Здесь сломать нечего.
/// </para>
/// <para>
/// Описание папки живёт в её <c>_index.md</c>: та же строка «когда», а тело — обзор раздела. Это
/// не изобретение, а то, чем уже являются файлы <c>справочник-*.md</c>; миграция их просто
/// переименовывает.
/// </para>
/// <para>
/// Правка — только фрагментом. Тот, кому позволено переписать файл целиком, однажды вернёт
/// укороченную версию, и накопленное исчезнет за один ход, молча. <see cref="Write"/> существует
/// для создания и осознанной замены, <see cref="Edit"/> — для всего остального.
/// </para>
/// </summary>
public sealed class DocTree
{
    public const int MaxWhen = 60;
    public const int MaxBody = 5000;

    private readonly string _root;
    private readonly ISawmill _sawmill;

    /// <summary>Путь внутри монтирования («атмосфера/насосы») → статья.</summary>
    private readonly Dictionary<string, Doc> _docs = new(StringComparer.Ordinal);

    /// <summary>Каталоги, включая пустые: без этого <c>mkdir</c> не оставлял бы следа.</summary>
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    /// <summary>
    /// Дерево читают из двух потоков: <see cref="Reload"/> — поток агента на шаге перестройки
    /// префикса, листинг — главный, когда консоль или отладчик печатают состояние. Замена словаря
    /// посреди перечисления даёт «Collection was modified», которое проявится только на живой
    /// смене.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Куда сообщать о правках, или <c>null</c>, когда шина отладки выключена.
    ///
    /// <para>
    /// Формат событий остался прежним — <c>skill.updated</c> и <c>skills.reloaded</c> с полями
    /// <c>name/when/body</c>, — хотя хранение сменилось целиком. Менять проводной формат заодно с
    /// хранением значило бы чинить две вещи сразу и не знать, какая из них сломалась. Поле
    /// <c>name</c> теперь несёт путь внутри монтирования.
    /// </para>
    /// </summary>
    private IAgentEventSink? _sink;

    public DocTree(string root, ISawmill sawmill)
    {
        _root = root;
        _sawmill = sawmill;
    }

    public string Root => _root;

    /// <summary>Начать сообщать о правках. Зовётся при сборке файловой системы, по разу.</summary>
    public void AttachSink(IAgentEventSink sink)
    {
        lock (_sync)
            _sink = sink;
    }

    private static Skill AsSkill(Doc doc) => new(doc.Path, doc.When, doc.Body);

    /// <summary>
    /// Единственное место, куда статья попадает в память, — и потому единственное, что о ней
    /// сообщает. Зовётся уже ПОСЛЕ успешной записи на диск, так что отказ диска не публикует
    /// ничего. Вызывающий держит лок.
    /// </summary>
    private void Commit(string relative, Doc doc)
    {
        _docs[relative] = doc;
        _sink?.SkillUpdated(AsSkill(doc));
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _docs.Count;
        }
    }

    /// <summary>Всё дерево записями шины. Для снимка состояния в отладчике.</summary>
    public IReadOnlyList<Skill> All
    {
        get
        {
            lock (_sync)
                return _docs.Values.OrderBy(d => d.Path, StringComparer.Ordinal).Select(AsSkill).ToList();
        }
    }

    // ------------------------------------------------------------------- разбор

    public static Doc? Parse(string path, string text, DateTime modified)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        if (lines.Length == 0)
            return null;

        var first = lines[0].Trim();

        if (!first.StartsWith('#'))
            return null;

        var title = first.TrimStart('#').Trim();
        var when = string.Empty;
        var bodyStart = 1;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith("когда:", StringComparison.OrdinalIgnoreCase))
            {
                when = line["когда:".Length..].Trim();
                bodyStart = i + 1;
            }

            break;
        }

        var body = string.Join('\n', lines.Skip(bodyStart)).Trim();
        return new Doc(path, title, when, body, modified);
    }

    public static string Render(Doc doc) => $"# {doc.Title}\nкогда: {doc.When}\n{doc.Body}\n";

    // ---------------------------------------------------------------------- диск

    public void Reload()
    {
        // Каталог читается целиком до взятия лока: держать лок поверх файлового ввода-вывода
        // значит поставить задержку диска перед тем, что делает главный поток.
        var docs = new Dictionary<string, Doc>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                    dirs.Add(RelativeOf(dir));

                foreach (var file in Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories))
                {
                    // Ключ — путь как на диске, с расширением. Это настоящая файловая система,
                    // просто смонтированная; расходиться с ней в именах незачем.
                    var rel = RelativeOf(file);

                    var doc = Parse(rel, File.ReadAllText(file), File.GetLastWriteTime(file));

                    if (doc == null)
                    {
                        _sawmill.Warning($"не разобрался как статья: {file}");
                        continue;
                    }

                    docs[rel] = doc;
                }
            }
        }
        catch (Exception e)
        {
            // Оставляем то, что уже в памяти: разовая ошибка чтения не должна опустошить
            // библиотеку, которую агент собирал месяцами.
            _sawmill.Warning($"дерево {_root} не читается: {e.Message}");
            return;
        }

        lock (_sync)
        {
            _docs.Clear();
            foreach (var (key, doc) in docs)
                _docs[key] = doc;

            _dirs.Clear();
            foreach (var dir in dirs)
                _dirs.Add(dir);

            // Один кадр на всё дерево, а не по кадру на уцелевшего. Перечитывание — единственный
            // путь, которым статья может исчезнуть, а событие про уцелевших об исчезнувших молчит.
            _sink?.SkillsReloaded(_docs.Values.Select(AsSkill).ToList());
        }
    }

    private string RelativeOf(string absolute) =>
        Path.GetRelativePath(_root, absolute).Replace('\\', '/');

    private string DiskPath(string relative) =>
        Path.Combine(_root, Path.Combine(relative.Split('/')));

    /// <summary>
    /// Ключ статьи по пути из запроса: как написано, а если такого нет — с дописанным <c>.md</c>.
    /// </summary>
    /// <remarks>
    /// Единственная поблажка адресации. Расширение — деталь хранения, и требовать его в каждом
    /// пути значило бы ловить промахи там, где ошибки нет. Точное совпадение проверяется первым:
    /// файл, названный без расширения, остаётся достижимым под своим настоящим именем.
    /// </remarks>
    private string? KeyOf(string relative)
    {
        if (relative.Length == 0)
            return null;

        if (_docs.ContainsKey(relative))
            return relative;

        var withExtension = VfsPath.WithExtension(relative);
        return _docs.ContainsKey(withExtension) ? withExtension : null;
    }

    // ------------------------------------------------------------------- чтение

    public bool TryGet(string relative, out Doc doc)
    {
        lock (_sync)
        {
            var key = KeyOf(relative);

            if (key != null)
                return _docs.TryGetValue(key, out doc!);

            doc = null!;
            return false;
        }
    }

    public bool HasDir(string relative)
    {
        if (relative.Length == 0)
            return true;

        lock (_sync)
            return _dirs.Contains(relative) || _docs.Keys.Any(k => k.StartsWith(relative + "/", StringComparison.Ordinal));
    }

    /// <summary>Прямые дети каталога: сначала папки, потом статьи, обе группы по алфавиту.</summary>
    public IReadOnlyList<VfsEntry> Children(string relative)
    {
        lock (_sync)
        {
            var prefix = relative.Length == 0 ? string.Empty : relative + "/";
            var dirs = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var files = new List<VfsEntry>();

            foreach (var dir in _dirs)
            {
                if (!Under(dir, prefix, out var rest) || rest.Contains('/'))
                    continue;

                dirs.TryAdd(rest, 0);
            }

            foreach (var (key, doc) in _docs)
            {
                if (!Under(key, prefix, out var rest))
                    continue;

                var slash = rest.IndexOf('/');

                if (slash >= 0)
                {
                    var head = rest[..slash];
                    dirs.TryGetValue(head, out var n);
                    dirs[head] = n + 1;
                    continue;
                }

                // _index описывает саму папку и в её же листинге не показывается: иначе каждая
                // папка содержала бы файл с именем своего собственного оглавления.
                if (rest == VfsPath.IndexFile + VfsPath.Extension)
                    continue;

                files.Add(new VfsEntry(rest, false, doc.When, doc.Body.Length, doc.Modified));
            }

            var result = new List<VfsEntry>();

            foreach (var (name, count) in dirs)
            {
                var indexKey = prefix + name + "/" + VfsPath.IndexFile + VfsPath.Extension;
                var desc = _docs.TryGetValue(indexKey, out var index) ? index.When : string.Empty;
                result.Add(new VfsEntry(name, true, desc, count, null));
            }

            result.AddRange(files.OrderBy(f => f.Name, StringComparer.Ordinal));
            return result;
        }
    }

    private static bool Under(string key, string prefix, out string rest)
    {
        rest = string.Empty;

        if (prefix.Length == 0)
        {
            rest = key;
            return true;
        }

        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        rest = key[prefix.Length..];
        return rest.Length > 0;
    }

    /// <summary>Имена, ближайшие по Левенштейну, — для внятного отказа вместо «нет такого».</summary>
    public IReadOnlyList<string> Nearest(string relative, int count = 3)
    {
        lock (_sync)
            return _docs.Keys
                .OrderBy(k => Tools.AiToolRegistry.Distance(k, relative))
                .ThenBy(k => k, StringComparer.Ordinal)
                .Take(count)
                .ToList();
    }

    public IReadOnlyList<VfsHit> Grep(string needle, string relative, string mountPoint, int limit)
    {
        var hits = new List<VfsHit>();
        var prefix = relative.Length == 0 ? string.Empty : relative + "/";

        lock (_sync)
        {
            foreach (var (key, doc) in _docs.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal) && key != relative)
                    continue;

                var line = 0;

                foreach (var text in doc.Body.Split('\n'))
                {
                    line++;

                    if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new VfsHit($"/{mountPoint}/{key}", line, text.Trim()));

                    if (hits.Count >= limit)
                        return hits;
                }

                // Строка «когда» тоже ищется: чаще всего именно в ней стоит слово, которым экипаж
                // называет предмет, а тело говорит уже подробностями.
                if (doc.When.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new VfsHit($"/{mountPoint}/{key}", 0, "когда: " + doc.When));

                if (hits.Count >= limit)
                    return hits;
            }
        }

        return hits;
    }

    // -------------------------------------------------------------------- запись

    public VfsWrite Write(string relative, string title, string when, string body)
    {
        when = when.Trim();
        body = body.Trim();

        if (relative.Length == 0)
            return VfsWrite.No("нужен путь файла");

        // Пишем всегда с расширением: иначе следующее перечитывание каталога, идущее по «*.md»,
        // просто не увидит файл, и запись выглядела бы как удавшаяся ровно до перезапуска.
        relative = VfsPath.WithExtension(relative);

        if (when.Length == 0)
            return VfsWrite.No("нужно описание — по нему файл и находят в листинге");

        if (when.Length > MaxWhen)
            return VfsWrite.No(
                $"описание длиннее {MaxWhen} символов ({when.Length}). Это единственная строка, " +
                "которую видно в ls, — всё за пределом просто не доедет до листинга. Сократи.");

        if (body.Length > MaxBody)
            return VfsWrite.No($"тело длиннее {MaxBody} символов ({body.Length})");

        lock (_sync)
        {
            if (!_docs.ContainsKey(relative))
            {
                var clash = FindOverlapping(relative);

                if (clash != null)
                    return VfsWrite.No(
                        $"«{clash}» — то же имя с довеском или без него, то есть тот же самый файл. " +
                        "Не плоди близнецов: дополни его через edit_file. Если имя осмысленно только " +
                        "для сегодняшней задачи, оно неправильное. Соседнее имя в той же папке " +
                        "(другое слово, а не уточнение к этому) записать можно.",
                        new[] { clash });
            }

            var existed = _docs.ContainsKey(relative);
            var doc = new Doc(relative, title.Trim().Length > 0 ? title.Trim() : NameOf(relative), when, body, DateTime.Now);

            if (!TrySave(doc, out var error))
                return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({error})");

            Commit(relative, doc);
            EnsureDirsFor(relative);

            return VfsWrite.Fine(existed ? "файл перезаписан" : "файл создан");
        }
    }

    public VfsWrite Edit(string relative, string match, string replacement)
    {
        match = match.Trim();

        lock (_sync)
        {
            var key = KeyOf(relative);

            if (key == null || !_docs.TryGetValue(key, out var doc))
                return VfsWrite.No($"нет файла «{relative}»", NearestLocked(relative));

            relative = key;

            if (match.Length == 0)
            {
                var appended = doc with { Body = (doc.Body + "\n" + replacement.Trim()).Trim(), Modified = DateTime.Now };

                if (appended.Body.Length > MaxBody)
                    return VfsWrite.No($"тело превысит {MaxBody} символов — сначала выбрось лишнее");

                if (!TrySave(appended, out var appendError))
                    return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({appendError})");

                Commit(relative, appended);
                return VfsWrite.Fine("дописано в конец");
            }

            var count = Occurrences(doc.Body, match);

            if (count == 0)
                return VfsWrite.No(
                    "такого фрагмента в файле нет — ты помнишь текст неточно, открой его через " +
                    "cat и скопируй дословно");

            if (count > 1)
                return VfsWrite.No("фрагмент встречается несколько раз — возьми подлиннее");

            var body = doc.Body.Replace(match, replacement.Trim(), StringComparison.Ordinal).Trim();

            if (body.Length > MaxBody)
                return VfsWrite.No($"тело превысит {MaxBody} символов");

            var edited = doc with { Body = body, Modified = DateTime.Now };

            if (!TrySave(edited, out var saveError))
                return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({saveError})");

            Commit(relative, edited);
            return VfsWrite.Fine(replacement.Trim().Length == 0 ? "фрагмент удалён" : "фрагмент заменён");
        }
    }

    public VfsWrite MakeDir(string relative)
    {
        if (relative.Length == 0)
            return VfsWrite.No("нужен путь папки");

        lock (_sync)
        {
            if (_docs.ContainsKey(relative))
                return VfsWrite.No($"«{relative}» — это файл, а не папка");

            if (_dirs.Contains(relative))
                return VfsWrite.Fine("папка уже была");

            try
            {
                Directory.CreateDirectory(DiskPath(relative));
            }
            catch (Exception e)
            {
                return VfsWrite.No($"папку создать не удалось ({e.GetType().Name}: {e.Message})");
            }

            _dirs.Add(relative);
            EnsureDirsFor(relative);

            return VfsWrite.Fine("папка создана");
        }
    }

    public VfsWrite Remove(string relative)
    {
        if (relative.Length == 0)
            return VfsWrite.No("корень удалить нельзя");

        lock (_sync)
        {
            if (KeyOf(relative) is { } key)
            {
                relative = key;

                try
                {
                    File.Delete(DiskPath(relative));
                }
                catch (Exception e)
                {
                    return VfsWrite.No($"удалить не удалось ({e.GetType().Name}: {e.Message})");
                }

                _docs.Remove(relative);

                // Пропажу отдельным событием не описать: у skill.updated нет формы «этого больше
                // нет», и клиент, сворачивающий события в карту, держал бы призрак вечно. Поэтому
                // удаление публикуется целым набором — так же, как перечитывание.
                _sink?.SkillsReloaded(_docs.Values.Select(AsSkill).ToList());

                return VfsWrite.Fine("файл удалён");
            }

            if (!_dirs.Contains(relative))
                return VfsWrite.No($"нет ни файла, ни папки «{relative}»", NearestLocked(relative));

            // Непустую папку сносить отказываемся. Рекурсивное удаление одной строкой — это способ
            // потерять раздел справочника опечаткой в пути, а обратной операции у нас нет.
            var children = _docs.Keys.Count(k => k.StartsWith(relative + "/", StringComparison.Ordinal));

            if (children > 0)
                return VfsWrite.No($"в папке ещё {children} файлов — удали их сначала");

            try
            {
                Directory.Delete(DiskPath(relative));
            }
            catch (Exception e)
            {
                return VfsWrite.No($"папку удалить не удалось ({e.GetType().Name}: {e.Message})");
            }

            _dirs.Remove(relative);
            return VfsWrite.Fine("папка удалена");
        }
    }

    public VfsWrite Move(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0)
            return VfsWrite.No("нужны оба пути");

        lock (_sync)
        {
            if (KeyOf(from) is not { } fromKey || !_docs.TryGetValue(fromKey, out var doc))
                return VfsWrite.No($"нет файла «{from}»", NearestLocked(from));

            from = fromKey;
            to = VfsPath.WithExtension(to);

            if (_docs.ContainsKey(to))
                return VfsWrite.No($"«{to}» уже занято");

            var moved = doc with { Path = to, Modified = DateTime.Now };

            if (!TrySave(moved, out var error))
                return VfsWrite.No($"на диск записать не удалось, ничего не изменилось ({error})");

            try
            {
                File.Delete(DiskPath(from));
            }
            catch (Exception e)
            {
                // Копия уже легла: сообщаем правду вместо «переименовано», иначе на диске тихо
                // останутся оба файла, и следующий Reload покажет близнеца.
                _sawmill.Warning($"старый файл {from} не удалён: {e.Message}");
                return VfsWrite.No($"скопировано в «{to}», но старый файл удалить не удалось ({e.GetType().Name})");
            }

            _docs.Remove(from);
            Commit(to, moved);
            EnsureDirsFor(to);

            return VfsWrite.Fine($"переименовано в «{to}»");
        }
    }

    // ------------------------------------------------------------- внутренности

    private bool TrySave(Doc doc, out string error)
    {
        try
        {
            var path = DiskPath(doc.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Render(doc));
            File.Move(tmp, path, overwrite: true);

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _sawmill.Error($"файл «{doc.Path}» не сохранён: {error}");
            return false;
        }
    }

    private void EnsureDirsFor(string relative)
    {
        var parts = relative.Split('/');

        for (var i = 1; i < parts.Length; i++)
            _dirs.Add(string.Join('/', parts[..i]));
    }

    /// <summary>Имя файла без каталога и без расширения — для сравнения имён между собой.</summary>
    private static string NameOf(string relative)
    {
        var name = relative.Contains('/') ? relative[(relative.LastIndexOf('/') + 1)..] : relative;

        return name.EndsWith(VfsPath.Extension, StringComparison.Ordinal)
            ? name[..^VfsPath.Extension.Length]
            : name;
    }

    private IReadOnlyList<string> NearestLocked(string relative, int count = 3) =>
        _docs.Keys
            .OrderBy(k => Tools.AiToolRegistry.Distance(k, relative))
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(count)
            .ToList();

    /// <summary>
    /// Файл, который есть то же имя с довеском, — в пределах ОДНОЙ папки.
    ///
    /// <para>
    /// Общее слово поводом не считается: «питание/апц» и «питание/смес» — разные предметы, и
    /// отказывать второму из-за первого значило бы не оставить агенту ни одного разрешённого
    /// имени. Ловится ровно то, что ловилось на прежнем развёртывании: <c>safe_mine_ore</c> рядом
    /// с <c>mine_ore</c>, то есть подмножество слов.
    /// </para>
    /// <para>
    /// Область поиска — соседи по папке, а не всё дерево. В плоской библиотеке это было одно и то
    /// же; в дереве «атмосфера/насосы» и «питание/насосы» — законные разные статьи.
    /// </para>
    /// </summary>
    private string? FindOverlapping(string relative)
    {
        var dir = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : string.Empty;
        var words = Words(NameOf(relative));

        if (words.Count == 0)
            return null;

        foreach (var existing in _docs.Keys)
        {
            var otherDir = existing.Contains('/') ? existing[..existing.LastIndexOf('/')] : string.Empty;

            if (!string.Equals(dir, otherDir, StringComparison.Ordinal))
                continue;

            var other = Words(NameOf(existing));

            if (other.Count == 0)
                continue;

            if (words.IsSubsetOf(other) || other.IsSubsetOf(words))
                return existing;
        }

        return null;

        static HashSet<string> Words(string name) =>
            name.Split('-', '_', ' ')
                .Where(w => w.Length > 2)
                .ToHashSet(StringComparer.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;

        var count = 0;
        var i = 0;

        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }
}
