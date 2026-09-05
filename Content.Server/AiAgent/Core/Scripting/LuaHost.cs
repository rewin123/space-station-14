using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MoonSharp.Interpreter;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>How a script run ended. The same codes that go to the model in <see cref="Tools.ToolError"/>.</summary>
public static class LuaError
{
    public const string Syntax = Tools.ToolError.ScriptSyntax;
    public const string Runtime = Tools.ToolError.ScriptError;
    public const string Budget = Tools.ToolError.ScriptBudget;
}

/// <summary>Ceilings for a single run. Not pacing regulators, but fuses against a runaway script.</summary>
public sealed class LuaLimits
{
    /// <summary>Total Lua instructions. Five million is seconds of computing, not a "until it gets bored" loop.</summary>
    public long MaxSteps { get; init; } = 5_000_000;

    /// <summary>
    /// Instructions between cancellation checks. The smaller this is, the more responsive <c>bp_stop</c>
    /// is and the more expensive the accounting: a measurement on this machine gave 533 slices of
    /// 20000 instructions in 300 ms, meaning the check costs a fraction of a millisecond.
    /// </summary>
    public int SliceInstructions { get; init; } = 20_000;

    /// <summary>
    /// A wall-clock ceiling, not a round-time one — unlike the agent's timers.
    ///
    /// Timers live in round time so they don't wake the agent in a frozen world. Here the task is the
    /// opposite: don't let the thread spin forever. When paused, the round clock stops, and a round-time
    /// ceiling would never be reached — exactly the case this ceiling exists for.
    /// </summary>
    public TimeSpan MaxWall { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>The run's result: either the value the chunk returned, or an error code with a line number.</summary>
public sealed record LuaOutcome(bool Ok, string? Error, string? Detail, DynValue Return, long Steps);

/// <summary>
/// The sandbox in which the agent's script executes.
///
/// <para>
/// The module set is assembled by hand, because the ready-made <c>Preset_HardSandbox</c> didn't fit
/// on any front: it strips out not only dangerous things but also <c>pcall</c> with metatables.
/// Without <c>pcall</c>, a script has no way to survive a tool refusal, and the whole convention of
/// the mode rests on it. What the sandbox doesn't have and never will: <c>io</c>, <c>os</c>,
/// <c>require</c>, <c>load</c>, <c>dofile</c>, <c>debug</c> — that is, files, processes, and loading
/// of outside code. This is a boundary by construction, not a list of prohibitions: these concepts
/// simply don't exist in this interpreter.
/// </para>
/// <para>
/// The chunk executes as a coroutine with <c>AutoYieldCounter</c>, not a direct call. This is the
/// only way to wrest control back from a <c>while true do end</c>: cancellation and ceilings are
/// checked between slices. Without it, <c>bp_stop</c> would be a promise with nothing to back it up
/// — exactly why the engine's stock C# scripting doesn't fit this job.
/// </para>
/// <para>
/// One host — one script — one thread. MoonSharp isn't thread-safe, and there is deliberately no
/// shared state between processes here: two scripts can't corrupt each other's globals table.
/// </para>
/// </summary>
public sealed class LuaHost
{
    private const CoreModules Caged =
        CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling | CoreModules.Metatables;

    private readonly Script _script;
    private readonly LuaLimits _limits;

    public LuaHost(LuaLimits? limits = null, Action<string>? print = null)
    {
        _limits = limits ?? new LuaLimits();
        _script = new Script(Caged);

        // print goes into the process's buffer, not the server console: this is the script's output,
        // which the model reads through bp_get_output.
        if (print != null)
            _script.Options.DebugPrint = print;
    }

    /// <summary>A function visible to the script under the name <paramref name="name"/>.</summary>
    public void Bind(string name, Func<CallbackArguments, DynValue> body)
    {
        _script.Globals[name] = DynValue.NewCallback((_, args) => body(args), name);
    }

    /// <summary>A table visible to the script under the name <paramref name="name"/> — for <c>raw</c>.</summary>
    public Table NewTable(string name)
    {
        var table = new Table(_script);
        _script.Globals[name] = table;
        return table;
    }

    /// <summary>A single-pair table belonging to this script — for substituting a bare argument.</summary>
    public DynValue NewValue(string key, DynValue value)
    {
        var table = new Table(_script);
        table.Set(key, value);
        return DynValue.NewTable(table);
    }

    /// <summary>A function inside a table — how tools whose names are taken by Lua keywords live.</summary>
    public void BindInto(Table table, string name, Func<CallbackArguments, DynValue> body)
    {
        table[name] = DynValue.NewCallback((_, args) => body(args), name);
    }

    /// <summary>All names known to the script. Needed by the typo linter before a run.</summary>
    public IEnumerable<string> GlobalNames()
    {
        foreach (var pair in _script.Globals.Pairs)
        {
            if (pair.Key.Type == DataType.String)
                yield return pair.Key.String;
        }
    }

    /// <summary>
    /// A tool's result as a Lua table. Lives here rather than with the caller because the table must
    /// belong to this same script: a foreign one would crash the interpreter on the first access.
    /// </summary>
    public DynValue ToLua(System.Text.Json.JsonElement element) => LuaBridge.FromJson(_script, element);

    /// <summary>The agent's own code (prelude) — executes before the script and declares its own functions.</summary>
    public void LoadPrelude(string source)
    {
        // The prelude is ours, not the model's: a syntax error here is a build defect, not a behavior
        // one, and it's obligated to fail on the test, not silently leave the agent without go().
        _script.DoString(source, null, "прелюдия");
    }

    /// <summary>
    /// Execute the model's script. Cancellation (<c>bp_stop</c>, session death) surfaces as
    /// <see cref="OperationCanceledException"/> — verified to propagate through the coroutine cleanly.
    /// </summary>
    public LuaOutcome Run(string code, CancellationToken ct)
    {
        DynValue chunk;
        try
        {
            chunk = _script.LoadString(code, null, "скрипт");
        }
        catch (SyntaxErrorException e)
        {
            return new LuaOutcome(false, LuaError.Syntax, e.DecoratedMessage, DynValue.Nil, 0);
        }

        var coroutine = _script.CreateCoroutine(chunk).Coroutine;
        coroutine.AutoYieldCounter = _limits.SliceInstructions;

        long steps = 0;
        var watch = Stopwatch.StartNew();

        try
        {
            var result = coroutine.Resume();

            while (coroutine.State == CoroutineState.ForceSuspended)
            {
                steps += _limits.SliceInstructions;
                ct.ThrowIfCancellationRequested();

                if (steps > _limits.MaxSteps)
                {
                    return new LuaOutcome(false, LuaError.Budget,
                        $"скрипт съел {_limits.MaxSteps} инструкций и снят — похоже на вечный цикл",
                        DynValue.Nil, steps);
                }

                if (watch.Elapsed > _limits.MaxWall)
                {
                    return new LuaOutcome(false, LuaError.Budget,
                        $"скрипт работает дольше {_limits.MaxWall.TotalMinutes:0} мин и снят",
                        DynValue.Nil, steps);
                }

                result = coroutine.Resume();
            }

            return new LuaOutcome(true, null, null, result, steps);
        }
        catch (SyntaxErrorException e)
        {
            return new LuaOutcome(false, LuaError.Syntax, e.DecoratedMessage, DynValue.Nil, steps);
        }
        catch (ScriptRuntimeException e)
        {
            return new LuaOutcome(false, LuaError.Runtime, e.DecoratedMessage, DynValue.Nil, steps);
        }
    }
}
