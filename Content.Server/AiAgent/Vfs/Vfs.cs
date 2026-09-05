using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Locale;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Vfs.Mounts;

namespace Content.Server.AiAgent.Vfs;

/// <summary>
/// One agent's filesystem: the mount table and routing over it.
///
/// <para>
/// Each body has its own, and that's the main difference from the previous design. Previously
/// memory, skills, and notes about people existed as a single instance per process, so a combat
/// cyborg hauled twenty kilobytes of the Station AI's library in its own prefix — including crew
/// dossiers it has no use for and shouldn't know about. Now only the reference library is shared,
/// and it's shared as one instance, not a copy per agent.
/// </para>
/// </summary>
public sealed class Vfs
{
    /// <summary>
    /// Declaration order from the builder, not dictionary order.
    ///
    /// <para>
    /// <see cref="Dictionary{TKey,TValue}"/> makes no guarantee about enumeration order, neither in
    /// the documentation nor across runtime versions. Zone 0 hinges on this here: reordered root
    /// lines mean a different prefix SHA, i.e. a full prefill every turn, with not a single error in
    /// the log. The same trick is already applied to the tool array, which is sorted by name for
    /// exactly this reason.
    /// </para>
    /// </summary>
    private readonly List<VfsMount> _ordered;

    private readonly Dictionary<string, VfsMount> _byPoint;
    private readonly string _root;

    /// <summary>
    /// Content troubles noticed during assembly: an empty reference library, an unreadable directory.
    ///
    /// A non-empty list means the agent works but doesn't know something. Contradictions that fail
    /// the table don't end up here — <see cref="VfsBuilder.Build"/> throws an exception for those.
    /// </summary>
    public IReadOnlyList<string> Complaints { get; }

    internal Vfs(
        IReadOnlyList<VfsMount> mounts,
        IReadOnlyList<string>? complaints = null,
        AgentLang lang = AgentLang.Ru)
    {
        Complaints = complaints ?? System.Array.Empty<string>();

        _ordered = mounts.ToList();

        _byPoint = mounts.ToDictionary(m => m.Point, StringComparer.Ordinal);
        _root = RenderRootText(_ordered, AgentLocale.Of(lang));

        foreach (var mount in _ordered)
        {
            switch (mount)
            {
                case MemoryMount memory:
                    Memory = memory.Store;
                    break;
                case NotesMount notes:
                    Notes = notes.Store;
                    break;
                case TextMount text:
                    Curator = text;
                    break;
                case DocMount { Shared: false } doc:
                    Skills = doc.Tree;
                    break;
            }
        }
    }

    public IReadOnlyList<VfsMount> Mounts => _ordered;

    /// <summary>
    /// This agent's long-term memory, or <c>null</c> if it isn't mounted.
    ///
    /// <para>
    /// A direct reference, not a lookup by mount point: the system prompt assembly needs a memory
    /// snapshot on every prefix rebuild, and looking it up there by string would tie zone 0 to a
    /// folder name.
    /// </para>
    /// </summary>
    public MemoryStore? Memory { get; private set; }

    /// <summary>This agent's notes about people. The NOTE line is fed from these as well.</summary>
    public PlayerNoteStore? Notes { get; private set; }

    /// <summary>The agent's own notes — what the curator writes to. Needed by the debugger and the console.</summary>
    public DocTree? Skills { get; private set; }

    /// <summary>The segment-review prompt, if it's mounted.</summary>
    public TextMount? Curator { get; private set; }

    /// <summary>
    /// How many times something was written to this tree over the session's lifetime.
    ///
    /// <para>
    /// This exists for the curator's sake. It used to count writes by the NAMES of wire calls
    /// (<c>write_file</c>, <c>edit_file</c>), but in script mode those names aren't on the wire at
    /// all — they're Lua functions — so the counter always stayed at zero and the review report
    /// never went into the dialogue. The counter here sits BELOW both paths: both the wire and Lua
    /// call the same tool handler.
    /// </para>
    /// </summary>
    public int Writes => _writes;

    private int _writes;

