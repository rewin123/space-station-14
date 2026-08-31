using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Content.Server.AiAgent.Vfs;

/// <summary>
/// Разбор и нормализация пути. Единственное место, где строка от модели становится путём.
///
/// <para>
/// У заметок о людях обход каталогов невыразим по построению: <c>PlayerNoteStore</c> собирает имя
/// файла из белого списка символов, и что бы ни прислала модель, точки и слэши до диска не
/// доезжают. В файловой системе так не выйдет — путь здесь и есть аргумент, — поэтому проверка
/// стоит отдельным типом, а не рассыпана по командам: пропущенная в одной команде проверка
/// выглядит точно так же, как работающая.
/// </para>
/// <para>
/// Путь здесь обычный, как в любой файловой системе: буква в букву, с расширением, с учётом
/// регистра. Ни приведения к нижнему регистру, ни замены пробелов — модель списывает имена из
/// <c>ls</c>, а не сочиняет их, и «умная» нормализация только разошлась бы с тем, что лежит на
/// диске. Единственная поблажка живёт не здесь, а в поиске: имя без <c>.md</c> ищется и с ним.
/// </para>
/// </summary>
public sealed class VfsPath
{
    /// <summary>Имя файла с метаданными папки: описание в строке «когда:», тело — обзор раздела.</summary>
    public const string IndexFile = "_index";

    /// <summary>Расширение на диске. В путях необязательно: «насосы» и «насосы.md» — одно и то же.</summary>
    public const string Extension = ".md";

    private readonly string[] _segments;

    private VfsPath(string[] segments)
    {
        _segments = segments;
    }

    public static VfsPath Root { get; } = new(System.Array.Empty<string>());

    public int Count => _segments.Length;
    public bool IsRoot => _segments.Length == 0;

    /// <summary>Первый сегмент — точка монтирования, или пустая строка для корня.</summary>
    public string Mount => _segments.Length == 0 ? string.Empty : _segments[0];

    public IReadOnlyList<string> Segments => _segments;

    /// <summary>Последний сегмент — имя файла или папки.</summary>
    public string Name => _segments.Length == 0 ? "/" : _segments[^1];

    /// <summary>Путь внутри монтирования, без первого сегмента.</summary>
    public string Relative => string.Join('/', _segments.Skip(1));

    public override string ToString() => "/" + string.Join('/', _segments);

    /// <summary>
    /// Разобрать путь. <paramref name="error"/> объясняет отказ словами, которые видит модель.
    ///
    /// Относительные пути не поддерживаются намеренно: текущего каталога у агента нет, а значит
    /// «насосы» без ведущего слэша — это не путь, а надежда. Отказать понятнее, чем угадать.
    /// </summary>
    public static bool TryParse(string? raw, out VfsPath path, out string error)
    {
        path = Root;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "пустой путь";
            return false;
        }

        var text = raw.Trim().Replace('\\', '/');

        if (text[0] != '/')
        {
            error = $"путь должен начинаться со слэша: «/{text.TrimStart('/')}», а не «{text}»";
            return false;
        }

        var parts = new List<string>();

        foreach (var rawSegment in text.Split('/'))
        {
            var segment = rawSegment.Trim();

            if (segment.Length == 0)
                continue;

            // Точка и две точки отсекаются здесь и только здесь. Ниже по течению путь уже собран
            // из проверенных сегментов, и склеить из них выход за корень нечем.
            if (segment == "." || segment == "..")
            {
                error = "«.» и «..» в путях не бывает: путь всегда полный, от корня";
                return false;
            }

            if (segment.Any(c => c is '\0' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            {
                error = $"недопустимые символы в «{segment}»";
                return false;
            }

            parts.Add(segment);
        }

        if (parts.Count == 0)
        {
            path = Root;
            return true;
        }

        path = new VfsPath(parts.ToArray());
        return true;
    }

    /// <summary>
    /// Имя с расширением: <c>«насосы»</c> → <c>«насосы.md»</c>, уже готовое остаётся как есть.
    ///
    /// <para>
    /// Единственная поблажка во всей адресации. Расширение — деталь хранения, а не часть имени
    /// статьи, и заставлять модель дописывать его к каждому пути значило бы ловить промахи там,
    /// где ошибки нет. Обратной операции (снимать расширение) нет намеренно: имя файла на диске и
    /// имя в пути должны совпадать, иначе <c>ls</c> показывает одно, а работает другое.
    /// </para>
    /// </summary>
    public static string WithExtension(string name) =>
        name.EndsWith(Extension, System.StringComparison.Ordinal) ? name : name + Extension;

    /// <summary>Путь без первого сегмента — то, что видит само монтирование.</summary>
    public VfsPath WithoutMount() =>
        _segments.Length <= 1 ? Root : new VfsPath(_segments[1..]);

    public VfsPath Child(string name) =>
        new(_segments.Append(name.Trim()).ToArray());

    public VfsPath Parent() =>
        _segments.Length == 0 ? Root : new VfsPath(_segments[..^1]);
}
