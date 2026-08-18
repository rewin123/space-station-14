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
/// Граница между таблицей Lua и <see cref="JsonElement"/>, в обе стороны.
///
/// <para>
/// Зачем вообще JSON посередине. Инструмент агента принимает <c>JsonElement</c> и отдаёт
/// <see cref="Tools.ToolResult"/> — этот контракт написан для модели и проверен тестами схем.
/// Скрипт не заводит второй, параллельный: он переводится в тот же самый. Значит вызов из Lua
/// проходит ровно те же разборы аргументов, те же ворота и те же коды ошибок, что и вызов
/// tool call'ом, и разойтись эти два пути не могут в принципе.
/// </para>
/// <para>
/// Числа пишутся целыми, когда они целые. В Lua всё число — <c>double</c>, и наивная запись дала бы
/// <c>{"count":3.0}</c> там, где схема ждёт целое; половина инструментов на этом отказала бы с
/// <c>bad_args</c>, а модель увидела бы отказ, в котором её код ни при чём.
/// </para>
/// </summary>
public static class LuaBridge
{
    /// <summary>
    /// Потолок вложенности. Он же — единственная защита от таблицы, ссылающейся на себя:
    /// обход циклической структуры иначе не вернулся бы никогда, а скрипт исполняется на потоке,
    /// который в этот момент держит вызов инструмента.
    /// </summary>
    public const int MaxDepth = 12;

    /// <summary>Аргументы вызова: таблица Lua → <see cref="JsonElement"/> для обработчика инструмента.</summary>
    public static JsonDocument ToJson(DynValue value, string what)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, LlmJson.WriterOptions))
        {
            // Инструмент всегда ждёт объект. Голое nil — это вызов без аргументов, самый частый
            // случай (drop(), laws()), и разворачивать его в отказ было бы враньём.
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

    /// <summary>Результат инструмента: <see cref="JsonElement"/> → таблица Lua.</summary>
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
                var index = 1; // Lua считает с единицы, и ipairs на нуле останавливается сразу.
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
    /// Значение, которое вернул скрипт, — в обычный объект .NET для поля <c>effect</c>.
    ///
    /// Отдельно от <see cref="ToJson"/> потому, что <c>effect</c> сериализуется штатным
    /// сериализатором позже: сунуть туда готовую строку JSON значило бы закодировать её дважды и
    /// показать модели экранированную кашу вместо ответа её собственного скрипта.
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
            throw new ScriptRuntimeException($"{what}: таблица вложена глубже {MaxDepth} уровней");

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
                    $"{what}: значение типа {value.Type} инструменту не передаётся — нужны строки, числа, булевы и таблицы");
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, double number)
    {
        if (double.IsFinite(number) && Math.Abs(number) < 9e15 && Math.Abs(number - Math.Truncate(number)) < double.Epsilon)
            w.WriteNumberValue((long) number);
        else if (double.IsFinite(number))
            w.WriteNumberValue(number);
        else
            throw new ScriptRuntimeException("в аргументах inf или nan");
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
                    $"{what}: ключом таблицы может быть только строка или число, а не {key.Type}"),
            };

            w.WritePropertyName(name);
            Write(w, pair.Value, depth + 1, what);
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// Таблица считается массивом, только если её ключи — ровно 1..n без дыр.
    ///
    /// Список хендлов должен уехать массивом, иначе <c>ipairs</c> на той стороне не пойдёт; но
    /// смешанная таблица (<c>{1,2,name='x'}</c>) обязана уехать объектом, иначе часть данных
    /// потерялась бы молча.
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
