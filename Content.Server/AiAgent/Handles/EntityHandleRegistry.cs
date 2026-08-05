using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Handles;

/// <summary>
/// Stable, human-readable names for the entities the AI may refer to: <c>door-3</c>, <c>apc-1</c>,
/// <c>crew-7</c>.
///
/// The model never sees an <c>EntityUid</c>. Two independent reasons, and both matter:
///
/// 1. <b>Readability.</b> A raw uid means nothing to a language model; a name it can reason about
///    ("door-3, the one by medbay") is worth far more, and survives being quoted back at it.
/// 2. <b>Parity.</b> The uid behind a voice on the radio is information a human Station AI player
///    simply does not have. Handles are only ever minted for things the AI can actually perceive —
///    see <c>look</c>, local speech and the crew monitor. A radio line carries a voice name and
///    nothing else, so overhearing someone can never hand the model a key to their entity.
/// </summary>
public sealed class EntityHandleRegistry
{
    private readonly Dictionary<string, EntityUid> _byHandle = new();
    private readonly Dictionary<EntityUid, string> _byUid = new();
    private readonly Dictionary<string, int> _counters = new();

    public int Count => _byHandle.Count;

    /// <summary>Get the existing handle for an entity, or mint one.</summary>
    public string GetOrCreate(EntityUid uid, string kind)
    {
        if (_byUid.TryGetValue(uid, out var existing))
            return existing;

        var n = _counters.TryGetValue(kind, out var c) ? c + 1 : 1;
        _counters[kind] = n;

        var handle = $"{kind}-{n}";
        _byHandle[handle] = uid;
        _byUid[uid] = handle;
        return handle;
    }

    public bool TryResolve(string handle, out EntityUid uid) =>
        _byHandle.TryGetValue(handle.Trim(), out uid);

    public bool TryGetHandle(EntityUid uid, out string handle) =>
        _byUid.TryGetValue(uid, out handle!);

    /// <summary>
    /// The nearest known handles by edit distance, for a helpful <c>stale_handle</c> reply.
    /// Prefers handles of the same kind: a mistyped door is almost never an APC.
    ///
    /// Narrowed to the kind BEFORE sorting, not sorted by kind. A stale handle is the agent's most
    /// common failure, this runs on the main thread, and by late in a shift the registry holds every
    /// object the AI has ever looked at — so an edit-distance sort over the whole of it is a tick
    /// cost paid at exactly the wrong moment.
    /// </summary>
    public IReadOnlyList<string> Nearest(string handle, int count = 3)
    {
        var kind = KindOf(handle);
        var sameKind = _byHandle.Keys.Where(k => KindOf(k) == kind).ToList();
        var pool = sameKind.Count > 0 ? sameKind : _byHandle.Keys.ToList();

        return pool
            .OrderBy(k => Tools.AiToolRegistry.Distance(k, handle))
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Forget handles whose entity is gone.
    ///
    /// The registry only ever grew: every <c>look</c> mints a handle for everything on screen, and
    /// over a full shift that is tens of thousands of entries, most of them long deleted. Uids are
    /// never reused within a process, so dropping a dead one can never alias onto something new —
    /// the handle simply becomes <c>stale_handle</c>, which is what it already was in truth.
    /// </summary>
    public int Prune(Func<EntityUid, bool> alive)
    {
        var dead = _byUid.Keys.Where(uid => !alive(uid)).ToList();

        foreach (var uid in dead)
        {
            if (_byUid.Remove(uid, out var handle))
                _byHandle.Remove(handle);
        }

        return dead.Count;
    }

    public IEnumerable<string> HandlesOfKind(string kind) =>
        _byHandle.Keys.Where(k => KindOf(k) == kind).OrderBy(k => k, StringComparer.Ordinal);

    private static string KindOf(string handle)
    {
        var dash = handle.LastIndexOf('-');
        return dash <= 0 ? handle : handle[..dash];
    }

    public void Clear()
    {
        _byHandle.Clear();
        _byUid.Clear();
        _counters.Clear();
    }
}
