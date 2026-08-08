using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Tools;

/// <summary>
/// Turns a station console into something the model can read and press, by reflecting over the
/// data contract rather than over the interface.
///
/// <b>Why not the interface.</b> There is no interface on the server. Layout lives entirely in the
/// client's XAML and its hundred-odd <c>BoundUserInterface</c> subclasses; all the server keeps is
/// a UI key, the <em>name</em> of a client class and two numbers
/// (<c>UserInterfaceComponent.InterfaceData</c>). Nothing here can enumerate buttons because
/// nothing here has any.
///
/// <b>What there is instead.</b> A netserializable state object and the set of
/// <see cref="BoundUserInterfaceMessage"/> types the entity actually handles. That pair is the
/// whole contract between client and server, and for a language model it is the better of the two
/// descriptions: <c>PressureAverage: 101.3</c> needs no localisation, and an enum parameter
/// enumerates its own legal values. A human needs labels and a layout; a model needs names and
/// types, and names and types are exactly what survives on this side of the wire.
///
/// <b>Why it should keep working.</b> The churn in this game is in the client: windows get
/// redesigned constantly. The wire contract does not — of the eighty-eight state classes in
/// Content.Shared, thirty-two were touched in a year and almost all of those once. When a field is
/// renamed the model simply sees the new name; when a message is added it appears as a new action.
/// There is no table here to fall out of date, which is the entire point of doing it this way.
/// </summary>
public static class UiContract
{
    /// <summary>
    /// How deep to walk a nested value before giving up.
    ///
    /// Three levels covers every real console — state, entry, field — while stopping the walk from
    /// wandering into an entity graph and serialising half the map into a tool response.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>Longest collection rendered in full before it is summarised as a count.</summary>
    private const int MaxItems = 24;

    // ------------------------------------------------------------------- reading state

    /// <summary>
    /// Flatten a UI state object into name/value pairs.
    ///
    /// Both fields and properties, because the eighty-eight state classes are split between the two
    /// styles and which one a given console picked is an accident of when it was written.
    /// </summary>
    public static Dictionary<string, object?> Describe(object? state)
    {
        var result = new Dictionary<string, object?>();

        if (state == null)
            return result;

        foreach (var (name, value) in Members(state))
            result[name] = Simplify(value, 0);

        return result;
    }

