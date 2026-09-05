using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.AiAgent.Borg;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// What happens while a borg is WALKING.
///
/// <para>
/// This file was started from two complaints off the live server that turned out to be one
/// bug. First: "as soon as a borg starts moving, the game's fps tanks." Second: "borgs
/// complain they can't reach me." Both were caused by the stall counter in
/// <c>WatchForStall</c>, which measured not staleness but per-tick displacement, and compared
/// it against a threshold of 0.15 tiles — while the chassis walks at a sprint speed of
/// 4.5 tiles per second and the tickrate is 30, which is exactly 0.15 tiles per tick.
/// </para>
/// <para>
/// A walking robot ended up looking stalled on EVERY tick. From there the rest followed: once
/// every thirty ticks it declared the tile it was walking on impassable and replanned its
/// route. Replanning is a full A* over the station, and it runs directly inside <c>Update</c>,
/// bypassing the world bus and its budget entirely. Hence the lag that never shows up in the
/// profiler, and a path that grows longer with every attempt until it eventually ends in "no
/// route."
/// </para>
/// <para>
/// So there are two guards here approaching it from different sides: one watches the counter
/// itself, the other watches its consequences. Neither asks the clock how many milliseconds
/// elapsed: milliseconds on the build machine measure hardware, while the number of replans on
/// a clear road is required to be zero regardless of where the test runs.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class RouteCostTests
{
    /// <summary>Spawn a borg, claim it, and wait for the foreign navmesh to become ready.</summary>
    private static async Task<EntityUid> Ready(AiStation w)
    {
        var ent = w.Ent;
        var borg = EntityUid.Invalid;

        await w.Pair.Server.WaitPost(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(sys.TrySpawnBorg(null, out borg, out var placed), Is.True, placed);
            Assert.That(sys.TryClaim(borg, out var why), Is.True, why);
        });

        // The pilot's navmesh builds asynchronously, and our passability check relies on it.
        for (var i = 0; i < 80; i++)
        {
            var ready = await w.Read(() =>
            {
                var pf = w.Pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
                return pf.GetPoly(ent.GetComponent<TransformComponent>(borg).Coordinates) != null;
            });

            if (ready)
                break;

            await w.Pair.Server.WaitRunTicks(10);
        }

        return borg;
    }

    /// <summary>
    /// While walking, the stall counter regularly resets to zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The main assertion of this file, and the only one that looks directly at the bug. Everything
    /// else is a consequence of it, and those depend on the map, the doors, and who is standing in
    /// the corridor.
    /// </para>
    /// <para>
    /// This checks not the counter's magnitude but that it RESETS TO ZERO. A simple "counter stays
    /// low" check would not do here: a robot on a real station genuinely stands still at every
    /// closed door for a dozen ticks until it's opened, and the counter is expected to grow at that
    /// moment — that's exactly what it's for. A broken counter is not distinguished by its height
    /// but by the fact that it never drops: at cruising speed it grew by one every tick and reached
    /// a hundred over a short walk.
    /// </para>
    /// <para>
    /// The bench measurement behind this wording: during acceleration the per-tick displacement
    /// runs 0.0667, 0.1130, 0.1335, 0.1427 and settles at 0.1500 — exactly at the old threshold,
    /// never crossing it. That is exactly 4.5 tiles per second at a tickrate of 30.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Walking_IsNotMistakenForStalling()
    {
        await using var w = await AiStation.Create();
        var ent = w.Ent;
        var borg = await Ready(w);

        var sys = await w.Read(() => w.Pair.Server.System<AiBorgSystem>());

        var r = await w.InvokeOn(borg, "goto", "{\"to\":\"Kitchen\"}");
        Assert.That(r.Ok, Is.True, $"goto отказал: {r.Error} {r.Detail}");

        // Cruising speed: a tick in which the robot covered almost a full step. This is exactly the
        // state that the old counter used to record as a stall.
        const float Cruising = 0.12f;

        var moved = 0f;
        var run = 0;
        var worstRun = 0;
        var cruisingTicks = 0;

        var prev = await w.Read(() =>
            w.Pair.Server.System<SharedTransformSystem>().GetMapCoordinates(borg).Position);

        for (var i = 0; i < 150; i++)
        {
            await w.Pair.Server.WaitRunTicks(1);

            var (now, stalls, walking) = await w.Read(() => (
                w.Pair.Server.System<SharedTransformSystem>().GetMapCoordinates(borg).Position,
                sys.StallsForTest(borg),
                sys.WalkStatusForTest(borg)));

            if (!walking.StartsWith("идёт"))
                break;

            var step = (now - prev).Length();
            moved += step;
            prev = now;

            if (step < Cruising)
            {
                // The robot is genuinely standing still — at a door, in a crowd, still accelerating.
                // The counter is expected to grow here, and we break the streak instead of counting it.
                run = 0;
                continue;
            }

            cruisingTicks++;
            run = stalls == 0 ? 0 : run + 1;
            worstRun = Math.Max(worstRun, run);
        }

        TestContext.Out.WriteLine(
            $"прошёл {moved:F1} тайла, крейсерских тиков {cruisingTicks}, " +
            $"самая длинная серия без обнуления {worstRun}");

        Assert.That(moved, Is.GreaterThan(3f),
            "робот никуда не пошёл — сцена не проверяет то, ради чего заведена");

        Assert.That(cruisingTicks, Is.GreaterThan(30),
            "крейсерского хода почти не было — мерить нечего");

        // Moving half a tile takes three to four ticks, plus some slack for accelerating after a
        // door. A streak of dozens of ticks means the counter never resets at all, i.e. it is again
        // measuring per-tick displacement.
        Assert.That(worstRun, Is.LessThan(12),
            $"на крейсерском ходу счётчик не обнулялся {worstRun} тиков подряд — " +
            "он снова мерит сдвиг за тик, а не застой");
    }

    /// <summary>
    /// The robot does not replan a route it is calmly walking along.
    /// </summary>
    /// <remarks>
    /// This guards the consequence. Replanning costs a full A* over the station and runs on the
    /// main thread past the bus budget, and on top of that it poisons the robot's own corridor:
    /// right before it, <c>WatchForStall</c> declares the tile the robot was heading toward
    /// impassable. On the live round this showed up as the path to Tools growing from attempt to
    /// attempt — 6, 18, 35, 43, 64 tiles — and ending in "no route" three steps from the target.
    /// </remarks>
    [Test]
    public async Task Walking_DoesNotReplanTheRouteItIsWalking()
    {
        await using var w = await AiStation.Create();
        var borg = await Ready(w);

        var sys = await w.Read(() => w.Pair.Server.System<AiBorgSystem>());
        await w.Post(() => sys.ResetRouteCost());

        var r = await w.InvokeOn(borg, "goto", "{\"to\":\"Kitchen\"}");
        Assert.That(r.Ok, Is.True, $"goto отказал: {r.Error} {r.Detail}");

        var moved = 0f;
        var prev = await w.Read(() =>
            w.Pair.Server.System<SharedTransformSystem>().GetMapCoordinates(borg).Position);

        for (var i = 0; i < 150; i++)
        {
            await w.Pair.Server.WaitRunTicks(1);

            var (now, walking) = await w.Read(() => (
                w.Pair.Server.System<SharedTransformSystem>().GetMapCoordinates(borg).Position,
                sys.WalkStatusForTest(borg)));

            if (!walking.StartsWith("идёт"))
                break;

            moved += (now - prev).Length();
            prev = now;
        }

        var (searches, totalMs, worstMs, worstProbes) = await w.Read(() => sys.RouteCost);
        var blocked = await w.Read(() => sys.BlockedTilesForTest(borg));

        TestContext.Out.WriteLine(
            $"прошёл {moved:F1} тайла: поисков {searches}, суммарно {totalMs:F1}мс, " +
            $"худший {worstMs:F1}мс ({worstProbes} проверок проходимости), " +
            $"тайлов объявлено непроходимыми {blocked}");

        Assert.That(moved, Is.GreaterThan(3f), "робот никуда не пошёл");

        Assert.Multiple(() =>
        {
            // One is the goto itself. Anything beyond that on a clear road is a replan.
            Assert.That(searches, Is.EqualTo(1),
                $"на чистой дороге маршрут строился {searches} раз вместо одного");

            Assert.That(blocked, Is.Zero,
                $"робот объявил непроходимыми {blocked} тайлов, идя по ним же");
        });
    }
}
