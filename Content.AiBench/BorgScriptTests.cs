using System;
using System.Threading.Tasks;
using Content.Server.AiAgent.Borg;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// A borg in script mode: what the mode was written for.
///
/// <para>
/// A measurement from a live run: 37 turns, 661 calls to the model, 680 tool calls — exactly one
/// round trip through the LLM for every elementary action, at 14 seconds and 41k prompt tokens per
/// "step onto a tile". This proves that the walk to the reactor and working the console fit into
/// ONE call, not a dozen turns — and that <c>go</c> genuinely waits for arrival instead of
/// answering "on my way".
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
        // The core stays on the classic toolset in the same shift where the borg is already
        // writing scripts — this is needed so there is something to compare against.
        //
        // What separates them is NOT the prototype but the moment the session starts: both the
        // core and the borg read the same `ai.script_mode` when the body is assembled and never
        // re-read it again afterward. The core is set up inside Create, the borg after
        // SetScriptMode(true), hence the difference. That is why the bench forcibly sets the cvar
        // to false before starting (see AiStation) rather than relying on the production default:
        // as of 20.08.2026 it is true, and without this safeguard the test would already be
        // catching a scripted core.
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
        // The difference between go and raw['goto'] is the whole point of the mode. The instant
        // version answers "on my way" and spreads the work across separate turns with an
        // observation wait between them; the waiting version returns control only once arrived,
        // and the next script line already has hands to work with.
        //
        // The route is deliberately short. What's tested is the wait semantics, not walking
        // distance: on long routes, upstream pathability by itself is not guaranteed, and the test
        // would fail on someone else's problem.
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
        // The very scenario that in classic mode cost a dozen turns with an observation wait
        // between each one: walk across half the station and read the AME console.
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
