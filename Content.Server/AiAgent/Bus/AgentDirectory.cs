using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// One agent, as the HTTP thread sees it.
/// </summary>
/// <remarks>
/// <para>
/// The handle carries <b>delegates</b>, not a reference to <see cref="AgentSession"/>, and that is
/// the same argument by which <c>AgentBody</c> carries delegates: the router must know nothing about
/// the session, and a test must not be able to construct one just to exercise the route. This is
/// also the one thing that physically stops the HTTP thread from reaching the <c>_sessions</c>
/// dictionary, which the main thread mutates.
/// </para>
/// <para>
/// <see cref="Alive"/> is the only mutable field, and that is not an oversight. The core's body
/// liveness is computed through <c>IsPlayable</c>, i.e. by touching <c>EntityManager</c>, and that
/// cannot be reached from a foreign thread. So the main thread samples the value once a second, and
/// the HTTP thread only reads it. Hence the limit too: <b>good for an indicator and nothing else</b>
/// — it lies for the first second after death.
/// </para>
/// </remarks>
public sealed class AgentHandle
{
    /// <summary>Body identifier: <c>core</c>, <c>borg-1</c>, <c>combat-2</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The agent's in-game name.</summary>
    public required string Name { get; init; }

    /// <summary>The brain entity — the same number that arrives in the <c>session.started</c> frame.</summary>
    public required int Brain { get; init; }

    /// <summary>The round the session started in.</summary>
    public required int Round { get; init; }

    /// <summary>Frame number at session start: lets the client tell a reclaim apart from the same agent.</summary>
    public required long StartedSeq { get; init; }

    /// <summary>The full session snapshot. Called from the HTTP thread and touches only its own locks.</summary>
    public required Func<AgentSessionDto> Capture { get; init; }

    /// <summary>Cheap roster row: no system prompt, no history.</summary>
    public required Func<AgentRosterEntryDto> Roster { get; init; }

    /// <summary>Put an operator's message in the agent's inbox. Any thread: the inbox has its own lock.</summary>
    public required Func<string, (bool Ok, string Reason)> Send { get; init; }

    /// <inheritdoc cref="AgentHandle"/>
    public volatile bool Alive;

    /// <summary>Roster row with current liveness.</summary>
    public AgentRosterEntryDto RosterEntry() => Roster() with { Alive = Alive };
}

/// <summary>
/// Roster of live agents: written by the main thread, read by the HTTP thread.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the single <c>volatile AgentSession?</c> that made the debugger show whoever last
/// claimed the body.
/// </para>
/// <para>
/// <b>A dictionary AND a published array, not just one of them.</b> The dictionary is needed for
/// <see cref="Find"/> — called on every snapshot request and every command — and so that
/// <see cref="Add"/> can DETECT a taken identifier instead of clobbering someone else's handle. The
/// array is needed for the roster: enumerating a <c>ConcurrentDictionary</c> is safe, but its order
/// is arbitrary, and the order of tabs in the UI must not jump around between requests.
/// </para>
/// </remarks>
public sealed class AgentDirectory
{
    private readonly ConcurrentDictionary<string, AgentHandle> _byId = new(StringComparer.Ordinal);

    /// <summary>The published ordered array. The HTTP thread NEVER walks the dictionary.</summary>
    private volatile AgentHandle[] _ordered = Array.Empty<AgentHandle>();

    /// <summary>All agents in display order. Any thread.</summary>
    public AgentHandle[] All => _ordered;

    /// <summary>How many agents are alive. Any thread.</summary>
    public int Count => _ordered.Length;

    /// <summary>The agent by identifier, or null. Any thread.</summary>
    public AgentHandle? Find(string? id) =>
        id != null && _byId.TryGetValue(id, out var handle) ? handle : null;

    /// <summary>The whole roster. Any thread.</summary>
    public List<AgentRosterEntryDto> Roster() => _ordered.Select(h => h.RosterEntry()).ToList();

    /// <summary>Add an agent. Main thread. False if the identifier is already taken.</summary>
    public bool Add(AgentHandle handle)
    {
        if (!_byId.TryAdd(handle.Id, handle))
            return false;

        Republish();
        return true;
    }

    /// <summary>
    /// Remove an agent. Main thread. Removes ONLY the handle that was passed in.
    /// </summary>
    /// <remarks>
    /// The reference comparison is mandatory: a borg can be reclaimed within the same tick, and an
    /// unconditional removal by identifier would knock the NEW agent's handle off the roster when
    /// the old one lets go. The same class of bug was already closed in <c>DetachDebugSession</c>
    /// with a <c>ReferenceEquals</c> check.
    /// </remarks>
    public bool Remove(string id, AgentHandle expected)
    {
        if (!_byId.TryGetValue(id, out var current) || !ReferenceEquals(current, expected))
            return false;

        if (!_byId.TryRemove(id, out _))
            return false;

        Republish();
        return true;
    }

    /// <summary>
    /// Keep only the listed ones. Main thread.
    /// </summary>
    /// <remarks>
    /// Insurance against a leak. Should a path ever appear that removes a session bypassing
    /// <c>Release</c>, the handle would go on living with a reference to a closed session — and a
    /// snapshot request would run into a cancelled token. Three lines close off a whole class of bug.
    /// </remarks>
    public void RetainOnly(IReadOnlyCollection<string> ids)
    {
        var live = new HashSet<string>(ids, StringComparer.Ordinal);
        var stale = _byId.Keys.Where(id => !live.Contains(id)).ToList();

        if (stale.Count == 0)
            return;

        foreach (var id in stale)
            _byId.TryRemove(id, out _);

        Republish();
    }

    /// <summary>
    /// Rebuild the published order: the core first, then alphabetically.
    /// </summary>
    /// <remarks>
    /// The order is decided here, not on the client, so the two sides cannot diverge. A purely
    /// alphabetical order would put <c>borg-1</c> ahead of <c>core</c>, and the default tab would
    /// jump around depending on who happens to exist in the round.
    /// </remarks>
    private void Republish() =>
        _ordered = _byId.Values
            .OrderByDescending(h => h.Id == StationAiAgentSystem.CoreAgentId)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .ToArray();
}
