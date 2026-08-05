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

    // ------------------------------------------------------------------- helpers

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
