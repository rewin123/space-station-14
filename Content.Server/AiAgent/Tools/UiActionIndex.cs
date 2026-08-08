using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Tools;

/// <summary>
/// Which <see cref="BoundUserInterfaceMessage"/> types a given entity actually handles.
///
/// <b>Why this is reflection.</b> There is no registry mapping a console to the messages it
/// accepts — <c>InterfaceData</c> holds a client class name and two numbers, and nothing else.
/// The only authoritative answer lives in the event bus, which records, per entity, every event
/// type its components subscribe to. That table is <c>internal</c> and there is no
/// <c>HasSubscription</c> on <see cref="IEventBus"/>, so the one way to read it from content is to
/// reflect. The engine reads it the same way for its own <c>dump_event_tables</c> command, which
/// is where the field names below come from.
///
/// <b>Why guessing was rejected.</b> The alternative — matching message types to consoles by
/// namespace or by name — is a heuristic that silently offers the model an action the console will
/// ignore. A refusal it can see beats a button that does nothing.
///
/// <b>When upstream renames the field.</b> Everything degrades to an empty list: the tool reports
/// that it cannot enumerate this console's actions and the agent moves on. It never throws into a
/// turn. <c>Available</c> says whether the reflection still binds, and a bench test asserts it, so
/// an engine bump fails in CI rather than quietly emptying every console in the game.
/// </summary>
public sealed class UiActionIndex
{
    private readonly IEntityManager _entMan;
    private readonly ISawmill _sawmill;

    private readonly FieldInfo? _tablesField;
    private readonly FieldInfo? _indicesField;
    private readonly FieldInfo? _interfacesField;

    /// <summary>Reported once, not per call: a broken bind would otherwise flood the log.</summary>
    private bool _complained;

    public UiActionIndex(IEntityManager entMan, ISawmill sawmill)
    {
        _entMan = entMan;
        _sawmill = sawmill;

        // Bound once at construction rather than per lookup, and deliberately not cached across
        // instances: a benchmark builds several worlds in one process.
        _tablesField = entMan.EventBus.GetType()
            .GetField("_entEventTables", BindingFlags.NonPublic | BindingFlags.Instance);

        var tableType = _tablesField?.FieldType.GetGenericArguments().ElementAtOrDefault(1);

        _indicesField = tableType?
            .GetField("EventIndices", BindingFlags.Public | BindingFlags.Instance);

        // The interface list is internal for the same reason the event table is: content is not
        // expected to ask. We have to, because a BUI message only reaches its handler when the key
        // stamped on it matches the one the handler subscribed with — Subs.BuiEvents filters on it.
        // Hardcoding a key is why the current device_ui can only talk to the comms console.
        _interfacesField = typeof(UserInterfaceComponent)
            .GetField("Interfaces", BindingFlags.NonPublic | BindingFlags.Instance);
    }

    /// <summary>Whether the event table is reachable. False means every lookup returns nothing.</summary>
    public bool Available => _tablesField != null && _indicesField != null;

    /// <summary>Whether UI keys can be enumerated. False means actions cannot be dispatched at all.</summary>
    public bool KeysAvailable => _interfacesField != null;

    /// <summary>
    /// The UI keys this entity exposes, in a stable order.
    ///
    /// Read from the component rather than from <c>States</c>, because a console that has never
    /// pushed a state still has an interface — and those are exactly the consoles whose state only
    /// materialises once somebody opens them.
    /// </summary>
    public IReadOnlyList<Enum> KeysFor(EntityUid uid)
    {
        if (_interfacesField == null || !_entMan.TryGetComponent<UserInterfaceComponent>(uid, out var comp))
            return Array.Empty<Enum>();

        try
        {
            if (_interfacesField.GetValue(comp) is not IDictionary interfaces)
                return Array.Empty<Enum>();

            var keys = new List<Enum>();

            foreach (var key in interfaces.Keys)
            {
                if (key is Enum e && !PhysicalKeys.Contains(e.GetType().Name))
                    keys.Add(e);
            }

            keys.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
            return keys;
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не прочитать список UI у {uid}: {e.GetType().Name}: {e.Message}");
            return Array.Empty<Enum>();
        }
    }

