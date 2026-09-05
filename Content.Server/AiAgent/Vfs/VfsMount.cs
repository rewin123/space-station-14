using System;
using System.Collections.Generic;

namespace Content.Server.AiAgent.Vfs;

/// <summary>What a mount allows doing. A property of the mount, not of the file.</summary>
/// <remarks>
/// Permissions that whoever walks them can rewrite aren't permissions. That's why a file has no
/// access field at all: the reference library is read-only because it's mounted that way, and the
/// agent can't change that with any call.
/// </remarks>
public enum VfsAccess
{
    Read,
    Write,
}

/// <summary>One listing line: a file or a folder, with a description.</summary>
/// <param name="Name">Name without the extension — what gets substituted into the path.</param>
/// <param name="IsDir">Whether it's a folder. Folders take their description from their <c>_index</c>.</param>
/// <param name="Desc">The "when:" line — this is what the model uses to decide whether to open it.</param>
/// <param name="Size">Body size in characters. For folders, the number of children.</param>
/// <param name="Modified">Time of the last edit, or <c>null</c> if the mount doesn't know it.</param>
public sealed record VfsEntry(string Name, bool IsDir, string Desc, int Size, DateTime? Modified);

/// <summary>Outcome of a mutating operation. A rejection is explained in words the model will see.</summary>
public sealed record VfsWrite(bool Ok, string Message, IReadOnlyList<string>? Hints = null)
{
    public static VfsWrite Fine(string message) => new(true, message);
    public static VfsWrite No(string message, IReadOnlyList<string>? hints = null) => new(false, message, hints);
}

/// <summary>One <c>grep</c> match: the full path, the line number, the line itself.</summary>
public sealed record VfsHit(string Path, int Line, string Text);

/// <summary>
/// One mount.
///
/// <para>
/// Writing is declared here with a default rejection, rather than in a separate interface. The
/// reason is practical: there are more read-only mounts than writable ones, and forcing the
/// reference library and the game wiki to implement six methods for six identical rejections is
/// six places where the wrong rejection will eventually end up. Permission checking is done by
/// <see cref="Vfs"/> before the call; these stubs are a second line of defense in case a mount is
/// reached directly from code.
/// </para>
/// </summary>
public abstract class VfsMount
{
    /// <summary>Mount point without a leading slash: "wiki_ru", "skills", "memory.md".</summary>
    public required string Point { get; init; }

    /// <summary>Line for the root listing in zone 0. Constant: doesn't depend on content.</summary>
    public required string Description { get; set; }

    public required VfsAccess Access { get; init; }

    /// <summary>The mount is a single file, not a tree. Then <c>ls</c> won't expand it.</summary>
    public virtual bool IsFile => false;

    /// <summary>
    /// The instance is shared across all agents, and it shouldn't be reloaded by every one of them.
    ///
    /// <para>
    /// Set only via <c>VfsBuilder.AddShared</c>. It's the INSTANCE that's shared, not the directory:
    /// the reference library weighs a megabyte and a half, and keeping a copy per body would mean
    /// four times the memory and four times the work on every prefix rebuild.
    /// </para>
    /// </summary>
    public bool Shared { get; init; }

    public bool Writable => Access == VfsAccess.Write;

    // ------------------------------------------------------------------- reading

    /// <summary>Contents of a folder. An empty path is the mount's root.</summary>
    public abstract IReadOnlyList<VfsEntry> List(VfsPath relative, out string error);

    /// <summary>A file's body. For a folder, the body of its <c>_index</c>, if it has one.</summary>
    public abstract bool TryRead(VfsPath relative, out string content, out string error);

    /// <summary>Word search. The implementation must respect <paramref name="limit"/>.</summary>
    public abstract IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit);

    /// <summary>Reload from disk. Called during the prefix rebuild step.</summary>
    public virtual void Reload() { }

    // -------------------------------------------------------------------- writing

    public virtual VfsWrite Write(VfsPath relative, string desc, string content) => Denied();
    public virtual VfsWrite Edit(VfsPath relative, string match, string replacement) => Denied();
    public virtual VfsWrite MakeDir(VfsPath relative) => Denied();
    public virtual VfsWrite Remove(VfsPath relative) => Denied();
    public virtual VfsWrite Move(VfsPath from, VfsPath to) => Denied();

    protected VfsWrite Denied() =>
        VfsWrite.No($"/{Point} — только для чтения, менять его нельзя");
}
