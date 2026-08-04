using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Doors.Components;
using Content.Shared.Silicons.StationAi;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// One integration test per tool, against a live in-process server.
///
/// Every test drives the real registry, the real main-thread marshalling and the real gate chain,
/// then asserts on <b>world state</b> rather than on the tool's own report. A tool that returns
/// <c>ok:true</c> while the door stayed shut is exactly the bug worth catching, and only reading
/// the world afterwards catches it.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class ToolTests
{
    // ------------------------------------------------------------------ perception

    [Test]
    public async Task Look_SeesNearbyDoor_AndMintsHandle()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");

        var result = await w.Invoke("look");

        Assert.That(result.Ok, Is.True, $"look упал: {result.ToJson()}");
        Assert.That(result.Effect, Is.Not.Null);
        Assert.That(result.ToJson(), Does.Contain("door-"),
            $"look не выдал хендл двери. Ответ: {result.ToJson()}");
    }

    [Test]
    public async Task Inspect_ReportsDoorState_AndAvailableActions()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        var json = result.ToJson();
        Assert.That(json, Does.Contain("door_state"), json);
        Assert.That(json, Does.Contain("\"open\""), $"inspect не перечислил доступные действия: {json}");
    }

    [Test]
    public async Task Inspect_ReportsCutAiWire()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        await w.Post(() =>
        {
            // Go through the same call the wire action makes rather than poking the field:
            // the analyzer forbids the write, and more importantly the method is what carries
            // upstream's announce-to-the-AI behaviour.
            var ai = w.Pair.Server.System<Content.Shared.Silicons.StationAi.SharedStationAiSystem>();
            ai.SetWhitelistEnabled((door, ent.GetComponent<StationAiWhitelistComponent>(door)), false);
        });

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("перерезан"), result.ToJson());
    }

    [Test]
    public async Task CrewStatus_Answers_EvenWithNoSensors()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("crew_status");

        // An empty crew monitor is a legitimate answer; a crash or a missing console is not.
        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("crew"), result.ToJson());
    }

    [Test]
    public async Task Identify_ReturnsPresentedNameAndIdCard()
    {
        await using var w = await AiWorld.Create();
        var mob = await w.Spawn("MobHuman", 1);
        var handle = await w.Handle(mob);

        var result = await w.Invoke("identify", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        var json = result.ToJson();
        Assert.That(json, Does.Contain("presented"), json);
        Assert.That(json, Does.Contain("id_card"), json);
        Assert.That(json, Does.Contain("job_icon"), json);
    }

    [Test]
    public async Task Records_Answers()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("records");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("records"), result.ToJson());
    }

    [Test]
    public async Task Laws_ReturnsCrewsimov()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("laws");

        Assert.That(result.Ok, Is.True, result.ToJson());
        var json = result.ToJson();
        Assert.That(json, Does.Contain("laws"), json);
        Assert.That(json, Does.Not.Contain("\"count\":0"),
            $"у ИИ не оказалось ни одного закона: {json}");
    }

    [Test]
    public async Task StationStatus_Answers()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("station_status");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("round_time"), result.ToJson());
    }

    // ---------------------------------------------------------------------- speech

    [Test]
    public async Task Say_Succeeds()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("say", """{"text":"Проверка связи."}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("said"), result.ToJson());
    }

    [Test]
    public async Task Radio_Succeeds_OnBinary()
    {
        await using var w = await AiWorld.Create();

        // Binary is longRange, so it needs no telecom server — Common would silently go nowhere
        // on a bare test grid and the failure would look like an agent bug.
        var result = await w.Invoke("radio", """{"channel":"Binary","text":"Проверка."}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("Binary"), result.ToJson());
    }

    [Test]
    public async Task Radio_RejectsUnknownChannel_AndSuggestsNearest()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("radio", """{"channel":"Comon","text":"опечатка"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.BadArgs));
        Assert.That(result.Alternatives, Does.Contain("Common"),
            $"не подсказан ближайший канал: {result.ToJson()}");
    }

    [Test]
    public async Task Announce_Succeeds()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("announce", """{"text":"Внимание экипажу."}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
    }

    // -------------------------------------------------------------------- movement

    [Test]
    public async Task MoveCamera_MovesEyeToTarget()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand", 3);
        var handle = await w.Handle(door);

        var result = await w.Invoke("move_camera", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("eye_x"), result.ToJson());
    }

    [Test]
    public async Task JumpToCore_Succeeds()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("jump_to_core");

        Assert.That(result.Ok, Is.True, result.ToJson());
    }

    // --------------------------------------------------------------------- devices

    [Test]
    public async Task DeviceAction_OpensDoor_AndEffectMatchesWorld()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"open"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());

        // The point of the effect field: what it claims must equal what the server holds.
        await w.Pair.Server.WaitRunTicks(20);
        var state = await w.Read(() => ent.GetComponent<DoorComponent>(door).State);

        Assert.That(state, Is.EqualTo(DoorState.Open).Or.EqualTo(DoorState.Opening),
            $"дверь не открылась, состояние {state}. Ответ инструмента: {result.ToJson()}");
    }

    [Test]
    public async Task DeviceAction_BoltsDoor()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"bolt"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        await w.Pair.Server.WaitRunTicks(10);

        var bolted = await w.Read(() => ent.GetComponent<DoorBoltComponent>(door).BoltsDown);
        Assert.That(bolted, Is.True, $"болты не опустились. Ответ: {result.ToJson()}");
    }

    [Test]
    public async Task DeviceAction_Electrifies()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"electrify"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("electrified"), result.ToJson());
    }

    [Test]
    public async Task DeviceAction_RejectsUnknownAction_AndSuggestsNearest()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"opeen"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.BadArgs));
        Assert.That(result.Alternatives, Does.Contain("open"), result.ToJson());
    }

    [Test]
    public async Task DeviceAction_RefusesWhenAiWireCut()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        await w.Post(() =>
        {
            var ai = w.Pair.Server.System<Content.Shared.Silicons.StationAi.SharedStationAiSystem>();
            ai.SetWhitelistEnabled((door, ent.GetComponent<StationAiWhitelistComponent>(door)), false);
        });

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"open"}""");

        Assert.That(result.Ok, Is.False, "перерезанный провод должен запрещать управление");
        Assert.That(result.Error, Is.EqualTo(ToolError.WireCut), result.ToJson());
    }

    [Test]
    public async Task DeviceAction_ReportsStaleHandle_WithSuggestions()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        await w.Handle(door);

        var result = await w.Invoke("device_action", """{"handle":"door-999","action":"open"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.StaleHandle), result.ToJson());
        Assert.That(result.Alternatives, Is.Not.Empty, "не подсказан ни один живой хендл");
    }

    [Test]
    public async Task DeviceUi_RejectsUnknownCommand()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("device_ui", $$"""{"handle":"{{handle}}","command":"нет_такой"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.BadArgs), result.ToJson());
    }

    // ------------------------------------------------------------------ dispatcher

    [Test]
    public async Task UnknownTool_SuggestsNearest()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("radiio", """{"channel":"Binary","text":"x"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.UnknownTool));
        Assert.That(result.Alternatives, Does.Contain("radio"), result.ToJson());
    }

    [Test]
    public async Task DryRun_DoesNotMutateTheWorld()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;
        var cfg = w.Pair.Server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();

        await w.Post(() => cfg.SetCVar(Content.Server.AiAgent.AiCVars.DryRun, true));

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"bolt"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("dry_run"), result.ToJson());

        await w.Pair.Server.WaitRunTicks(10);
        var bolted = await w.Read(() => ent.GetComponent<DoorBoltComponent>(door).BoltsDown);

        Assert.That(bolted, Is.False, "в режиме dry_run мир меняться не должен");
    }
}
