using System.Threading.Tasks;
using Content.Shared.Silicons.StationAi;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Auto-claim takes the core ON THE STATION, and no other.
///
/// A real incident on August 14-15. The world spawns two cores every round: the station's own and
/// the one Central Command itself ships with (<c>centcomm.yml</c>, <c>PlayerStationAiEmpty</c> at
/// position -0.5,-2.5). The scan ran over an unfiltered query and took the first match — for two
/// days straight it landed on the Centcomm one.
///
/// By itself this looked harmless: the agent was alive, moved, spoke on the radio, and the log was
/// clean. But <c>RadioSystem.cs:150</c> drops any recipient whose map does not match the speaker's,
/// and Centcomm is a separate map. So the agent heard none of the crew's radio traffic and none of
/// its own transmissions ever landed. Over August 15: 222 observations, RADIO — exactly zero, the
/// only things audible were the vending machines standing next to it on Centcomm.
///
/// The test pins the invariant "the core belongs to the station", not "the core is not on
/// Centcomm": there are as many off-station maps as you like — salvage, ruins, planets — and a core
/// on any of them breaks the agent exactly the same way.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class CoreClaimTests
{
    /// <summary>Release the agent and remove the core the bench spawns the world with.</summary>
    private static async Task ClearTheWorldsCore(AiWorld w)
    {
        await w.Post(() =>
        {
            w.System.ReleaseAll("core claim test");
            w.Ent.DeleteEntity(w.Core);
        });

        await w.Pair.Server.WaitRunTicks(3);
    }

    [Test]
    public async Task ACoreOffStationIsNotClaimed()
    {
        await using var w = await AiWorld.Create();
        await ClearTheWorldsCore(w);

        // A second map with no station — exactly what Centcomm is: EmergencyShuttleSystem loads
        // it onto its own map and never makes it part of the station.
        var elsewhere = await w.Pair.CreateTestMap();

        await w.Post(() => w.Ent.SpawnEntity("PlayerStationAiEmpty", elsewhere.GridCoords));
        await w.Pair.Server.WaitRunTicks(3);

        var (claimed, reason) = await w.Read(() =>
        {
            var ok = w.System.TryClaimAnyCore(out var why);
            return (ok, why);
        });

        Assert.Multiple(() =>
        {
            Assert.That(claimed, Is.False,
                "агент занял ядро вне станции — он не услышит рацию и его самого не услышат");
            Assert.That(reason, Does.Contain("off-station"),
                "отказ обязан называть причину: иначе в логе это «агент почему-то не появился»");
        });
    }

    [Test]
    public async Task StationCoreWinsOverAnEarlierOffStationOne()
    {
        // Order matters: the foreign core is created FIRST, meaning that before the fix the scan
        // would have reached it earlier and stopped there. Without this arrangement the test would
        // pass even with the bug present.
        await using var w = await AiWorld.Create();
        await ClearTheWorldsCore(w);

        var elsewhere = await w.Pair.CreateTestMap();

        var stray = await w.Read(() => w.Ent.SpawnEntity("PlayerStationAiEmpty", elsewhere.GridCoords));
        await w.Pair.Server.WaitRunTicks(3);

        var onStation = await w.Read(() => w.Ent.SpawnEntity("PlayerStationAiEmpty", w.Map.GridCoords));
        await w.Pair.Server.WaitRunTicks(3);

        var claimed = await w.Read(() => w.System.TryClaimAnyCore(out _));

        var stationAi = w.Pair.Server.System<SharedStationAiSystem>();

        var (stationHolds, strayHolds) = await w.Read(() => (
            stationAi.TryGetHeld(new Entity<StationAiCoreComponent?>(onStation, null), out _),
            stationAi.TryGetHeld(new Entity<StationAiCoreComponent?>(stray, null), out _)));

        Assert.Multiple(() =>
        {
            Assert.That(claimed, Is.True, "станционное ядро было свободно — занять его было обязано");
            Assert.That(stationHolds, Is.True, "мозг положен не в станционное ядро");
            Assert.That(strayHolds, Is.False, "мозг ушёл в ядро вне станции");
        });
    }
}
