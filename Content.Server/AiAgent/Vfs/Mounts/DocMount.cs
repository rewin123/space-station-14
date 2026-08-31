using System.Collections.Generic;

namespace Content.Server.AiAgent.Vfs.Mounts;

/// <summary>
/// Монтирование поверх <see cref="DocTree"/> — дерева статей на диске.
///
/// <para>
/// Одним и тем же классом смонтированы и справочник, и личные записи агента. Разница между ними
/// не в устройстве, а ровно в одном поле: справочник объявлен <see cref="VfsAccess.Read"/>, и
/// поэтому все изменяющие методы уходят в отказ базового класса, не доходя до диска.
/// </para>
/// </summary>
public sealed class DocMount : VfsMount
{
    public required DocTree Tree { get; init; }

    public override IReadOnlyList<VfsEntry> List(VfsPath relative, out string error)
    {
        error = string.Empty;
        var rel = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments);

        if (Tree.HasDir(rel))
            return Tree.Children(rel);

        if (Tree.TryGet(rel, out _))
        {
            error = $"«{rel}» — файл, а не папка: читай через cat";
            return System.Array.Empty<VfsEntry>();
        }

        error = $"нет папки «/{Point}/{rel}»";
        return System.Array.Empty<VfsEntry>();
    }

    public override bool TryRead(VfsPath relative, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        var rel = relative.IsRoot ? string.Empty : string.Join('/', relative.Segments);

        if (Tree.TryGet(rel, out var doc))
        {
            content = DocTree.Render(doc);
            return true;
        }

        // cat по папке отдаёт её оглавление. Это не поблажка: у раздела есть собственный текст —
        // обзор и список статей, — и именно с него правильно начинать, а не с угадывания имени
        // файла внутри.
        if (Tree.HasDir(rel))
        {
            var indexKey = rel.Length == 0 ? VfsPath.IndexFile : rel + "/" + VfsPath.IndexFile;

            if (Tree.TryGet(indexKey, out var index))
            {
                content = DocTree.Render(index);
                return true;
            }

            error = $"«/{Point}/{rel}» — папка без оглавления: смотри ls";
            return false;
        }

        error = $"нет файла «/{Point}/{rel}»";
        return false;
    }

    public override IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit) =>
        Tree.Grep(needle, relative.IsRoot ? string.Empty : string.Join('/', relative.Segments), Point, limit);

    public override void Reload() => Tree.Reload();

    public override VfsWrite Write(VfsPath relative, string desc, string content) =>
        Writable
            ? Tree.Write(Rel(relative), relative.Name, desc, content)
            : Denied();

    public override VfsWrite Edit(VfsPath relative, string match, string replacement) =>
        Writable ? Tree.Edit(Rel(relative), match, replacement) : Denied();

    public override VfsWrite MakeDir(VfsPath relative) =>
        Writable ? Tree.MakeDir(Rel(relative)) : Denied();

    public override VfsWrite Remove(VfsPath relative) =>
        Writable ? Tree.Remove(Rel(relative)) : Denied();

    public override VfsWrite Move(VfsPath from, VfsPath to) =>
        Writable ? Tree.Move(Rel(from), Rel(to)) : Denied();

    private static string Rel(VfsPath path) =>
        path.IsRoot ? string.Empty : string.Join('/', path.Segments);
}
