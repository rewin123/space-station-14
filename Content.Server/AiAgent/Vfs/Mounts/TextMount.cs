using System;
using System.Collections.Generic;
using System.IO;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// Один текстовый файл с диска, без разбора на «когда» и тело.
///
/// <para>
/// Здесь живёт промпт разбора — <c>CURATOR.md</c>. Он не статья справочника: у него нет строки
/// «когда», по которой его ищут, и открывать его по совпадению ситуации не нужно. Смонтирован он
/// только на чтение, и это осознанно: инструкция разбора, которую разбор может себе переписать,
/// перестаёт быть инструкцией.
/// </para>
/// </summary>
public sealed class TextMount : VfsMount
{
    /// <summary>Путь на диске. Может не существовать — тогда файл читается как отсутствующий.</summary>
    public required string File { get; init; }

    public override bool IsFile => true;

    private string _cache = string.Empty;
    private bool _loaded;

    public override IReadOnlyList<VfsEntry> List(VfsPath relative, out string error)
    {
        error = $"/{Point} — файл, а не папка: читай через cat";
        return Array.Empty<VfsEntry>();
    }

    public override bool TryRead(VfsPath relative, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        if (!relative.IsRoot)
        {
            error = $"/{Point} — файл, внутри него путей нет";
            return false;
        }

        content = Text();

        if (content.Length == 0)
        {
            error = $"файла /{Point} нет на диске";
            return false;
        }

        return true;
    }

    public override IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit)
    {
        var hits = new List<VfsHit>();
        var line = 0;

        foreach (var text in Text().Split('\n'))
        {
            line++;

            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                hits.Add(new VfsHit("/" + Point, line, text.Trim()));

            if (hits.Count >= limit)
                break;
        }

        return hits;
    }

    public override void Reload()
    {
        _loaded = false;
        _cache = string.Empty;
    }

    /// <summary>Содержимое файла, или пустая строка, если его нет. Читается один раз и кэшируется.</summary>
    public string Text()
    {
        if (_loaded)
            return _cache;

        try
        {
            _cache = System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File).Trim() : string.Empty;
        }
        catch (Exception)
        {
            // Молча пустой файл — законный ответ: у вызывающего есть запасной путь, а звать сюда
            // sawmill ради каждой попытки чтения незачем. О пропаже CURATOR.md кричит куратор.
            _cache = string.Empty;
        }

        _loaded = true;
        return _cache;
    }
}
