using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent.Core.Scripting;
using Content.Server.AiAgent.Tools;
using Content.Shared.Doors.Components;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Script mode against a live server: tools as Lua functions, background processes, control.
///
/// <para>
/// What's checked isn't "the tool returned ok", but the world and the wire. The measurement the
/// mode was written for: in a live borg run, 661 model calls came with 680 tool invocations — one
/// round trip through the LLM per elementary action. This proves that a single <c>script</c> call
/// does work that used to cost a dozen turns, and that it doesn't get a single road into the world
/// that bypasses the usual gates.
/// </para>
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class ScriptToolTests
{
    // ------------------------------------------------------------------ either/or

    [Test]
    public async Task ScriptMode_ReplacesTheToolset_RatherThanExtendingIt()
    {
        // Mixing the two would give the model two ways to do the same thing and force it to
        // choose between them on every turn. Hence both sides are checked: what's absent from the
        // wire and what's present on it.
        await using var w = await AiWorld.CreateScripted();
        var wire = await w.Wire();

        Assert.Multiple(() =>
        {
            Assert.That(wire, Does.Contain("\"script\""));
            Assert.That(wire, Does.Contain("bp_get_output"));
            Assert.That(wire, Does.Contain("bp_stop"));
            Assert.That(wire, Does.Contain("\"noop\""), "ход нечем было бы закрыть, кроме прозы");
            Assert.That(wire, Does.Not.Contain("\"look\""), "в режиме скрипта look — функция Lua, а не вызов");
            Assert.That(wire, Does.Not.Contain("device_action"));
        });
    }

    [Test]
    public async Task ClassicMode_HasNoScriptTool()
    {
        await using var w = await AiWorld.Create();
        var wire = await w.Wire();

        Assert.Multiple(() =>
        {
            Assert.That(wire, Does.Contain("\"look\""));
            Assert.That(wire, Does.Not.Contain("\"script\""));
            Assert.That(wire, Does.Not.Contain("bp_stop"));
        });
    }

    // ------------------------------------------------------------------ work

    [Test]
    public async Task Script_ActsOnTheWorld_ThroughTheSameTools()
    {
        await using var w = await AiWorld.CreateScripted();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("script",
            $$"""{"code":"device_action{handle='{{handle}}', action='open'} return 'готово'"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());

        var state = await w.Read(() => w.Ent.GetComponent<DoorComponent>(door).State);
        Assert.That(state, Is.Not.EqualTo(DoorState.Closed), $"дверь не поехала: {result.ToJson()}");
        Assert.That(result.ToJson(), Does.Contain("готово"), "последний return обязан доехать до модели");
    }

    [Test]
    public async Task Script_LoopsWithoutAskingTheModelBetweenSteps()
    {
        // Exactly what all of this was built for: a loop over several targets inside ONE call.
        await using var w = await AiWorld.CreateScripted();
        var first = await w.Spawn("AirlockCommand");
        var second = await w.Spawn("AirlockCommand");
        var one = await w.Handle(first);
        var two = await w.Handle(second);

        var result = await w.Invoke("script",
            $$"""
              {"code":"local n = 0 for _, h in ipairs({'{{one}}','{{two}}'}) do device_action{handle=h, action='open'} n = n + 1 end return n"}
              """);

        Assert.That(result.Ok, Is.True, result.ToJson());

        var states = await w.Read(() => (
            w.Ent.GetComponent<DoorComponent>(first).State,
            w.Ent.GetComponent<DoorComponent>(second).State));

        Assert.Multiple(() =>
        {
            Assert.That(states.Item1, Is.Not.EqualTo(DoorState.Closed));
            Assert.That(states.Item2, Is.Not.EqualTo(DoorState.Closed));
            Assert.That(result.ToJson(), Does.Contain("2"));
        });
    }

    [Test]
    public async Task Script_PrintIsWhatTheModelReadsBack()
    {
        await using var w = await AiWorld.CreateScripted();

        var result = await w.Invoke("script", """{"code":"print('панель 3 поставлена')"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("панель 3 поставлена"));
    }

    [Test]
    public async Task Help_ComesFromTheToolSchemas_NotFromTheProse()
    {
        // The hole that once cost a reactor startup: in script mode, tool schemas don't go out on
        // the wire, and anything the prompt didn't restate stops existing for the model. The agent
        // spent ten turns unable to insert a jar into the controller, because the with_item
        // argument lived only in the schema. Help must be read from the registry, otherwise it
        // will drift out of sync with it on the very next tool edit.
        await using var w = await AiWorld.CreateScripted();

        var listed = await w.Invoke("script", """{"code":"local r = help() for _, l in ipairs(r.effect['функции']) do print(l) end"}""");
        var one = await w.Invoke("script", """{"code":"local r = help{tool='device_action'} print(r.effect['аргументы'])"}""");

        Assert.Multiple(() =>
        {
            Assert.That(listed.Ok, Is.True, listed.ToJson());
            Assert.That(listed.ToJson(), Does.Contain("device_action"), "справка обязана перечислять инструменты тела");
            Assert.That(listed.ToJson(), Does.Not.Contain("bp_stop"), "управление процессами — не функция скрипта");
            Assert.That(one.Ok, Is.True, one.ToJson());
            Assert.That(one.ToJson(), Does.Contain("action"), "схема обязана приехать целиком: " + one.ToJson());
        });
    }

    // ------------------------------------------------------------------ errors

    [Test]
    public async Task Script_WithAnUnknownFunction_IsRefusedBeforeAnythingHappens()
    {
        // The price of a dynamic language, paid up front: a typo surfaces before the first
        // action, not halfway through, once the robot has already crossed half the station.
        await using var w = await AiWorld.CreateScripted();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("script",
            $$"""{"code":"device_action{handle='{{handle}}', action='open'} oepn_door()"}""");

        var state = await w.Read(() => w.Ent.GetComponent<DoorComponent>(door).State);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Is.EqualTo(ToolError.ScriptSyntax));
            Assert.That(result.Detail, Does.Contain("oepn_door"));
            Assert.That(state, Is.EqualTo(DoorState.Closed),
                "линтер обязан отказать ДО запуска: первая строка не должна была выполниться");
        });
    }

    [Test]
    public async Task Script_ThatFailsMidway_SaysWhereAndKeepsWhatItPrinted()
    {
        await using var w = await AiWorld.CreateScripted();

        var result = await w.Invoke("script",
            """{"code":"print('начал')\ninspect{handle='door-999'}\nprint('сюда не дойдём')"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Is.EqualTo(ToolError.ScriptError));
            Assert.That(result.Detail, Does.Contain("(2,"), "номер строки — половина пользы отчёта");
            Assert.That(result.ToJson(), Does.Contain("начал"), "напечатанное до падения не теряется");
            Assert.That(result.ToJson(), Does.Not.Contain("сюда не дойдём"));
        });
    }

    [Test]
    public async Task Script_CanSurviveARefusalWithPcall()
    {
        // The whole convention of the mode: a refusal is an exception, tolerating it is pcall.
        // Without this test, "there's no separate must() wrapper" would be a claim with no proof.
        await using var w = await AiWorld.CreateScripted();

        var result = await w.Invoke("script",
            """{"code":"local ok, e = pcall(inspect, {handle='door-999'})\nprint('пережил: '..tostring(ok))\nreturn 'дошёл до конца'"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("пережил: false"));
            Assert.That(result.ToJson(), Does.Contain("дошёл до конца"));
        });
    }

    // ------------------------------------------------------------------ background

    /// <summary>
    /// Stop the loop while keeping the session and its scripts.
    ///
    /// Delivering the result lives in the tick, not in the loop, and without this the loop would
    /// consume the observation before the check runs. This doesn't affect scripts: their
    /// cancellation is tied to the process table, not to the loop.
    /// </summary>
    private static async Task Freeze(AiWorld w)
    {
        await w.Post(() => w.System.GetSession(w.Brain).Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);
    }

    [Test]
    public async Task Script_LongerThanTheForeground_GoesToBackground_AndWakesTheAgentWhenDone()
    {
        // A turn stuck for half a minute on a long task means an agent deaf for that whole time.
        // So a long script is released into the background, and the result arrives as an
        // observation that wakes the loop by itself: without waking the model, it would have to
        // poll bp_get_output, and polling costs exactly the model call that the whole mode was
        // written to save.
        await using var w = await AiWorld.CreateScripted();
        await w.SetScriptForeground(100);
        await Freeze(w);

        var result = await w.Invoke("script", """{"code":"sleep(1.5) print('досчитал') return 7"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("идёт"), $"скрипт обязан был уйти в фон: {result.ToJson()}");

        var table = await w.ScriptTable();
        var session = await w.Read(() => w.System.GetSession(w.Brain));

        Assert.That(await w.WaitFor(() => table.Running().Count == 0), Is.True, "скрипт не закончился");
        Assert.That(await w.WaitFor(() => session.Queue.Count > 0), Is.True, "итог не доехал до наблюдения");

        var woken = session.Woken.CurrentCount;
        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("СКРИПТ #1").And.Contains("готово"), observation);
            Assert.That(observation, Does.Contain("досчитал"), "хвост вывода едет вместе с итогом: " + observation);
            Assert.That(woken, Is.GreaterThan(0), "скрипт, не будящий петлю, оставил бы агента спать до тика");
        });
    }

    [Test]
    public async Task BpGetOutput_ReturnsOnlyWhatIsNew()
    {
        // The cursor here isn't a convenience: without it, every poll would re-insert the whole
        // output into the conversation.
        await using var w = await AiWorld.CreateScripted();
        await w.SetScriptForeground(50);

        await w.Invoke("script", """{"code":"print('первая') sleep(1.2) print('вторая')"}""");

        var first = await w.Invoke("bp_get_output", """{"pid":1}""");
        Assert.That(first.ToJson(), Does.Contain("первая"), first.ToJson());

        var table = await w.ScriptTable();
        Assert.That(await w.WaitFor(() => table.Running().Count == 0), Is.True, "скрипт не закончился");

        var second = await w.Invoke("bp_get_output", """{"pid":1}""");

        Assert.Multiple(() =>
        {
            Assert.That(second.ToJson(), Does.Contain("вторая"));
            Assert.That(second.ToJson(), Does.Not.Contain("первая"), "прочитанное второй раз не отдаётся");
        });
    }

    [Test]
    public async Task BpStop_StopsAScriptThatWouldNotStopItself()
    {
        await using var w = await AiWorld.CreateScripted();
        await w.SetScriptForeground(50);

        var started = await w.Invoke("script", """{"code":"while true do sleep(0.2) end"}""");
        Assert.That(started.ToJson(), Does.Contain("идёт"), started.ToJson());

        var stopped = await w.Invoke("bp_stop", """{"pid":1}""");
        Assert.That(stopped.Ok, Is.True, stopped.ToJson());

        var table = await w.ScriptTable();
        Assert.That(await w.WaitFor(() => table.Running().Count == 0), Is.True, "скрипт не снялся");

        Assert.That(table.Get(1).Status, Is.EqualTo(ScriptStatus.Stopped));
    }

    [Test]
    public async Task BpStop_OnANonexistentProcess_SaysSoPlainly()
    {
        await using var w = await AiWorld.CreateScripted();

        var result = await w.Invoke("bp_stop", """{"pid":42}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Is.EqualTo(ToolError.NoProcess));
        });
    }

    [Test]
    public async Task SecondScript_IsRefusedWhileTheFirstIsWorking()
    {
        // There's one body, and two scripts both moving it isn't parallelism, it's a fight over
        // the legs.
        await using var w = await AiWorld.CreateScripted();
        await w.SetScriptForeground(50);
        await w.SetScriptProcesses(1);

        await w.Invoke("script", """{"code":"sleep(2)"}""");
        var second = await w.Invoke("script", """{"code":"print('второй')"}""");

        Assert.Multiple(() =>
        {
            Assert.That(second.Ok, Is.False);
            Assert.That(second.Error, Is.EqualTo(ToolError.Refused));
            Assert.That(second.Detail, Does.Contain("bp_stop"), "отказ обязан говорить, что с этим делать");
        });
    }

    [Test]
    public async Task LosingTheAgent_KillsItsScripts()
    {
        // A process outlives a turn, but not the session: otherwise a released agent would keep
        // moving the body.
        await using var w = await AiWorld.CreateScripted();
        await w.SetScriptForeground(50);

        await w.Invoke("script", """{"code":"while true do sleep(0.2) end"}""");
        var table = await w.ScriptTable();

        await w.Post(() => w.System.Release(w.Brain, "тест"));
        Assert.That(await w.WaitFor(() => table.Running().Count == 0), Is.True, "скрипты пережили агента");

        Assert.That(table.Running(), Is.Empty, "скрипты обязаны умереть вместе с агентом");
    }
}
