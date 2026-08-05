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

    /// <summary>
    /// Scratch directory for this world's skills and memory.
    ///
    /// Fully qualified because <see cref="System"/> — the agent system property on this class —
    /// shadows the System namespace inside it.
    /// </summary>
    public string DataDir { get; } = global::System.IO.Path.Combine(
        global::System.IO.Path.GetTempPath(), "ss14ai-bench", global::System.IO.Path.GetRandomFileName());

    public IEntityManager Ent => Pair.Server.ResolveDependency<IEntityManager>();

    /// <param name="radius">
    /// Half-width of the plating square. The default is enough for devices beside the core; tests
    /// about reach need floor further out, because an unfloored tile is not a "no camera" refusal
    /// but a "not on the station" one and would hide the thing under test.
    /// </param>
    /// <param name="gridOffset">
    /// Move the whole station away from the world origin before anything is spawned.
    ///
    /// Not a curiosity: a real station is loaded wherever the map places it, and code that confuses
    /// world coordinates with grid coordinates is perfectly correct at (0,0) and completely wrong
    /// anywhere else. A suite that only ever tests at the origin cannot see that class of bug, and
    /// did not — the agent could not open a door one tile from its own eye on a live station while
    /// every benchmark passed.
    /// </param>
    public static Task<AiWorld> Create(ScriptedLlmClient llm = null, int radius = 6, float gridOffset = 0f) =>
        Build(llm ?? new ScriptedLlmClient(), radius, gridOffset);

    /// <summary>
    /// A world driven by an arbitrary model stand-in rather than the scripted one.
    ///
    /// For tests about <em>timing</em> rather than about content — a client that blocks until the
    /// test says so is the only way to catch the loop mid-call, which is the moment teardown bugs
    /// live in.
    /// </summary>
    public static Task<AiWorld> CreateWith(Content.Server.AiAgent.Llm.ILlmClient llm, int radius = 6,
        float gridOffset = 0f) =>
        Build(llm, radius, gridOffset);

    /// <summary>
    /// A world driven by the REAL model, for behavioural benchmarks.
    ///
    /// Leaves <c>AiTestHooks.LlmFactory</c> null so the system builds its own
    /// <c>LlamaClient</c> from the CVars — the same code path a live server takes, endpoint and
    /// sampling included. Turns tick fast because a benchmark waiting eight seconds per turn is a
    /// benchmark nobody runs.
    /// </summary>
    public static Task<AiWorld> CreateLive() => Build(null, 6, 0f);

    private static async Task<AiWorld> Build(Content.Server.AiAgent.Llm.ILlmClient llm, int radius, float gridOffset)
    {
        var world = new AiWorld { Llm = llm as ScriptedLlmClient };

        // The factory is a settable static rather than an IoC registration: registering would mean
        // patching an upstream file, and the whole layout of this fork depends on not doing that.
        AiTestHooks.LlmFactory = llm == null ? null : () => llm;

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

            // Fast turns. Eight seconds of idle per turn would make the behavioural suite take
            // twenty minutes and teach everyone to skip it.
            cfg.SetCVar(AiCVars.TickSeconds, 1f);
            cfg.SetCVar(AiCVars.TickSecondsIdle, 2f);

            // Agent files go to a scratch directory so a benchmark never writes into the live
            // agent's memory or skill library.
            cfg.SetCVar(AiCVars.DataDir, world.DataDir);
            cfg.SetCVar(AiCVars.CuratorEnabled, false);
        });

        world.Map = await world.Pair.CreateTestMap();
        world.System = server.System<StationAiAgentSystem>();

        // Before anything is laid or spawned: everything below is placed in grid-local coordinates,
        // so shifting the grid now shifts the whole scenario with it and only the world/local
        // distinction changes.
        if (gridOffset != 0f)
        {
            var xforms = server.System<Robust.Shared.GameObjects.SharedTransformSystem>();
            await server.WaitPost(() =>
                xforms.SetWorldPosition(world.Map.Grid.Owner, new System.Numerics.Vector2(gridOffset, gridOffset)));
            await server.WaitRunTicks(3);
        }

        // The pool hands back a server a previous scenario already used, and the agent system
        // caches its model client. Without this reset a live scenario inherits the scripted client
        // the previous scenario installed and silently never acts.
        await server.WaitPost(() =>
        {
            world.System.ResetLlmClient();
            world.System.ReloadAgentFiles();
        });

        var ent = server.ResolveDependency<IEntityManager>();

        // CreateTestMap lays exactly ONE tile, at (0,0). Anything spawned beside it hangs over
        // open space — which reads as "unpowered" and "not visible" and looks exactly like an
        // agent bug until you go and check. Lay a proper floor patch first.
        await world.LayFloor(server, radius);

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

    /// <summary>
    /// Send a radio transmission from a throwaway crewman and wait for the world to change.
    ///
    /// Goes through the real <c>RadioSystem</c>, so it exercises the same path a player's voice
    /// takes. Binary on purpose: it is longRange and needs no telecom server, whereas Common would
    /// silently go nowhere on a bare test grid and the failure would look like an agent bug.
    /// </summary>
    public async Task<bool> SayToAiAndWait(string text, Func<bool> untilWorldSays, int seconds = 90)
    {
        await Pair.Server.WaitPost(() => System.InjectRadio("Binary", text, out _));

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var done = false;
            await Pair.Server.WaitPost(() => done = untilWorldSays());
            if (done)
                return true;

            await Pair.Server.WaitRunTicks(15);
        }

        return false;
    }

    /// <summary>Everything the agent said out loud or over radio, for asserting that it replied at all.</summary>
    public async Task<int> SpeechCount()
    {
        var count = 0;
        await Pair.Server.WaitPost(() => count = System.GetSession(Brain)?.Turns ?? 0);
        return count;
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
            // A leftover temp directory is not worth failing a benchmark over.
        }

        if (Pair != null)
        {
            await Pair.Server.WaitPost(() => System?.ReleaseAll("bench teardown"));
            await Pair.CleanReturnAsync();
        }
    }
}
