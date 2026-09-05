using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Content.Server.AiAgent.Core.Scripting;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The sandbox the agent's script runs in, and the bridge between Lua and tool arguments.
///
/// No server is needed here: what's checked isn't the robot's behavior, but the medium that
/// behavior will be written in. Three things deserve a separate test, because the script mode
/// can't hold up without any one of them: the script must have no door to the machine, an
/// infinite loop must be interruptible, and a tool refusal must be catchable with plain pcall —
/// this convention is what the whole style of code the model writes rests on.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class LuaHostTests
{
    private static LuaHost Host(LuaLimits? limits = null, Action<string>? print = null) => new(limits, print);

    // ------------------------------------------------------------------ sandbox

    [Test]
    public void Cage_HasNoDoorToTheMachine()
    {
        // The script's author is a language model, not a trusted human. The absence of
        // io/os/require isn't a list of prohibitions somebody enforces — those concepts simply
        // don't exist in the interpreter.
        var outcome = Host().Run(
            "return tostring(io)..' '..tostring(os)..' '..tostring(require)..' '..tostring(load)" +
            "..' '..tostring(dofile)..' '..type(pcall)", CancellationToken.None);

        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        Assert.That(outcome.Return.String, Is.EqualTo("nil nil nil nil nil function"));
    }

    [Test]
    public void SyntaxError_NamesTheLine()
    {
        // The model learns about its typo from a line number, not the word "error". Otherwise it
        // fixes things at random and burns a turn on every attempt.
        var outcome = Host().Run("local a = 1\nlocal b = ((\n", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Ok, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(LuaError.Syntax));
            Assert.That(outcome.Detail, Does.Contain("3"), "в тексте обязан быть номер строки");
        });
    }

    [Test]
    public void RuntimeError_NamesTheLine()
    {
        var outcome = Host().Run("local a = 1\nlocal b = nil\nreturn b.x\n", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Ok, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(LuaError.Runtime));
            Assert.That(outcome.Detail, Does.Contain("(3,"), "ошибка обязана указывать на строку 3");
        });
    }

    [Test]
    public void EndlessLoop_IsCutOffByTheStepBudget()
    {
        // Exactly the case the mode was written in Lua for instead of the engine's built-in
        // C# scripting: in .NET, a thread that's gone into while(true) can't be stopped by anything.
        var outcome = Host(new LuaLimits { MaxSteps = 100_000, SliceInstructions = 1_000 })
            .Run("local i = 0 while true do i = i + 1 end", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Ok, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(LuaError.Budget));
            Assert.That(outcome.Steps, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Stop_InterruptsAScriptMidLoop()
    {
        // bp_stop: cancellation must reach a script that has no intention of ending on its own.
        var cts = new CancellationTokenSource();
        var host = Host(new LuaLimits { SliceInstructions = 1_000 });
        var slices = 0;
        host.Bind("tick", _ =>
        {
            if (++slices >= 3)
                cts.Cancel();
            return DynValue.Nil;
        });

        Assert.Throws<OperationCanceledException>(
            () => host.Run("while true do tick() end", cts.Token));
    }

    [Test]
    public void Cancellation_FromInsideAToolCall_LeavesTheScript()
    {
        // Cancellation can catch a script not between instructions but inside a tool call that's
        // waiting on the world. Verified here: it propagates through the coroutine cleanly and
        // doesn't turn into a script_error.
        var host = Host();
        host.Bind("waitForever", _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(
            () => host.Run("waitForever()", CancellationToken.None));
    }

    [Test]
    public void ToolFailure_IsAnExceptionTheScriptCanCatch()
    {
        // The mode's convention: a tool refusal is an exception, tolerating a refusal is plain
        // pcall. There's no separate wrapper like must() precisely because the language already
        // does this.
        var host = Host();
        host.Bind("pickup", _ => throw new ScriptRuntimeException("no_access: карта не подходит"));

        var outcome = host.Run(
            "local ok, e = pcall(pickup, {target='ящик-1'}) return tostring(ok)..'|'..tostring(e)",
            CancellationToken.None);

        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        Assert.That(outcome.Return.String, Is.EqualTo("false|no_access: карта не подходит"));
    }

    [Test]
    public void UncaughtToolFailure_StopsTheScriptAtThatLine()
    {
        var host = Host();
        host.Bind("pickup", _ => throw new ScriptRuntimeException("no_access: карта не подходит"));

        var outcome = host.Run("local a = 1\npickup{target='ящик-1'}\nreturn 'сюда не дойдём'",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Ok, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(LuaError.Runtime));
            Assert.That(outcome.Detail, Does.Contain("no_access"));
            Assert.That(outcome.Detail, Does.Contain("(2,"));
        });
    }

    [Test]
    public void Print_GoesToTheProcessBuffer()
    {
        // Script output is read by the model through bp_get_output, not by an admin in the server console.
        var lines = new List<string>();
        var outcome = Host(print: lines.Add).Run("print('панель 3 поставлена')", CancellationToken.None);

        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        Assert.That(lines, Is.EqualTo(new[] { "панель 3 поставлена" }));
    }

    [Test]
    public void Prelude_DeclaresFunctionsTheScriptCanUse()
    {
        var host = Host();
        host.LoadPrelude("function twice(n) return n * 2 end");

        var outcome = host.Run("return twice(21)", CancellationToken.None);

        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        Assert.That(outcome.Return.Number, Is.EqualTo(42));
    }

    // ------------------------------------------------------------------ bridge

    /// <summary>Run a call's argument through the bridge and return the JSON a tool would see.</summary>
    private static string ArgsAsJson(string call)
    {
        string? seen = null;
        var host = Host();
        host.Bind("tool", args =>
        {
            using var document = LuaBridge.ToJson(args.Count > 0 ? args[0] : DynValue.Nil, "tool");
            seen = document.RootElement.GetRawText();
            return DynValue.Nil;
        });

        var outcome = host.Run(call, CancellationToken.None);
        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        return seen!;
    }

    [Test]
    public void Bridge_CarriesCyrillicKeysAndNesting()
    {
        // Cyrillic must reach the tool as literal characters, not escape sequences: \uXXXX costs
        // six times as many bytes and tokenizes as garbage (fork pitfall #13).
        Assert.That(ArgsAsJson("tool{цель='дверь-3', как={чем='лом', сила=2}}"),
            Is.EqualTo("{\"цель\":\"дверь-3\",\"как\":{\"чем\":\"лом\",\"сила\":2}}"));
    }

    [Test]
    public void Bridge_KeepsWholeNumbersWhole()
    {
        // In Lua every number is a double. A naive encoding would produce {"count":3.0}, and a
        // tool schema expecting an integer would refuse with bad_args — the bridge's fault, not the script's.
        Assert.That(ArgsAsJson("tool{count=3, range=1.5}"), Is.EqualTo("{\"count\":3,\"range\":1.5}"));
    }

    [Test]
    public void Bridge_SendsAListAsAList()
    {
        Assert.That(ArgsAsJson("tool{targets={'дверь-1','дверь-2'}}"),
            Is.EqualTo("{\"targets\":[\"дверь-1\",\"дверь-2\"]}"));
    }

    [Test]
    public void Bridge_TurnsAMixedTableIntoAnObject()
    {
        // {1,2,name='x'} can't go out as an array — half the data would be silently lost.
        Assert.That(ArgsAsJson("tool{7, 8, name='x'}"),
            Does.StartWith("{").And.Contains("\"name\":\"x\"").And.Contains("\"1\":7"));
    }

    [Test]
    public void Bridge_CallWithoutArguments_IsAnEmptyObject()
    {
        Assert.That(ArgsAsJson("tool()"), Is.EqualTo("{}"));
    }

    [Test]
    public void Bridge_RefusesATableThatContainsItself()
    {
        // Walking a cyclic table would never return, and the thread is holding a tool call the
        // whole time. A nesting depth cap is the only defense, and it must surface as a script
        // error, not as the agent hanging.
        var host = Host();
        host.Bind("tool", args =>
        {
            using var _ = LuaBridge.ToJson(args[0], "tool");
            return DynValue.Nil;
        });

        var outcome = host.Run("local t = {} t.self = t tool(t)", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Ok, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(LuaError.Runtime));
            Assert.That(outcome.Detail, Does.Contain("вложена глубже"));
        });
    }

    [Test]
    public void Bridge_BringsAToolResultBackAsATable()
    {
        // The other direction: the model reads a tool's result as a table, not as a JSON string,
        // otherwise every script would start with parsing text.
        var host = Host();
        host.Bind("look", _ =>
        {
            using var document = JsonDocument.Parse(
                "{\"ok\":true,\"effect\":{\"видно\":[\"дверь-3\",\"ящик-1\"],\"счёт\":2}}");
            return host.ToLua(document.RootElement);
        });

        var outcome = host.Run(
            "local r = look() return tostring(r.ok)..'|'..r.effect['видно'][2]..'|'..tostring(r.effect['счёт'])",
            CancellationToken.None);

        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        Assert.That(outcome.Return.String, Is.EqualTo("true|ящик-1|2"));
    }
}
