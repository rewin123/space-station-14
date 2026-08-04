using System;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Tools;
using Content.Shared.Silicons.StationAi;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.UnitTesting.Pool;

namespace Content.AiBench;

/// <summary>
/// A minimal station the agent can act on: a grid, an AI core with an LLM-driven brain, and
/// whatever devices a given test spawns beside it.
///
/// Everything is placed within a few tiles of the core on purpose — the core itself carries a
/// <c>StationAiVision</c> seed, so anything next to it is inside the AI's camera coverage and the
/// vision gate passes without needing to build a camera network first. Tests that want to prove
/// the <em>opposite</em> (that distance or a cut wire refuses) move or break things explicitly.
/// </summary>
public sealed class AiWorld : IAsyncDisposable
{
    public TestPair Pair { get; private set; }
    public TestMapData Map { get; private set; }
    public StationAiAgentSystem System { get; private set; }
    public ScriptedLlmClient Llm { get; private set; }

    public EntityUid Core { get; private set; }
    public EntityUid Brain { get; private set; }

    public IEntityManager Ent => Pair.Server.ResolveDependency<IEntityManager>();

    public static async Task<AiWorld> Create(ScriptedLlmClient llm = null)
    {
        var world = new AiWorld { Llm = llm ?? new ScriptedLlmClient() };

        // The factory is a settable static rather than an IoC registration: registering would mean
        // patching an upstream file, and the whole layout of this fork depends on not doing that.
        AiTestHooks.LlmFactory = () => world.Llm;

        world.Pair = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = true,
            Connected = false,
            Dirty = true,
        });

        var server = world.Pair.Server;
        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();

        await server.WaitPost(() =>
        {
            cfg.SetCVar(AiCVars.Enabled, true);
            cfg.SetCVar(AiCVars.AutoClaim, false);   // tests claim explicitly, at a known moment
            cfg.SetCVar(AiCVars.DryRun, false);
        });

        world.Map = await world.Pair.CreateTestMap();
        world.System = server.System<StationAiAgentSystem>();

        var ent = server.ResolveDependency<IEntityManager>();

        // CreateTestMap lays exactly ONE tile, at (0,0). Anything spawned beside it hangs over
        // open space — which reads as "unpowered" and "not visible" and looks exactly like an
        // agent bug until you go and check. Lay a proper floor patch first.
        await world.LayFloor(server, 6);

        await server.WaitPost(() =>
        {
            world.Core = ent.SpawnEntity("PlayerStationAiEmpty", world.Map.GridCoords);

            if (ent.TryGetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(world.Core, out var recv))
                recv.NeedsPower = false;
        });

        // Let the broadphase and the vision seeds settle before anything queries them.
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            world.Brain = world.System.ClaimForTest(world.Core)
                          ?? throw new InvalidOperationException("не удалось занять ядро ИИ");
        });

        await server.WaitRunTicks(5);
        return world;
    }

    /// <summary>Lay a square of plating so spawned devices stand on actual floor.</summary>
    private async Task LayFloor(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server, int radius)
    {
        var maps = server.System<Robust.Shared.GameObjects.SharedMapSystem>();
        var defMan = server.ResolveDependency<Robust.Shared.Map.ITileDefinitionManager>();
        var tileId = defMan["Plating"].TileId;

        await server.WaitPost(() =>
        {
            for (var x = -radius; x <= radius; x++)
            for (var y = -radius; y <= radius; y++)
            {
                maps.SetTile(Map.Grid.Owner, Map.Grid.Comp,
                    new Robust.Shared.Map.EntityCoordinates(Map.Grid.Owner, x, y),
                    new Robust.Shared.Map.Tile(tileId));
            }
        });

        await server.WaitRunTicks(3);
    }

    /// <summary>
    /// Spawn an entity a few tiles from the core, powered.
    ///
    /// Power is forced with <c>NeedsPower = false</c> rather than by building an APC and a cable
    /// network: the same shortcut upstream's own GravityGridTest uses, and the power grid is not
    /// what these tests are about.
    /// </summary>
    public async Task<EntityUid> Spawn(string prototype, int dx = 2, int dy = 0, bool powered = true)
    {
        EntityUid uid = default;
        var ent = Ent;

        await Pair.Server.WaitPost(() =>
        {
            uid = ent.SpawnEntity(prototype, Map.GridCoords.Offset(new System.Numerics.Vector2(dx, dy)));

            if (powered && ent.TryGetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(uid, out var recv))
                recv.NeedsPower = false;
        });

        await Pair.Server.WaitRunTicks(3);
        return uid;
    }

    /// <summary>Mint a handle so a test can address an entity without going through look first.</summary>
    public async Task<string> Handle(EntityUid uid)
    {
        var handle = string.Empty;
        await Pair.Server.WaitPost(() => handle = System.HandleFor(Brain, uid));
        return handle;
    }

    /// <summary>
    /// Invoke a tool and wait for it, ticking the server meanwhile.
    ///
    /// The ticking is not optional: tool bodies are posted to the main thread and only run when
    /// the game loop pumps its pending-task queue, so awaiting the task without running ticks
    /// would deadlock every single time.
    /// </summary>
    public async Task<ToolResult> Invoke(string tool, string argsJson = "{}")
    {
        var task = System.InvokeToolForTest(Brain, tool, argsJson);
        await PoolManager.WaitUntil(Pair.Server, () => task.IsCompleted, maxTicks: 600);

        if (!task.IsCompleted)
            Assert.Fail($"инструмент {tool} не завершился за 600 тиков");

        return await task;
    }

    /// <summary>Read something off the world on the main thread.</summary>
    public async Task<T> Read<T>(Func<T> fn)
    {
        var value = default(T);
        await Pair.Server.WaitPost(() => value = fn());
        return value;
    }

    public async Task Post(Action act) => await Pair.Server.WaitPost(act);

    public async ValueTask DisposeAsync()
    {
        AiTestHooks.LlmFactory = null;

        if (Pair != null)
        {
            await Pair.Server.WaitPost(() => System?.ReleaseAll("bench teardown"));
            await Pair.CleanReturnAsync();
        }
    }
}
