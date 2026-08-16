using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

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

            // The unit must be in the signature: the state the model reads back is in radians, and
            // an unlabelled number is a guess the model will make in both directions.
            if (u == typeof(Angle)) return "угол (градусы)";

            return u.Name;
        }
    }

    /// <summary>
    /// Build the callable name and parameter list for one message type.
    ///
    /// The game has two idioms for carrying a payload, and a console is unusable if the model is
    /// shown only one of them.
    ///
    /// <b>Constructor-based.</b> <c>CommunicationsConsoleAnnounceMessage(string, string)</c>. The
    /// parameters come from the longest public constructor; it is the author's own statement of
    /// what a caller has to provide, and it excludes the base class plumbing (<c>Actor</c>,
    /// <c>UiKey</c>, <c>Entity</c>) that the caller must never set.
    ///
    /// <b>Field-based.</b> <c>SolarControlConsoleAdjustMessage</c> declares no constructor at all:
    /// the client fills its public fields with an object initializer. Hundreds of messages in the
    /// game are written this way. For those the parameter list is the public fields — because the
    /// alternative is what shipped first: the action is listed with no arguments, every argument
    /// the model guesses is dropped on the floor, and the message goes out with default values,
    /// which for an angle is zero. The agent then calls a "no-argument" action over and over
    /// watching the state refuse to move, and spends its turn budget guessing parameters that are
    /// never read.
    /// </summary>
    public static UiAction? Describe(Type message)
    {
        if (!typeof(BoundUserInterfaceMessage).IsAssignableFrom(message) || message.IsAbstract)
            return null;

        var pars = new List<UiParam>();
        var ctorParams = LongestPublicCtor(message)?.GetParameters() ?? Array.Empty<ParameterInfo>();
        var fields = ctorParams.Length == 0 ? PayloadFields(message) : null;

        if (fields != null)
        {
            foreach (var f in fields)
            {
                var underlying = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;

                pars.Add(new UiParam(
                    Snake(f.Name),
                    f.FieldType,
                    underlying != f.FieldType,
                    underlying.IsEnum ? Enum.GetNames(underlying) : null));
            }
        }
        else
        {
            foreach (var p in ctorParams)
            {
                var underlying = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;

                pars.Add(new UiParam(
                    Snake(p.Name ?? "arg"),
                    p.ParameterType,
                    p.HasDefaultValue || underlying != p.ParameterType,
                    underlying.IsEnum ? Enum.GetNames(underlying) : null));
            }
        }

        return new UiAction(ActionName(message), message, pars);
    }

    private static ConstructorInfo? LongestPublicCtor(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

    /// <summary>
    /// The fields a ctorless message carries its payload in, in declaration order.
    ///
    /// Declaration order (metadata tokens, then name) is the order the author wrote the fields in
    /// and is stable between calls; an order that shuffles would read to the model as the console
    /// changing under it. The base class plumbing is excluded by name: an object initializer — the
    /// only way the client fills these fields — cannot and must not set it.
    /// </summary>
    private static FieldInfo[] PayloadFields(Type message) =>
        message
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsInitOnly && !f.Name.Contains('<') && !Plumbing.Contains(f.Name))
            .OrderBy(f => f.MetadataToken)
            .ThenBy(f => f.Name)
            .ToArray();

    /// <summary>Base-class plumbing a caller must never fill.</summary>
    private static readonly HashSet<string> Plumbing = new()
    {
        "Actor", "UiKey", "Entity",
    };

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

        var ctor = LongestPublicCtor(action.Message);

        if (ctor == null)
        {
            error = $"у '{action.Name}' нет публичного конструктора";
            return null;
        }

        var pars = ctor.GetParameters();

        // The same fallback Describe used to describe this action: a ctorless message is built by
        // invoking the default constructor and filling its payload fields. The two must never
        // disagree, or the model would be offered a signature that cannot be called.
        var fields = pars.Length == 0 ? PayloadFields(action.Message) : Array.Empty<FieldInfo>();
        var fieldMode = fields.Length > 0;

        var count = fieldMode ? fields.Length : pars.Length;
        var values = new object?[count];

        for (var i = 0; i < count; i++)
        {
            var spec = action.Params[i];
            var type = fieldMode ? fields[i].FieldType : pars[i].ParameterType;
            var supplied = args is { ValueKind: JsonValueKind.Object } &&
                           args.Value.TryGetProperty(spec.Name, out var raw)
                ? raw
                : (JsonElement?)null;

            if (supplied == null)
            {
                // In field mode a missing field is never forgiven silently: the handler on the
                // other end treats any present value as "set this", so a default-filled field is
                // not "unchanged" but a real write of zero.
                if (!spec.Optional)
                {
                    error = $"'{action.Name}': нужен аргумент {spec.Describe()}";
                    return null;
                }

                values[i] = fieldMode
                    ? Default(type)
                    : pars[i].HasDefaultValue ? pars[i].DefaultValue : Default(pars[i].ParameterType);

                continue;
            }

            if (!TryConvert(supplied.Value, type, out values[i], out var why))
            {
                error = $"'{action.Name}', аргумент '{spec.Name}': {why}";
                return null;
            }
        }

        try
        {
            var message = (BoundUserInterfaceMessage)(fieldMode ? ctor.Invoke(null) : ctor.Invoke(values));

            if (fieldMode)
            {
                for (var i = 0; i < fields.Length; i++)
                    fields[i].SetValue(message, values[i]);
            }

            return message;
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

            // Angle is a readonly struct with no setters and no System.Text.Json converter: the
            // generic path below cannot build it, and the engine's own serializer
            // (AngleSerializer) lives on a different wire. The client's UI takes degrees, so
            // degrees is the unit the model gets — the state it reads back is in radians, and the
            // model converts.
            if (type == typeof(Angle))
            {
                var text = raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.ToString();

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
                {
                    error = "ожидается число — угол в градусах";
                    return false;
                }

                value = Angle.FromDegrees(degrees);
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
