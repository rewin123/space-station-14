using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Server.Solar.EntitySystems;
using Content.Shared.Atmos.Monitor;
using Content.Shared.Atmos.Monitor.Components;
using Content.Shared.Atmos.Piping.Trinary.Components;
using Content.Shared.Communications;
using Content.Shared.Solar;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Maths;

namespace Content.AiBench;

/// <summary>
/// The reflected console contract: can the agent read a station console it was never told about,
/// and press a button nobody wrote a handler for it.
///
/// Two of these tests exist to fail when RobustToolbox is rebased. The event table and the
/// interface list are engine internals with no public accessor, so <c>device_ui</c> reaches them by
/// name; a rename upstream would otherwise turn every console in the game into an empty one at
/// runtime, silently, with the agent reporting that consoles have no actions.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class UiContractTests
{
    // ------------------------------------------------------- engine internals still reachable

    private static UiActionIndex IndexFor(AiWorld w) =>
        new(w.Ent, w.Pair.Server.ResolveDependency<ILogManager>().GetSawmill("ai.test"));

    [Test]
    public async Task Index_BindsAgainstTheEngine()
    {
        await using var w = await AiWorld.Create();
        var index = IndexFor(w);

        Assert.That(index.Available, Is.True,
            "не читается EntityEventBus._entEventTables — device_ui перестанет видеть действия консолей");
        Assert.That(index.KeysAvailable, Is.True,
            "не читается UserInterfaceComponent.Interfaces — сообщения консолям пойдут с неверным ключом");
    }

    [Test]
    public async Task Index_FindsRealActionsOnARealConsole()
    {
        await using var w = await AiWorld.Create();
        var console = await w.Spawn("ComputerComms");
        var index = IndexFor(w);

        var keys = await w.Read(() => index.KeysFor(console));
        var actions = await w.Read(() => index.ActionsFor(console));

        Assert.That(keys, Is.Not.Empty, "у консоли связи не нашлось ни одного UI-ключа");

        // Not an exhaustive list on purpose: upstream may add actions, and a test that pins the
        // full set would fail on every unrelated feature. These four are the console's reason to
        // exist, and the shuttle pair is what the silicon rules require the agent to be able to do.
        Assert.That(actions.Keys, Does.Contain("communications_console_announce"), Dump(actions));
        Assert.That(actions.Keys, Does.Contain("communications_console_broadcast"), Dump(actions));
        Assert.That(actions.Keys, Does.Contain("communications_console_call_emergency_shuttle"), Dump(actions));
        Assert.That(actions.Keys, Does.Contain("communications_console_recall_emergency_shuttle"), Dump(actions));

        static string Dump(System.Collections.Generic.IReadOnlyDictionary<string, UiContract.UiAction> a) =>
            "нашлось: " + string.Join(", ", a.Values.Select(v => v.Signature));
    }

    // ------------------------------------------------------------------ describing a contract

    [Test]
    public void Describe_TurnsAMessageIntoACallableSignature()
    {
        var announce = UiContract.Describe(typeof(CommunicationsConsoleAnnounceMessage));
        Assert.That(announce, Is.Not.Null);
        Assert.That(announce!.Name, Is.EqualTo("communications_console_announce"));
        Assert.That(announce.Params, Has.Count.EqualTo(1));
        Assert.That(announce.Signature, Does.Contain("текст"));

        var shuttle = UiContract.Describe(typeof(CommunicationsConsoleCallEmergencyShuttleMessage));
        Assert.That(shuttle!.Params, Is.Empty, "у безаргументного сообщения не должно быть параметров");
        Assert.That(shuttle.Signature, Is.EqualTo("communications_console_call_emergency_shuttle"));
    }

    [Test]
    public void Describe_EnumeratesEnumChoices()
    {
        // The whole reason an enum parameter is usable without documentation: the model is handed
        // the legal values instead of guessing at them.
        var mode = UiContract.Describe(typeof(AirAlarmUpdateAlarmModeMessage));

        Assert.That(mode, Is.Not.Null);
        Assert.That(mode!.Params, Has.Count.EqualTo(1));
        Assert.That(mode.Params[0].Choices, Is.Not.Null.And.Contains(nameof(AirAlarmMode.Panic)));
        Assert.That(mode.Signature, Does.Contain("Panic"));
    }

    // --------------------------------------------- field-based messages (no explicit constructor)

    // The second payload idiom in the game. SolarControlConsoleAdjustMessage has no constructor:
    // the client fills its public fields with an object initializer. Until the contract learned
    // this idiom, the action was listed with no arguments, every argument the model guessed was
    // dropped, and each call sent the default Angle (zero) — the live incident where the agent
    // called solar_control_console_adjust eleven times and the panels never moved.

    [Test]
    public void Describe_ExposesTheFieldsOfACtorlessMessage()
    {
        var action = UiContract.Describe(typeof(SolarControlConsoleAdjustMessage));

        Assert.That(action, Is.Not.Null);
        Assert.That(action!.Name, Is.EqualTo("solar_control_console_adjust"));
        Assert.That(action.Params, Has.Count.EqualTo(2),
            "payload живёт в полях — без них действие снова будет безаргументным: " + action.Signature);
        Assert.That(action.Params[0].Name, Is.EqualTo("rotation"));
        Assert.That(action.Params[1].Name, Is.EqualTo("angular_velocity"));
        Assert.That(action.Signature, Does.Contain("угол (градусы)"),
            "единица измерения обязана быть в сигнатуре: состояние читается в радианах");
        Assert.That(action.Signature, Does.Not.Contain("actor"),
            "сантехника базового класса не должна попадать в параметры");
    }

    [Test]
    public void Describe_KeepsCtorMessagesOnTheirConstructor()
    {
        // A message that has BOTH a constructor and public fields stays on the constructor: it is
        // the author's own statement of the payload, and the fields are its backing storage.
        var message = UiContract.Describe(typeof(GasMixerChangeOutputPressureMessage));

        Assert.That(message, Is.Not.Null);
        Assert.That(message!.Params, Has.Count.EqualTo(1));
        Assert.That(message.Params[0].Name, Is.EqualTo("pressure"));
    }

    [Test]
    public void Build_SetsTheFieldsOfACtorlessMessage()
    {
        var action = UiContract.Describe(typeof(SolarControlConsoleAdjustMessage))!;
        var args = JsonDocument.Parse("""{"rotation":30,"angular_velocity":0.5}""").RootElement;

        var msg = UiContract.Build(action, args, out var error);

        Assert.That(msg, Is.Not.Null, error);
        Assert.That(((SolarControlConsoleAdjustMessage)msg!).Rotation.Degrees, Is.EqualTo(30).Within(0.001), error);
        Assert.That(((SolarControlConsoleAdjustMessage)msg!).AngularVelocity.Degrees, Is.EqualTo(0.5).Within(0.001), error);
    }

    [Test]
    public void Build_RefusesAMissingField_AndNamesIt()
    {
        var action = UiContract.Describe(typeof(SolarControlConsoleAdjustMessage))!;
        var args = JsonDocument.Parse("""{"rotation":30}""").RootElement;

        var msg = UiContract.Build(action, args, out var error);

        Assert.That(msg, Is.Null);
        Assert.That(error, Does.Contain("angular_velocity"),
            "отказ обязан называть недостающее поле, иначе модель снова будет угадывать");
    }

    [Test]
    public void Build_RefusesAWordWhereAnAngleIsExpected()
    {
        var action = UiContract.Describe(typeof(SolarControlConsoleAdjustMessage))!;
        var args = JsonDocument.Parse("""{"rotation":"на солнце","angular_velocity":0}""").RootElement;

        var msg = UiContract.Build(action, args, out var error);

        Assert.That(msg, Is.Null);
        Assert.That(error, Does.Contain("градус"), "в отказе должна читаться единица измерения");
    }

    [Test]
    public void Describe_FlattensStateIntoReadableValues()
    {
        var state = new CommunicationsConsoleInterfaceState(canAnnounce: true, canCall: false);

        var flat = UiContract.Describe(state);

        // Readonly fields, not properties — the eighty-eight state classes are split between the
        // two styles, and reading only one of them would blank out half the consoles in the game.
        Assert.That(flat, Does.ContainKey("CanAnnounce"));
        Assert.That(flat["CanAnnounce"], Is.True);
        Assert.That(flat["CanCall"], Is.False);
        Assert.That(flat, Does.ContainKey("CountdownStarted"));
    }

    // -------------------------------------------------------------------- building a message

    [Test]
    public void Build_ConstructsFromJsonArguments()
    {
        var action = UiContract.Describe(typeof(CommunicationsConsoleAnnounceMessage))!;
        var args = JsonDocument.Parse("""{"message":"Пожар в баре"}""").RootElement;

        var msg = UiContract.Build(action, args, out var error);

        Assert.That(msg, Is.Not.Null, error);
        Assert.That(((CommunicationsConsoleAnnounceMessage)msg!).Message, Is.EqualTo("Пожар в баре"));
    }

    [Test]
    public void Build_ParsesAnEnumByName()
    {
        var action = UiContract.Describe(typeof(AirAlarmUpdateAlarmModeMessage))!;
        var args = JsonDocument.Parse("""{"mode":"Panic"}""").RootElement;

        var msg = UiContract.Build(action, args, out var error);

        Assert.That(msg, Is.Not.Null, error);
        Assert.That(((AirAlarmUpdateAlarmModeMessage)msg!).Mode, Is.EqualTo(AirAlarmMode.Panic));
    }

    [Test]
    public void Build_RefusesAMissingArgument_AndNamesIt()
    {
        var action = UiContract.Describe(typeof(CommunicationsConsoleAnnounceMessage))!;

        var msg = UiContract.Build(action, null, out var error);

        Assert.That(msg, Is.Null);
        Assert.That(error, Does.Contain("message"),
            "отказ обязан называть недостающий аргумент, иначе модель будет угадывать");
    }

    [Test]
    public void Build_RefusesAnUnknownEnumValue_AndListsTheRealOnes()
    {
        var action = UiContract.Describe(typeof(AirAlarmUpdateAlarmModeMessage))!;
        var args = JsonDocument.Parse("""{"mode":"вырубить"}""").RootElement;

        var msg = UiContract.Build(action, args, out var error);

        Assert.That(msg, Is.Null);
        Assert.That(error, Does.Contain(nameof(AirAlarmMode.Panic)),
            "в отказе должны быть перечислены допустимые значения");
    }

    // ------------------------------------------------------------------------- the tool itself

    [Test]
    public async Task DeviceUi_ReadsAConsoleWithoutBeingToldWhatItIs()
    {
        await using var w = await AiWorld.Create();
        var console = await w.Spawn("ComputerComms");
        var handle = await w.Handle(console);

        var result = await w.Invoke("device_ui", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("communications_console_announce"),
            $"device_ui не перечислил действия консоли: {result.ToJson()}");
    }

    [Test]
    public async Task DeviceUi_RejectsAnInventedAction_AndSuggestsRealOnes()
    {
        await using var w = await AiWorld.Create();
        var console = await w.Spawn("ComputerComms");
        var handle = await w.Handle(console);

        var result = await w.Invoke("device_ui",
            $$"""{"handle":"{{handle}}","action":"взорвать_станцию"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.ToJson(), Does.Contain("communications_console_announce"),
            $"в alternatives должны прийти настоящие действия: {result.ToJson()}");
    }

    [Test]
    public async Task DeviceUi_ReachesTheHolopad_WithoutLeakingItsWirePanel()
    {
        // Two things at once, and the second is why this test exists.
        //
        // The holopad is in the AI whitelist and was unreachable purely because nobody had written
        // a command for it; reflecting the contract exposed it with no holopad-specific code. But
        // the same entity also carries a wire panel, and reflecting that verbatim handed over every
        // wire's colour, letter and cut state — the puzzle, solved, for any device the agent looked
        // at. Both halves are asserted together so neither can be fixed by breaking the other.
        await using var w = await AiWorld.Create();
        var pad = await w.Spawn("Holopad");
        var handle = await w.Handle(pad);

        var result = await w.Invoke("device_ui", $$"""{"handle":"{{handle}}"}""");
        var json = result.ToJson();

        Assert.That(result.Ok, Is.True, json);
        Assert.That(json, Does.Contain("holopad_answer_call"), json);
        Assert.That(json, Does.Contain("holopad_activate_projector"), json);

        Assert.That(json, Does.Not.Contain("WiresList"), $"утекла панель проводов: {json}");
        Assert.That(json, Does.Not.Contain("IsCut"), $"утекло состояние проводов: {json}");
        Assert.That(json, Does.Not.Contain("WireSeed"), $"утёк seed проводов: {json}");
    }
    [Test]
    public async Task DeviceUi_TurnsTheSolarPanelsThroughACtorlessConsole()
    {
        // The live incident, end to end: a player asks the AI to aim the panels at the sun. The
        // read must now show a parameterised signature, and the call with arguments must change
        // the real target the solar system drives the panels to — not just return success.
        await using var w = await AiWorld.Create();
        var console = await w.Spawn("ComputerSolarControl");
        var handle = await w.Handle(console);

        var read = await w.Invoke("device_ui", $$"""{"handle":"{{handle}}"}""");
        var json = read.ToJson();

        Assert.That(read.Ok, Is.True, json);
        Assert.That(json, Does.Contain("solar_control_console_adjust(rotation"),
            "действие должно показаться с параметрами: " + json);

        // Not a raw string: the JSON ends in "}}", which a "$$" raw string would read as a hole.
        // The velocity is zero on purpose: a non-zero target keeps advancing the rotation while
        // the test is running, and a moving target cannot be asserted exactly. The velocity value
        // itself is covered by the unit test above.
        var result = await w.Invoke("device_ui",
            "{\"handle\":\"" + handle + "\",\"action\":\"solar_control_console_adjust\","
            + "\"args\":{\"rotation\":45,\"angular_velocity\":0}}");

        Assert.That(result.Ok, Is.True, result.ToJson());

        var target = await w.Read(() => w.Pair.Server.System<PowerSolarSystem>().TargetPanelRotation);
        Assert.That(target.Degrees, Is.EqualTo(45).Within(0.001),
            $"панели не повернулись: цель {target.Degrees}°, ждали 45°");
    }

    [Test]
    public async Task DeviceUi_ReadsAConsoleWhoseStateLivesInAComponent()
    {
        // The atmospheric monitoring console, reported from a live round as "the AI could not get
        // its properties". It is read-only, so it has no BUI messages at all, and its readings are
        // networked through AtmosMonitoringConsoleComponent rather than pushed as a state object —
        // so reflecting only the state object returned an empty shell: no actions, no readings,
        // nothing the agent could tell the crew.
        await using var w = await AiWorld.Create();
        var console = await w.Spawn("ComputerAtmosMonitoring");
        var handle = await w.Handle(console);

        var result = await w.Invoke("device_ui", $$"""{"handle":"{{handle}}"}""");
        var json = result.ToJson();

        Assert.That(result.Ok, Is.True, json);
        Assert.That(json, Does.Contain("AtmosDevices"), $"состояние консоли не прочиталось: {json}");

        // And without the engine's boilerplate, which is true of every component in the game and
        // would bury the few fields that carry meaning.
        Assert.That(json, Does.Not.Contain("LifeStage"), json);
        Assert.That(json, Does.Not.Contain("CreationTick"), json);
    }
}
