using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Doors.Components;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The same scenarios, on a station that is not sitting at the world origin.
///
/// This whole fixture exists because of one live failure. Every benchmark passed, and on a real
/// station the agent could not open a door one tile from its own eye: it reported "no cameras" for
/// everything, forever. The cause was a world-versus-grid coordinate confusion in the vision fast
/// path, which is invisible at (0,0) and total anywhere else — so the suite could not see it.
///
/// Duplicating a couple of scenarios at an offset is cheap insurance against every future member of
/// that family, and the family is large: any code that mixes GetWorldPosition with a grid-local API
/// behaves perfectly here and fails in play.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class OffsetGridTests
{
    private const float Offset = -570f;

    [Test]
    public async Task Door_OpensBesideTheEye_OnAnOffsetGrid()
    {
        await using var w = await AiWorld.Create(gridOffset: Offset);
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"open"}""");

        Assert.That(result.Ok, Is.True,
            "дверь в двух тайлах от глаза обязана открываться независимо от того, где стоит грид. "
            + result.ToJson());

        var state = await w.Read(() => ent.GetComponent<DoorComponent>(door).State);
        Assert.That(state, Is.EqualTo(DoorState.Open).Or.EqualTo(DoorState.Opening), $"состояние двери: {state}");
    }

    [Test]
    public async Task Inspect_ReportsVisible_OnAnOffsetGrid()
    {
        // The gate reports itself through inspect's "visible" field. When the coordinate confusion
        // was live this said false for a door the AI was standing next to, which is the single most
        // misleading answer the tool surface can give.
        await using var w = await AiWorld.Create(gridOffset: Offset);
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("\"visible\":true"), result.ToJson());
        });
    }

    [Test]
    public async Task Eye_ReachesARemoteCamera_OnAnOffsetGrid()
    {
        await using var w = await AiWorld.Create(radius: 24, gridOffset: Offset);
        var camera = await w.Spawn("SurveillanceCameraGeneral", dx: 15, dy: 0);
        var handle = await w.Handle(camera);

        var result = await w.Invoke("move_camera", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, "глаз должен доходить до камеры и на смещённом гриде: " + result.ToJson());
    }

    [Test]
    public async Task LookNear_StillReportsBearings_OnAnOffsetGrid()
    {
        // Bearings are computed from map positions, so an offset grid is exactly where a sign error
        // would show up as "the door south of me" when it is north.
        await using var w = await AiWorld.Create(gridOffset: Offset);
        var crew = await w.Spawn("MobHuman", dx: 2, dy: 0);
        await w.Spawn("AirlockCommand", dx: 2, dy: 3);

        var name = await w.Read(() => Content.Shared.IdentityManagement.Identity.Name(crew, w.Ent));
        var result = await w.Invoke("look", $$"""{"near":"{{name}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            // The point of the test is unchanged: an offset grid must not rotate or shift the answer.
        // Δ is relative to the person, so it stays (0,4) however far from the origin the grid sits —
        // while the absolute pair beside it moves with the grid.
        Assert.That(result.ToJson(), Does.Contain("Δ(0,4)"),
                "смещение от человека не должно зависеть от положения грида: " + result.ToJson());
        });
    }
}
