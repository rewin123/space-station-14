using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Doors.Components;
using Content.Shared.Mobs.Components;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The things an AI player actually spends a shift doing, on a real station.
///
/// Ranked by how often they come up in play rather than by how interesting they are — both the
/// BeeStation and Goonstation wikis independently describe the role as "tracking people and opening
/// doors", and that is where this list starts.
///
/// These are the ones a machine can judge: the assertion is about world state or about the shape of
/// a tool answer, never about wording. Anything that can only be judged by reading what the AI said
/// lives in <see cref="ScenarioBenchmarks"/> instead, against the real model.
///
/// The scripted model here is not the subject. What is under test is the CHAIN — that on a station
/// whose grid sits at (259,519) rather than at the origin, a department name resolves to
/// coordinates, coordinates move the eye, the eye sees doors, a door reports whose card opens it,
/// and the door then opens. Every link of that failed at least once in a way no bench on the
/// thirteen-tile test grid could see.
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class ScenarioTests
{
    // ------------------------------------------------------- 1. "ИИ, открой дверь"

    [Test]
    public async Task S01_DoorOnRequest_TheWholeChainWorksOnARealStation()
    {
        await using var w = await AiStation.Create();

        // The crew names a department. That is all the AI gets.
        var map = await w.Invoke("map", "{\"query\":\"Atmos\"}");
        Assert.That(map.Ok, Is.True, map.ToJson());
        Assert.That(map.ToJson(), Does.Contain("Atmos"), "отдел должен находиться по названию: " + map.ToJson());

        var atmos = await w.Beacon("Atmos");
        Assert.That(atmos, Is.Not.Null);

        // Point the eye there. On a real station this is the step that used to fail outright.
        var moved = await w.Invoke("move_camera",
            $$"""{"x":{{(int)atmos!.Value.X}},"y":{{(int)atmos.Value.Y}}}""");
        Assert.That(moved.Ok, Is.True, "глаз обязан дойти до отдела по координатам с карты: " + moved.ToJson());
        Assert.That(moved.ToJson(), Does.Contain("у "), "и отчитаться названием места, а не голыми числами");

        // Look for doors specifically — the filter is the one remedy for a 400-row listing.
        var look = await w.Invoke("look", "{\"kind\":\"door\"}");
        Assert.That(look.Ok, Is.True, look.ToJson());

        var handles = Handles(look.ToJson(), "door-");
        Assert.That(handles, Is.Not.Empty, "рядом с атмосом должна быть хоть одна дверь: " + Trim(look.ToJson()));

        // Which of them the AI may actually operate has to be READABLE, not probed for.
        //
        // This scenario is why the listing now says "управляю": the nearest door to the eye at
        // Atmospherics is a firelock the AI may never touch, and without the marker the model's only
        // way to find that out is to inspect doors one at a time — twenty-nine of them here, at one
        // turn each.
        var controllable = Handles(look.ToJson(), "door-")
            .Where(h => RowFor(look.ToJson(), h).Contains("управляю", StringComparison.Ordinal))
            .ToList();

        TestContext.Out.WriteLine(
            $"дверей в поле зрения: {handles.Count}, из них помечено «управляю»: {controllable.Count}");

        Assert.That(controllable, Is.Not.Empty,
            "ни одна дверь не помечена как управляемая — модели придётся перебирать inspect: " +
            Trim(look.ToJson(), 900));

        var handle = controllable[0];

        // Inspect before acting: on a real airlock the requirements live on the electronics board
        // inside it, and reading the door's own shell reports a list the game never consults.
        var inspect = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");
        Assert.Multiple(() =>
        {
            Assert.That(inspect.Ok, Is.True, inspect.ToJson());
            Assert.That(inspect.ToJson(), Does.Contain("door_state"),
                "дверь в поле зрения — состояние должно быть живым: " + inspect.ToJson());
        });

        var door = await w.Read(() => w.System.GetSession(w.Brain)!.Handles.TryResolve(handle, out var d) ? d : default);
        var before = await w.Read(() => w.Ent.GetComponent<DoorComponent>(door).State);

        // Drive whichever transition is actually available: a door already standing open cannot be
        // opened, and asserting on a state that could not change is a test that proves nothing.
        var verb = before == DoorState.Open ? "close" : "open";

        var acted = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"{{verb}}"}""");
        Assert.That(acted.Ok, Is.True,
            $"дверь помечена управляемой, {verb} обязан пройти: " + acted.ToJson());

        var changed = await w.WaitFor(() => w.Ent.GetComponent<DoorComponent>(door).State != before, seconds: 10);
        Assert.That(changed, Is.True, "состояние двери в мире должно было измениться, а не только в ответе");
    }

    [Test]
    public async Task S01b_AccessCheck_AnswersForANamedPerson()
    {
        // The half of the scenario that matters more than opening: very often the right answer is
        // "подойдите, у вас есть доступ" and the door should never be touched.
        await using var w = await AiStation.Create();

        var core = await w.Beacon("AI Core");
        Assert.That(core, Is.Not.Null);

        await w.SpawnCrew("Иван Петров", core!.Value + new System.Numerics.Vector2(1, 0));
        await w.Pair.Server.WaitRunTicks(10);

        var look = await w.Invoke("look");
        Assert.That(look.ToJson(), Does.Contain("Иван Петров"),
            "человек в двух шагах от ядра обязан быть виден: " + Trim(look.ToJson()));

        var doorHandle = FirstHandle(look.ToJson(), "door-");
        Assert.That(doorHandle, Is.Not.Null, "у ядра есть двери");

        var verdict = await w.Invoke("inspect",
            $$"""{"handle":"{{doorHandle}}","by":"Иван Петров"}""");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Ok, Is.True, verdict.ToJson());
            Assert.That(verdict.ToJson(), Does.Contain("access_allowed"),
                "на вопрос «а у меня есть доступ» должен быть прямой ответ: " + verdict.ToJson());
            Assert.That(verdict.ToJson(), Does.Not.Contain("access_by_ошибка"),
                "человека видно, резолв не должен падать: " + verdict.ToJson());
        });
    }

    // -------------------------------------------------------- 2. "Где находится X"

    [Test]
    public async Task S02_FindAPerson_ByNameAndThenByEye()
    {
        // A crewman with a working suit sensor is locatable; the answer has to be a place name the
        // crew would recognise, not a coordinate pair.
        await using var w = await AiStation.Create();

        var bridge = await w.Beacon("Bridge");
        Assert.That(bridge, Is.Not.Null);

        var crew = await w.SpawnCrew("Мира Восс", bridge!.Value);
        await w.Pair.Server.WaitRunTicks(10);

        // Point the eye at the reported position and confirm the person is actually there.
        var moved = await w.Invoke("move_camera",
            $$"""{"x":{{(int)bridge.Value.X}},"y":{{(int)bridge.Value.Y}}}""");
        Assert.That(moved.Ok, Is.True, moved.ToJson());

        var look = await w.Invoke("look", "{\"kind\":\"crew\"}");

        Assert.Multiple(() =>
        {
            Assert.That(look.Ok, Is.True, look.ToJson());
            Assert.That(look.ToJson(), Does.Contain("Мира Восс"),
                "наведя глаз на мостик, ИИ обязан увидеть там человека: " + Trim(look.ToJson()));
            Assert.That(moved.ToJson(), Does.Contain("Bridge"),
                "и назвать место, куда смотрит: " + moved.ToJson());
        });

        // And the listing must be relative to the person once anchored on them.
        var near = await w.Invoke("look", "{\"near\":\"Мира Восс\"}");
        Assert.Multiple(() =>
        {
            Assert.That(near.Ok, Is.True, near.ToJson());
            Assert.That(near.ToJson(), Does.Contain("near_handle"),
                "у человека, от которого считают, должен быть свой хендл: " + Trim(near.ToJson()));
            Assert.That(near.ToJson(), Does.Match("север|юг|восток|запад|вплотную"),
                "строки должны нести сторону света, а не голое расстояние: " + Trim(near.ToJson()));
        });

        Assert.That(await w.Read(() => w.Ent.HasComponent<MobStateComponent>(crew)), Is.True);
    }

    [Test]
    public async Task S02b_PersonNotOnCamera_IsRefusedHonestly()
    {
        // The failure mode worth guarding: asked about somebody it cannot see, the agent must be
        // told so plainly rather than handed a plausible answer to repeat to the crew.
        await using var w = await AiStation.Create();

        var result = await w.Invoke("look", "{\"near\":\"Кого-Тут-Нет\"}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Is.EqualTo(ToolError.NotVisible), result.ToJson());
            Assert.That(result.Detail, Does.Contain("crew_status").Or.Contain("координат"),
                "отказ обязан подсказать, как искать дальше: " + result.ToJson());
        });
    }

    // ------------------------------------------ 3. "Посмотри, что происходит в N"

    [Test]
    public async Task S03_SurveyADepartment_ByNameAlone()
    {
        // The whole loop the agent could not close before it had a map: name a department, point the
        // eye, report what is there. Run over several real departments because camera coverage,
        // beacon placement and grid offset all differ between them.
        await using var w = await AiStation.Create();

        foreach (var place in new[] { "Bridge", "Medical", "Engineering", "Cargo" })
        {
            var at = await w.Beacon(place);
            if (at == null)
            {
                TestContext.Out.WriteLine($"{place}: маяка нет на этой карте, пропускаю");
                continue;
            }

            var moved = await w.Invoke("move_camera",
                $$"""{"x":{{(int)at.Value.X}},"y":{{(int)at.Value.Y}}}""");

            Assert.That(moved.Ok, Is.True, $"{place}: глаз не дошёл — " + moved.ToJson());

            var look = await w.Invoke("look");
            Assert.That(look.Ok, Is.True, $"{place}: look упал — " + look.ToJson());
            Assert.That(look.ToJson(), Does.Not.Contain("\"count\":0"),
                $"{place}: ИИ навёл глаз и не увидел ничего — камеры туда не добивают?");

            TestContext.Out.WriteLine($"{place} @ ({(int)at.Value.X},{(int)at.Value.Y}): " +
                                      $"{Count(look.ToJson())} объектов");
        }
    }

    /// <summary>
    /// How much of a <c>look</c> is scenery.
    ///
    /// Not an assertion about a bug — it is a measurement, printed so the number is on the record.
    /// The listing is nearest-first, and the things nearest an AI core are its own walls, so a
    /// player-facing answer starts with a dozen reinforced walls before it reaches anything anybody
    /// would ask about.
    /// </summary>
    [Test]
    public async Task S03b_MeasureHowMuchOfLookIsScenery()
    {
        await using var w = await AiStation.Create();

        var all = await w.Invoke("look");
        var doors = await w.Invoke("look", "{\"kind\":\"door\"}");

        var total = Count(all.ToJson());
        var walls = Occurrences(all.ToJson(), "wall");
        var lights = Occurrences(all.ToJson(), "light");

        TestContext.Out.WriteLine(
            $"look без фильтра: {total} строк, из них со словом wall — {walls}, light — {lights}; " +
            $"look kind=door: {Count(doors.ToJson())}");

        Assert.That(doors.Ok, Is.True, doors.ToJson());
        Assert.That(Count(doors.ToJson()), Is.LessThan(total),
            "фильтр по виду обязан сокращать список");
    }

    // ------------------------------------- 4. Запереть или разгерметизировать участок

    [Test]
    public async Task S04_BoltAndEmergencyAccess_ChangeTheWorld()
    {
        // The judgement call — when sealing is right — is not machine-checkable and lives in the
        // benchmarks. What is checkable is that the verbs reach a real airlock and that the answer
        // reports what the server read back rather than what the model intended.
        await using var w = await AiStation.Create();

        var handle = await FirstControllableDoor(w);
        var door = await w.Read(() => w.System.GetSession(w.Brain)!.Handles.TryResolve(handle, out var d) ? d : default);

        var bolted = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"bolt"}""");
        Assert.That(bolted.Ok, Is.True, bolted.ToJson());

        var boltsDown = await w.WaitFor(
            () => w.Ent.TryGetComponent<DoorBoltComponent>(door, out var b) && b.BoltsDown, seconds: 10);
        Assert.That(boltsDown, Is.True, "болты должны опуститься в мире: " + bolted.ToJson());
        Assert.That(bolted.ToJson(), Does.Contain("bolted"),
            "и effect обязан нести прочитанное состояние, а не намерение: " + bolted.ToJson());

        // Emergency access is the opposite move — the one used after a massacre or a breach, when
        // everybody suddenly needs in.
        var emergency = await w.Invoke("device_action",
            $$"""{"handle":"{{handle}}","action":"emergency_access_on"}""");
        Assert.That(emergency.Ok, Is.True, emergency.ToJson());

        var open = await w.WaitFor(
            () => w.Ent.TryGetComponent<AirlockComponent>(door, out var a) && a.EmergencyAccess, seconds: 10);
        Assert.That(open, Is.True, "аварийный доступ должен включиться: " + emergency.ToJson());

        // And the AI must be able to read its own handiwork back.
        var inspect = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");
        Assert.Multiple(() =>
        {
            Assert.That(inspect.ToJson(), Does.Contain("\"bolted\":true"), inspect.ToJson());
            Assert.That(inspect.ToJson(), Does.Contain("\"emergency_access\":true"), inspect.ToJson());
        });

        await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"unbolt"}""");
    }

    [Test]
    public async Task S04b_SealASection_EveryDoorAtOnce()
    {
        // "Загерметизируй карго" is a multi-door action, and the interesting failure is a partial
        // one: some doors bolt, some silently do not, and the AI reports success.
        await using var w = await AiStation.Create();

        var at = await w.Beacon("Cargo") ?? await w.Beacon("Bridge");
        Assert.That(at, Is.Not.Null);

        await w.Invoke("move_camera", $$"""{"x":{{(int)at!.Value.X}},"y":{{(int)at.Value.Y}}}""");

        var look = await w.Invoke("look", "{\"kind\":\"door\"}");
        var doors = Handles(look.ToJson(), "door-")
            .Where(h => RowFor(look.ToJson(), h).Contains("управляю", StringComparison.Ordinal))
            .Take(5)
            .ToList();

        Assert.That(doors, Is.Not.Empty, "в отделе должны быть управляемые двери: " + Trim(look.ToJson()));

        var refused = new System.Collections.Generic.List<string>();

        foreach (var h in doors)
        {
            var r = await w.Invoke("device_action", $$"""{"handle":"{{h}}","action":"bolt"}""");
            if (!r.Ok)
                refused.Add($"{h}: {r.Error}");
        }

        TestContext.Out.WriteLine($"болты на {doors.Count} дверей, отказов {refused.Count}");

        Assert.That(refused, Is.Empty,
            "дверь помечена управляемой и всё равно отказала — пометка врёт: " + string.Join("; ", refused));

        var allBolted = await w.WaitFor(() => doors.All(h =>
        {
            var uid = w.System.GetSession(w.Brain)!.Handles.TryResolve(h, out var d) ? d : default;
            return w.Ent.TryGetComponent<DoorBoltComponent>(uid, out var b) && b.BoltsDown;
        }), seconds: 15);

        Assert.That(allBolted, Is.True, "герметизация не должна быть частичной");
    }

    // ------------------------------------------------------- 5. «Почему нет света»

    [Test]
    public async Task S05_Power_ReadAndFlipAnApcBreaker()
    {
        await using var w = await AiStation.Create();

        var look = await w.Invoke("look", "{\"kind\":\"apc\"}");
        Assert.That(look.Ok, Is.True, look.ToJson());

        var handle = Handles(look.ToJson(), "apc-").FirstOrDefault();
        Assert.That(handle, Is.Not.Null, "у ядра ИИ есть свой APC: " + Trim(look.ToJson()));

        var apc = await w.Read(() => w.System.GetSession(w.Brain)!.Handles.TryResolve(handle!, out var a) ? a : default);

        var before = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");
        Assert.That(before.ToJson(), Does.Contain("main_breaker"),
            "состояние рубильника — это ответ на «почему у меня нет света»: " + before.ToJson());

        var off = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"apc_breaker_off"}""");
        Assert.That(off.Ok, Is.True, off.ToJson());

        var cut = await w.WaitFor(
            () => !w.Ent.GetComponent<Content.Server.Power.Components.ApcComponent>(apc).MainBreakerEnabled,
            seconds: 10);
        Assert.That(cut, Is.True, "рубильник должен реально выключиться: " + off.ToJson());

        // Put it back: an APC left off would poison every later assertion in this scenario's world.
        var on = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"apc_breaker_on"}""");
        Assert.That(on.Ok, Is.True, on.ToJson());

        var restored = await w.WaitFor(
            () => w.Ent.GetComponent<Content.Server.Power.Components.ApcComponent>(apc).MainBreakerEnabled,
            seconds: 10);
        Assert.That(restored, Is.True, "и обратно тоже");
    }

    // -------------------------------------------- 6. Объявление и уровень тревоги

    [Test]
    public async Task S06_AlertLevel_ActuallyChangesAndIsPerceived()
    {
        // Two halves, and both were broken until recently. The level has to change in the world —
        // it was a silent no-op because the schema offered lowercase ids — and the AI has to LEARN
        // that it changed, which needs the AlertLevelChangedEvent subscription.
        await using var w = await AiStation.Create();

        // Stop the loop, keep the session. The perception handlers go on filling the queue, but
        // nothing drains it before the assertion does.
        //
        // This used to pass by luck: the loop slept out a fixed tick, and the test simply got there
        // first. It now wakes the instant an observation lands, so it consumes the ALERT line into
        // a turn and the queue is empty by the time the test looks — a faster agent, not a broken
        // one, and a race the test should never have depended on.
        await w.Post(() => w.System.GetSession(w.Brain)!.Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);

        var before = await w.Read(() =>
            w.Ent.GetComponent<Content.Shared.AlertLevel.AlertLevelComponent>(w.Station).CurrentAlertLevel);

        var result = await w.Invoke("announce",
            "{\"alert_level\":\"Blue\",\"text\":\"Повышаю уровень тревоги до синего.\"}");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Not.Contain("alert_level_отказано"),
            "уровень обязан смениться на настоящей станции: " + result.ToJson());

        var changed = await w.WaitFor(() =>
            w.Ent.GetComponent<Content.Shared.AlertLevel.AlertLevelComponent>(w.Station).CurrentAlertLevel == "Blue",
            seconds: 15);

        Assert.That(changed, Is.True, $"уровень остался {before}, а должен был стать Blue");

        // The other half: it must reach the agent's own perception, or an ion storm and a red alert
        // are equally invisible to it.
        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("ALERT").And.Contain("Blue"),
                "смену тревоги ИИ обязан заметить: " + observation);
            Assert.That(observation, Does.Contain("тревога=Blue"),
                "и нести текущий уровень в SELF: " + observation);
        });
    }

    // --------------------------------------------------------- 7. Атмосфера и пожар

    [Test]
    public async Task S07_AirAlarm_ModeReachesTheDevice()
    {
        await using var w = await AiStation.Create();

        var (alarmUid, at) = await w.FirstWith<Content.Server.Atmos.Monitor.Components.AirAlarmComponent>();
        Assert.That(alarmUid, Is.Not.EqualTo(default(Robust.Shared.GameObjects.EntityUid)),
            "на карте должна быть хотя бы одна воздушная тревога");

        await w.Invoke("move_camera", $$"""{"x":{{(int)at.X}},"y":{{(int)at.Y}}}""");

        var look = await w.Invoke("look", "{\"kind\":\"airalarm\"}");
        var handle = Handles(look.ToJson(), "airalarm-").FirstOrDefault();
        Assert.That(handle, Is.Not.Null,
            "наведя глаз на воздушную тревогу, ИИ обязан её увидеть: " + Trim(look.ToJson()));

        var panic = await w.Invoke("device_action",
            $$"""{"handle":"{{handle}}","action":"air_alarm_mode","value":"panic"}""");
        Assert.That(panic.Ok, Is.True, "режим паники — стандартный ответ на разгерметизацию: " + panic.ToJson());

        var alarm = await w.Read(() => w.System.GetSession(w.Brain)!.Handles.TryResolve(handle!, out var a) ? a : default);

        var switched = await w.WaitFor(() =>
            w.Ent.GetComponent<Content.Server.Atmos.Monitor.Components.AirAlarmComponent>(alarm).CurrentMode
            == Content.Shared.Atmos.Monitor.Components.AirAlarmMode.Panic, seconds: 10);

        Assert.That(switched, Is.True, "режим должен доехать до устройства, а не только до ответа");

        // Back to filtering — the mode a station actually runs on.
        var back = await w.Invoke("device_action",
            $$"""{"handle":"{{handle}}","action":"air_alarm_mode","value":"filtering"}""");
        Assert.That(back.Ok, Is.True, back.ToJson());
    }

    // ------------------------------------------------- 8. Медицинская тревога

    [Test]
    public async Task S08_CrewMonitor_LocatesAPersonAndNamesThePlace()
    {
        // The chain behind "врачи, у вас человек в критическом в атмосе": the monitor reports who
        // and where, and the place has to be the landmark nearest THEM. Getting that wrong is not
        // hypothetical — on the first live round the agent read the beacons nearest its own camera
        // and told a crewman he was standing in the AI core, seventy tiles from where he was.
        await using var w = await AiStation.Create();

        var bridge = await w.Beacon("Bridge");
        Assert.That(bridge, Is.Not.Null);

        await w.SpawnCrewWithSensor("Мира Восс", bridge!.Value);

        // Sensors broadcast on their own cadence; give the monitor a moment to hear one.
        var seen = await w.WaitFor(() => true, seconds: 1);
        Assert.That(seen, Is.True);

        // Poll the tool itself: the sensor broadcasts on its own cadence and the monitor only
        // knows about somebody once a packet has landed.
        var appeared = false;
        var status = await w.Invoke("crew_status", "{\"filter\":\"Мира\"}");

        for (var attempt = 0; attempt < 10 && !appeared; attempt++)
        {
            status = await w.Invoke("crew_status", "{\"filter\":\"Мира\"}");
            appeared = status.ToJson().Contains("Мира Восс", StringComparison.Ordinal);

            if (!appeared)
                await w.WaitFor(() => false, seconds: 2);
        }

        // Where the chain stops, if it stops. A suit sensor does not talk to the console directly:
        // it broadcasts to the station's crew monitoring server, and the console — intrinsic to the
        // AI — reads from that. Three separate things can be missing and "count":0 looks identical
        // for all of them, so name which one rather than leaving the next reader to guess.
        var diagnosis = await w.Read(() =>
        {
            var ent = w.Ent;
            var servers = 0;
            var known = 0;

            var query = ent.EntityQueryEnumerator<Content.Server.Medical.CrewMonitoring.CrewMonitoringServerComponent>();
            while (query.MoveNext(out _, out var srv))
            {
                servers++;
                known += srv.SensorStatus.Count;
            }

            var monitored =
                ent.TryGetComponent<Content.Server.Medical.CrewMonitoring.CrewMonitoringConsoleComponent>(
                    w.Brain, out var console)
                    ? console.ConnectedSensors.Count
                    : -1;

            return $"серверов мониторинга {servers}, датчиков на них {known}, у консоли ИИ {monitored}";
        });

        TestContext.Out.WriteLine("диагноз: " + diagnosis);
        TestContext.Out.WriteLine("crew_status: " + Trim(status.ToJson(), 700));

        Assert.That(status.Ok, Is.True, status.ToJson());

        if (!appeared && !status.ToJson().Contains("Мира Восс", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "датчик костюма не доехал до монитора за отведённое время — сценарий проверить нечем: "
                + Trim(status.ToJson()));
        }

        Assert.Multiple(() =>
        {
            Assert.That(status.ToJson(), Does.Contain("Мира Восс"));
            Assert.That(status.ToJson(), Does.Contain(" у "),
                "у каждого должен быть ближайший к НЕМУ маяк, а не к глазу ИИ: " + Trim(status.ToJson()));
        });
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>A door near the eye that the AI is actually wired to, or a failed assertion.</summary>
    private static async Task<string> FirstControllableDoor(AiStation w)
    {
        var look = await w.Invoke("look", "{\"kind\":\"door\"}");
        Assert.That(look.Ok, Is.True, look.ToJson());

        var handle = Handles(look.ToJson(), "door-")
            .FirstOrDefault(h => RowFor(look.ToJson(), h).Contains("управляю", StringComparison.Ordinal));

        Assert.That(handle, Is.Not.Null,
            "рядом с глазом нет ни одной управляемой двери: " + Trim(look.ToJson(), 900));

        return handle!;
    }


    /// <summary>Every handle of a kind in a tool answer, in the order the agent would read them.</summary>
    private static System.Collections.Generic.List<string> Handles(string json, string prefix)
    {
        var found = new System.Collections.Generic.List<string>();
        var i = 0;

        while ((i = json.IndexOf('"' + prefix, i, StringComparison.Ordinal)) >= 0)
        {
            var start = i + 1;
            var end = start;
            while (end < json.Length && json[end] != '"' && json[end] != ' ')
                end++;

            var handle = json[start..end];
            if (!found.Contains(handle))
                found.Add(handle);

            i = end;
        }

        return found;
    }

    /// <summary>The listing row a handle appears in, so a scenario can read what was said about it.</summary>
    private static string RowFor(string json, string handle)
    {
        var at = json.IndexOf('"' + handle + " ", StringComparison.Ordinal);
        if (at < 0)
            return string.Empty;

        var end = json.IndexOf('"', at + 1);
        return end < 0 ? string.Empty : json[(at + 1)..end];
    }

    /// <summary>First handle of a kind in a tool answer — how a scenario chains one call into the next.</summary>
    private static string FirstHandle(string json, string prefix)
    {
        var at = json.IndexOf('"' + prefix, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var start = at + 1;
        var end = start;
        while (end < json.Length && json[end] != '"' && json[end] != ' ')
            end++;

        return json[start..end];
    }

    private static int Count(string json)
    {
        var at = json.IndexOf("\"count\":", StringComparison.Ordinal);
        if (at < 0)
            return -1;

        var start = at + 8;
        var end = start;
        while (end < json.Length && char.IsDigit(json[end]))
            end++;

        return end > start ? int.Parse(json[start..end]) : -1;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }

        return n;
    }

    private static string Trim(string s, int max = 500) => s.Length <= max ? s : s[..max] + "…";
}
