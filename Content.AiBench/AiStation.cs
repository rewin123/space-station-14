using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Tools;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.Pinpointer;
using Content.Shared.Silicons.StationAi;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.AiBench;

/// <summary>
/// A real station, not a test grid.
///
/// Everything in <see cref="AiWorld"/> happens on a thirteen-tile square with whatever the scenario
/// spawned beside the core, and that is right for asking whether a tool works. It is wrong for
/// asking whether the AI can do its job, because the job is made of distance, department names,
/// camera gaps and doors that belong to somebody. The bug that made the agent inert on a live
/// station passed every bench for exactly that reason: the test grid sat at the world origin.
///
/// So the scenario suite loads Box — a full map with an empty AI core, 377 surveillance cameras,
/// 53 navigation beacons and real airlocks with real access lists — and drives it the way the live
/// server does: <c>game.map</c>, a real round start, and the agent's own auto-claim. That last part
/// matters: claiming through the same path production takes is the difference between testing the
/// agent and testing the harness.
///
/// It is slow — a full map load per scenario — which is why these live in their own category and
/// not in the run that guards every commit.
/// </summary>
public sealed class AiStation : IAsyncDisposable
{
    /// <summary>
    /// Box. Chosen over Saltern (which has no AI core at all) and over the bigger maps because it
    /// is the smallest one that still has a core, a full beacon set and a real camera network.
    /// </summary>
    public const string MapProto = "Box";

    public TestPair Pair { get; private set; } = default!;
    public StationAiAgentSystem System { get; private set; } = default!;
    public ScriptedLlmClient Llm { get; private set; }

    public EntityUid Core { get; private set; }
    public EntityUid Brain { get; private set; }
    public EntityUid Grid { get; private set; }
    public EntityUid Station { get; private set; }

    public string DataDir { get; } = global::System.IO.Path.Combine(
        global::System.IO.Path.GetTempPath(), "ss14ai-scenario", global::System.IO.Path.GetRandomFileName());

    public IEntityManager Ent => Pair.Server.ResolveDependency<IEntityManager>();

    /// <summary>A station driven by a scripted model — deterministic, for assertions on world state.</summary>
    public static Task<AiStation> Create(ScriptedLlmClient llm = null) =>
        Build(llm ?? new ScriptedLlmClient());

    /// <summary>
    /// A station driven by the REAL model, for behavioural benchmarks.
    ///
    /// Leaves the factory null so the system builds its own <c>LlamaClient</c> from the CVars — the
    /// same code path a live server takes, endpoint and sampling included.
    /// </summary>
    public static Task<AiStation> CreateLive() => Build(null);