    /// <summary>
    /// Mark a successful write.
    ///
    /// <para>
    /// Called FROM the <c>write_file</c> and <c>edit_file</c> tool HANDLERS, not from the mounts
    /// themselves: <c>VfsMount.Write</c> is virtual and overridden by each kind, and the counter
    /// would have to be duplicated in the base class across every descendant. The handler is the
    /// one place both paths, wire and Lua, pass through.
    /// </para>
    /// <para>
    /// Hence an obligation for test benches: a stand-in <c>write_file</c> implementation that
    /// writes straight to the mount won't increment the counter, and the segment review will report
    /// zero writes despite a file having been written. A bench that swaps out the handler must call
    /// this method itself.
    /// </para>
    /// </summary>
    public void NoteWrite() => System.Threading.Interlocked.Increment(ref _writes);

    /// <summary>
    /// Start reporting edits to the debug bus.
    ///
    /// Shared mounts are skipped: the reference library is one per process, and attaching it to one
    /// agent's sink would mean attributing its edits to someone else's session.
    /// </summary>
    public void AttachSink(IAgentEventSink sink)
    {
        foreach (var mount in _ordered)
        {
            if (mount.Shared)
                continue;

            switch (mount)
            {
                case DocMount doc:
                    doc.Tree.AttachSink(sink);
                    break;
                case NotesMount notes:
                    notes.Store.AttachSink(sink);
                    break;
                case MemoryMount memory:
                    memory.Store.AttachSink(sink);
                    break;
            }
        }
    }

    /// <summary>
    /// The block for zone 0: how to navigate and what lives where.
    ///
    /// <para>
    /// Built once in the constructor and does NOT depend on the tree's contents — no counters, no
    /// "229 articles". The old index changed with every write and dragged a prefix rebuild along
    /// with it; this block stays constant as long as the mount table stays constant. Zone 0 goes
    /// from growing to fixed, and that — not saving characters — is the main win.
    /// </para>
    /// </summary>
    public string RenderRoot() => _root;

    private static string RenderRootText(IReadOnlyList<VfsMount> mounts, AgentLocale loc)
    {
        var sb = new StringBuilder();
        sb.Append(loc.VfsHeading).Append('\n');

        var width = mounts.Count == 0 ? 0 : mounts.Max(m => m.Point.Length);

        foreach (var mount in mounts)
        {
            var point = ("/" + mount.Point).PadRight(width + 2);
            var access = mount.Writable ? "rw-" : "r--";
            sb.Append($"  {point} {access}  {mount.Description}\n");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------ routing

    /// <summary>
    /// Find a mount by path. A rejection names the existing mount points: the model shouldn't have to guess.
    /// </summary>
    public bool TryResolve(VfsPath path, out VfsMount mount, out VfsPath relative, out string error)
    {
        mount = null!;
        relative = VfsPath.Root;
        error = string.Empty;

        if (path.IsRoot)
        {
            error = "это корень, у него нет содержимого кроме папок ниже";
            return false;
        }

        // The exact mount point, or with ".md" appended if there isn't one. The same concession as
        // for files: "/memory.md" sits at the root, and there's no need to require the extension
        // in every path.
        if (!_byPoint.TryGetValue(path.Mount, out mount!)
            && !_byPoint.TryGetValue(VfsPath.WithExtension(path.Mount), out mount!))
        {
            error = $"нет такой папки в корне: «/{path.Mount}»";
            return false;
        }

        relative = path.WithoutMount();
        return true;
    }

    public IReadOnlyList<string> MountPoints() =>
        _ordered.Select(m => "/" + m.Point).ToList();

    /// <summary>Root listing — the same tree as in zone 0, but as a tool response.</summary>
    public IReadOnlyList<VfsEntry> RootEntries() =>
        _ordered
            .Select(m => new VfsEntry(m.Point, !m.IsFile, m.Description, 0, null))
            .ToList();

    /// <summary>
    /// Reload its own content from disk. Called during the prefix rebuild step.
    ///
    /// <para>
    /// Shared mounts are skipped deliberately: the reference library is one per process, and
    /// reloading it once per each of the four agents would be four traversals of a megabyte and a
    /// half instead of one, and that's inside the compaction ritual, which already pays for prefill.
    /// The system updates it once, itself, via the instance's own <see cref="VfsMount.Reload"/>.
    /// </para>
    /// </summary>
    public void Reload()
    {
        foreach (var mount in _ordered)
        {
            if (mount.Shared)
                continue;

            mount.Reload();
        }
    }
}
