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
using Robust.Shared;
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

    /// <summary>
    /// This bench's map. Usually <see cref="MapProto"/>, but a scenario can ask for a different one.
    ///
    /// Needed because compartment-connectivity questions differ across maps: "get from the bar to
    /// the reactor" is not the same question on Box as on Packed, and checking it on a map that
    /// isn't in rotation means checking the wrong thing.
    /// </summary>
    public string Map { get; private set; } = MapProto;

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

    /// <summary>The same bench, but on the named map.</summary>
    public static Task<AiStation> CreateOnMap(string map) =>
        Build(new ScriptedLlmClient(), map);

    /// <summary>
    /// A station driven by the REAL model, for behavioural benchmarks.
    ///
    /// Leaves the factory null so the system builds its own <c>LlamaClient</c> from the CVars — the
    /// same code path a live server takes, endpoint and sampling included.
    /// </summary>
    public static Task<AiStation> CreateLive() => Build(null);

    private static async Task<AiStation> Build(Content.Server.AiAgent.Llm.ILlmClient llm, string map = MapProto)
    {
        var w = new AiStation { Llm = llm as ScriptedLlmClient, Map = map };
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
            cfg.SetCVar(CCVars.GameMap, w.Map);
            cfg.SetCVar(CCVars.GameLobbyEnabled, false);

            // The world must not pause: there are no players in the pool, and `game.auto_pause_empty`
            // freezes the simulation by default in exactly this case — CurTick stops advancing, and
            // the agent, whose loop runs on real time, correspondingly makes zero turns. Loop tests
            // then simply never live to see the model get called.
            cfg.SetCVar(CVars.GameAutoPauseEmpty, false);
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

            // The classic toolset is the BENCH'S BASELINE, not an inheritance from the production
            // default.
            //
            // On 20.08.2026 `ai.script_mode` was turned on by default in production, and
            // `BorgScriptTests` immediately broke: it spins up a core, then enables script mode and
            // checks that the borg is already scripted while the core stayed on its own toolset.
            // With the default turned on, the core would already be scripted before the test's first
            // line ran.
            //
            // The fix belongs here, not in the test: four hundred scenarios are written against the
            // classic toolset, and they should not depend on a production setting that an owner is
            // free to flip with a console command. Whoever needs script mode turns it on themselves
            // via `SetScriptMode(true)`, and then it's visible that the test is about it.
            cfg.SetCVar(AiCVars.ScriptMode, false);

            cfg.SetCVar(AiCVars.DataDir, w.DataDir);
            SeedLibrary(w.DataDir);
            cfg.SetCVar(AiCVars.CuratorEnabled, false);

            // Which model answers, from the environment.
            //
            // A behavioural benchmark is only worth running twice if the second run can be against
            // a different model on the same scenario — that comparison is the whole reason to have
            // scripted stations at all. The key comes from the environment rather than a file so it
            // never lands in the repository by accident.
            //
            //   AI_ENDPOINT=https://api.deepseek.com/v1 AI_MODEL=deepseek-v4-flash \
            //   AI_API_KEY=… AI_MAX_TOKENS=3000 Tools/aibench --live
            Override(cfg, "AI_ENDPOINT", AiCVars.Endpoint);
            Override(cfg, "AI_MODEL", AiCVars.Model);
            Override(cfg, "AI_API_KEY", AiCVars.ApiKey);

            if (int.TryParse(Environment.GetEnvironmentVariable("AI_MAX_TOKENS"), out var maxTokens))
                cfg.SetCVar(AiCVars.MaxTokens, maxTokens);
        });

        // Bring the real SOUL.md along.
        //
        // The agent's data directory points at a scratch folder so a run cannot write into the live
        // agent's memory — but a behavioural benchmark is asking whether the AI behaves the way its
        // soul says it should, and against an empty directory it would be grading the base prompt
        // instead. Memory and skills are deliberately NOT copied: those accumulate, and a benchmark
        // that inherits them is measuring history rather than policy.
        w.CopySoul();

        w.System = server.System<StationAiAgentSystem>();

        await server.WaitPost(() =>
        {
            w.System.ResetLlmClient();
            w.System.ReloadSharedLibrary();
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

    private static void Override(IConfigurationManager cfg, string variable, CVarDef<string> cvar)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
            cfg.SetCVar(cvar, value);
    }

    /// <summary>
    /// The repository root, found by walking up to the solution file.
    ///
    /// Anchored on SpaceStation14.slnx rather than on a directory name: tests run from bin/, which
    /// has a Content.Server folder of its own and would stop the walk one level too early.
    /// </summary>
    public static string RepoRoot()
    {
        var dir = global::System.IO.Directory.GetCurrentDirectory();
        while (dir != null
               && !global::System.IO.File.Exists(global::System.IO.Path.Combine(dir, "SpaceStation14.slnx")))
        {
            dir = global::System.IO.Directory.GetParent(dir)?.FullName;
        }

        return dir;
    }

    /// <summary>
    /// Put the repository's soul files into this run's scratch data directory.
    ///
    /// The whole <c>SOUL*.md</c> family, not just the main file: the "rogue AI" mode has its own
    /// personality per mode (<c>SOUL_ROGUE_HIDDEN.md</c>, <c>SOUL_ROGUE_OPEN.md</c>), and a scenario
    /// that raised that mode's flag would read it from an empty directory — meaning it would be
    /// grading the base prompt while thinking it was grading the mode. On top of that, a missing
    /// mode file is an ERROR in the journal, and the bench treats a logged error as a test failure.
    /// </summary>
    private void CopySoul()
    {
        try
        {
            var root = RepoRoot();
            if (root == null)
                return;

            var dir = global::System.IO.Path.Combine(root, "ai_data");
            if (!global::System.IO.Directory.Exists(dir))
                return;

            global::System.IO.Directory.CreateDirectory(DataDir);

            foreach (var source in global::System.IO.Directory.EnumerateFiles(dir, "SOUL*.md"))
            {
                var name = global::System.IO.Path.GetFileName(source);
                global::System.IO.File.Copy(source, global::System.IO.Path.Combine(DataDir, name), true);
            }
        }
        catch
        {
            // A missing soul means the benchmark grades the base prompt, which is worth knowing but
            // not worth failing the run over.
        }
    }

    // ------------------------------------------------------------------ the station

    /// <summary>
    /// Map coordinates of a navigation beacon by name — "Bridge", "Atmospherics", "Medical".
    ///
    /// These are the labels the crew uses on the radio and the same ones the agent's own
    /// <c>map</c> tool reports, so a scenario phrased as "open the door to atmos" can be set up
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

    /// <summary>Say something to the AI over the radio from an anonymous throwaway voice.</summary>
    public async Task Radio(string text, string channel = "Common") =>
        await Pair.Server.WaitPost(() => System.InjectRadio(channel, text, out _));

    /// <summary>
    /// Say something over the radio AS a particular crewman.
    ///
    /// The difference is not cosmetic, and a benchmark taught it: <see cref="Radio"/> spawns a
    /// throwaway speaker and deletes it, so the voice the AI hears belongs to nobody. Asked to open
    /// a door by that voice, the agent spent fourteen turns hunting for a person who no longer
    /// existed — behaving perfectly reasonably against a question that could not be answered. Any
    /// scenario about judging a request has to let the AI find out who is asking.
    /// </summary>
    public async Task RadioFrom(EntityUid speaker, string text, string channel = "Common")
    {
        await Pair.Server.WaitPost(() =>
        {
            var radio = Pair.Server.System<Content.Server.Radio.EntitySystems.RadioSystem>();
            radio.SendRadioMessage(speaker, text,
                new Robust.Shared.Prototypes.ProtoId<Content.Shared.Radio.RadioChannelPrototype>(channel), speaker);
        });

        await Pair.Server.WaitRunTicks(3);
    }

    /// <summary>Wait until the agent has said more than it had, in wall-clock time.</summary>
    public async Task<bool> WaitForSpeech(int wasAtLeast, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < deadline)
        {
            if (await SpokenCount() > wasAtLeast)
                return true;

            await Pair.Server.WaitRunTicks(20);
        }

        return false;
    }

    /// <summary>
    /// How many times the agent has put words in front of the crew this session.
    ///
    /// Counts tool RESULTS carrying a spoken line, not the model's prose. That distinction is the
    /// whole point of this project: prose is inaudible to the station, and a benchmark that counted
    /// it would call a mute agent talkative. Learned the hard way here too — the first version
    /// counted assistant content, saw two idle musings, and concluded the AI had answered while its
    /// actual refusal sat in a radio call it never looked at.
    /// </summary>
    public async Task<int> SpokenCount() => await Read(() =>
    {
        var session = System.GetSession(Brain);
        return session?.Conv.Body.Count(m =>
            m.Role == "tool" && m.Content != null && m.Content.Contains("\"said\"", StringComparison.Ordinal)) ?? 0;
    });

    /// <summary>Invoke a tool through the real dispatcher, ticking so marshalled calls can land.</summary>
    public Task<ToolResult> Invoke(string tool, string argsJson = "{}") => InvokeOn(Brain, tool, argsJson);

    /// <summary>
    /// The same thing, but on a specific agent.
    ///
    /// Came in together with the second body: <see cref="Invoke"/> always addresses the brain in
    /// the core, and there is no way to check a borg's tool through it at all.
    /// </summary>
    public async Task<ToolResult> InvokeOn(EntityUid agent, string tool, string argsJson = "{}")
    {
        var task = System.InvokeToolForTest(agent, tool, argsJson);
        await PoolManager.WaitUntil(Pair.Server, () => task.IsCompleted, maxTicks: 900);

        if (!task.IsCompleted)
            Assert.Fail($"инструмент {tool} не завершился");

        return await task;
    }

    /// <summary>
    /// A tool that needs REAL time: the script walks, sleeps, and waits on long-running actions.
    ///
    /// <see cref="InvokeOn"/> counts ticks and spins through nine hundred of them in a fraction of a
    /// second on an empty server — for a script that reads as "gave up waiting" in a case where it's
    /// actually working as intended.
    /// </summary>
    public async Task<ToolResult> InvokeSlow(EntityUid agent, string tool, string argsJson = "{}", int seconds = 120)
    {
        var task = System.InvokeToolForTest(agent, tool, argsJson);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Pair.Server.WaitRunTicks(3);
            await Task.Delay(25);
        }

        if (!task.IsCompleted)
            Assert.Fail($"инструмент {tool} не завершился за {seconds} с");

        return await task;
    }

    /// <summary>Switch the next spun-up agent into script mode.</summary>
    public Task SetScriptMode(bool on) => Post(() =>
        Pair.Server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>()
            .SetCVar(AiCVars.ScriptMode, on));

    /// <summary>
    /// The window after which a script moves to the background.
    ///
    /// Tests about waiting deliberately crank this up: otherwise any script longer than a second
    /// moves to the background, and what would have to be checked is the follow-up observation
    /// delivery instead of the thing the test was actually written for.
    /// </summary>
    public Task SetScriptForeground(int ms) => Post(() =>
        Pair.Server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>()
            .SetCVar(AiCVars.ScriptForegroundMs, ms));

    /// <summary>A specific agent's wire — this is where you can see whether modes got mixed up.</summary>
    public Task<string> WireOf(EntityUid agent) => Read(() => System.GetSession(agent).Registry.WireJson());

    public async Task<T> Read<T>(Func<T> fn)
    {
        var value = default(T);
        await Pair.Server.WaitPost(() => value = fn());
        return value;
    }

    public async Task Post(Action act) => await Pair.Server.WaitPost(act);

    /// <summary>
    /// Who this is, where it stands, and what size it is — for a failed test's message.
    ///
    /// "The fast path lost one entity out of 2794" isn't a diagnosis, it's an invitation to guess.
    /// A name, a prototype, and a bounding box extent in tiles turn that same line into a pointer to
    /// where to look.
    /// </summary>
    public async Task<string> Describe(EntityUid uid) => await Read(() =>
    {
        var ent = Ent;
        if (!ent.EntityExists(uid))
            return $"{uid} (уже удалена)";

        var meta = ent.GetComponent<MetaDataComponent>(uid);
        var xform = ent.GetComponent<TransformComponent>(uid);
        var aabb = Pair.Server.System<EntityLookupSystem>().GetWorldAABB(uid);

        return $"{meta.EntityName} [{meta.EntityPrototype?.ID ?? "—"}] " +
               $"поз={xform.Coordinates.Position} рамка={aabb.Width:F1}×{aabb.Height:F1} тайлов " +
               $"якорь={xform.Anchored} родитель={ent.ToPrettyString(xform.ParentUid)}";
    });

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

    /// <summary>
    /// Put a minimal reference library into the working directory.
    ///
    /// <para>
    /// Not cosmetic. <c>/wiki_ru</c> is mounted read-only, and an empty directory under a read mount
    /// is an <c>Error</c> in the journal: "the agent forgot how" would otherwise take days to
    /// diagnose, so the signal is deliberately loud. And the bench fails any test whose server wrote
    /// even a single error. Muting the signal for the sake of green tests would mean disabling the
    /// only alarm for a missing library; instead the bench gets a real reference library, tiny as
    /// it is.
    /// </para>
    /// </summary>
    private static void SeedLibrary(string dataDir)
    {
        var wiki = global::System.IO.Path.Combine(dataDir, "wiki_ru", "атмосфера");
        global::System.IO.Directory.CreateDirectory(wiki);

        global::System.IO.File.WriteAllText(
            global::System.IO.Path.Combine(dataDir, "wiki_ru", "_index.md"),
            "# справочник\nкогда: Вопрос про устройство станции\nОглавление справочника.\n");

        global::System.IO.File.WriteAllText(
            global::System.IO.Path.Combine(wiki, "_index.md"),
            "# атмосфера\nкогда: Газы, трубы, разгерметизация\nОбзор раздела.\n");

        global::System.IO.File.WriteAllText(
            global::System.IO.Path.Combine(wiki, "насосы.md"),
            "# насосы\nкогда: Насосы, вентили, давление в трубах\nGas Volume Pump качает объём.\n");
    }
}
