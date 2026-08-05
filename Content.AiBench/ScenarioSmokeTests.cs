using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Does a real station come up at all, and does the agent see anything on it?
///
/// Everything in the scenario suite rests on this, so it is worth failing here — loudly and with
/// numbers — rather than inside a scenario where a blank <c>look</c> would read as an agent
/// problem. It also records how long a full map load costs, which is what decides whether the
/// scenario suite can run per-commit or has to stay a separate button.
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class ScenarioSmokeTests
{
    [Test]
    public async Task Station_ComesUp_AndTheAgentClaimsACore()
    {
        var clock = Stopwatch.StartNew();
        await using var w = await AiStation.Create();
        var loadMs = clock.ElapsedMilliseconds;

        TestContext.Out.WriteLine($"загрузка станции {AiStation.MapProto}: {loadMs} мс");

        var beacons = await w.Beacons();
        var session = await w.Read(() => w.System.GetSession(w.Brain));

        Assert.Multiple(() =>
        {
            Assert.That(w.Brain, Is.Not.EqualTo(default(Robust.Shared.GameObjects.EntityUid)));
            Assert.That(w.Core, Is.Not.EqualTo(default(Robust.Shared.GameObjects.EntityUid)),
                "агент занял мозг, но ядро не нашлось");
            Assert.That(w.Grid, Is.Not.EqualTo(default(Robust.Shared.GameObjects.EntityUid)));
            Assert.That(w.Station, Is.Not.EqualTo(default(Robust.Shared.GameObjects.EntityUid)),
                "грид не привязан к станции — не будет ни тревоги, ни записей");
            Assert.That(session, Is.Not.Null);
            Assert.That(beacons, Is.Not.Empty, "на карте нет маяков — инструмент map будет пуст");
        });

        TestContext.Out.WriteLine($"маяков: {beacons.Count}; примеры: {string.Join(", ", beacons.Take(8))}");
    }

    [Test]
    public async Task Agent_SeesTheRoomAroundItsCore()
    {
        // The failure this guards against is the one that already happened once: on a real station
        // the grid sits hundreds of tiles from the world origin, and code that confuses world with
        // grid coordinates reports a powered door one tile away as invisible. Every bench passed,
        // because every bench put the grid at (0,0).
        await using var w = await AiStation.Create();

        var look = await w.Invoke("look");
        var map = await w.Invoke("map");
        var self = await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

        TestContext.Out.WriteLine("SELF: " + self.Replace("\n", " | "));
        TestContext.Out.WriteLine("look: " + Trim(look.ToJson(), 600));
        TestContext.Out.WriteLine("map:  " + Trim(map.ToJson(), 600));

        Assert.Multiple(() =>
        {
            Assert.That(look.Ok, Is.True, look.ToJson());
            Assert.That(look.ToJson(), Does.Not.Contain("\"count\":0"),
                "ИИ не видит ничего вокруг собственного ядра — это ровно тот баг мировых/гридовых координат");
            Assert.That(map.Ok, Is.True, map.ToJson());
            Assert.That(map.ToJson(), Does.Not.Contain("\"count\":0"),
                "карта пуста, хотя на Box полсотни маяков");
            Assert.That(self, Does.Contain("место="), "в SELF должен быть ближайший маяк, а не пустота");
        });
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
