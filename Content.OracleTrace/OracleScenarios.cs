using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Content.IntegrationTests;
using Content.IntegrationTests.Fixtures;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.OracleTrace;

/// <summary>
/// Сценарии оракула. Каждый тест — один сценарий: читает traces/&lt;имя&gt;/in.jsonl,
/// исполняет его на оригинале и кладёт рядом cs.jsonl.zst и meta.json.
///
/// Это НЕ проверка игры. Проверка — tracediff, который потом сравнит эту
/// трассу с трассой порта. Здесь только два утверждения: сценарий доехал до
/// конца и трасса не пуста; на дверях добавлена проверка здравости — переход
/// Closed -> Opening -> Open обязан быть в трассе и обязан занимать ровно
/// OpenTimeOne, как его ждёт Content.IntegrationTests/Tests/Doors/AirlockTest.cs.
/// </summary>
[TestFixture]
public sealed class OracleScenarios : GameTest
{
    /// <summary>
    /// Сид на все сценарии. Один на проект, а не по сценарию: значение важно
    /// не само по себе, важно, что оно ЗАФИКСИРОВАНО и записано в meta.json.
    /// </summary>
    public const int Seed = 1337;

    /// <summary>
    /// Fresh + Destructive — пара под каждый сценарий поднимается заново и
    /// после не переиспользуется. Переиспользованная пара тащит за собой
    /// сущности и состояние прошлого сценария, а значит и другую нумерацию
    /// спавнов: трасса перестала бы зависеть только от своего in.jsonl.
    /// </summary>
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Fresh = true,
        Destructive = true,
        ServerSeed = Seed,
        ClientSeed = Seed,
    };

    [Test]
    public Task DoorOpenClose() => RunScenario("door-open-close", checkDoorSanity: true);

    [Test]
    public Task DoorCollideOnClose() => RunScenario("door-collide-on-close", checkDoorSanity: true);

    [Test]
    public Task DoorDestroy() => RunScenario("door-destroy", checkDoorSanity: true);

    [Test]
    public Task ContainerInsertEject() => RunScenario("container-insert-eject", checkDoorSanity: false);

    // Два сценария дописаны владельцем проекта ПОСЛЕ того, как обе стороны уже
    // были готовы и сошлись на первых четырёх. Смысл ровно в этом: исполнитель
    // на TS писался, когда эталонные трассы уже существовали, и совпадение
    // могло оказаться подгонкой. Этих входов он не видел.
    [Test]
    public Task DoorInterrupt() => RunScenario("door-interrupt", checkDoorSanity: false);

    // Прямая проверка предсказания самого исполнителя: он доложил, что порт не
    // перерождает вложенную сущность на владельца и что это всплывёт, как
    // только они окажутся в разных точках. Здесь они в разных.
    [Test]
    public Task ContainerApart() => RunScenario("container-apart", checkDoorSanity: false);

    private async Task RunScenario(string name, bool checkDoorSanity)
    {
        var dir = OraclePaths.ScenarioDir(name);
        var inPath = Path.Combine(dir, "in.jsonl");
        if (!File.Exists(inPath))
            Assert.Fail($"нет сценария {inPath}");

        var scenario = Scenario.Load(name, inPath);

        // Прототипы сценариев лежат ОДНИМ файлом рядом с трассами, а не
        // константой [TestPrototypes] в этой сборке: тот же текст обязан
        // прочитать порт, а до C#-константы он не дотянется.
        var protoPath = Path.Combine(OraclePaths.TraceRoot(), "prototypes.yml");
        if (!File.Exists(protoPath))
            Assert.Fail($"нет файла прототипов {protoPath}");

        await Pair.LoadPrototypes(new List<string> { await File.ReadAllTextAsync(protoPath) });

        var map = await Pair.CreateTestMap();
        await Pair.ReallyBeIdle(5);

        TraceRecorder recorder = null;
        ScenarioRunner runner = null;

        await Server.WaitPost(() =>
        {
            var factory = Server.ResolveDependency<IComponentFactory>();
            recorder = new TraceRecorder(SEntMan, factory, SGameTiming, scenario.Observe);
            runner = new ScenarioRunner(SEntMan, factory, recorder, map.MapId);
            recorder.Arm();
        });

        try
        {
            var pending = new List<ScenarioOp>();

            foreach (var op in scenario.Ops)
            {
                if (op is TickOp tick)
                {
                    for (var i = 0; i < tick.N; i++)
                    {
                        if (i == 0 && pending.Count > 0)
                        {
                            var batch = pending.ToArray();
                            await Server.WaitPost(() =>
                            {
                                foreach (var queued in batch)
                                    runner.Apply(queued);
                            });
                            pending.Clear();
                        }

                        await Server.WaitRunTicks(1);
                        await Server.WaitIdleAsync();
                        await Server.WaitPost(() => recorder.Capture());
                    }

                    continue;
                }

                if (op is ObserveOp)
                    continue;

                pending.Add(op);
            }

            Assert.That(pending, Is.Empty, "операции после последнего tick не попали бы в трассу");
        }
        finally
        {
            await Server.WaitPost(() => recorder.Disarm());
        }

        Assert.That(recorder.Ticks, Is.EqualTo(scenario.TotalTicks),
            "снимков должно быть ровно столько, сколько тиков объявил сценарий");
        Assert.That(recorder.Lines, Is.Not.Empty, "трасса пуста");

        var (sha, dirty) = OraclePaths.OriginRevision();
        var (engineSha, engineDirty) = OraclePaths.EngineRevision();
        var meta = new JsonObject
        {
            ["scenario"] = name,
            ["engine"] = "cs",
            ["originSha"] = sha,
            ["originDirty"] = dirty,
            ["engineSha"] = engineSha,
            ["engineDirty"] = engineDirty,
            ["seed"] = Pair.ServerSeed,
            ["tickrate"] = SGameTiming.TickRate,
            ["ticks"] = recorder.Ticks,
            ["toleranceProfile"] = "tools/tracediff/tolerance.yml",
            ["prototypes"] = "../prototypes.yml",
            ["format"] = "tools/tracediff/types.ts",
        };

        TraceOutput.Write(dir, name, recorder.Lines, meta);

        var zst = new FileInfo(Path.Combine(dir, "cs.jsonl.zst"));
        Assert.That(zst.Exists, Is.True, $"{zst.FullName} не создан");
        Assert.That(zst.Length, Is.GreaterThan(0), $"{zst.FullName} пуст");

        if (checkDoorSanity)
            TraceSanity.AssertDoorOpens(recorder.Lines, SGameTiming.TickRate, name);
    }
}
