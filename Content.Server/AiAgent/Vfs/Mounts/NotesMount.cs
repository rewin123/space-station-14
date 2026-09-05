using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// Notes about people as a folder. An untouched <see cref="PlayerNoteStore"/> sits underneath.
///
/// <para>
/// An adapter, not a replacement. Things that aren't visible from the filesystem and mustn't be
/// lost live in the store: the <c>[round N · date]</c> stamp, set by the server, not the model; a
/// slug from a character whitelist instead of a path; a limit per note rather than on the whole
/// store; fragment-based editing. All of that stays exactly where it was.
/// </para>
/// <para>
/// One file per person, so there's no nesting here: <c>ls</c> returns a flat list, and <c>grep</c>
/// searches entry bodies. The file name is a slug, but the description carries the real name,
/// because there's no reason to show the model "ivan-petrov" instead of "Ivan Petrov".
/// </para>
/// </summary>
public sealed class NotesMount : VfsMount
{
    public required PlayerNoteStore Store { get; init; }

    /// <summary>Round stamp. Set by the server: the date and shift number aren't something the model should remember.</summary>
    public required Func<string> Stamp { get; init; }

    public override IReadOnlyList<VfsEntry> List(VfsPath relative, out string error)
    {
        error = string.Empty;

        if (!relative.IsRoot)
        {
            error = $"в /{Point} нет подпапок — здесь по файлу на человека";
            return Array.Empty<VfsEntry>();
        }

        return Store.All
            .Select(n => new VfsEntry(
                n.Slug,
                false,
                $"{n.Name} — записей {n.Entries.Count}",
                n.Entries.Sum(e => e.Length),
                null))
            .ToList();
    }

    public override bool TryRead(VfsPath relative, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        if (relative.Count != 1)
        {
            error = $"нужен путь вида /{Point}/иван-петров";
            return false;
        }

        var result = Store.Read(relative.Name);

        if (!result.Ok)
        {
            var near = Store.Search(relative.Name).Select(r => r.Name).Take(3).ToList();

            error = near.Count > 0
                ? $"{result.Message}; похожие: {string.Join(", ", near)}"
                : $"{result.Message}, и похожих имён тоже нет — это новый для тебя человек";

            return false;
        }

        content = $"# {result.Message}\n" + string.Join("\n§\n", result.Entries!);
        return true;
    }

    public override IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit)
    {
        var hits = new List<VfsHit>();

        foreach (var note in Store.All)
        {
            if (relative.Count == 1 && !string.Equals(note.Slug, relative.Name, StringComparison.Ordinal))
                continue;

            var line = 0;

            foreach (var entry in note.Entries)
            {
                line++;

                if (entry.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || note.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new VfsHit($"/{Point}/{note.Slug}", line, entry.Trim()));
                }

                if (hits.Count >= limit)
                    return hits;
            }
        }

        return hits;
    }

    public override void Reload() => Store.LoadFromDisk();

    /// <summary>
    /// Writing a new note. The stamp is applied here, "content" becomes a single entry.
    /// </summary>
    /// <remarks>
    /// A note can't be rewritten wholesale — and that's not an oversight of the adapter, it's the
    /// store's rule. Notes are dated by round, and a "rewrite" would erase someone else's shift in
    /// one move.
    /// </remarks>
    public override VfsWrite Write(VfsPath relative, string desc, string content)
    {
        if (!Writable)
            return Denied();

        if (relative.Count != 1)
            return VfsWrite.No($"нужен путь вида /{Point}/иван-петров");

        var text = string.IsNullOrWhiteSpace(content) ? desc : content;
        var result = Store.Add(relative.Name, text, Stamp());

        return result.Ok
            ? VfsWrite.Fine($"{result.Message} ({result.Usage})")
            : VfsWrite.No(result.Message, result.Entries);
    }

    public override VfsWrite Edit(VfsPath relative, string match, string replacement)
    {
        if (!Writable)
            return Denied();

        if (relative.Count != 1)
            return VfsWrite.No($"нужен путь вида /{Point}/иван-петров");

        var hasMatch = !string.IsNullOrWhiteSpace(match);
        var hasNew = !string.IsNullOrWhiteSpace(replacement);

        // The same contract as the rest of writing: empty match means append, empty replacement
        // means remove. Both empty means "nothing was said", not "erase everything".
        var result = (hasMatch, hasNew) switch
        {
            (false, true) => Store.Add(relative.Name, replacement, Stamp()),
            (true, true) => Store.Replace(relative.Name, match, replacement),
            (true, false) => Store.Remove(relative.Name, match),
            _ => new NoteResult(false, "нечего записывать: заполни replacement, чтобы дописать, или match, чтобы поправить"),
        };

        return result.Ok
            ? VfsWrite.Fine($"{result.Message} ({result.Usage})")
            : VfsWrite.No(result.Message, result.Entries);
    }

    public override VfsWrite MakeDir(VfsPath relative) =>
        VfsWrite.No($"в /{Point} подпапок не бывает — здесь по файлу на человека");

    public override VfsWrite Remove(VfsPath relative)
    {
        if (!Writable)
            return Denied();

        // We don't tear down a note wholesale: its entries carry stamps from different shifts, and
        // deleting the file means losing someone else's round. An outdated entry is thrown out as
        // a fragment, via edit_file.
        return VfsWrite.No(
            "заметку целиком удалить нельзя: в ней записи разных смен. Выбрось устаревшую запись " +
            "фрагментом — edit_file с пустым replacement");
    }

    public override VfsWrite Move(VfsPath from, VfsPath to) =>
        VfsWrite.No($"переименование в /{Point} не поддерживается: имя файла — это имя человека");
}