    /// <summary>
    /// Every BUI message type this entity has a handler for, sorted so the list is stable between
    /// calls — an order that shuffles reads to the model as the console having changed.
    ///
    /// The result is entity-wide, not per UI key: the key filter lives inside the closure that
    /// <c>Subs.BuiEvents</c> registers and cannot be seen from outside. For a console with one
    /// interface — which is nearly all of them — that distinction does not arise; for the rest the
    /// list is a superset, and the message simply does nothing if aimed at the wrong key.
    /// </summary>
    public IReadOnlyList<Type> MessagesFor(EntityUid uid)
    {
        if (!Available)
        {
            if (!_complained)
            {
                _complained = true;
                _sawmill.Error(
                    "таблица подписок движка не читается — device_ui не сможет перечислять действия консолей. " +
                    "Скорее всего апстрим переименовал EntityEventBus._entEventTables или EventTable.EventIndices.");
            }

            return Array.Empty<Type>();
        }

        try
        {
            if (_tablesField!.GetValue(_entMan.EventBus) is not IDictionary tables)
                return Array.Empty<Type>();

            if (!tables.Contains(uid))
                return Array.Empty<Type>();

            if (_indicesField!.GetValue(tables[uid]) is not IDictionary indices)
                return Array.Empty<Type>();

            var found = new List<Type>();

            foreach (var key in indices.Keys)
            {
                if (key is Type t && !t.IsAbstract &&
                    typeof(BoundUserInterfaceMessage).IsAssignableFrom(t) && IsOffered(t))
                    found.Add(t);
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return found;
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не прочитать подписки {uid}: {e.GetType().Name}: {e.Message}");
            return Array.Empty<Type>();
        }
    }

    /// <summary>
    /// Physical interactions that happen to arrive as BUI messages, and so would otherwise be
    /// offered as if they were console buttons.
    ///
    /// The wire panel is the case that matters. Its messages reach the same entity as the console's
    /// do, and the subscription's UI-key filter is invisible from out here, so reflection cannot
    /// tell them apart. But a wire panel is a screwdriver held against an opened maintenance hatch:
    /// a station AI has no hands and in vanilla cannot open one at all. Offering it would put the
    /// agent past parity in the worst available direction — the wires behind those panels include
    /// the one that cuts AI control of the device.
    /// </summary>
    private static readonly HashSet<string> Physical = new()
    {
        "WiresActionMessage",
    };

    /// <summary>
    /// Interfaces excluded from reading, by the type name of their key.
    ///
    /// Filtering the wire panel's <em>action</em> was not enough. Its <em>state</em> is the puzzle
    /// itself — every wire's colour, letter and cut status, plus the seed — and a holopad or an
    /// airlock carries that interface alongside its real one. Reflected verbatim it handed the
    /// agent the solved wire panel of every device it looked at, which no player of any role can
    /// see without a screwdriver and a pair of cutters.
    /// </summary>
    private static readonly HashSet<string> PhysicalKeys = new()
    {
        "WiresUiKey",
    };

    /// <summary>
    /// Engine plumbing is filtered by namespace rather than by name: <c>OpenBoundInterfaceMessage</c>
    /// and its siblings are how the UI system opens and closes itself, they are meaningless as
    /// actions, and a future sibling should be excluded the day it appears rather than the day
    /// somebody notices it in a console listing.
    /// </summary>
    private static bool IsOffered(Type message) =>
        message.Namespace?.StartsWith("Robust.", StringComparison.Ordinal) != true &&
        !Physical.Contains(message.Name);

    /// <summary>
    /// The component a console keeps its data in, when it keeps it there instead of in a state
    /// object.
    ///
    /// <c>UserInterfaceComponent.States</c> is described upstream as legacy — newer interfaces
    /// network a component and let the client read that. Reflecting only the state object therefore
    /// showed some consoles as empty shells, which is how the atmospheric monitoring console
    /// arrived: no actions, because it is read-only, and no readings either, because all of them
    /// live in <c>AtmosMonitoringConsoleComponent</c>. The agent could see it and learn nothing.
    ///
    /// The pairing is taken from the author's own layout, not guessed at: <c>XUiKey</c> and
    /// <c>XComponent</c> declared in the same namespace — for atmospherics, in the same file. Both
    /// halves are required, so a coincidence of naming somewhere else in the game cannot match. A
    /// miss returns null and the caller falls back to saying there is no state block, which is what
    /// it did before this existed.
    /// </summary>
    public object? StateComponentFor(EntityUid uid, Enum key)
    {
        var keyType = key.GetType();

        if (!keyType.Name.EndsWith("UiKey", StringComparison.Ordinal))
            return null;

        var wanted = string.Concat(keyType.Name.AsSpan(0, keyType.Name.Length - "UiKey".Length), "Component");

        foreach (var component in _entMan.GetComponents(uid))
        {
            var type = component.GetType();

            if (type.Name == wanted && type.Namespace == keyType.Namespace)
                return component;
        }

        return null;
    }

    /// <summary>
    /// The entity's actions, keyed by the short name the model calls them by.
    ///
    /// Collisions are dropped rather than disambiguated. Two messages that shorten to one name are
    /// vanishingly rare, and an action whose name resolves to the wrong message is worse than an
    /// action that is missing: the model would press it, see an unrelated effect, and record that
    /// as how the console works.
    /// </summary>
    public IReadOnlyDictionary<string, UiContract.UiAction> ActionsFor(EntityUid uid)
    {
        var actions = new Dictionary<string, UiContract.UiAction>();
        var collided = new HashSet<string>();

        foreach (var type in MessagesFor(uid))
        {
            var action = UiContract.Describe(type);
            if (action == null)
                continue;

            if (!actions.TryAdd(action.Name, action))
                collided.Add(action.Name);
        }

        foreach (var name in collided)
        {
            actions.Remove(name);
            _sawmill.Warning($"действие '{name}' у {uid} неоднозначно — скрыто");
        }

        return actions;
    }
}
