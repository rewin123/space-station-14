using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Tools;
using MoonSharp.Interpreter;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// Как инструмент агента становится функцией Lua.
///
/// <para>
/// Вызов из скрипта идёт через тот же <see cref="ToolDispatcher"/>, что и вызов от модели tool
/// call'ом. Это не экономия кода, а единственный способ не завести второй, расходящийся контракт:
/// те же ворота режима разбора, те же коды ошибок, тот же разбор аргументов и та же маршрутизация
/// на главный поток. Скрипт не получает никакой дороги в мир, которой не было бы у обычного хода.
/// </para>
/// <para>
/// Отказ инструмента превращается в исключение Lua, а не в поле таблицы. Так прямой код читается
/// сверху вниз без проверки после каждой строки, а терпимость к отказу берётся штатным
/// <c>pcall</c>. Обёртки вроде <c>must()</c> здесь нет намеренно: она добавляла бы шум в каждую
/// строку ради того, что язык уже умеет.
/// </para>
/// </summary>
public sealed class ScriptRuntime
{
    /// <summary>
    /// Инструменты, которых скрипт не видит.
    ///
    /// <c>script</c> и управление процессами — чтобы скрипт не порождал скрипты: одного тела на
    /// всех и так мало, а дерево процессов сделало бы <c>bp_stop</c> обещанием без покрытия.
    /// <c>noop</c> — потому что закрывать ход модели с фонового потока бессмысленно: ход к тому
    /// времени давно кончился.
    /// </summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "script", "bp_get_output", "bp_stop", "noop",
    };

    /// <summary>Имена, занятые самим языком. Такой инструмент доступен только через <c>raw</c>.</summary>
    private static readonly HashSet<string> LuaWords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if",
        "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    private readonly AgentSession _session;
    private readonly Func<DispatchGate> _gate;
    private readonly Func<LuaLimits> _limits;
    private readonly Func<int> _maxCalls;
    private readonly Dictionary<string, string?> _firstRequired = new(StringComparer.Ordinal);

    public ScriptRuntime(AgentSession session, Func<DispatchGate> gate, Func<LuaLimits> limits, Func<int> maxCalls)
    {
        _session = session;
        _gate = gate;
        _limits = limits;
        _maxCalls = maxCalls;
    }

    /// <summary>Клетка со всеми инструментами внутри, готовая исполнять код этого процесса.</summary>
    public LuaHost Build(ScriptProcess process)
    {
        var host = new LuaHost(_limits(), process.Print);
        var raw = host.NewTable("raw");

        foreach (var tool in _session.Registry.Tools)
        {
            if (Hidden.Contains(tool.Name))
                continue;

            var bound = tool;
            DynValue Call(CallbackArguments args) => Invoke(host, process, bound, args);

            // В raw инструмент лежит всегда и под своим настоящим именем — это единственный
            // честный способ дотянуться до goto, чьё имя занято языком.
            host.BindInto(raw, bound.Name, Call);

            if (!LuaWords.Contains(bound.Name))
                host.Bind(bound.Name, Call);
        }

        // Вторым проходом: ждущая версия забирает себе короткое имя.
        //
        // Порядок важен — псевдоним обязан перекрыть мгновенный инструмент, а не наоборот.
        // Сделано привязкой, а не функцией-обёрткой в прелюдии, ради сообщений об ошибке: обёртка
        // подставляла бы в них свою строку («прелюдия:(7,4)») вместо строки скрипта, и модель
        // правила бы код, которого не писала.
        foreach (var tool in _session.Registry.Tools)
        {
            var alias = AliasOf(tool.Name);
            if (alias == null || Hidden.Contains(tool.Name))
                continue;

            var bound = tool;
            host.Bind(alias, args => Invoke(host, process, bound, args));
        }

        host.Bind("sleep", args => Sleep(process, args));
        host.LoadPrelude(ScriptPrelude.Source);
        return host;
    }

    /// <summary>Имена, известные скрипту до запуска, — их сверяет линтер опечаток.</summary>
    public IReadOnlyList<string> KnownNames()
    {
        var names = new List<string>();

        foreach (var tool in _session.Registry.Tools)
        {
            if (!Hidden.Contains(tool.Name))
                names.Add(tool.Name);
        }

        foreach (var tool in _session.Registry.Tools)
        {
            var alias = AliasOf(tool.Name);
            if (alias != null && !Hidden.Contains(tool.Name))
                names.Add(alias);
        }

        names.Add("sleep");
        names.Add("raw");
        names.AddRange(ScriptPrelude.Names);
        names.AddRange(LuaStandardNames);
        return names;
    }

    /// <summary>Что даёт сама клетка: базовая библиотека без файлов, процессов и загрузки кода.</summary>
    private static readonly string[] LuaStandardNames =
    {
        "assert", "collectgarbage", "error", "ipairs", "next", "pairs", "pcall", "xpcall", "print",
        "select", "tonumber", "tostring", "type", "unpack", "pack", "rawequal", "rawget", "rawlen",
        "rawset", "setmetatable", "getmetatable", "string", "table", "math", "bit32", "_G",
    };

    /// <summary>
    /// Короткое имя ждущей версии: <c>goto_wait</c> живёт в скрипте как <c>go</c>.
    ///
    /// Соглашение, а не список: тело, добавившее ждущий инструмент, получает псевдоним даром, и
    /// ядру не приходится знать имена инструментов борга.
    /// </summary>
    public static string? AliasOf(string name)
    {
        if (!name.EndsWith("_wait", StringComparison.Ordinal))
            return null;

        var head = name[..^"_wait".Length];

        // goto занято языком — ждущая версия и есть та самая go, ради которой всё это писалось.
        return head == "goto" ? "go" : head;
    }

    private DynValue Sleep(ScriptProcess process, CallbackArguments args)
    {
        var seconds = args.Count > 0 && args[0].Type == DataType.Number ? args[0].Number : 1.0;
        seconds = Math.Clamp(seconds, 0.05, 60.0);

        // Ждём на токене, а не Thread.Sleep: bp_stop обязан будить спящий скрипт сразу, иначе
        // «снять процесс» означало бы «подождать до минуты».
        if (process.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
            process.Token.ThrowIfCancellationRequested();

        return DynValue.Nil;
    }

    private DynValue Invoke(LuaHost host, ScriptProcess process, AiTool tool, CallbackArguments args)
    {
        var calls = process.CountCall();
        var max = _maxCalls();

        if (calls > max)
        {
            throw new ScriptRuntimeException(
                $"script_budget: скрипт позвал инструменты {max} раз и снят — похоже, он зациклился");
        }

        process.Token.ThrowIfCancellationRequested();

        string arguments;
        try
        {
            using var document = LuaBridge.ToJson(Normalize(host, tool, args), tool.Name);
            arguments = document.RootElement.GetRawText();
        }
        catch (ScriptRuntimeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ScriptRuntimeException($"bad_args: {tool.Name} не понял аргументы ({e.GetType().Name})");
        }

        var call = new ToolCallDto
        {
            Id = $"скрипт-{process.Pid}",
            Function = new FunctionCallDto { Name = tool.Name, Arguments = arguments },
        };

        var invocation = _session.Dispatcher
            .InvokeAsync(call, _gate(), process.Token)
            .GetAwaiter()
            .GetResult();

        var result = invocation.Result;

        if (!result.Ok)
        {
            var detail = string.IsNullOrEmpty(result.Detail) ? tool.Name : result.Detail;
            throw new ScriptRuntimeException($"{result.Error}: {detail}");
        }

        using var back = JsonDocument.Parse(result.ToJson());
        return host.ToLua(back.RootElement);
    }

    /// <summary>
    /// Голый аргумент вместо таблицы — самая частая описка, и она того не стоит.
    ///
    /// Модель пишет <c>examine("ящик-1")</c> там, где схема ждёт <c>{target=...}</c>. Отказ был бы
    /// формально правильным и практически бесполезным: имя единственного обязательного поля
    /// известно из самой схемы, так что подставим его сами.
    /// </summary>
    private DynValue Normalize(LuaHost host, AiTool tool, CallbackArguments args)
    {
        if (args.Count == 0)
            return DynValue.Nil;

        var first = args[0];
        if (first.Type == DataType.Table || first.IsNilOrNan())
            return first;

        if (first.Type != DataType.String && first.Type != DataType.Number && first.Type != DataType.Boolean)
            return first;

        var property = FirstRequired(tool);
        if (property == null)
        {
            throw new ScriptRuntimeException(
                $"bad_args: {tool.Name} принимает таблицу, например {tool.Name}{{...}}");
        }

        return host.NewValue(property, first);
    }

    private string? FirstRequired(AiTool tool)
    {
        if (_firstRequired.TryGetValue(tool.Name, out var cached))
            return cached;

        string? name = null;

        try
        {
            using var schema = JsonDocument.Parse(tool.SchemaJson);
            if (schema.RootElement.TryGetProperty("required", out var required)
                && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    name = item.GetString();
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Схема инструмента разбирается на старте сессии и не может быть сломанной; но если
            // всё же — это не повод ронять скрипт, просто не будет удобной подстановки.
        }

        _firstRequired[tool.Name] = name;
        return name;
    }
}
