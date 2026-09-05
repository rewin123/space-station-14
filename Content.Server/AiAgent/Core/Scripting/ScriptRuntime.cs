using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Tools;
using MoonSharp.Interpreter;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// How an agent tool becomes a Lua function.
///
/// <para>
/// A call from a script goes through the same <see cref="ToolDispatcher"/> as a call from the model
/// as a tool call. This isn't a code-saving trick but the only way to avoid a second, diverging
/// contract: the same review-mode gate, the same error codes, the same argument parsing, and the
/// same routing to the main thread. A script gets no road into the world that an ordinary turn
/// wouldn't already have.
/// </para>
/// <para>
/// A tool refusal turns into a Lua exception, not a table field. This way straight-line code reads
/// top to bottom without a check after every line, and tolerance for a refusal is picked up via
/// stock <c>pcall</c>. There's deliberately no wrapper like <c>must()</c> here: it would add noise
/// to every line for something the language already does.
/// </para>
/// </summary>
public sealed class ScriptRuntime
{
    /// <summary>
    /// Tools the script doesn't see.
    ///
    /// <c>script</c> and process control — so a script can't spawn scripts: one body for everyone is
    /// already scarce, and a tree of processes would turn <c>bp_stop</c> into a promise with no
    /// coverage. <c>noop</c> — because closing the model's turn from a background thread is
    /// pointless: the turn ended long ago by that point.
    /// </summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "script", "bp_get_output", "bp_stop", "noop",
    };

    /// <summary>Names taken by the language itself. Such a tool is reachable only through <c>raw</c>.</summary>
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

    /// <summary>The sandbox with all the tools inside, ready to run this process's code.</summary>
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

            // In raw, the tool always sits under its real name — this is the only honest way to
            // reach goto, whose name is taken by the language.
            host.BindInto(raw, bound.Name, Call);

            if (!LuaWords.Contains(bound.Name))
                host.Bind(bound.Name, Call);
        }

        // Second pass: the waiting version claims the short name for itself.
        //
        // Order matters — the alias must override the instant tool, not the other way around.
        // Done as a binding rather than a wrapper function in the prelude, for the sake of error
        // messages: a wrapper would substitute its own line ("прелюдия:(7,4)") into them instead of
        // the script's line, and the model would go editing code it never wrote.
        foreach (var tool in _session.Registry.Tools)
        {
            var alias = AliasOf(tool.Name);
            if (alias == null || Hidden.Contains(tool.Name))
                continue;

            var bound = tool;
            host.Bind(alias, args => Invoke(host, process, bound, args));
        }

        host.Bind("sleep", args => Sleep(process, args));
        host.LoadPrelude(_session.Locale.ScriptPrelude);
        return host;
    }

    /// <summary>Names known to the script before a run — the typo linter checks against these.</summary>
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

    /// <summary>What the sandbox itself provides: the base library, minus files, processes, and code loading.</summary>
    private static readonly string[] LuaStandardNames =
    {
        "assert", "collectgarbage", "error", "ipairs", "next", "pairs", "pcall", "xpcall", "print",
        "select", "tonumber", "tostring", "type", "unpack", "pack", "rawequal", "rawget", "rawlen",
        "rawset", "setmetatable", "getmetatable", "string", "table", "math", "bit32", "_G",
    };

    /// <summary>
    /// The short name of the waiting version: <c>goto_wait</c> lives in the script as <c>go</c>.
    ///
    /// A convention, not a list: a body that adds a waiting tool gets the alias for free, and the
    /// core doesn't have to know the borg's tool names.
    /// </summary>
    public static string? AliasOf(string name)
    {
        if (!name.EndsWith("_wait", StringComparison.Ordinal))
            return null;

        var head = name[..^"_wait".Length];

        // goto is taken by the language — the waiting version is exactly that go, the whole reason
        // this was written.
        return head == "goto" ? "go" : head;
    }

    private DynValue Sleep(ScriptProcess process, CallbackArguments args)
    {
        var seconds = args.Count > 0 && args[0].Type == DataType.Number ? args[0].Number : 1.0;
        seconds = Math.Clamp(seconds, 0.05, 60.0);

        // Wait on the token, not Thread.Sleep: bp_stop is obligated to wake a sleeping script
        // immediately, otherwise "stop the process" would mean "wait up to a minute."
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
    /// A bare argument instead of a table — the most common slip, and it isn't worth punishing.
    ///
    /// The model writes <c>examine("box-1")</c> where the schema expects <c>{target=...}</c>. A
    /// refusal would be formally correct and practically useless: the name of the single required
    /// field is known from the schema itself, so let's substitute it ourselves.
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
            // The tool schema is parsed at session start and can't be broken; but if it somehow is,
            // that's no reason to crash the script — there just won't be a convenient substitution.
        }

        _firstRequired[tool.Name] = name;
        return name;
    }
}
