using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Perception;
using NUnit.Framework;
using Content.IntegrationTests;
using Robust.Shared.Configuration;

namespace Content.AiBench;

/// <summary>
/// Будильники агента: три инструмента, которыми он назначает себе следующий ход.
///
/// Проверяется не «инструмент вернул ok», а то, ради чего он заведён: сработавший таймер обязан
/// доехать до наблюдения тем же путём, что и речь экипажа, и разбудить петлю. Тул, отчитавшийся
/// об успехе, пока агент продолжает спать, — это ровно тот баг, который здесь ловится.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class TimerTests
{
    // ------------------------------------------------------------------ хранилище

    private static TimerStore Store() => new();

    [Test]
    public void Set_WithTheSameName_Reschedules_RatherThanAddingATwin()
    {
        // «Напомни ещё через десять минут» — самая частая правка собственного плана. Второй таймер
        // с тем же именем означал бы два срабатывания на одно дело и нечем снять нужный.
        var store = Store();
        var now = TimeSpan.FromMinutes(10);

        store.Set("обход", "проверить атмос", TimeSpan.FromMinutes(5), null, now, max: 8);
        var second = store.Set("Обход", "проверить атмос", TimeSpan.FromMinutes(9), null, now, max: 8);

        Assert.Multiple(() =>
        {
            Assert.That(second.Ok, Is.True);
            Assert.That(second.Replaced, Is.True, "перестановка обязана называться перестановкой");
            Assert.That(store.Count, Is.EqualTo(1), "имя сравнивается без регистра");
            Assert.That(store.All()[0].DueAt, Is.EqualTo(TimeSpan.FromMinutes(19)));
        });
    }

    [Test]
    public void Set_RefusesPastTheCeiling_ButStillLetsExistingOnesMove()
    {
        var store = Store();
        var now = TimeSpan.Zero;

        for (var i = 0; i < 3; i++)
            store.Set($"т{i}", "текст", TimeSpan.FromMinutes(5), null, now, max: 3);

        var extra = store.Set("четвёртый", "текст", TimeSpan.FromMinutes(5), null, now, max: 3);
        var moved = store.Set("т1", "текст", TimeSpan.FromMinutes(7), null, now, max: 3);

        Assert.Multiple(() =>
        {
            Assert.That(extra.Ok, Is.False, "потолок обязан отказывать новому");
            Assert.That(moved.Ok, Is.True, "но переставить уже заведённый потолок мешать не должен");
            Assert.That(store.Count, Is.EqualTo(3));
        });
    }

    [Test]
    public void TakeDue_FiresOnce_AndForgetsAOneShot()
    {
        var store = Store();
        store.Set("обход", "проверить атмос", TimeSpan.FromMinutes(5), null, TimeSpan.Zero, max: 8);

        var early = store.TakeDue(TimeSpan.FromMinutes(4));
        var onTime = store.TakeDue(TimeSpan.FromMinutes(5));
        var after = store.TakeDue(TimeSpan.FromMinutes(6));

        Assert.Multiple(() =>
        {
            Assert.That(early, Is.Empty, "до срока срабатывать нечему");
            Assert.That(onTime.Count, Is.EqualTo(1));
            Assert.That(after, Is.Empty, "одноразовый обязан исчезнуть после срабатывания");
            Assert.That(store.Count, Is.Zero);
        });
    }

    [Test]
    public void TakeDue_RearmsARepeatFromTheMomentItFired_NotFromItsOldDeadline()
    {
        // Иначе таймер, проспавший паузу сервера, отстрелялся бы столько раз, сколько интервалов
        // уместилось в простой, — и агент получил бы пачку одинаковых напоминаний об одном деле.
        var store = Store();
        store.Set("обход", "проверить атмос", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
            TimeSpan.Zero, max: 8);

        var fired = store.TakeDue(TimeSpan.FromMinutes(60));

        Assert.Multiple(() =>
        {
            Assert.That(fired.Count, Is.EqualTo(1), "за час простоя — одно срабатывание, а не двенадцать");
            Assert.That(store.All()[0].DueAt, Is.EqualTo(TimeSpan.FromMinutes(65)),
                "следующий круг считается от момента срабатывания");
        });
    }

    [Test]
    public void TakeDue_ReturnsSeveralAtOnce_InDeadlineOrder()
    {
        var store = Store();
        store.Set("поздний", "б", TimeSpan.FromMinutes(4), null, TimeSpan.Zero, max: 8);
        store.Set("ранний", "а", TimeSpan.FromMinutes(2), null, TimeSpan.Zero, max: 8);

        var fired = store.TakeDue(TimeSpan.FromMinutes(5));

        Assert.That(fired.Select(t => t.Name), Is.EqualTo(new[] { "ранний", "поздний" }),
            "порядок фиксирован: одно и то же состояние обязано давать одни и те же байты");
    }

    [Test]
    public void Snapshot_CarriesTimersThroughARestart()
    {
        var state = new AgentState();
        state.Timers.Set("реактор", "проверить давление", TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), max: 8);

        var restored = new AgentState();
        restored.Restore(state.ToSnapshot("hash", roundId: 7));

        var timer = restored.Timers.All().Single();

        Assert.Multiple(() =>
        {
            Assert.That(timer.Name, Is.EqualTo("реактор"));
            Assert.That(timer.Message, Is.EqualTo("проверить давление"));
            Assert.That(timer.DueAt, Is.EqualTo(TimeSpan.FromMinutes(13)));
            Assert.That(timer.Every, Is.EqualTo(TimeSpan.FromMinutes(10)),
                "повторный таймер обязан остаться повторным, иначе он тихо станет одноразовым");
        });
    }

    // ---------------------------------------------------------------- живой сервер

    /// <summary>
    /// Остановить петлю, оставив сессию: обход таймеров живёт в тике, а не в петле, и без этого
    /// петля вычитала бы наблюдение раньше проверки.
    /// </summary>
    private static async Task Freeze(AiWorld w)
    {
        await w.Post(() => w.System.GetSession(w.Brain)!.Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);
    }

    /// <summary>Опустить нижнюю границу срока: ждать боевые полминуты в тестах незачем.</summary>
    private static async Task AllowShortTimers(AiWorld w)
    {
        var cfg = w.Pair.Server.ResolveDependency<IConfigurationManager>();
        await w.Post(() => cfg.SetCVar(AiCVars.TimerMinSeconds, 1));
    }

    [Test]
    public async Task NewTimer_FiresIntoTheObservation_AndWakesTheLoop()
    {
        await using var w = await AiWorld.Create();
        await AllowShortTimers(w);
        await Freeze(w);

        var set = await w.Invoke("new_timer",
            """{"name":"обход","msg":"проверить давление в баре","duration":1}""");

        Assert.That(set.Ok, Is.True, set.ToJson());

        var session = await w.Read(() => w.System.GetSession(w.Brain)!);

        // Пробуждение снимается ДО вычитки: сигнал одноразовый, и наблюдение его не восстановит.
        await PoolManager.WaitUntil(w.Pair.Server, () => session.Queue.Count > 0, maxTicks: 600);

        var woken = session.Woken.CurrentCount;
        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("TIMER").And.Contain("проверить давление в баре"),
                "сработавший таймер обязан приехать в наблюдение наравне с речью экипажа: " + observation);
            Assert.That(observation, Does.Contain("обход"),
                "без имени два таймера неразличимы, и снять нужный нечем: " + observation);
            Assert.That(woken, Is.GreaterThan(0),
                "таймер, не будящий петлю, бесполезен: на тихой станции хода не будет вовсе");
        });
    }

    [Test]
    public async Task PendingTimers_AreVisibleInSelf_SoTheAgentDoesNotSetThemTwice()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var set = await w.Invoke("new_timer",
            """{"name":"реактор","msg":"проверить инжекторы","duration":600}""");
        Assert.That(set.Ok, Is.True, set.ToJson());

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("таймеры=реактор@T+"),
            "заведённые таймеры — состояние, и скрытым оно быть не должно: " + observation);
    }

    [Test]
    public async Task ListTimers_ReportsTheTextAndHowLongIsLeft()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        await w.Invoke("new_timer",
            """{"name":"эвакуация","msg":"напомнить про шаттл","duration":300,"repeat":true}""");

        var list = await w.Invoke("list_timers");
        var json = list.ToJson();

        Assert.Multiple(() =>
        {
            Assert.That(list.Ok, Is.True, json);
            Assert.That(json, Does.Contain("эвакуация").And.Contain("напомнить про шаттл"), json);
            Assert.That(json, Does.Contain("\"повтор_секунд\":300"),
                "повторный таймер обязан быть отличим от одноразового: " + json);
        });
    }

    [Test]
    public async Task DelTimer_RemovesIt_AndTheAgentStopsBeingWoken()
    {
        await using var w = await AiWorld.Create();
        await AllowShortTimers(w);
        await Freeze(w);

        await w.Invoke("new_timer", """{"name":"отменённое","msg":"уже не нужно","duration":2}""");
        var del = await w.Invoke("del_timer", """{"name":"отменённое"}""");

        Assert.That(del.Ok, Is.True, del.ToJson());

        await w.Pair.Server.WaitRunTicks(180);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Not.Contain("TIMER"),
            "снятый таймер не должен сработать: " + observation);
    }

    [Test]
    public async Task DelTimer_OnAWrongName_SaysWhatIsActuallyThere()
    {
        // Промах по имени — норма; оставить модель гадать второй раз — это потерянный ход.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        await w.Invoke("new_timer", """{"name":"обход","msg":"проверить атмос","duration":600}""");

        var del = await w.Invoke("del_timer", """{"name":"обходы"}""");

        Assert.Multiple(() =>
        {
            Assert.That(del.Ok, Is.False);
            Assert.That(del.ToJson(), Does.Contain("обход"),
                "в отказе обязано быть то, что реально заведено: " + del.ToJson());
        });
    }

    [Test]
    public async Task NewTimer_TooShort_IsRaisedToTheFloor_AndSaysSo()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        // Граница поджимает, а не отказывает: отказ стоил бы второго вызова из отпущенных на ход,
        // а настоящий срок всё равно назван в ответе — скрытого состояния не возникает.
        var set = await w.Invoke("new_timer", """{"name":"частый","msg":"а","duration":1}""");
        var json = set.ToJson();

        Assert.Multiple(() =>
        {
            Assert.That(set.Ok, Is.True, json);
            Assert.That(json, Does.Contain("срок_поправлен"), json);
            Assert.That(json, Does.Contain("\"через_секунд\":30"), json);
        });
    }
}
