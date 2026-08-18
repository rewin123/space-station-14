using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Content.Server.AiAgent.Core.Scripting;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Клетка, в которой исполняется скрипт агента, и мост между Lua и аргументами инструментов.
///
/// Сервер здесь не нужен: проверяется не поведение робота, а то, чем это поведение будет написано.
/// Три вещи стоят отдельного теста, потому что без любой из них режим скриптов держать нельзя:
/// у скрипта не должно быть двери к машине, вечный цикл обязан сниматься, а отказ инструмента
/// обязан ловиться штатным pcall — на этой конвенции держится весь стиль кода, который пишет модель.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class LuaHostTests
{
    private static LuaHost Host(LuaLimits? limits = null, Action<string>? print = null) => new(limits, print);

    // ------------------------------------------------------------------ клетка

    [Test]
    public void Cage_HasNoDoorToTheMachine()
    {
        // Автор скрипта — языковая модель, а не доверенный человек. Отсутствие io/os/require — это
        // не список запретов, которые кто-то проверяет, а отсутствие самих понятий в интерпретаторе.
        var outcome = Host().Run(
            "return tostring(io)..' '..tostring(os)..' '..tostring(require)..' '..tostring(load)" +
            "..' '..tostring(dofile)..' '..type(pcall)", CancellationToken.None);

        Assert.That(outcome.Ok, Is.True, outcome.Detail);
        Assert.That(outcome.Return.String, Is.EqualTo("nil nil nil nil nil function"));
    }

    [Test]
    public void SyntaxError_NamesTheLine()
    {
        // Модель узнаёт о своей опечатке номером строки, а не словом «ошибка». Иначе она правит
        // наугад и тратит ход на каждую попытку.
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
        // Ровно тот случай, из-за которого режим написан на Lua, а не на штатном C#-скриптинге
        // движка: в .NET поток, ушедший в while(true), не снимается ничем.
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
        // bp_stop: отмена обязана дойти до скрипта, который не собирается заканчивать сам.
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
        // Отмена может застать скрипт не между инструкциями, а внутри вызова инструмента, который
        // ждёт мир. Проверено, что она проходит корутину насквозь и не превращается в script_error.
        var host = Host();
        host.Bind("waitForever", _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(
            () => host.Run("waitForever()", CancellationToken.None));
    }

    [Test]
    public void ToolFailure_IsAnExceptionTheScriptCanCatch()
    {
        // Конвенция режима: отказ инструмента — исключение, терпимость к отказу — штатный pcall.
        // Отдельной обёртки вроде must() нет именно потому, что язык это уже умеет.
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
        // Вывод скрипта читает модель через bp_get_output, а не администратор в консоли сервера.
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

    // ------------------------------------------------------------------ мост

    /// <summary>Прогнать аргумент вызова через мост и вернуть JSON, который увидел бы инструмент.</summary>
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
        // Кириллица обязана дожить до инструмента буквами, а не escape-последовательностями:
        // \uXXXX стоит вшестеро больше байт и токенизируется как мусор (грабля форка №13).
        Assert.That(ArgsAsJson("tool{цель='дверь-3', как={чем='лом', сила=2}}"),
            Is.EqualTo("{\"цель\":\"дверь-3\",\"как\":{\"чем\":\"лом\",\"сила\":2}}"));
    }

    [Test]
    public void Bridge_KeepsWholeNumbersWhole()
    {
        // В Lua всякое число — double. Наивная запись дала бы {"count":3.0}, и схема инструмента,
        // ждущая целое, отказала бы с bad_args по вине моста, а не скрипта.
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
        // {1,2,name='x'} массивом уехать не может — половина данных потерялась бы молча.
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
        // Обход циклической таблицы не вернулся бы никогда, а поток в этот момент держит вызов
        // инструмента. Потолок вложенности — единственная защита, и она обязана быть ошибкой
        // скрипта, а не зависанием агента.
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
        // Обратная сторона: результат инструмента модель читает как таблицу, а не как строку JSON,
        // иначе каждый скрипт начинался бы с разбора текста.
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
