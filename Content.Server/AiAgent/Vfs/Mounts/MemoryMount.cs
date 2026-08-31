using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// Долгая память агента как один файл. Под ним — нетронутый <see cref="MemoryStore"/>.
///
/// <para>
/// Файловая система здесь второй способ добраться до памяти, а не замена первому. Главный —
/// прежний: <c>MEMORY.md</c> целиком стоит в системном промпте и грузится при старте сессии.
/// Замороженный снимок сохраняется: запись уходит на диск немедленно, а зона 0 держит старый
/// текст до следующей перестройки префикса, и именно это удерживает KV-кэш живым весь цикл
/// компакции.
/// </para>
/// <para>
/// Отсюда странность, которую надо знать: сразу после записи <c>cat /memory.md</c> покажет новую
/// запись, а блок ПАМЯТЬ выше по промпту — ещё старый текст. Это не рассинхрон, а устройство; оно
/// сходится на ближайшей перестройке префикса.
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
    /// Дописать запись. Перезаписи файла целиком нет, и это не упущение переходника.
    /// </summary>
    /// <remarks>
    /// Правило стора: тот, кому позволено переписать память целиком, однажды вернёт укороченную
    /// версию, и накопленное исчезнет за один ход, молча. Поэтому <c>write_file</c> здесь значит
    /// «добавить запись», а не «заменить файл».
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
