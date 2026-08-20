using System;
using System.Threading.Tasks;
using Content.Server.AiAgent.Borg;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Робот в режиме скрипта: то, ради чего режим и написан.
///
/// <para>
/// Замер боевого прогона: 37 ходов, 661 обращение к модели, 680 вызовов инструментов — ровно один
/// круг через LLM на каждое элементарное действие, по 14 секунд и 41k промпт-токенов за «шагни на
/// тайл». Здесь доказывается, что дорога до реактора и работа с пультом умещаются в ОДИН вызов, а
/// не в десяток ходов, — и что <c>go</c> при этом действительно ждёт прибытия, а не отвечает «иду».
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgScriptTests
{
    private static async Task<EntityUid> SpawnAndClaim(AiStation w, string beacon = null)
    {
        var borg = EntityUid.Invalid;

        await w.Pair.Server.WaitPost(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(system.TrySpawnBorg(beacon, out borg, out var placed), Is.True, placed);
            Assert.That(system.TryClaim(borg, out var reason), Is.True, reason);
        });

        await w.Pair.Server.WaitRunTicks(5);
        return borg;
    }

    [Test]
    public async Task BorgInScriptMode_LeavesTheCoreOnItsOwnToolset()
    {
        // Ядро остаётся на классическом наборе в той же смене, где робот уже пишет скрипты, — и
        // это нужно, чтобы было с чем сравнивать.
        //
        // Разделяет их НЕ прототип, а момент старта сессии: и ядро, и робот читают один и тот же
        // `ai.script_mode` при сборке тела и дальше не перечитывают его никогда. Ядро заводится
        // внутри Create, робот — после SetScriptMode(true), отсюда и разница. Стенд поэтому
        // принудительно ставит cvar в false перед стартом (см. AiStation), а не полагается на
        // боевое умолчание: с 20.08.2026 оно true, и без этой опоры тест ловил бы уже скриптовое
        // ядро.
        await using var w = await AiStation.Create();
        await w.SetScriptMode(true);
        var borg = await SpawnAndClaim(w);

        var borgWire = await w.WireOf(borg);
        var coreWire = await w.WireOf(w.Brain);

        Assert.Multiple(() =>
        {
            Assert.That(borgWire, Does.Contain("\"script\""));
            Assert.That(borgWire, Does.Not.Contain("\"goto\""), "в режиме скрипта goto — это raw['goto']");
            Assert.That(borgWire, Does.Not.Contain("goto_wait"), "ждущие версии на провод не уходят");
            Assert.That(borgWire, Does.Not.Contain("walk_status"));
            Assert.That(coreWire, Does.Contain("\"look\""), "ядро осталось на своём наборе");
            Assert.That(coreWire, Does.Not.Contain("\"script\""));
        });
    }

    [Test]
    public async Task Go_ReturnsOnlyAfterTheRobotHasArrived()
    {
        // Разница между go и raw['goto'] — вся суть режима. Мгновенная версия отвечает «иду» и
        // распускает работу на отдельные ходы с ожиданием наблюдения между ними; ждущая возвращает
        // управление уже на месте, и следующая строка скрипта работает руками.
        //
        // Маршрут короткий намеренно. Проверяется семантика ожидания, а не дальность ходьбы:
        // на длинных переходах апстримовая проходимость сама по себе не гарантирована, и тест
        // падал бы на чужой проблеме.
        await using var w = await AiStation.CreateOnMap("Packed");
        await w.SetScriptMode(true);
        await w.SetScriptForeground(120_000);
        var borg = await SpawnAndClaim(w, "AME");
        var ent = w.Ent;

        await w.Pair.Server.WaitRunTicks(60);

        var controller = await w.Read(() =>
        {
            var query = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeControllerComponent>();
            return query.MoveNext(out var uid, out _) ? uid : EntityUid.Invalid;
        });

        Assert.That(controller.IsValid(), Is.True, "на карте нет пульта АМЭ");

        var handle = await w.Read(() => w.System.HandleFor(borg, controller));
        var before = await w.Read(() =>
            (ent.GetComponent<TransformComponent>(borg).LocalPosition
             - ent.GetComponent<TransformComponent>(controller).LocalPosition).Length());

        var result = await w.InvokeSlow(borg, "script",
            $$"""{"code":"local r = go('{{handle}}') return r.effect['итог']"}""",
            seconds: 180);

        var after = await w.Read(() =>
            (ent.GetComponent<TransformComponent>(borg).LocalPosition
             - ent.GetComponent<TransformComponent>(controller).LocalPosition).Length());

        var walking = await w.InvokeOn(borg, "walk_status");

        TestContext.Out.WriteLine($"ХОДЬБА: было {before:F1} тайлов, стало {after:F1}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("дошёл"), result.ToJson());
            Assert.That(before, Is.GreaterThan(2f), "робот стартовал вплотную — ходьбу тест не проверил");
            Assert.That(after, Is.LessThan(2f), $"скрипт вернулся, а робот в {after:F1} тайлах от цели");
            Assert.That(walking.EffectJson(), Does.Not.Contain("идёт"),
                "управление вернулось, пока робот ещё шёл — значит go не дождался");
        });
    }

    [Test]
    public async Task OneScript_WalksToTheReactorAndWorksItsConsole()
    {
        // Тот самый сценарий, который в классическом режиме стоил десятка ходов с ожиданием
        // наблюдения между каждым: дойти через полстанции и прочитать пульт АМЭ.
        await using var w = await AiStation.CreateOnMap("Packed");
        await w.SetScriptMode(true);
        await w.SetScriptForeground(240_000);
        var borg = await SpawnAndClaim(w, "AME");
        var ent = w.Ent;

        await w.Pair.Server.WaitRunTicks(60);

        var controller = await w.Read(() =>
        {
            var query = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeControllerComponent>();
            return query.MoveNext(out var uid, out _) ? uid : EntityUid.Invalid;
        });

        Assert.That(controller.IsValid(), Is.True, "на карте нет пульта АМЭ — сценарий невозможен");

        var handle = await w.Read(() => w.System.HandleFor(borg, controller));
        var startedAt = await w.Read(() =>
            (ent.GetComponent<TransformComponent>(borg).LocalPosition
             - ent.GetComponent<TransformComponent>(controller).LocalPosition).Length());

        var result = await w.InvokeSlow(borg, "script",
            $$"""
              {"code":"go('{{handle}}')\nlocal r = console{target='{{handle}}'}\nprint('пульт прочитан')\nreturn r.ok"}
              """,
            seconds: 240);

        TestContext.Out.WriteLine("СКРИПТ: " + result.ToJson());

        var distance = await w.Read(() =>
            (ent.GetComponent<TransformComponent>(borg).LocalPosition
             - ent.GetComponent<TransformComponent>(controller).LocalPosition).Length());

        TestContext.Out.WriteLine($"ПРОШЁЛ: {startedAt:F1} тайлов до пульта было в начале");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(distance, Is.LessThan(2f), $"робот встал в {distance:F1} тайлах от пульта");
            Assert.That(result.ToJson(), Does.Contain("пульт прочитан"));
            Assert.That(startedAt, Is.GreaterThan(2f), "робот стартовал вплотную к пульту — ходьбу тест не проверил");
        });
    }
}
