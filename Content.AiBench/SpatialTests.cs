using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Tools;
using Content.Shared.IdentityManagement;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Spatial reference: the crew describes the station from where they stand, not from where the
/// AI's eye happens to be.
///
/// "Открой дверь рядом со мной", "которая надо мной", "на которую я смотрю" were all unanswerable
/// before this: look reported distance from the eye and nothing else, and a radio call hands over
/// a voice name with no handle attached. The AI could see the door and see the person and still had
/// no way to relate the two — while a human player relates them at a glance.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class SpatialTests
{
    private async Task<string> NameOf(AiWorld w, EntityUid uid) =>
        await w.Read(() => Identity.Name(uid, w.Ent));

    [Test]
    public async Task LookNear_ReportsBearingFromThePerson_NotFromTheEye()
    {
        // The door sits three tiles north of the crewman. Whatever the eye's own distance to it is,
        // the answer to "the door above me" has to come out as north.
        await using var w = await AiWorld.Create();
        var crew = await w.Spawn("MobHuman", dx: 2, dy: 0);
        await w.Spawn("AirlockCommand", dx: 2, dy: 3);

        var name = await NameOf(w, crew);
        var result = await w.Invoke("look", $$"""{"near":"{{name}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("север"),
                "дверь к северу от человека должна быть описана как север: " + result.ToJson());
            Assert.That(result.ToJson(), Does.Contain(name),
                "в ответе должно быть видно, от кого считали: " + result.ToJson());
        });
    }

    [Test]
    public async Task LookNear_AcceptsAHandleToo()
    {
        // Radio gives a name, the AI's own previous look gives a handle. Both have to work, or the
        // model has to remember which kind of string it is holding.
        await using var w = await AiWorld.Create();
        var crew = await w.Spawn("MobHuman", dx: 2, dy: 0);
        await w.Spawn("AirlockCommand", dx: 4, dy: 0);

        await w.Invoke("look");
        var handle = await w.Handle(crew);

        var result = await w.Invoke("look", $$"""{"near":"{{handle}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("восток"),
                "дверь к востоку от человека: " + result.ToJson());
        });
    }

    [Test]
    public async Task LookNear_ReportsWhichWayThePersonFaces()
    {
        // "Открой дверь, на которую я смотрю" needs the facing, and it is on screen for a player.
        await using var w = await AiWorld.Create();
        var crew = await w.Spawn("MobHuman", dx: 2, dy: 0);

        var name = await NameOf(w, crew);
        var result = await w.Invoke("look", $$"""{"near":"{{name}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("near_facing"),
                "куда смотрит человек — обязательная часть ответа: " + result.ToJson());
        });
    }

    [Test]
    public async Task LookNear_UnknownPerson_SaysNotVisible_AndPointsAtTheWayOut()
    {
        // Failing here is normal play, not a fault: the person is out of camera reach. The model
        // must be told the route (crew monitor, then move the eye) rather than left to invent one.
        await using var w = await AiWorld.Create();

        var result = await w.Invoke("look", """{"near":"Кого Тут Нет"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False, result.ToJson());
            Assert.That(result.Error, Is.EqualTo(ToolError.NotVisible), result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("crew_status"),
                "в отказе должен быть путь дальше: " + result.ToJson());
        });
    }

    [Test]
    public async Task MoveCamera_AcceptsAPoint_SoCrewCoordinatesAreActionable()
    {
        // crew_status hands out coordinates; without this they are a number the AI cannot use.
        await using var w = await AiWorld.Create();

        var before = await w.Read(() => w.System.GetSession(w.Brain) != null);
        Assert.That(before, Is.True);

        var result = await w.Invoke("move_camera", """{"x":3,"y":3}""");

        Assert.That(result.Ok, Is.True, "глаз должен уметь идти в точку под камерами: " + result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("eye_x"), result.ToJson());
    }

    [Test]
    public async Task MoveCamera_WithoutTarget_ExplainsBothWays()
    {
        await using var w = await AiWorld.Create();

        var result = await w.Invoke("move_camera", "{}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False, result.ToJson());
            Assert.That(result.Error, Is.EqualTo(ToolError.BadArgs), result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("crew_status"),
                "отказ должен называть оба способа адресации: " + result.ToJson());
        });
    }
}
