using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent.Core.Scripting;
using Content.Server.AiAgent.Tools;
using Content.Shared.Doors.Components;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Режим скрипта против живого сервера: инструменты как функции Lua, фоновые процессы, управление.
///
/// <para>
/// Проверяется не «инструмент вернул ok», а мир и провод. Замер, ради которого режим написан:
/// в боевом прогоне борга на 661 обращение к модели пришлось 680 вызовов инструментов — по одному
/// кругу через LLM на каждое элементарное действие. Здесь доказывается, что один вызов
/// <c>script</c> делает работу, которая раньше стоила бы десятка ходов, и что он при этом не
/// получил ни одной дороги в мир мимо обычных ворот.
/// </para>
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class ScriptToolTests
{
    // ------------------------------------------------------------------ или/или

    [Test]
    public async Task ScriptMode_ReplacesTheToolset_RatherThanExtendingIt()
    {
        // Смешение дало бы модели два способа сделать одно и то же и заставило бы выбирать между
        // ними на каждом ходу. Поэтому проверяются обе стороны: чего в проводе нет и чего в нём есть.
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

    // ------------------------------------------------------------------ работа

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
        // Ровно то, ради чего всё затевалось: цикл по нескольким целям внутри ОДНОГО вызова.
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
        // Дыра, которая стоила запуска реактора: в режиме скрипта схемы инструментов на провод не
        // уходят, и всё, чего не пересказал промпт, для модели перестаёт существовать. Агент
        // десять ходов не мог вставить банку в контроллер, потому что аргумент with_item жил
        // только в схеме. Справка обязана читаться из реестра, иначе она разойдётся с ним на
        // первой же правке инструмента.
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

    // ------------------------------------------------------------------ ошибки

    [Test]
    public async Task Script_WithAnUnknownFunction_IsRefusedBeforeAnythingHappens()
    {
        // Плата за динамический язык, внесённая заранее: опечатка всплывает до первого действия,
        // а не на середине, когда робот уже прошёл полстанции.
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
        // Конвенция режима целиком: отказ — исключение, терпимость — pcall. Без этого теста
        // «отдельной обёртки must() нет» было бы утверждением без доказательства.
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

    // ------------------------------------------------------------------ фон

    /// <summary>
    /// Остановить петлю, оставив сессию и её скрипты.
    ///
    /// Досылка итога живёт в тике, а не в петле, и без этого петля вычитала бы наблюдение раньше
    /// проверки. Скриптов это не касается: их отмена связана с таблицей процессов, а не с петлёй.
    /// </summary>
    private static async Task Freeze(AiWorld w)
    {
        await w.Post(() => w.System.GetSession(w.Brain).Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);
    }

    [Test]
    public async Task Script_LongerThanTheForeground_GoesToBackground_AndWakesTheAgentWhenDone()
    {
        // Ход, висящий полминуты на длинном деле, — это агент, глухой всё это время. Поэтому
        // длинный скрипт отпускают в фон, а итог приезжает наблюдением и будит петлю сам: без
        // пробуждения модели пришлось бы опрашивать bp_get_output, а опрос стоит ровно того
        // обращения к модели, ради экономии которого весь режим и написан.
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
        // Курсор здесь не удобство: без него каждый опрос заново вкладывал бы в диалог весь вывод.
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
        // Тело одно, и два скрипта, оба двигающие его, — это не параллелизм, а драка за ноги.
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
        // Процесс переживает ход, но не сессию: иначе снятый агент продолжал бы двигать телом.
        await using var w = await AiWorld.CreateScripted();
        await w.SetScriptForeground(50);

        await w.Invoke("script", """{"code":"while true do sleep(0.2) end"}""");
        var table = await w.ScriptTable();

        await w.Post(() => w.System.Release(w.Brain, "тест"));
        Assert.That(await w.WaitFor(() => table.Running().Count == 0), Is.True, "скрипты пережили агента");

        Assert.That(table.Running(), Is.Empty, "скрипты обязаны умереть вместе с агентом");
    }
}
