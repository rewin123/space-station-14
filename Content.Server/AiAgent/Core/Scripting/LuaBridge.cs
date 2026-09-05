using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Content.Server.AiAgent.Llm;
using MoonSharp.Interpreter;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// The boundary between a Lua table and <see cref="JsonElement"/>, in both directions.
///
/// <para>
/// Why have JSON in the middle at all. An agent tool accepts <c>JsonElement</c> and returns
/// <see cref="Tools.ToolResult"/> — this contract is written for the model and covered by schema
/// tests. A script does not get a second, parallel one: it is translated into that same contract.
/// So a call from Lua goes through exactly the same argument parsing, the same gates and the same
/// error codes as a tool call does, and the two paths cannot diverge in principle.
/// </para>
/// <para>
/// Numbers are written as integers when they are integers. In Lua every number is a <c>double</c>,
/// and a naive write would produce <c>{"count":3.0}</c> where the schema expects an integer; half
/// the tools would reject that with <c>bad_args</c>, and the model would see a rejection that has
/// nothing to do with its code.
/// </para>
/// </summary>
public static class LuaBridge
{
    /// <summary>
    /// The nesting depth ceiling. It is also the only defense against a table that references
    /// itself: walking a cyclic structure would otherwise never return, and the script executes on
    /// the thread that currently holds the tool call.
    /// </summary>
    public const int MaxDepth = 12;

    /// <summary>Call arguments: a Lua table → <see cref="JsonElement"/> for the tool handler.</summary>
    public static JsonDocument ToJson(DynValue value, string what)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, LlmJson.WriterOptions))
        {
            // A tool always expects an object. Bare nil is a call with no arguments, the most
            // common case (drop(), laws()), and turning it into a rejection would be a lie.
            if (value.IsNilOrNan() || value.Type == DataType.Void)
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
            else
            {
                Write(w, value, 0, what);
            }
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    /// <summary>Tool result: <see cref="JsonElement"/> → a Lua table.</summary>
    public static DynValue FromJson(Script script, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var table = new Table(script);
                foreach (var property in element.EnumerateObject())
                    table.Set(property.Name, FromJson(script, property.Value));
                return DynValue.NewTable(table);
            }
            case JsonValueKind.Array:
            {
                var table = new Table(script);
                var index = 1; // Lua counts from one, and ipairs stops immediately at zero.
                foreach (var item in element.EnumerateArray())
                    table.Set(index++, FromJson(script, item));
                return DynValue.NewTable(table);
            }
            case JsonValueKind.String:
                return DynValue.NewString(element.GetString());
            case JsonValueKind.Number:
                return DynValue.NewNumber(element.GetDouble());
            case JsonValueKind.True:
                return DynValue.True;
            case JsonValueKind.False:
                return DynValue.False;
            default:
                return DynValue.Nil;
        }
    }

    /// <summary>
    /// The value a script returned, converted into a plain .NET object for the <c>effect</c> field.
    ///
    /// Kept separate from <see cref="ToJson"/> because <c>effect</c> is serialized later by the
    /// regular serializer: shoving a ready-made JSON string in there would encode it twice and show
    /// the model an escaped mess instead of its own script's answer.
    /// </summary>
    public static object? ToObject(DynValue value, int depth = 0)
    {
        if (depth > MaxDepth)
            return null;

        switch (value.Type)
        {
            case DataType.Boolean:
                return value.Boolean;
            case DataType.Number:
                return Math.Abs(value.Number - Math.Truncate(value.Number)) < double.Epsilon
                       && Math.Abs(value.Number) < 9e15
                    ? (long) value.Number
                    : value.Number;
            case DataType.String:
                return value.String;
            case DataType.Table:
            {
                var table = value.Table;
                if (IsArray(table, out var length))
                {
                    var list = new List<object?>(length);
                    for (var i = 1; i <= length; i++)
                        list.Add(ToObject(table.Get(i), depth + 1));
                    return list;
                }

                var map = new Dictionary<string, object?>();
                foreach (var pair in table.Pairs)
                {
                    var key = pair.Key.Type switch
                    {
                        DataType.String => pair.Key.String,
                        DataType.Number => pair.Key.Number.ToString(CultureInfo.InvariantCulture),
                        _ => null,
                    };

                    if (key != null)
                        map[key] = ToObject(pair.Value, depth + 1);
                }

                return map;
            }
            default:
                return null;
        }
    }

    private static void Write(Utf8JsonWriter w, DynValue value, int depth, string what)
    {
        if (depth > MaxDepth)
            throw new ScriptRuntimeException($"{what}: table nested deeper than {MaxDepth} levels");

        switch (value.Type)
        {
            case DataType.Nil:
            case DataType.Void:
                w.WriteNullValue();
                break;
            case DataType.Boolean:
                w.WriteBooleanValue(value.Boolean);
                break;
            case DataType.Number:
                WriteNumber(w, value.Number);
                break;
            case DataType.String:
                w.WriteStringValue(value.String);
                break;
            case DataType.Table:
                WriteTable(w, value.Table, depth, what);
                break;
            default:
                throw new ScriptRuntimeException(
                    $"{what}: a value of type {value.Type} cannot be passed to a tool — strings, numbers, booleans and tables only");
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, double number)
    {
        if (double.IsFinite(number) && Math.Abs(number) < 9e15 && Math.Abs(number - Math.Truncate(number)) < double.Epsilon)
            w.WriteNumberValue((long) number);
        else if (double.IsFinite(number))
            w.WriteNumberValue(number);
        else
            throw new ScriptRuntimeException("inf or nan in the arguments");
    }

    private static void WriteTable(Utf8JsonWriter w, Table table, int depth, string what)
    {
        if (IsArray(table, out var length))
        {
            w.WriteStartArray();
            for (var i = 1; i <= length; i++)
                Write(w, table.Get(i), depth + 1, what);
            w.WriteEndArray();
            return;
        }

        w.WriteStartObject();
        foreach (var pair in table.Pairs)
        {
            var key = pair.Key;
            var name = key.Type switch
            {
                DataType.String => key.String,
                DataType.Number => key.Number.ToString(CultureInfo.InvariantCulture),
                _ => throw new ScriptRuntimeException(
                    $"{what}: a table key can only be a string or a number, not {key.Type}"),
            };

            w.WritePropertyName(name);
            Write(w, pair.Value, depth + 1, what);
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// A table counts as an array only if its keys are exactly 1..n with no gaps.
    ///
    /// A list of handles must go out as an array, or <c>ipairs</c> won't work on the other side;
    /// but a mixed table (<c>{1,2,name='x'}</c>) must go out as an object, or part of the data
    /// would be silently lost.
    /// </summary>
    private static bool IsArray(Table table, out int length)
    {
        length = table.Length;
        if (length == 0)
            return false;

        var count = 0;
        foreach (var pair in table.Pairs)
        {
            if (pair.Key.Type != DataType.Number)
                return false;

            var index = pair.Key.Number;
            if (index < 1 || index > length || Math.Abs(index - Math.Truncate(index)) > double.Epsilon)
                return false;

            count++;
        }

        return count == length;
    }
}
