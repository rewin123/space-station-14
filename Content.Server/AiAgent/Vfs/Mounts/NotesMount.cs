using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// Заметки о людях как папка. Под ней — нетронутый <see cref="PlayerNoteStore"/>.
///
/// <para>
/// Переходник, а не замена. В сторе живут вещи, которые из файловой системы не видны и которые
/// потерять нельзя: штамп <c>[раунд N · дата]</c>, который ставит сервер, а не модель; слаг из
/// белого списка символов вместо пути; лимит на одну заметку, а не на всё хранилище; правка
/// фрагментом. Всё это остаётся ровно там, где было.
/// </para>
/// <para>
/// Один файл на человека, поэтому вложенности здесь нет: <c>ls</c> отдаёт плоский список, а
/// <c>grep</c> ищет по телу записей. Имя файла — слаг, но в описании стоит настоящее имя, потому
/// что показывать модели «иван-петров» вместо «Иван Петров» незачем.
/// </para>
/// </summary>
public sealed class NotesMount : VfsMount
{
    public required PlayerNoteStore Store { get; init; }

    /// <summary>Штамп раунда. Ставит сервер: дата и номер смены — не то, что модель должна помнить.</summary>
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
    /// Запись новой заметки. Штамп проставляется здесь, «содержимое» становится одной записью.
    /// </summary>
    /// <remarks>
    /// Целиком переписать заметку нельзя — и это не упущение переходника, а правило стора.
    /// Заметки датированы по раундам, и «перезапись» затёрла бы чужую смену одним движением.
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

        // Тот же контракт, что у остальной записи: пусто в match — дописать, пусто в replacement —
        // удалить. Пустое и то и другое означает «ничего не сказано», а не «сотри всё».
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

        // Заметку целиком не сносим: у записей стоят штампы разных смен, и удаление файла — это
        // потеря чужого раунда. Устаревшую запись выбрасывают фрагментом, через edit_file.
        return VfsWrite.No(
            "заметку целиком удалить нельзя: в ней записи разных смен. Выбрось устаревшую запись " +
            "фрагментом — edit_file с пустым replacement");
    }

    public override VfsWrite Move(VfsPath from, VfsPath to) =>
        VfsWrite.No($"переименование в /{Point} не поддерживается: имя файла — это имя человека");
}