    private static IEnumerable<(string Name, object? Value)> Members(object obj)
    {
        var type = obj.GetType();

        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            // Compiler-generated backing fields would double every auto-property.
            if (!f.Name.Contains('<') && !IsPlumbing(f))
                yield return (f.Name, Get(() => f.GetValue(obj)));
        }

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length == 0 && p.CanRead && !IsPlumbing(p))
                yield return (p.Name, Get(() => p.GetValue(obj)));
        }

        // Anything a component inherits from the engine's base class: LifeStage, CreationTick,
        // NetSyncEnabled, Deleted and the rest. Every one of them is true of every component in the
        // game, so none of them tells the model anything about the console it just opened — and
        // there are ten of them, enough to bury the four readings that matter.
        static bool IsPlumbing(MemberInfo m) => m.DeclaringType == typeof(Component);

        // A property getter on a state object is usually trivial, but it is content code and it is
        // being called off its usual path. One that throws must cost the model a field, not the
        // whole console.
        static object? Get(Func<object?> read)
        {
            try
            {
                return read();
            }
            catch (Exception e)
            {
                return $"<не читается: {e.GetType().Name}>";
            }
        }
    }

    /// <summary>
    /// Reduce an arbitrary value to something that survives JSON and stays readable.
    ///
    /// Enums become their member name rather than a number — the name is the documentation, and a
    /// bare 3 tells the model nothing about what it may write back.
    /// </summary>
    private static object? Simplify(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or char:
                return value is char c ? c.ToString() : value;
            case Enum e:
                return e.ToString();
        }

        var type = value.GetType();

        if (type.IsPrimitive || value is decimal)
            return value;

        // NetEntity, EntityUid, TimeSpan, Color, ProtoId<T>, FixedPoint2 and friends: all of them
        // have a ToString that says more than a field-by-field expansion would.
        if (type.IsValueType && type.Namespace?.StartsWith("Content.") != true && !IsTuple(type))
            return value.ToString();

        if (depth >= MaxDepth)
            return value.ToString();

        if (value is IDictionary dict)
        {
            var map = new Dictionary<string, object?>();
            var n = 0;

            foreach (DictionaryEntry entry in dict)
            {
                if (n++ >= MaxItems)
                {
                    map["…"] = $"ещё {dict.Count - MaxItems}";
                    break;
                }

                map[Simplify(entry.Key, depth + 1)?.ToString() ?? "?"] = Simplify(entry.Value, depth + 1);
            }

            return map;
        }

        if (value is IEnumerable list)
        {
            var items = new List<object?>();

            foreach (var item in list)
            {
                if (items.Count >= MaxItems)
                {
                    items.Add($"…ещё {items.Count} и больше");
                    break;
                }

                items.Add(Simplify(item, depth + 1));
            }

            return items;
        }

        var nested = new Dictionary<string, object?>();
        foreach (var (name, member) in Members(value))
            nested[name] = Simplify(member, depth + 1);

        return nested.Count > 0 ? nested : value.ToString();
    }

    private static bool IsTuple(Type t) =>
        t.IsGenericType && t.FullName?.StartsWith("System.ValueTuple`") == true;

    // ------------------------------------------------------------------ describing actions

    /// <summary>One thing the model can press, and what it must supply to press it.</summary>
    public sealed record UiAction(string Name, Type Message, IReadOnlyList<UiParam> Params)
    {
        /// <summary>The line the model reads when deciding what to call.</summary>
        public string Signature => Params.Count == 0
            ? Name
            : $"{Name}({string.Join(", ", Params.Select(p => p.Describe()))})";
    }

    public sealed record UiParam(string Name, Type Type, bool Optional, IReadOnlyList<string>? Choices)
    {
        public string Describe()
        {
            var kind = Choices is { Count: > 0 } ? string.Join("|", Choices) : Friendly(Type);
            return Optional ? $"{Name}?: {kind}" : $"{Name}: {kind}";
        }

        private static string Friendly(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;

            if (u == typeof(string)) return "текст";
            if (u == typeof(bool)) return "да/нет";
            if (u == typeof(int) || u == typeof(uint) || u == typeof(long)) return "целое";
            if (u == typeof(float) || u == typeof(double)) return "число";

            return u.Name;
        }
    }

    /// <summary>
    /// Build the callable name and parameter list for one message type.
    ///
    /// The parameters come from the longest public constructor rather than from the fields. Field
    /// order is meaningless and includes the base class plumbing (<c>Actor</c>, <c>UiKey</c>,
    /// <c>Entity</c>) that the caller must never set; the constructor is the author's own statement
    /// of what a caller has to provide.
    /// </summary>
    public static UiAction? Describe(Type message)
    {
        if (!typeof(BoundUserInterfaceMessage).IsAssignableFrom(message) || message.IsAbstract)
            return null;

        var ctor = message
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        var pars = new List<UiParam>();

        foreach (var p in ctor?.GetParameters() ?? Array.Empty<ParameterInfo>())
        {
            var underlying = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;

            pars.Add(new UiParam(
                Snake(p.Name ?? "arg"),
                p.ParameterType,
                p.HasDefaultValue || Nullable.GetUnderlyingType(p.ParameterType) != null,
                underlying.IsEnum ? Enum.GetNames(underlying) : null));
        }

        return new UiAction(ActionName(message), message, pars);
    }

    /// <summary>
    /// <c>CommunicationsConsoleAnnounceMessage</c> becomes
    /// <c>communications_console_announce</c>.
    ///
    /// Only the <c>Message</c> suffix goes. Trimming the console's own name out as well reads
    /// better — <c>announce</c> — but it is a guess about which words are noise, and a guess that
    /// mis-collapses two messages onto one name hands the model an action that does something else.
    /// A long name costs a few tokens in a list the model asked for; a wrong one costs a wrong
    /// action on a live station. The type name is also what the fork's own logs and the upstream
    /// source call it, which is worth more than brevity when something has to be traced.
    /// </summary>
    public static string ActionName(Type message)
    {
        var name = message.Name;

        if (name.EndsWith("Message", StringComparison.Ordinal) && name.Length > "Message".Length)
            name = name[..^"Message".Length];

        return Snake(name);
    }

    private static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 6);

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];

            if (char.IsUpper(ch) && i > 0 && (!char.IsUpper(name[i - 1]) ||
                                              (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                sb.Append('_');

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ building a message

    /// <summary>
    /// Construct the message from the model's JSON arguments.
    ///
    /// Returns null and an explanation rather than throwing: a bad argument is an ordinary tool
    /// failure the model can correct on the next call, and the explanation names the parameter so
    /// it can.
    /// </summary>
    public static BoundUserInterfaceMessage? Build(UiAction action, JsonElement? args, out string error)
    {
        error = string.Empty;

        var ctor = action.Message
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor == null)
        {
            error = $"у '{action.Name}' нет публичного конструктора";
            return null;
        }

        var pars = ctor.GetParameters();
        var values = new object?[pars.Length];

        for (var i = 0; i < pars.Length; i++)
        {
            var spec = action.Params[i];
            var supplied = args is { ValueKind: JsonValueKind.Object } &&
                           args.Value.TryGetProperty(spec.Name, out var raw)
                ? raw
                : (JsonElement?)null;

            if (supplied == null)
            {
                if (!spec.Optional && !pars[i].HasDefaultValue)
                {
                    error = $"'{action.Name}': нужен аргумент {spec.Describe()}";
                    return null;
                }

                values[i] = pars[i].HasDefaultValue
                    ? pars[i].DefaultValue
                    : Default(pars[i].ParameterType);

                continue;
            }

            if (!TryConvert(supplied.Value, pars[i].ParameterType, out values[i], out var why))
            {
                error = $"'{action.Name}', аргумент '{spec.Name}': {why}";
                return null;
            }
        }

        try
        {
            return (BoundUserInterfaceMessage)ctor.Invoke(values);
        }
        catch (Exception e)
        {
            // A constructor that validates its input and throws is telling us the same thing a bad
            // argument would, so it is reported the same way rather than as an internal error.
            error = $"'{action.Name}' отверг аргументы: {(e.InnerException ?? e).Message}";
            return null;
        }
    }

    private static object? Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    private static bool TryConvert(JsonElement raw, Type target, out object? value, out string error)
    {
        value = null;
        error = string.Empty;

        var type = Nullable.GetUnderlyingType(target) ?? target;

        if (raw.ValueKind == JsonValueKind.Null)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) == null)
            {
                error = "здесь нельзя null";
                return false;
            }

            return true;
        }

        try
        {
            if (type.IsEnum)
            {
                var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.ToString();

                if (!Enum.TryParse(type, text, ignoreCase: true, out value))
                {
                    error = $"допустимо только {string.Join(", ", Enum.GetNames(type))}";
                    return false;
                }

                return true;
            }

            if (type == typeof(string))
            {
                value = raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.ToString();
                return true;
            }

            if (type == typeof(bool))
            {
                value = raw.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => bool.Parse(raw.ToString()),
                };

                return true;
            }

            // Everything else — numbers, ProtoId<T>, NetEntity, structures — goes through the same
            // serializer the wire uses, so whatever the client could send, the model can too.
            value = JsonSerializer.Deserialize(raw.GetRawText(), type);
            return true;
        }
        catch (Exception e)
        {
            error = $"не разобрать как {type.Name} ({e.GetType().Name})";
            return false;
        }
    }
}
