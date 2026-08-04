using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Camera reach: can the eye actually travel the camera network?
///
/// On a live station the agent listed cameras in its own look output and then refused to move the
/// eye to every one of them — two vision predicates disagreeing about the same entity. A camera
/// carries StationAiVision itself, so its own tile is always within its own range; if the eye
/// cannot go there, the AI is permanently confined to whatever its core can see, and the whole
/// role collapses to one room.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class CameraReachTests
{
    [Test]
    public async Task Eye_ReachesACameraBeyondTheCoresOwnRange()
    {
        // Fifteen tiles out: well past the core's 7.5-tile vision, so the only thing that can make
        // this tile reachable is the camera standing on it.
        await using var w = await AiWorld.Create(radius: 24);
        var camera = await w.Spawn("SurveillanceCameraGeneral", dx: 15, dy: 0);
        var handle = await w.Handle(camera);

        var result = await w.Invoke("move_camera", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True,
            "глаз обязан доходить до камеры — она сама является источником ИИ-зрения. " + result.ToJson());
    }

    [Test]
    public async Task Eye_ReachesTheTileUnderARemoteCamera_ByCoordinates()
    {
        // Same reach, addressed the way a crew_status position arrives: as numbers.
        await using var w = await AiWorld.Create(radius: 24);
        var camera = await w.Spawn("SurveillanceCameraGeneral", dx: 15, dy: 0);

        var pos = await w.Read(() => w.Pair.Server
            .System<Robust.Shared.GameObjects.SharedTransformSystem>()
            .GetMapCoordinates(camera));

        var result = await w.Invoke("move_camera",
            $$"""{"x":{{pos.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"y":{{pos.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""");

        Assert.That(result.Ok, Is.True,
            "к координатам камеры глаз тоже обязан идти: " + result.ToJson());
    }

    [Test]
    public async Task Eye_RefusesAPointWithNoCameraAtAll()
    {
        // The other half of the contract: reach must stay bounded by the camera network, or the AI
        // gains sight no player has.
        await using var w = await AiWorld.Create(radius: 24);

        var result = await w.Invoke("move_camera", """{"x":200,"y":200}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False, result.ToJson());
            Assert.That(result.Error, Is.EqualTo(ToolError.NotVisible), result.ToJson());
        });
    }
}
