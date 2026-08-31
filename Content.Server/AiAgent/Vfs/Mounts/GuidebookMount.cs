using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Guidebook;
using Robust.Shared.ContentPack;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// Внутренняя вика игры — та, которую игрок открывает клавишей.
///
/// <para>
/// Смонтирована ПРОТОТИПАМИ, а не каталогом, и это важнее, чем кажется. В
/// <c>Resources/Prototypes/Guidebook/*.yml</c> уже лежат идентификатор, локализованное имя, список
/// детей и путь к тексту. То есть дерево, имена и порядок берутся у самой игры: разделы совпадают
/// с тем, что видит игрок, статей-сирот не бывает по построению, и при обновлении апстрима вика
/// агента не расходится с настоящей. Обход каталога дал бы плоскую кучу файлов с машинными
/// именами и без иерархии.
/// </para>
/// <para>
/// Содержимое отдаётся сырой разметкой. Она читаемая — текст в ней преобладает над тегами, — а
/// конвертер стал бы ещё одним местом, которое молча теряет абзац при смене формата у апстрима.
/// Зачем это вообще нужно: агент говорит «проверьте насос», а игрок смотрит на панель, где
/// написано <c>Gas Volume Pump</c>. Английские имена машин есть только здесь.
/// </para>
/// </summary>
public sealed class GuidebookMount : VfsMount
{
    private readonly IPrototypeManager _proto;
    private readonly IResourceManager _res;
    private readonly ISawmill _sawmill;

    /// <summary>
    /// Путь внутри монтирования → статья. Строится ТОЛЬКО на главном потоке, в <see cref="Reload"/>.
    ///
    /// <para>
    /// Лениво строить его нельзя, и это выяснилось на живом сервере, а не в тесте. Инструменты
    /// файловой системы намеренно не маршалятся — они трогают файлы, а не сущности, — то есть
    /// первое обращение приходит с потока агента. А имя статьи разворачивается через
    /// <c>Loc.GetString</c>, который лезет в <c>IoCManager.Resolve</c>, а тот на чужом потоке
    /// бросает «IoC has no context on this thread». Падал весь вызов целиком: <c>ls /</c> и
    /// <c>grep</c> без пути обходят все монтирования, включая это.
    /// </para>
    /// </summary>
    private Dictionary<string, Article>? _index;

    /// <summary>Статья вики: прототип плюс УЖЕ развёрнутое имя.</summary>
    /// <remarks>
    /// Имя разворачивается при построении индекса, на главном потоке, и дальше только читается.
    /// Держать здесь один прототип значило бы звать локализацию из листинга, то есть с потока агента.
    /// </remarks>
    private sealed record Article(GuideEntryPrototype Entry, string Title);

    /// <summary>Путь → тело статьи. Наполняется лениво: полтора мегабайта разом никому не нужны.</summary>
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    private readonly object _sync = new();

    public GuidebookMount(IPrototypeManager proto, IResourceManager res, ISawmill sawmill)
    {
        _proto = proto;
        _res = res;
        _sawmill = sawmill;
    }

    // ------------------------------------------------------------------- индекс

    /// <summary>
    /// Готовый индекс. Если его ещё не построили — пустой, а не построенный на месте.
    /// </summary>
    /// <remarks>
    /// Пустой ответ означает «вика не смонтирована», и это честнее, чем построить индекс с потока
    /// агента и уронить весь вызов инструмента на локализации.
    /// </remarks>
    private Dictionary<string, Article> Index()
    {
        lock (_sync)
            return _index ?? Empty;
    }

    private static readonly Dictionary<string, Article> Empty = new(StringComparer.Ordinal);

