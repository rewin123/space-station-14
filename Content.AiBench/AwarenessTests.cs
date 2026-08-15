using System;
using System.Threading.Tasks;
using Content.Shared.AlertLevel;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Silicons.Laws;
using Content.Shared.Silicons.Laws.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

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

    /// <summary>
    /// Поднять спавн так, как это делает GameTicker.
    /// </summary>
    /// <remarks>
    /// <c>lateJoin: false</c> здесь не про смысл сценария, а про соседей по событию: на этом
    /// событии сидят ещё AntagSelectionSystem и ArrivalsSystem, и обе первым делом проверяют
    /// именно этот флаг. С ним false они выходят сразу, и тест остаётся тестом нашего обработчика,
    /// а не всей цепочки поздних заходов. Наш обработчик на LateJoin не смотрит вовсе.
    /// Профиль пустой, но не null: TraitSystem читает у него список черт без проверки.
    /// </remarks>
    private static async Task Spawn(AiWorld w, EntityUid mob, string jobId, bool silent = false)
    {
        await w.Post(() =>
        {
            var ev = new PlayerSpawnCompleteEvent(
                mob, null, jobId, lateJoin: false, silent, joinOrder: 1, w.Station,
                new HumanoidCharacterProfile());

            w.Ent.EventBus.RaiseLocalEvent(mob, ev, true);
        });

        await w.Pair.Server.WaitRunTicks(5);
    }

    [Test]
    public async Task CrewArrival_ReachesTheAgent()
    {
        // Найдено на живом сервере: за 15 августа четыре человека отыграли смену, и агент не узнал
        // ни об одном. В наблюдения попадали только речь, рация и объявления, так что молчаливый
        // игрок не существовал для него вовсе — он вёл смену, обращаясь к пустой станции.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var mob = await w.Spawn("MobHuman");
        await w.Post(() => w.Pair.Server.System<MetaDataSystem>().SetEntityName(mob, "Иван Петров"));

        await Spawn(w, mob, "Passenger");

        // Должность сверяем с прототипом, а не с «Пассажиром» строкой: локаль тестового сервера
        // не обязана быть русской, и захардкоженное слово ловило бы язык, а не проводку.
        //
        // Через Read, потому что LocalizedName идёт в Loc, а тот резолвится через IoC — с потока
        // NUnit его контекста нет, и обращение падает ассертом ещё до проверки.
        var job = await w.Read(() => w.Pair.Server.ResolveDependency<IPrototypeManager>()
            .Index<JobPrototype>("Passenger").LocalizedName);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("ARRIVAL").And.Contain("Иван Петров").And.Contain(job),
            "приход человека на смену агент обязан заметить: " + observation);
    }

    [Test]
    public async Task SilentSpawn_IsNotReported()
    {
        // Тихий спавн — это администратор, спрятавший появление от станции: тем же флагом выше по
        // стеку гасится и объявление о прибытии. Агент, объявляющий такого человека в эфир, сводит
        // на нет всё решение админа.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var mob = await w.Spawn("MobHuman");
        await w.Post(() => w.Pair.Server.System<MetaDataSystem>().SetEntityName(mob, "Иван Петров"));

        await Spawn(w, mob, "Passenger", silent: true);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Not.Contain("ARRIVAL"),
            "тихий спавн станция не замечает, и агент тоже: " + observation);
    }

    /// <summary>
    /// Реплика от одноразового техника; имя говорящего — «Тестовый Техник».
    ///
    /// Binary, а не Common: это тот канал, на котором ядро в этой обвязке гарантированно слушает,
    /// и на нём же построены остальные тесты рации.
    /// </summary>
    private static async Task Say(AiWorld w, string text)
    {
        await w.Post(() =>
        {
            if (!w.System.InjectRadio("Binary", text, out var why))
                throw new InvalidOperationException($"реплику не удалось передать: {why}");
        });

        await w.Pair.Server.WaitRunTicks(5);
    }

    [Test]
    public async Task NoteHint_FollowsTheFirstUtteranceOnlyOnce()
    {
        // Заметки поданы лениво: в системный промпт они не вклеиваются, и без этой строки знакомый
        // человек ничем не отличался бы для агента от нового.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        await w.Post(() => w.System.Notes.Add(
            "Тестовый Техник", "Раньше просил открыть атмос.", "[раунд 1 · 01.01]"));

        await Say(w, "первая реплика");
        var first = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        await Say(w, "вторая реплика");
        var second = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Contain("NOTE").And.Contain("Тестовый Техник"),
                "на первой реплике знакомого агенту обязаны напомнить: " + first);
            Assert.That(second, Does.Not.Contain("NOTE"),
                "напоминание приходит один раз за смену, а не на каждую реплику: " + second);
        });
    }

    [Test]
    public async Task NoteHint_StaysSilentForSomeoneWithoutANote()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        await Say(w, "здравствуйте");
        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Not.Contain("NOTE"),
            "про незнакомого напоминать нечего: " + observation);
    }
}
