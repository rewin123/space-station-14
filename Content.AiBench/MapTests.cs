using System.Threading.Tasks;
using Content.Shared.Pinpointer;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Orientation: does the agent know where anything is?
///
/// Seeing was never the problem — look works. Knowing that the door two tiles north belongs to
/// engineering rather than to a maintenance closet was, and without it every request naming a
/// department dead-ended in "назовите, где вы находитесь". The station already carries the answer:
/// the navigation map the AI's own crew monitoring console draws, labelled with the same words the
/// crew uses on the radio.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class MapTests
{
    /// <summary>
    /// A test grid is not a station grid, so nothing ever raises StationGridAdded for it and the
    /// nav map component never appears. Adding it up front is what makes beacons register at all —
    /// their MapInit handler writes into the grid's map, and silently does nothing without one.
    /// </summary>
    private static async Task WithNavMap(AiWorld w)
    {
        var ent = w.Ent;
        await w.Post(() => ent.EnsureComponent<NavMapComponent>(w.Map.Grid.Owner));
        await w.Pair.Server.WaitRunTicks(3);
    }

    private static async Task Beacon(AiWorld w, string prototype, int dx, int dy)
    {
        await w.Spawn(prototype, dx, dy);
        await w.Pair.Server.WaitRunTicks(3);
    }

    [Test]
    public async Task Map_ListsPlacesWithCoordinatesAndBearings()
    {
        await using var w = await AiWorld.Create(radius: 12);
        await WithNavMap(w);
        await Beacon(w, "DefaultStationBeaconBridge", 5, 0);

        var result = await w.Invoke("map");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("Bridge"), result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("восток"),
                "у места должно быть направление от глаза: " + result.ToJson());
        });
    }

    [Test]
    public async Task Map_FiltersByQuery()
    {
        await using var w = await AiWorld.Create(radius: 12);
        await WithNavMap(w);
        await Beacon(w, "DefaultStationBeaconBridge", 5, 0);
        await Beacon(w, "DefaultStationBeaconEngineering", 0, 5);

        var result = await w.Invoke("map", """{"query":"Engineer"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("Engineering"), result.ToJson());
            Assert.That(result.ToJson(), Does.Not.Contain("Bridge"),
                "фильтр обязан отсекать: " + result.ToJson());
        });
    }

    [Test]
    public async Task Map_NothingFound_TellsHowToSeeEverything()
    {
        // A bare "count: 0" leaves the model guessing whether the station has no such place or the
        // map is broken. The way out has to be inside the answer.
        await using var w = await AiWorld.Create(radius: 12);
        await WithNavMap(w);
        await Beacon(w, "DefaultStationBeaconBridge", 5, 0);

        var result = await w.Invoke("map", """{"query":"такого места нет"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("без query"), result.ToJson());
        });
    }

    [Test]
    public async Task Map_CoordinatesAreTheOnesMoveCameraAccepts()
    {
        // The whole point of the tool: name a place, point the eye at it. If the coordinates it
        // hands out are not the ones move_camera takes, the loop stays broken.
        await using var w = await AiWorld.Create(radius: 12);
        await WithNavMap(w);
        var beacon = await w.Spawn("DefaultStationBeaconBridge", 5, 0);

        var pos = await w.Read(() => w.Pair.Server
            .System<Robust.Shared.GameObjects.SharedTransformSystem>()
            .GetMapCoordinates(beacon));

        var moved = await w.Invoke("move_camera",
            $$"""{"x":{{pos.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"y":{{pos.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""");

        Assert.That(moved.Ok, Is.True, "координаты места должны приниматься move_camera: " + moved.ToJson());
    }

    [Test]
    public async Task Self_CarriesTheNearestPlace()
    {
        // "eye=(24,4)" is not something the agent can say out loud; "место=Bridge" is.
        await using var w = await AiWorld.Create(radius: 12);
        await WithNavMap(w);
        await Beacon(w, "DefaultStationBeaconBridge", 3, 0);

        var observation = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        Assert.That(observation, Does.Contain("место="),
            "в SELF должно быть ближайшее место: " + observation);
    }
}