    /// <summary>Построить индекс. ТОЛЬКО главный поток: разворачивает имена через локализацию.</summary>
    private void BuildIndex()
    {
        lock (_sync)
        {
            var index = new Dictionary<string, Article>(StringComparer.Ordinal);
            var all = _proto.EnumeratePrototypes<GuideEntryPrototype>().ToDictionary(p => p.ID, StringComparer.Ordinal);

            // Корни — те, кого никто не назвал ребёнком. Считается по факту, а не по флагу:
            // отдельного признака «верхнего уровня» у прототипа нет, а приписать его вручную
            // значило бы завести второй список, который разойдётся с первым.
            var children = all.Values.SelectMany(p => p.Children).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var root in all.Values
                         .Where(p => !children.Contains(p.ID))
                         .OrderByDescending(p => p.Priority)
                         .ThenBy(p => p.ID, StringComparer.Ordinal))
            {
                Walk(root, string.Empty, all, index, new HashSet<string>(StringComparer.Ordinal));
            }

            _index = index;
        }
    }

    private void Walk(
        GuideEntryPrototype entry,
        string prefix,
        IReadOnlyDictionary<string, GuideEntryPrototype> all,
        Dictionary<string, Article> index,
        HashSet<string> seen)
    {
        // Вика апстрима — граф, а не дерево: одну и ту же статью законно вешают в двух разделах.
        // Без этой проверки такой перекрёсток превратился бы в бесконечный обход.
        if (!seen.Add(entry.ID))
            return;

        // Идентификатор прототипа как есть: это настоящее имя статьи в вике, и подгонять его под
        // свои привычки значило бы расходиться с тем, что видит игрок.
        var path = prefix.Length == 0 ? entry.ID : prefix + "/" + entry.ID;

        index[path] = new Article(entry, Title(entry));

        foreach (var child in entry.Children)
        {
            if (all.TryGetValue(child.Id, out var proto))
                Walk(proto, path, all, index, seen);
        }

        seen.Remove(entry.ID);
    }

    /// <summary>Развернуть имя. Только с главного потока — зовётся из <see cref="BuildIndex"/>.</summary>
    private static string Title(GuideEntryPrototype entry)
    {
        // Имя — ключ локализации. Если перевода нет, Loc возвращает сам ключ, и показывать
        // «guide-entry-apc» бессмысленнее, чем идентификатор.
        var name = Loc.GetString(entry.Name);
        return name == entry.Name ? entry.ID : name;
    }

    // ------------------------------------------------------------------- чтение

    public override IReadOnlyList<VfsEntry> List(VfsPath relative, out string error)
    {
        error = string.Empty;

        var index = Index();
        var prefix = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments) + "/";

        if (!relative.IsRoot && !index.ContainsKey(string.Join('/', relative.Segments)))
        {
            error = $"нет раздела «/{Point}/{string.Join('/', relative.Segments)}»";
            return Array.Empty<VfsEntry>();
        }

        var result = new List<VfsEntry>();

        foreach (var (path, article) in index)
        {
            if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = prefix.Length == 0 ? path : path[prefix.Length..];

            if (rest.Length == 0 || rest.Contains('/'))
                continue;

            var kids = index.Keys.Count(k => k.StartsWith(path + "/", StringComparison.Ordinal));
            result.Add(new VfsEntry(rest, kids > 0, article.Title, kids, null));
        }

        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public override bool TryRead(VfsPath relative, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        var key = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments);
        var index = Index();

        if (key.Length == 0 || !index.TryGetValue(key, out var article))
        {
            error = $"нет статьи «/{Point}/{key}»";
            return false;
        }

        content = Body(key, article.Entry);

        if (content.Length == 0)
        {
            error = $"статья «/{Point}/{key}» не читается";
            return false;
        }

        content = $"# {article.Title}\n{content}";
        return true;
    }

    private string Body(string key, GuideEntryPrototype entry)
    {
        lock (_sync)
        {
            if (_text.TryGetValue(key, out var cached))
                return cached;
        }

        var text = string.Empty;

        try
        {
            using var reader = _res.ContentFileReadText(entry.Text);
            text = reader.ReadToEnd().Trim();
        }
        catch (Exception e)
        {
            _sawmill.Warning($"статья вики {entry.Text} не читается: {e.Message}");
        }

        lock (_sync)
            _text[key] = text;

        return text;
    }

    public override IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit)
    {
        var hits = new List<VfsHit>();
        var index = Index();
        var prefix = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments);

        foreach (var (key, article) in index.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (prefix.Length > 0
                && !key.StartsWith(prefix + "/", StringComparison.Ordinal)
                && key != prefix)
            {
                continue;
            }

            var line = 0;

            foreach (var text in Body(key, article.Entry).Split('\n'))
            {
                line++;

                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new VfsHit($"/{Point}/{key}", line, text.Trim()));

                if (hits.Count >= limit)
                    return hits;
            }
        }

        return hits;
    }

    /// <summary>Перестроить индекс и сбросить кэш тел. ТОЛЬКО главный поток.</summary>
    public override void Reload()
    {
        lock (_sync)
            _text.Clear();

        BuildIndex();
    }

    /// <summary>Сколько статей в вике. Для проверки на старте, что монтирование не пустое.</summary>
    public int Count => Index().Count;
}
