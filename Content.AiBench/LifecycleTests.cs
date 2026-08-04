using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Tools;
using Content.Shared.Chat;
using Content.Shared.Doors.Components;
using Content.Shared.Mobs;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Lifecycle and parity scenarios: what happens when the AI is carded, killed, or asked to hear
/// something it has no business hearing.
///
/// Deterministic — the model is scripted — because these are about the code's contract with the
/// world, not about the model's judgement.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class LifecycleTests
{
    [Test]
    public async Task CardedAgent_RefusesDevices_ButKeepsTalking()
    {
        // A carded AI still hears Binary and can still speak; only the station equipment goes out
        // of reach. Getting this wrong in either direction is a bug — a mute intellicard is as
        // wrong as one that can still bolt doors.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = AgentMode.Carded);

        var device = await w.Invoke("device_action", $"{{\"handle\":\"{handle}\",\"action\":\"open\"}}");
        var radio = await w.Invoke("radio", "{\"channel\":\"Binary\",\"text\":\"я в интелликарте\"}");

        Assert.Multiple(() =>
        {
            Assert.That(device.Ok, Is.False, "из интелликарты оборудование недоступно");
            Assert.That(device.Error, Is.EqualTo(ToolError.Carded), device.ToJson());
            Assert.That(radio.Ok, Is.True, "но говорить по Binary карта не мешает: " + radio.ToJson());
        });
    }

    [Test]
    public async Task HearingParity_SpeechFarFromTheCoreIsNotHeard()
    {
        // Strict vanilla parity. The AI has no camera microphones: the only two
        // ExpandICChatRecipients handlers upstream are the surveillance mic (which needs a monitor
        // viewer, not the AI) and the holopad projection path. Speech out of earshot of the
        // physical core simply never reaches it, and the agent must not be handed it either.
        await using var w = await AiWorld.Create();

        // Stop the agent loop but keep the session alive: the perception handlers go on filling
        // the queue, and nothing races us to drain it. Benchmarks tick at one second, so without
        // this the loop consumes the observation before the assertion ever sees it.
        await w.Post(() => w.System.GetSession(w.Brain)!.Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);

        var near = await w.Spawn("MobHuman", dx: 2);
        var far = await w.Spawn("MobHuman", dx: 60, dy: 60);

        await w.Post(() =>
        {
            var chat = w.Pair.Server.System<Content.Server.Chat.Systems.ChatSystem>();

            chat.TrySendInGameICMessage(near, "рядом с ядром", InGameICChatType.Speak,
                ChatTransmitRange.Normal, hideLog: true, shell: null, player: null,
                nameOverride: null, checkRadioPrefix: false, ignoreActionBlocker: true);

            chat.TrySendInGameICMessage(far, "далеко от ядра", InGameICChatType.Speak,
                ChatTransmitRange.Normal, hideLog: true, shell: null, player: null,
                nameOverride: null, checkRadioPrefix: false, ignoreActionBlocker: true);
        });

        await w.Pair.Server.WaitRunTicks(5);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        // Case-insensitive on purpose: upstream's SanitizeInGameICMessage capitalises the first
        // letter of every line, so "рядом" arrives as "Рядом". Asserting on the literal string
        // fails for a reason that has nothing to do with what is being tested.
        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("ядом с ядром").IgnoreCase,
                "речь у ядра ИИ обязан слышать");
            Assert.That(observation, Does.Not.Contain("алеко от ядра").IgnoreCase,
                "речь вдали от ядра слышать НЕ должен — через камеры он не слышит");
        });
    }

    [Test]
    public async Task DeadAgent_CannotAct_AndTheWorldIsUntouched()
    {
        // The round can end or the core can be destroyed while a model call is in flight. The
        // generation counter has to drop the marshalled result rather than apply it to a world
        // that has already moved on.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        await w.Post(() =>
        {
            var mobState = w.Pair.Server.System<Content.Shared.Mobs.Systems.MobStateSystem>();
            mobState.ChangeMobState(w.Brain, MobState.Dead);
        });

        await w.Pair.Server.WaitRunTicks(10);

        var result = await w.Invoke("device_action", $"{{\"handle\":\"{handle}\",\"action\":\"bolt\"}}");

        Assert.That(result.Ok, Is.False, "мёртвый ИИ не должен управлять оборудованием");

        var bolted = await w.Read(() => ent.GetComponent<DoorBoltComponent>(door).BoltsDown);
        Assert.That(bolted, Is.False, "мир не должен был измениться");
    }
}
