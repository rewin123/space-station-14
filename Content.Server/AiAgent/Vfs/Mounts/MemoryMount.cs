using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// The agent's long-term memory as a single file. An untouched <see cref="MemoryStore"/> sits underneath.
///
/// <para>
/// The filesystem is a second way to reach memory here, not a replacement for the first. The main
/// one is the old one: <c>MEMORY.md</c> sits whole in the system prompt and loads at session start.
/// A frozen snapshot is kept: a write goes to disk immediately, while zone 0 holds the old text
/// until the next prefix rebuild, and that's exactly what keeps the KV cache alive through the
/// whole compaction cycle.
/// </para>
/// <para>
/// Hence an oddity worth knowing: right after a write, <c>cat /memory.md</c> shows the new entry,
/// while the MEMORY block earlier in the prompt still shows the old text. This isn't a desync, it's
/// the design; it converges on the next prefix rebuild.
/// </para>
/// </summary>
public sealed class MemoryMount : VfsMount
{
    public required MemoryStore Store { get; init; }

    public override bool IsFile => true;

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

        var entries = Store.Entries();
        content = string.Join(MemoryStore.Delimiter, entries);

        if (content.Length == 0)
            content = "(память пуста)";

        return true;
    }

    public override IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit)
    {
        var hits = new List<VfsHit>();
        var line = 0;

        foreach (var entry in Store.Entries())
        {
            line++;

            if (entry.Contains(needle, StringComparison.OrdinalIgnoreCase))
                hits.Add(new VfsHit("/" + Point, line, entry.Trim()));

            if (hits.Count >= limit)
                break;
        }

        return hits;
    }

    public override void Reload() => Store.LoadFromDisk();

    /// <summary>
    /// Append an entry. There is no full-file rewrite, and that's not an oversight of the adapter.
    /// </summary>
    /// <remarks>
    /// The store's rule: whoever is allowed to rewrite memory wholesale will eventually return a
    /// shortened version, and everything accumulated disappears in one silent move. So
    /// <c>write_file</c> here means "add an entry", not "replace the file".
    /// </remarks>
    public override VfsWrite Write(VfsPath relative, string desc, string content)
    {
        if (!Writable)
            return Denied();

        var text = string.IsNullOrWhiteSpace(content) ? desc : content;
        return Report(Store.Add(text));
    }

    public override VfsWrite Edit(VfsPath relative, string match, string replacement)
    {
        if (!Writable)
            return Denied();

        var hasMatch = !string.IsNullOrWhiteSpace(match);
        var hasNew = !string.IsNullOrWhiteSpace(replacement);

        var result = (hasMatch, hasNew) switch
        {
            (false, true) => Store.Add(replacement),
            (true, true) => Store.Replace(match, replacement),
            (true, false) => Store.Remove(match),
            _ => new MemoryResult(false, "нечего записывать: заполни replacement, чтобы дописать, или match, чтобы поправить"),
        };

        return Report(result);
    }

    public override VfsWrite Remove(VfsPath relative) =>
        VfsWrite.No($"/{Point} целиком не удаляется — выбрось запись фрагментом через edit_file");

    private static VfsWrite Report(MemoryResult result) =>
        result.Ok
            ? VfsWrite.Fine($"{result.Message} ({result.Usage})")
            : VfsWrite.No(result.Message, result.Entries);
}
