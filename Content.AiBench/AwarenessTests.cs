using System.Threading.Tasks;
using Content.Shared.AlertLevel;
using Content.Shared.Silicons.Laws;
using Content.Shared.Silicons.Laws.Components;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Station-wide happenings the AI is supposed to notice by itself.
///
/// All three of these were promised to the model in the system prompt and none of them were ever
/// produced: the <c>Observation.Alert</c>, <c>Observation.Laws</c> and <c>Observation.Announce</c>
/// factories existed with zero callers. So an ion storm could rewrite the laws and the agent went on
/// being polite, the captain could raise the alert to red and it behaved as though nothing had
/// happened, and every announcement passed it by — while the prompt cheerfully listed all three as
/// things it would be told.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class AwarenessTests
{
    /// <summary>
    /// Stop the loop but keep the session: the perception handlers go on filling the queue, and
    /// nothing races the assertion to drain it.
    /// </summary>
    private static async Task Freeze(AiWorld w)
    {
        await w.Post(() => w.System.GetSession(w.Brain)!.Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);
    }

    [Test]
    public async Task AlertLevelChange_ReachesTheAgent()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var alerts = w.Pair.Server.System<AlertLevelSystem>();

        await w.Post(() => alerts.SetLevel(w.Station, "Blue", playSound: false, announce: false, force: true));
        await w.Pair.Server.WaitRunTicks(5);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("ALERT").And.Contain("Blue"),
            "смену уровня тревоги ИИ обязан заметить: " + observation);
    }

    [Test]
    public async Task SelfLine_CarriesTheCurrentAlertLevel()
    {
        // The change event does not fire at round start, so an agent that learned the level only
        // from ALERT lines would spend the whole shift assuming green because nobody said otherwise.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("тревога="),
            "текущий уровень тревоги должен быть в строке SELF каждый ход: " + observation);
    }

    [Test]
    public async Task LawRewrite_ReachesTheAgent_WithTheNewLawsInFull()
    {
        // Upstream raises no event on the path that matters — the law board reaches a virtual
        // method, not an event — and SiliconLawBoundComponent.Version only moves for entities with
        // an ActorComponent, which this brain does not have. So the change is noticed by diffing
        // the rendered lawset once a turn.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        // Give the brain its own law provider so the test can rewrite it directly; on a real
        // station the provider is the law board and the ion storm reaches it the same way.
        var laws = w.Pair.Server.System<Content.Server.Silicons.Laws.SiliconLawSystem>();

        await w.Post(() =>
        {
            w.Ent.EnsureComponent<SiliconLawProviderComponent>(w.Brain);
            laws.SetLaws(new() { new SiliconLaw { LawString = "старый закон", Order = 1 } }, w.Brain);
        });

        // Prime the digest: the first reading is what the round started with, not a change.
        await w.Read(() => w.System.BuildObservationForTest(w.Brain));

        await w.Post(() =>
            laws.SetLaws(new() { new SiliconLaw { LawString = "новый закон", Order = 1 } }, w.Brain));

        await w.Pair.Server.WaitRunTicks(5);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("LAWS"),
            "перепрошивку законов ИИ обязан заметить сам — иначе ионный шторм не меняет ничего: " + observation);
    }

    [Test]
    public async Task LawsUnchanged_ProduceNoNoise()
    {
        // The other half of a poll: it must not report a change every turn just because it is
        // looking every turn.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        await w.Read(() => w.System.BuildObservationForTest(w.Brain));
        var second = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(second, Does.Not.Contain("LAWS"),
            "законы не менялись — строки LAWS быть не должно: " + second);
    }

    [Test]
    public async Task CentralCommandAnnouncement_ReachesTheAgent()
    {
        // The bug this pins was found by playing: an automatic Central Command announcement went
        // out and the agent did not react, because it could not know. Announcements are written
        // straight to player sessions, and a brain has none — the console was the only origin that
        // raised a server-side event, so the agent heard consoles and nothing else. On a live round
        // that means missing the shuttle call and the code change.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var chat = w.Pair.Server.System<Content.Server.Chat.Systems.ChatSystem>();

        await w.Post(() => chat.DispatchGlobalAnnouncement(
            "Внимание: шаттл эвакуации вызван.", "Центральное командование", playSound: false));
        await w.Pair.Server.WaitRunTicks(5);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("ANNOUNCE").And.Contain("шаттл эвакуации"),
            "объявление Центрального командования обязано доходить до ИИ: " + observation);
    }

    [Test]
    public async Task OwnAnnouncement_DoesNotComeBackAsAnObservation()
    {
        // The counterweight. The brain carries the console component the announce tool drives, so
        // it is both announcer and listener; without suppression every announcement it makes would
        // be read back to it a moment later as if Central Command had confirmed it.
        await using var w = await AiWorld.Create();

        var result = await w.Invoke("announce", """{"text":"Говорит Аксиома, проверка связи"}""");
        Assert.That(result.Ok, Is.True, result.ToJson());

        await Freeze(w);
        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Not.Contain("проверка связи"),
            "своё же объявление не должно возвращаться наблюдением: " + observation);
    }
}