    private static async Task<AiStation> Build(Content.Server.AiAgent.Llm.ILlmClient llm)
    {
        var w = new AiStation { Llm = llm as ScriptedLlmClient };
        AiTestHooks.LlmFactory = llm == null ? null : () => llm;

        // Dirty: a loaded station cannot be recycled back into the pool.
        w.Pair = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = true,
            Connected = false,
            Dirty = true,
        });

        var server = w.Pair.Server;
        var cfg = server.ResolveDependency<IConfigurationManager>();

        await server.WaitPost(() =>
        {
            cfg.SetCVar(CCVars.GameDummyTicker, false);
            cfg.SetCVar(CCVars.GameMap, MapProto);
            cfg.SetCVar(CCVars.GameLobbyEnabled, false);

            cfg.SetCVar(AiCVars.Enabled, true);

            // Auto-claim ON, deliberately. The live server claims a core through
            // OnRunLevelChanged when the round starts, and a scenario that claimed it by hand
            // would be testing the harness rather than the thing that runs in production.
            cfg.SetCVar(AiCVars.AutoClaim, true);
            cfg.SetCVar(AiCVars.DryRun, false);

            // Fast turns. Eight seconds of real time per turn would make a ten-scenario suite
            // take twenty minutes of waiting for nothing.
            cfg.SetCVar(AiCVars.TickSeconds, 1f);
            cfg.SetCVar(AiCVars.TickSecondsIdle, 2f);

            cfg.SetCVar(AiCVars.DataDir, w.DataDir);
            cfg.SetCVar(AiCVars.CuratorEnabled, false);
        });

        w.System = server.System<StationAiAgentSystem>();

        await server.WaitPost(() =>
        {
            w.System.ResetLlmClient();
            w.System.ReloadAgentFiles();
        });

        var ticker = server.System<GameTicker>();
        await server.WaitPost(() => ticker.RestartRound());

        // The map load is the slow part; give it real headroom rather than a fixed tick count.
        await PoolManager.WaitUntil(server, () => ticker.RunLevel == GameRunLevel.InRound, maxTicks: 3000);
        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound), "раунд так и не начался");

        // Auto-claim happens on the run-level change, but the core has to exist first and the
        // broadphase has to settle before the vision seeds mean anything.
        await PoolManager.WaitUntil(server, () => w.System.Sessions.Count > 0, maxTicks: 1200);

        var ent = server.ResolveDependency<IEntityManager>();

        await server.WaitPost(() =>
        {
            Assert.That(w.System.Sessions, Is.Not.Empty,
                "агент не занял ядро — на карте нет свободного StationAiCore?");

            w.Brain = w.System.Sessions.Keys.First();
            var stationAi = server.System<SharedStationAiSystem>();

            if (stationAi.TryGetCore(w.Brain, out var core) && core.Comp != null)
                w.Core = core.Owner;

            w.Grid = ent.GetComponent<TransformComponent>(w.Core).GridUid ?? default;
            w.Station = server.System<Content.Shared.Station.SharedStationSystem>()
                .GetOwningStation(w.Brain) ?? default;
        });

        await server.WaitRunTicks(10);
        return w;
    }

    // ------------------------------------------------------------------ the station

    /// <summary>
    /// Map coordinates of a navigation beacon by name — "Bridge", "Atmospherics", "Medical".
    ///
    /// These are the labels the crew uses on the radio and the same ones the agent's own
    /// <c>map</c> tool reports, so a scenario phrased as "открой дверь в атмос" can be set up
    /// against the place the crew would actually mean.
    /// </summary>
    public async Task<Vector2?> Beacon(string name)
    {
        Vector2? found = null;

        await Pair.Server.WaitPost(() =>
        {
            var ent = Ent;
            if (!ent.TryGetComponent<NavMapComponent>(Grid, out var navMap))
                return;

            var xform = Pair.Server.System<SharedTransformSystem>();

            foreach (var beacon in navMap.Beacons.Values)
            {
                if (beacon.Text == null || !beacon.Text.Contains(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                found = xform.ToMapCoordinates(new EntityCoordinates(Grid, beacon.Position)).Position;
                return;
            }
        });

        return found;
    }

    /// <summary>Every beacon label on the station, for a scenario that wants to pick one.</summary>
    public async Task<List<string>> Beacons()
    {
        var names = new List<string>();

        await Pair.Server.WaitPost(() =>
        {
            if (Ent.TryGetComponent<NavMapComponent>(Grid, out var navMap))
                names.AddRange(navMap.Beacons.Values.Where(b => b.Text != null).Select(b => b.Text));
        });

        return names;
    }

    /// <summary>
    /// The nearest entity of a given component type to a point — how a scenario finds "the door
    /// into atmospherics" without hardcoding a coordinate that the next map edit would invalidate.
    /// </summary>
    public async Task<EntityUid> NearestWith<T>(Vector2 at, float maxDistance = 12f) where T : IComponent
    {
        var best = EntityUid.Invalid;

        await Pair.Server.WaitPost(() =>
        {
            var ent = Ent;
            var xform = Pair.Server.System<SharedTransformSystem>();
            var bestDist = maxDistance * maxDistance;

            var query = ent.EntityQueryEnumerator<T, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var x))
            {
                if (x.GridUid != Grid)
                    continue;

                var d = (xform.GetWorldPosition(uid) - at).LengthSquared();
                if (d >= bestDist)
                    continue;

                bestDist = d;
                best = uid;
            }
        });

        return best;
    }

    /// <summary>
    /// The first entity on the station carrying a component, and where it is.
    ///
    /// Scenarios need "an air alarm, any air alarm" without hardcoding a coordinate that the next
    /// map edit would silently invalidate into a test that passes by looking at nothing.
    /// </summary>
    public async Task<(EntityUid Uid, Vector2 At)> FirstWith<T>() where T : IComponent
    {
        var found = (EntityUid.Invalid, Vector2.Zero);

        await Pair.Server.WaitPost(() =>
        {
            var ent = Ent;
            var xform = Pair.Server.System<SharedTransformSystem>();

            var query = ent.EntityQueryEnumerator<T, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var x))
            {
                if (x.GridUid != Grid)
                    continue;

                found = (uid, xform.GetWorldPosition(uid));
                return;
            }
        });

        return found;
    }

    /// <summary>
    /// A crewman the crew monitor can actually see.
    ///
    /// A bare <c>MobHuman</c> wears nothing, and the monitor is fed by suit sensors in jumpsuits —
    /// so without this a scenario about <c>crew_status</c> would be asserting against an empty list
    /// and would pass for the wrong reason. <c>SensorCords</c> is the mode that reports position;
    /// anything below it carries no coordinates, exactly as upstream.
    /// </summary>
    public async Task<EntityUid> SpawnCrewWithSensor(string name, Vector2 at, string job = "Engineer")
    {
        var uid = await SpawnCrew(name, at);

        await Pair.Server.WaitPost(() =>
        {
            var ent = Ent;
            var inventory = Pair.Server.System<Content.Shared.Inventory.InventorySystem>();
            var sensors = Pair.Server.System<Content.Server.Medical.SuitSensors.SuitSensorSystem>();
            var idCards = Pair.Server.System<Content.Shared.Access.Systems.SharedIdCardSystem>();

            var coords = ent.GetComponent<TransformComponent>(uid).Coordinates;

            var uniform = ent.SpawnEntity("ClothingUniformJumpsuitEngineering", coords);
            if (!inventory.TryEquip(uid, uniform, "jumpsuit", force: true))
                return;

            if (ent.TryGetComponent<Content.Shared.Medical.SuitSensors.SuitSensorComponent>(uniform, out var sensor))
                sensors.SetSensor((uniform, sensor), Content.Shared.Medical.SuitSensor.SuitSensorMode.SensorCords, null);

            // An ID card, because the crew monitor reports the name on the CARD, not the entity's.
            //
            // Learned from a scenario that spawned a perfectly good crewman and then could not find
            // him on the monitor: he was there, listed as "Unknown". That is not a bug — a human
            // player sees exactly the same for anyone without ID — but it means a scenario about
            // locating a named person has to give them the thing their name comes from.
            var card = ent.SpawnEntity("PassengerIDCard", coords);
            idCards.TryChangeFullName(card, name);
            idCards.TryChangeJobTitle(card, job);

            if (!inventory.TryEquip(uid, card, "id", force: true))
                TestContext.Out.WriteLine("ID-карта не надета — на мониторе человек будет как Unknown");
        });

        await Pair.Server.WaitRunTicks(5);
        return uid;
    }

    /// <summary>Put a crewman on the station at a point, with a job title and a working suit sensor.</summary>
    public async Task<EntityUid> SpawnCrew(string name, Vector2 at, string prototype = "MobHuman")
    {
        var uid = EntityUid.Invalid;

        await Pair.Server.WaitPost(() =>
        {
            var ent = Ent;
            var coords = new EntityCoordinates(Grid,
                Pair.Server.System<SharedTransformSystem>().ToCoordinates(Grid,
                    new MapCoordinates(at, ent.GetComponent<TransformComponent>(Grid).MapID)).Position);

            uid = ent.SpawnEntity(prototype, coords);
            Pair.Server.System<MetaDataSystem>().SetEntityName(uid, name);
        });

        await Pair.Server.WaitRunTicks(3);
        return uid;
    }

    // ------------------------------------------------------------------- the agent

    /// <summary>Say something to the AI over the radio, exactly as a crewman would.</summary>
    public async Task Radio(string text, string channel = "Common") =>
        await Pair.Server.WaitPost(() => System.InjectRadio(channel, text, out _));

    /// <summary>Invoke a tool through the real dispatcher, ticking so marshalled calls can land.</summary>
    public async Task<ToolResult> Invoke(string tool, string argsJson = "{}")
    {
        var task = System.InvokeToolForTest(Brain, tool, argsJson);
        await PoolManager.WaitUntil(Pair.Server, () => task.IsCompleted, maxTicks: 900);

        if (!task.IsCompleted)
            Assert.Fail($"инструмент {tool} не завершился");

        return await task;
    }

    public async Task<T> Read<T>(Func<T> fn)
    {
        var value = default(T);
        await Pair.Server.WaitPost(() => value = fn());
        return value;
    }

    public async Task Post(Action act) => await Pair.Server.WaitPost(act);

    /// <summary>
    /// Wait, in wall-clock time, for the world to change.
    ///
    /// Not a tick budget: the agent loop sleeps <c>ai.tick_seconds</c> of REAL time between turns
    /// and a test server runs its ticks as fast as it can, so counting ticks would time out before
    /// a single one of those seconds had passed.
    /// </summary>
    public async Task<bool> WaitFor(Func<bool> condition, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < deadline)
        {
            var done = false;
            await Pair.Server.WaitPost(() => done = condition());
            if (done)
                return true;

            await Pair.Server.WaitRunTicks(10);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        AiTestHooks.LlmFactory = null;

        try
        {
            if (global::System.IO.Directory.Exists(DataDir))
                global::System.IO.Directory.Delete(DataDir, true);
        }
        catch
        {
            // A leftover temp directory is not worth failing a scenario over.
        }

        if (Pair == null)
            return;

        // Tolerate a pair that is already gone.
        //
        // When a scenario fails hard enough to take the server down with it, the pool has already
        // disposed the pair — and an exception thrown from here replaces the real failure with
        // "Attempted to return a pair in an invalid state", which says nothing about what broke.
        // Losing the teardown is survivable; losing the diagnosis is not.
        try
        {
            await Pair.Server.WaitPost(() => System?.ReleaseAll("scenario teardown"));
            await Pair.CleanReturnAsync();
        }
        catch (Exception e)
        {
            TestContext.Out.WriteLine($"пара не вернулась в пул ({e.GetType().Name}: {e.Message}) — " +
                                      "смотри настоящую ошибку выше");
        }
    }
}
