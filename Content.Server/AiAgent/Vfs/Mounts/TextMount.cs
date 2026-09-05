using System;
using System.Collections.Generic;
using System.IO;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// A single text file from disk, with no split into "when" and body.
///
/// <para>
/// This is where the review prompt lives — <c>CURATOR.md</c>. It isn't a reference-library article:
/// it has no "when" line to be found by, and there's no need to open it on a situational match. It
/// is mounted read-only, deliberately: a review instruction that the review can rewrite for itself
/// stops being an instruction.
/// </para>
/// </summary>
public sealed class TextMount : VfsMount
{
    /// <summary>Path on disk. May not exist — then the file reads as missing.</summary>
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

    /// <summary>The file's content, or an empty string if it doesn't exist. Read once and cached.</summary>
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
            // A silently empty file is a legitimate answer: the caller has a fallback path, and
            // there's no reason to call the sawmill for every read attempt. The curator itself
            // shouts about a missing CURATOR.md.
            _cache = string.Empty;
        }

        _loaded = true;
        return _cache;
    }
}
