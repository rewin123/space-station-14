using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// What a single <c>look</c> costs, and whether it stalls a tick.
///
/// <para>
/// The breakage this file exists for lived in production for a month and a half without ever
/// showing up on the bench. A live server's log over one day: 111 calls to <c>look</c> and 111
/// budget overruns, median 98 ms, max 1908 — at a tickrate of 30, that's 33 ms per tick. The worst
/// call ate fifty-seven ticks in a row, and players saw it as "the server froze for a second."
/// </para>
/// <para>
/// The existing <c>MainThread_NeverStallsUnderRealLoad</c> slept through this, and not by
/// oversight: it runs on <see cref="AiWorld"/> — thirteen floor tiles and one airlock. The cost of
/// a look grew as the product of tile count and entity count, and on the bench both factors were
/// single digits. That's why these tests live on <see cref="AiStation"/>: a real map, a real
/// camera network, real lockers with real junk inside.
/// </para>
/// <para>
/// The main test here isn't about milliseconds. Milliseconds on a build machine measure hardware
/// and are noisy from a cold JIT, a neighboring process, and garbage collection; the threshold on
/// them has to be set so generous that it only catches a catastrophe. The number of broadphase
/// trips doesn't get noisy at all: it must be exactly one no matter how many tiles the look
/// returned, and a regression to a per-tile query breaks it instantly and unambiguously.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class LookCostTests
{
    /// <summary>
    /// One look — one trip to broadphase, no matter how many tiles are in it.
    ///
    /// This is the actual guard. The slow path made a query per tile — from 289 at <c>expand:0</c>
    /// to 1681 at <c>expand:3</c> — and each of those also re-walked the whole already-gathered set
    /// looking for containers. The check against one catches a regression to that, without asking
    /// the clock.
    /// </summary>
    [Test]
    public async Task Look_MakesExactlyOneBroadphaseQuery()
    {
        await using var w = await AiStation.Create();

        foreach (var expand in new[] { 0, 3 })
        {
            var result = await w.Invoke("look", $"{{\"expand\":{expand}}}");
            Assert.That(result.Ok, Is.True, $"look expand={expand} отказал: {result.Detail}");

            var cost = await w.Read(() => w.System.LastLookCost());

            TestContext.Out.WriteLine(
                $"expand={expand}: queries={cost.Queries} tiles={cost.Tiles} cand={cost.Candidates} " +
                $"scr={cost.OnScreen} rows={cost.Rows} | view={cost.ViewMs:F1}мс " +
                $"gather={cost.GatherMs:F1}мс rows={cost.RowsMs:F1}мс");

            Assert.Multiple(() =>
            {
                Assert.That(cost.Queries, Is.EqualTo(1),
                    $"look expand={expand} сходил в broadphase {cost.Queries} раз — вернулся поштучный запрос по тайлам");

                // Without this the test is self-satisfying: the single check would also pass on an
                // empty look, where there are no entities at all and the square has nothing to grow from.
                Assert.That(cost.Tiles, Is.GreaterThan(200),
                    "обзор вернул слишком мало тайлов — тест ничего не доказал, глаз стоит не там");
            });
        }
    }

    /// <summary>
    /// The fast path didn't lose anything the slow path saw.
    ///
    /// What's asserted is inclusion, not equality, and that's not a compromise but a consequence of
    /// geometry. Upstream checked the fixture against a tile shrunk by <c>TileEnlargementRadius</c>
    /// (a negative value); we check the entity's bounding box against the unshrunk tile. Bounding
    /// box ⊇ fixture, unshrunk tile ⊇ shrunk tile — so the new set must be a superset. If this test
    /// fails, it's the geometry itself that broke, not "things drifted slightly."
    ///
    /// Extras at the boundary are printed but don't fail the test: that direction of error is safe
    /// by parity — a sprite on the player's screen is drawn from position, not from the fixture.
    /// </summary>
    [Test]
    public async Task Look_SeesEverythingTheSlowPathSaw()
    {
        await using var w = await AiStation.Create();

        var places = new List<string> { "Bridge", "Atmospherics", "Medical", "Cargo" };
        var checkedSpots = 0;

        foreach (var place in places)
        {
            var at = await w.Beacon(place);
            if (at == null)
                continue;

            var moved = await w.Invoke("move_camera",
                $"{{\"x\":{at.Value.X.ToString("F0", global::System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"y\":{at.Value.Y.ToString("F0", global::System.Globalization.CultureInfo.InvariantCulture)}}}");

            // A beacon can land in a camera dead zone — that's a normal station response, not
            // a breakage. We just skip such a spot.
            if (!moved.Ok)
                continue;

            for (var expand = 0; expand <= 3; expand++)
            {
                // Both paths run within the same frame. Otherwise a tick passes between the two
                // measurements, and a pair that diverges by one entity reads as a loss when it's really
                // just someone's movement.
                var (slow, fast, slowMs, fastMs) =
                    await w.Read(() => w.System.CompareLookPathsForTest(w.Brain, expand));

                var lost = slow.Except(fast).ToList();
                var extra = fast.Except(slow).ToList();

                TestContext.Out.WriteLine(
                    $"{place} expand={expand}: медленный {slow.Count} ({slowMs:F1}мс), " +
                    $"быстрый {fast.Count} ({fastMs:F1}мс), потеряно {lost.Count}, " +
                    $"лишних на границе {extra.Count}");

                foreach (var uid in lost)
                    TestContext.Out.WriteLine("  ПОТЕРЯНО: " + await w.Describe(uid));

                Assert.That(lost, Is.Empty,
                    $"{place} expand={expand}: быстрый путь потерял {lost.Count} из того, что видел медленный");

                checkedSpots++;
            }
        }

        Assert.That(checkedSpots, Is.GreaterThan(0),
            "ни одна точка не проверена — на карте не нашлось ни одного достижимого маяка");
    }

    /// <summary>
    /// Cost doesn't explode with <c>expand</c>.
    ///
    /// A ratio, not an absolute: it cancels out machine speed, so the threshold doesn't have to be
    /// pushed up to meaninglessness. In the production log the ratio was ×12…×19 — exactly the
    /// signature of quadratic growth, because tile count and entity count grew together. Once the
    /// quadratic is removed, what's left is the growth of upstream's own look, which is linear in
    /// area: expect around ×3.
    ///
    /// The threshold is generous (×8) because garbage collection doesn't cancel out of the ratio.
    /// </summary>
    [Test]
    public async Task Look_CostDoesNotExplodeWithExpand()
    {
        await using var w = await AiStation.Create();

        // Warm-up: the first call pays for JIT-ing the whole chain, and measuring on it would measure
        // the compiler.
        await w.Invoke("look");

        var t0 = await Measure(w, 0);
        var t3 = await Measure(w, 3);

        TestContext.Out.WriteLine($"expand=0: {t0:F1}мс, expand=3: {t3:F1}мс, отношение {t3 / t0:F1}");

        Assert.That(t3 / t0, Is.LessThan(8.0),
            $"стоимость растёт по expand в {t3 / t0:F1} раз — похоже на возврат квадратичного сбора");
    }

    /// <summary>
    /// The absolute ceiling is a smoke test, not a target.
    ///
    /// Wall-clock time on a build machine is flaky: cold JIT, a noisy runner, garbage collection on
    /// top of a freshly loaded map. So the threshold sits where it catches a catastrophe (a full
    /// second of holding the tick), not where we'd like to see the result land. The actual target
    /// is guarded by <see cref="Look_MakesExactlyOneBroadphaseQuery"/>.
    /// </summary>
    [Test]
    public async Task Look_DoesNotHoldTheTickForASecond()
    {
        await using var w = await AiStation.Create();
        await w.Invoke("look");

        var worst = 0.0;

        for (var i = 0; i < 3; i++)
            worst = global::System.Math.Max(worst, await Measure(w, 3));

        TestContext.Out.WriteLine($"худший look expand=3: {worst:F1}мс");

        Assert.That(worst, Is.LessThan(150.0),
            $"look expand=3 удержал главный поток {worst:F0} мс — при тикрейте 30 это {worst / 33.3:F0} пропущенных тиков");
    }

    /// <summary>Total time for a single look, per the profiler, not per the test's external stopwatch.</summary>
    private static async Task<double> Measure(AiStation w, int expand)
    {
        var result = await w.Invoke("look", $"{{\"expand\":{expand}}}");
        Assert.That(result.Ok, Is.True, $"look expand={expand} отказал: {result.Detail}");

        var cost = await w.Read(() => w.System.LastLookCost());
        return cost.ViewMs + cost.GatherMs + cost.RowsMs;
    }
    /// <summary>
    /// The sliced view sees EXACTLY what upstream's does — tile for tile, at any slice granularity.
    ///
    /// <para>
    /// This is the only thing that justifies having our own copy of shadow casting. A copy of
    /// someone else's algorithm can drift from the original silently: the AI starts seeing one tile
    /// more or less than a player in that role would, and in-game that's indistinguishable from
    /// "the model just decided that." The claim "we ported it exactly" is either checkable or it's
    /// just a promise.
    /// </para>
    /// <para>
    /// Run twice. As one whole slice — a check that the port is correct on its own. As a
    /// zero-budget slice, i.e. one that breaks off at the first opportunity — a check that state
    /// survives a frame boundary: this is exactly where a forgotten field or a counter reset in the
    /// wrong place would surface.
    /// </para>
    /// </summary>
    [Test]
    public async Task SlicedView_MatchesUpstreamTileForTile()
    {
        await using var w = await AiStation.Create();

        var places = new List<string> { "Bridge", "Atmospherics", "Medical", "Cargo" };
        var checkedSpots = 0;

        foreach (var place in places)
        {
            var at = await w.Beacon(place);
            if (at == null)
                continue;

            var moved = await w.Invoke("move_camera",
                $"{{\"x\":{at.Value.X.ToString("F0", global::System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"y\":{at.Value.Y.ToString("F0", global::System.Globalization.CultureInfo.InvariantCulture)}}}");

            if (!moved.Ok)
                continue;

            foreach (var expand in new[] { 0, 2 })
            {
                foreach (var grain in new double[] { 1000, 0 })
                {
                    var (upstream, sliced, slices) =
                        await w.Read(() => w.System.CompareViewPathsForTest(w.Brain, expand, grain));

                    var lost = upstream.Except(sliced).ToList();
                    var extra = sliced.Except(upstream).ToList();

                    TestContext.Out.WriteLine(
                        $"{place} expand={expand} зерно={grain}мс: апстрим {upstream.Count} тайлов, " +
                        $"нарезкой {sliced.Count} за {slices} срезов, " +
                        $"потеряно {lost.Count}, лишних {extra.Count}");

                    Assert.Multiple(() =>
                    {
                        Assert.That(lost, Is.Empty,
                            $"{place} expand={expand}: нарезка потеряла {lost.Count} тайлов из тех, что видит апстрим");
                        Assert.That(extra, Is.Empty,
                            $"{place} expand={expand}: нарезка выдумала {extra.Count} тайлов, которых апстрим не видит");
                    });

                    // At zero grain it must slice at least once: otherwise the "survives a frame
                    // boundary" test checked nothing and just ran everything as one chunk.
                    if (grain == 0 && upstream.Count > 0)
                    {
                        Assert.That(slices, Is.GreaterThan(1),
                            $"{place} expand={expand}: обзор посчитался одним срезом — резка не сработала");
                    }

                    checkedSpots++;
                }
            }
        }

        Assert.That(checkedSpots, Is.GreaterThan(0),
            "ни одного маяка не нашлось — сравнивать было нечего, и зелёный тест ничего не значит");
    }

}
