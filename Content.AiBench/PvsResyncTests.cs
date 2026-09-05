using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Content.IntegrationTests;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Robust.Client.GameStates;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Content.AiBench;

/// <summary>
/// A bench for the full-PVS-resync loop.
///
/// <para>
/// <b>What we reproduce.</b> On the production server the client receives a delta for an entity it
/// does not have, throws <c>MissingMetadataException</c>, requests a full state — and roughly fifty
/// ticks later it happens again for the same entity. Measured from logs: one player sees 1.1 resyncs
/// per thousand ticks in a good round and 17.4 in a bad one, with a repeat period of 40-56 ticks that
/// is IDENTICAL for both our borgs and vanilla critters. The same period on unrelated entities means
/// the cycle is systemic, not about a specific body.
/// </para>
/// <para>
/// <b>Why the bench works at all in a single process.</b> The in-process <c>IntegrationNetManager</c>
/// is not Lidgren: it knows nothing about MTU, drops nothing, and never reorders. You'd think the
/// "state heavier than the threshold — send reliably and count it delivered" branch would be
/// unreachable here. But <c>ServerSendMessage</c> genuinely calls <c>WriteToBuffer</c>, so
/// <c>MsgState.MsgSize</c> is set by the time <c>ShouldSendReliably()</c> checks it, and the ack gets
/// clobbered exactly as it does in production. The only missing piece is loss — which is what
/// <c>ClientGameStateManager.DropStates</c> provides.
/// </para>
/// <para>
/// <b>What this does NOT cover.</b> Real MTU, fragmentation, reordering on the reliable and
/// unreliable channels. That needs a second tier — two processes and a headless client; the
/// <c>net.fakeloss</c> setting and its neighbors are silent no-ops in this bench, since only the
/// real <c>NetManager</c> reads them.
/// </para>
/// </summary>
[TestFixture]
// A measurement, not a regression check: the numbers here are resyncs per 1000 ticks per player, and
// they depend on the machine, its load, and whatever else is running on it. This has no place in CI —
// a green build must not depend on a neighboring process. Run it by hand on an idle machine.
[Category("Load")]
public sealed class PvsResyncTests : GameTest
{
    /// <summary>
    /// A real station with a real connected client.
    /// </summary>
    /// <remarks>
    /// The map must be a real one. On an empty map there are only a dozen entities in view, and the
    /// enter budget — the very thing this whole investigation is about — never gets exhausted. A bench
    /// on "Empty" would stay green no matter how broken things are.
    /// </remarks>
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        DummyTicker = false,
        Map = "Box",
        Dirty = true,
    };

    /// <summary>
    /// Bring the pair to production-like network settings.
    /// </summary>
    /// <remarks>
    /// The test pool overrides both of these: it sets <c>net.pvs</c> to <c>false</c>
    /// (<c>Robust.UnitTesting/Pool/PoolManager.cs</c>) and <c>net.buffer_size</c> to zero. With PVS
    /// disabled the server sends everything to everyone via <c>GetAllEntityStates</c> — no budget, no
    /// chunking, no entering-the-view-range, i.e. exactly the mechanisms we're here to check.
    /// </remarks>
    private async Task ProductionNetSettings(int newEntityBudget = 50)
    {
        await OverrideCVar(Side.Server, CVars.NetPVS, true);
        // The enter budgets live on the CLIENT, and that's not a nitpick about which side.
        //
        // `net.pvs_budget` and `net.pvs_enter_budget` are declared `CVar.REPLICATED | CVar.CLIENT`:
        // the client's value is authoritative, it travels to the server, and the server's own value
        // is ignored. On the bench's first run I set them on the server, got "budget 50 and budget a
        // million behave identically", and nearly buried the correct fix. That result was invalid.
        await OverrideCVar(Side.Client, CVars.NetPVSEntityBudget, newEntityBudget);
        await OverrideCVar(Side.Client, CVars.NetPVSEntityEnterBudget, Math.Max(200, newEntityBudget));
        await OverrideCVar(Side.Server, CVars.NetPvsAsync, false);
        await OverrideCVar(Side.Server, CVars.ThreadParallelCount, 0);

        // Vanilla range. Cutting it to 17 was a workaround for the resync loop and is no longer
        // needed: the bench must catch the regression at the same field of view as the upstream client.
        await OverrideCVar(Side.Server, CVars.NetMaxUpdateRange, 25f);

        // Vanilla forced-ack threshold. 15 was a workaround for a tick-cost explosion (PreviouslySent
        // couldn't find a genuine ack after 20 ticks of history) and it was again advance-acking the
        // client at a ping of ~20 ticks. The bench checks that 60 works again.
        await OverrideCVar(Side.Server, CVars.NetForceAckThreshold, 60);

        // The client's state buffer — as in production. With zero the client applies the state on
        // the very same tick, and "the client is lagging behind" stops being reproducible at all.
        await OverrideCVar(Side.Client, CVars.NetBufferSize, 2);

        await Pair.RunTicksSync(20);
    }

    /// <summary>
    /// THE MAIN TEST: a full state must actually be full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mechanism this test checks. In <c>PvsSystem.ToSendSet.cs</c> the enter-budget check sits
    /// BEFORE the <c>session.RequestedFull</c> branch. For a full state <c>fromTick</c> is zero, so
    /// <c>IsEnteringPvsRange</c> counts every entity as entering, and <c>ForceFullState</c> already
    /// zeroed out everyone's <c>EntityLastAcked</c> beforehand — meaning every entity also falls into
    /// the "new" counter. So the ceiling isn't <c>net.pvs_enter_budget</c> (200), it's
    /// <c>net.pvs_budget</c> — fifty entities for the entire full state.
    /// </para>
    /// <para>
    /// And on the client, <c>ApplyGameState</c> calls <c>PartialStateReset(curState, true)</c> when
    /// <c>FromSequence == 0</c>, which DELETES every networked entity absent from that state. So each
    /// full state wipes almost the entire world for the client, the server sends the rest back fifty
    /// at a time per tick, and each of those states is again heavier than the threshold, i.e. again
    /// gets advance-acked. The loop feeds itself, and the steady period in the log is its cycle.
    /// </para>
    /// <para>
    /// We measure the minimum over ticks, not the final value: within a dozen ticks the world catches
    /// back up, and the "after" measurement would show nothing. It's precisely the dip that fails.
    /// </para>
    /// </remarks>
    [Test]
    [TestCase(50, TestName = "бюджет боевой (50)")]
    [TestCase(1_000_000, TestName = "бюджет снят — обратный опыт")]
    public async Task FullState_IsNotTruncatedByTheEnterBudget(int newEntityBudget)
    {
        var pair = Pair;
        var (_, client) = pair;

        await ProductionNetSettings(newEntityBudget);

        // The FIRST full reset isn't a measurement, it's bringing the client to a clean state.
        //
        // The test pool spins up the pair with PVS disabled, so at the start of the test the client
        // already holds the entire map — 38 thousand entities. Measuring from that number is
        // meaningless: the very first honest full snapshot will leave only what's in view, and any
        // change would show "99.9% lost". It took one run to realize this: with budget 50 and budget
        // a million the result was identical — 54 and 53 entities.
        await client.ExecuteCommand("fullstatereset");
        await pair.RunTicksSync(60);

        var before = client.EntMan.EntityCount;
        TestContext.Out.WriteLine($"сущностей у клиента в зоне видимости: {before}");

        Assert.That(before, Is.GreaterThan(20),
            "у клиента почти пусто — станция не в зоне видимости, мерить нечего");

        await client.ExecuteCommand("fullstatereset");

        // One tick at a time, because we need the MINIMUM, and it lives on exactly one tick — the
        // one where the full state got applied.
        var worst = before;
        var worstAt = -1;

        for (var tick = 0; tick < 40; tick++)
        {
            await pair.RunTicksSync(1);

            var now = client.EntMan.EntityCount;
            if (now >= worst)
                continue;

            worst = now;
            worstAt = tick;
        }

        TestContext.Out.WriteLine(
            $"минимум {worst} на тике {worstAt}, потеряно {before - worst} из {before} " +
            $"({100.0 * (before - worst) / before:F1}%)");

        Assert.That(worst, Is.GreaterThan(before * 0.9),
            $"полное состояние оказалось урезанным: клиент потерял {before - worst} сущностей из " +
            $"{before}. Значит PartialStateReset удалил всё, чего не было в урезанном бюджетом " +
            "состоянии — это и есть оборот петли ресинков");
    }

    /// <summary>
    /// A client must not lose an entity forever because of dropped states.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This reproduces the second half of the trouble: the server declares a state delivered at the
    /// moment it's sent, if the state is heavier than the threshold (<c>PvsSystem.Send.cs</c>,
    /// <c>data.LastReceivedAck = CurTick</c> inside <c>ShouldSendReliably()</c>). The client's real
    /// ack that arrives afterward is discarded as stale, and everything that was in the unapplied
    /// state is treated by the server as something the client already has — forever.
    /// </para>
    /// <para>
    /// The threshold, incidentally, isn't 1388 bytes as the production config's comments claim:
    /// <c>MsgState.ReliableThreshold = kDefaultMTU - 20</c>, and the vendored Lidgren in this tree
    /// declares <c>kDefaultMTU = 508</c>. That is, <b>488 bytes</b> — almost every state of a
    /// populated station.
    /// </para>
    /// <para>
    /// The metric is the minimum loss window after which the client fails to recover. It's comparable
    /// across runs and doesn't depend on the test's length, unlike "how many times it failed".
    /// </para>
    /// <para>
    /// The 25-tick case is bigger than the old <c>DirtyBufferSize</c> (20) and smaller than
    /// <c>force_ack_threshold</c> (60). With a 20-tick history, the real ack could no longer find the
    /// sent-set, EntityLastAcked would freeze, and every body in view would serialize as entering.
    /// If this stays green, the history size is tied to the threshold, not to the dirty-entity ring.
    /// </para>
    /// </remarks>
    [Test]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(20)]
    [TestCase(25)]
    public async Task ClientRecovers_FromDroppedStates(int dropTicks)
    {
#if !DEBUG
        Assert.Ignore("ClientGameStateManager.DropStates существует только в отладочной сборке");
#else
        var pair = Pair;
        var (server, client) = pair;

        await ProductionNetSettings();

        // The entity we're watching. A human, not one of our borgs: first we need to confirm that
        // the engine itself is broken, and only then measure how much our bodies make it worse.
        //
        // Placed RIGHT NEXT TO THE PLAYER, and this is a hard requirement, not a convenience: with
        // PVS enabled, an entity spawned just anywhere never even enters the client's view range, and
        // the test would end up measuring its own carelessness instead of state loss.
        EntityUid watched = default;

        await server.WaitPost(() =>
        {
            var player = ServerSession?.AttachedEntity;
            Assert.That(player, Is.Not.Null, "у сессии нет тела — некуда ставить наблюдаемую сущность");

            var where = server.EntMan.GetComponent<TransformComponent>(player!.Value).Coordinates;
            watched = server.EntMan.SpawnAtPosition("MobHuman", where);
        });

        await pair.RunTicksSync(20);

        var netEnt = server.EntMan.GetNetEntity(watched);
        Assert.That(client.EntMan.TryGetEntity(netEnt, out _), Is.True,
            "клиент не получил сущность даже до потерь — стенд собран неверно");

        var before = client.EntMan.EntityCount;
        var stateMan = (ClientGameStateManager) client.ResolveDependency<IClientGameStateManager>();

        // The loss window. The server keeps sending and keeps treating what it sent as delivered.
        await client.WaitPost(() => stateMan.DropStates = true);
        await pair.RunTicksSync(dropTicks);
        await client.WaitPost(() => stateMan.DropStates = false);

        // We give ten times as long to recover as the loss lasted: if the client hasn't caught up
        // by then, it never will.
        await pair.RunTicksSync(Math.Max(60, dropTicks * 10));

        var after = client.EntMan.EntityCount;
        TestContext.Out.WriteLine(
            $"окно потери {dropTicks} тиков: сущностей {before} -> {after}");

        Assert.Multiple(() =>
        {
            Assert.That(client.EntMan.TryGetEntity(netEnt, out _), Is.True,
                $"после {dropTicks} потерянных состояний клиент потерял сущность безвозвратно");

            Assert.That(after, Is.GreaterThan(before * 0.9),
                $"после {dropTicks} потерянных состояний мир у клиента не восстановился: " +
                $"{after} сущностей против {before}");
        });
#endif
    }

    /// <summary>
    /// Round 205's loop: a walking entity plus one resync must not produce a SECOND resync.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What we reproduce.</b> Production round 205 (24.08.2026): a client on a local network —
    /// that is, WITHOUT a large ping and WITHOUT packet loss — got stuck in a "request a full state
    /// every 11-31 ticks" loop for six minutes, and every request named the same entity: a walking
    /// borg. The server, meanwhile, believed the client already had the entity (EntityLastAcked
    /// fresh, LastLeftView=0) and kept sending bare deltas. The in-process pair is the same case:
    /// delivery is instant, there's no loss. If the loop is systemic, it must reproduce here.
    /// </para>
    /// <para>
    /// Walking means SetLocalPosition every tick: the entity is dirty every tick and regularly
    /// crosses chunk boundaries (ChunkSize = 8), like a borg on its route. Two walkers, as in the round.
    /// </para>
    /// </remarks>
    [Test]
    [TestCase(false, false, TestName = "ходоки, sync, без массового входа")]
    [TestCase(true, false, TestName = "ходоки, async как на бою, без массового входа")]
    [TestCase(true, true, TestName = "ходоки, async, ресинк посреди массового входа (прилёт)")]
    public async Task WalkingEntity_SingleResync_DoesNotLoop(bool asyncPvs, bool massEntry)
    {
        var pair = Pair;
        var (server, client) = pair;

        await ProductionNetSettings();

        // The production server runs with net.pvs_async = true (the engine's default); the bench
        // sets false for its own reasons. The loop could live in an async-computation race — check both.
        await OverrideCVar(Side.Server, CVars.NetPvsAsync, asyncPvs);

        var xformSys = server.System<SharedTransformSystem>();

        EntityUid walkerA = default;
        EntityUid walkerB = default;
        Vector2 originA = default;
        Vector2 originB = default;
        EntityUid gridUid = default;

        await server.WaitPost(() =>
        {
            var player = ServerSession?.AttachedEntity;
            Assert.That(player, Is.Not.Null, "у сессии нет тела");

            var xform = server.EntMan.GetComponent<TransformComponent>(player!.Value);
            var where = xform.Coordinates;
            gridUid = xform.ParentUid;
            walkerA = server.EntMan.SpawnAtPosition("MobHuman", where);
            walkerB = server.EntMan.SpawnAtPosition("MobHuman", where.Offset(new Vector2(2, 0)));
            originA = server.EntMan.GetComponent<TransformComponent>(walkerA).LocalPosition;
            originB = server.EntMan.GetComponent<TransformComponent>(walkerB).LocalPosition;
        });

        await pair.RunTicksSync(20);

        var netA = server.EntMan.GetNetEntity(walkerA);
        var netB = server.EntMan.GetNetEntity(walkerB);
        Assert.That(client.EntMan.TryGetEntity(netA, out _), Is.True, "клиент не получил ходока A до ресинка");

        if (massEntry)
        {
            // Mass entry: round 205 got stuck in the loop right as the arrivals shuttle docked, when
            // the station entered the client's field of view all at once and the enter budget
            // stretched that entry over dozens of ticks. Here we reproduce the same profile more
            // cheaply: the player is teleported into the void beyond PVS range, the client's world
            // empties out, coming back is a mass entry, and the resync lands right in the middle of it.
            var player = ServerSession!.AttachedEntity!.Value;
            Vector2 home = default;
            await server.WaitPost(() =>
            {
                home = server.EntMan.GetComponent<TransformComponent>(player).LocalPosition;
                xformSys.SetLocalPosition(player, home + new Vector2(120f, 0));
            });
            await pair.RunTicksSync(30);
            await server.WaitPost(() => xformSys.SetLocalPosition(player, home));
            await pair.RunTicksSync(3); // entry has started, the 200/50 budget is nowhere near used up
        }

        // A single resync — like a MissingMetadataException in production: the client asks for the full world.
        await client.ExecuteCommand("fullstatereset");

        // Walking: back and forth over ±10 tiles, 0.25 tile per tick. Crosses a chunk boundary every
        // ~32 ticks — the same period the requests came in at during round 205.
        var lossesA = new List<int>();
        var lossesB = new List<int>();
        var presentA = true;
        var presentB = true;

        const int ticks = 400;
        for (var t = 0; t < ticks; t++)
        {
            var phase = t * 0.25f % 40f;
            var dx = phase < 20f ? phase : 40f - phase; // 0..10..0

            var t1 = t;
            await server.WaitPost(() =>
            {
                if (server.EntMan.Deleted(walkerA) || server.EntMan.Deleted(walkerB))
                    return;
                xformSys.SetLocalPosition(walkerA, originA + new Vector2(dx, 0));
                xformSys.SetLocalPosition(walkerB, originB + new Vector2(0, dx));
            });

            await pair.RunTicksSync(1);

            var nowA = client.EntMan.TryGetEntity(netA, out _);
            var nowB = client.EntMan.TryGetEntity(netB, out _);
            if (presentA && !nowA)
                lossesA.Add(t);
            if (presentB && !nowB)
                lossesB.Add(t);
            presentA = nowA;
            presentB = nowB;
        }

        TestContext.Out.WriteLine(
            $"потери ходока A на тиках: [{string.Join(", ", lossesA)}]; "
            + $"B: [{string.Join(", ", lossesB)}]");

        Assert.Multiple(() =>
        {
            // The first loss is the resync itself (PartialStateReset can wipe it for a tick while the
            // full state is in flight). Anything after the first 60 ticks is the loop itself.
            Assert.That(lossesA.FindAll(x => x > 60), Is.Empty,
                "ходок A потерян клиентом ПОСЛЕ восстановления от ресинка — петля раунда 205");
            Assert.That(lossesB.FindAll(x => x > 60), Is.Empty,
                "ходок B потерян клиентом ПОСЛЕ восстановления от ресинка — петля раунда 205");
            Assert.That(presentA && presentB, Is.True, "к концу прогона ходоки так и не вернулись");
        });
    }

    /// <summary>
    /// Re-entering view range without an ack must not pile the whole station's full states into one packet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Log from 26.08.2026: zero resyncs, server at 33 ms/tick, a client on a local network froze.
    /// <c>IsEnteringPvsRange</c> kept <c>entering=true</c> for every entity with
    /// <c>EntityLastAcked &lt; fromTick</c>, even if it had been sent the previous tick, and no budget
    /// was charged for it. The full state kept piling up: 200 → 2098 entities in three seconds.
    /// </para>
    /// <para>
    /// The bench: warm up the view range, move the player away (entities leave, the ack is still
    /// fresh), freeze the ack via <c>DropStates</c>, bring the player back. Walls aren't dirty — they
    /// only land in a packet through the entering branch. With the bug they stay in every subsequent
    /// packet; with patch #14 they appear only on the tick they show up, and after that the enter
    /// budget is 200 per tick with no accumulation.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ReentryWithoutAck_DoesNotAccumulateFullStates()
    {
// This test needs two things at once: ClientGameStateManager.DropStates (only exists under DEBUG)
// and the server-side TryGetPvsSendDiag diagnostic, which doesn't exist in the vanilla engine — it
// was our own addition and went away with the rollback to v286.0.0. The body is kept, not deleted:
// once the diagnostic comes back, the test comes back too — the symbol will need to be declared in csproj.
#if !FORK_PVS_SEND_DIAG
        Assert.Ignore("нужны DropStates (только DEBUG) и TryGetPvsSendDiag, которого в ванильном движке нет");
#else
        var pair = Pair;
        var (server, client) = pair;
        var xformSys = server.System<SharedTransformSystem>();

        await ProductionNetSettings();
        await pair.RunTicksSync(60);

        var session = ServerSession;
        Assert.That(session, Is.Not.Null, "нет сессии игрока");
        var player = session!.AttachedEntity;
        Assert.That(player, Is.Not.Null, "у сессии нет тела");

        var settled = client.EntMan.EntityCount;
        TestContext.Out.WriteLine($"зона после прогрева: {settled} сущностей у клиента");
        Assert.That(settled, Is.GreaterThan(100),
            "зона видимости слишком пустая — аккумулятору нечего копить");

        Vector2 home = default;
        await server.WaitPost(() =>
        {
            home = server.EntMan.GetComponent<TransformComponent>(player!.Value).LocalPosition;
            xformSys.SetLocalPosition(player.Value, home + new Vector2(120f, 0));
        });
        await pair.RunTicksSync(30);

        var stateMan = (ClientGameStateManager) client.ResolveDependency<IClientGameStateManager>();
        await client.WaitPost(() => stateMan.DropStates = true);
        await server.WaitPost(() => xformSys.SetLocalPosition(player!.Value, home));

        var maxEntities = 0;
        var maxEntered = 0;
        const int reentryTicks = 15;
        for (var i = 0; i < reentryTicks; i++)
        {
            await pair.RunTicksSync(1);
            var diag = await LastPvsDiag(session);
            maxEntities = Math.Max(maxEntities, diag.Entities);
            maxEntered = Math.Max(maxEntered, diag.Entered);
            TestContext.Out.WriteLine(
                $"возврат тик {i}: в пакете {diag.Entities} (вошло {diag.Entered}, новых {diag.Created})");
        }

        await client.WaitPost(() => stateMan.DropStates = false);
        await pair.RunTicksSync(30);

        TestContext.Out.WriteLine(
            $"за {reentryTicks} тиков возврата без ack: max в пакете {maxEntities}, " +
            $"max вошло {maxEntered}; зона была {settled}");

        // With the bug, the packet grows to the whole view range within a few ticks. With patch #14,
        // every packet holds no more than the enter budget (200) plus a bit of dirty state — nowhere
        // near the full view range.
        Assert.That(maxEntities, Is.LessThan(settled * 2 / 3),
            $"возврат без ack набрал {maxEntities} сущностей в одном пакете при зоне {settled}. " +
            "Полные состояния входящих копятся из тика в тик — патч №14 не сработал");
#endif
    }

    /// <summary>
    /// A dirty entity the client never acknowledged must not go out as a delta with no metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Klin's production signature: after a full snapshot, <c>LastSeen</c> is fresh,
    /// <c>LastLeftView=0</c>, <c>EntityLastAcked=0</c>, the entity is dirty every tick, and the server
    /// sends a delta from <c>fromTick</c> with no MetaData. The client never created the entity —
    /// MissingMetadata — a new full state. The in-process ack is instant, so
    /// <see cref="WalkingEntity_SingleResync_DoesNotLoop"/> doesn't catch this particular tail: by the
    /// time the walking starts, the client already has the entity.
    /// </para>
    /// <para>
    /// The bench: a full resync (EntityLastAcked zeroed, LastSeen set), then right after the full
    /// snapshot is applied we erase the entity on the client — like PartialStateReset / a lost
    /// fragment would — and dirty it every tick. Without patch #21, CreateNewEntity throws
    /// MissingMetadataException. With the patch, the entity's full state arrives, the client recreates
    /// it, and there's no second resync.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DirtyNeverAcked_SendsFullEntityState_DoesNotLoop()
    {
        var pair = Pair;
        var (server, client) = pair;
        var xformSys = server.System<SharedTransformSystem>();

        await ProductionNetSettings();

        EntityUid watched = default;
        Vector2 origin = default;

        await server.WaitPost(() =>
        {
            var player = ServerSession?.AttachedEntity;
            Assert.That(player, Is.Not.Null, "у сессии нет тела — некуда ставить наблюдаемую сущность");

            var where = server.EntMan.GetComponent<TransformComponent>(player!.Value).Coordinates;
            watched = server.EntMan.SpawnAtPosition("MobHuman", where);
            origin = server.EntMan.GetComponent<TransformComponent>(watched).LocalPosition;
        });

        await pair.RunTicksSync(20);

        var netEnt = server.EntMan.GetNetEntity(watched);
        Assert.That(client.EntMan.TryGetEntity(netEnt, out _), Is.True,
            "клиент не получил сущность даже до ресинка — стенд собран неверно");

        await client.ExecuteCommand("fullstatereset");

        var appeared = false;
        for (var i = 0; i < 30 && !appeared; i++)
        {
            await pair.RunTicksSync(1);
            appeared = client.EntMan.TryGetEntity(netEnt, out _);
        }

        Assert.That(appeared, Is.True, "после полного слепка клиент так и не получил сущность");

        // The client applied the snapshot, but the server hasn't advanced EntityLastAcked yet (an
        // advance LastReceivedAck without PendingAcks, patch #13). Erase the entity on the client —
        // from here on it's "new" to it.
        await client.WaitPost(() =>
        {
            if (client.EntMan.TryGetEntity(netEnt, out var uid))
                client.EntMan.DeleteEntity(uid.Value);
        });

        Assert.That(client.EntMan.TryGetEntity(netEnt, out _), Is.False,
            "не удалось стереть сущность у клиента — стенд не воспроизводит дыру");

        var losses = new List<int>();
        var present = false;
        const int ticks = 80;

        for (var t = 0; t < ticks; t++)
        {
            var phase = t * 0.25f % 40f;
            var dx = phase < 20f ? phase : 40f - phase;

            await server.WaitPost(() =>
            {
                if (server.EntMan.Deleted(watched))
                    return;
                xformSys.SetLocalPosition(watched, origin + new System.Numerics.Vector2(dx, 0));
            });

            await pair.RunTicksSync(1);

            var now = client.EntMan.TryGetEntity(netEnt, out _);
            if (present && !now)
                losses.Add(t);
            present = now;
        }

        TestContext.Out.WriteLine(
            $"после стирания сущность вернулась: {present}; повторные потери на тиках: [{string.Join(", ", losses)}]");

        Assert.Multiple(() =>
        {
            Assert.That(present, Is.True,
                "грязная неподтверждённая сущность не вернулась к клиенту — дельта ушла без MetaData (патч №21)");
            Assert.That(losses, Is.Empty,
                "сущность вернулась и снова пропала — петля MissingMetadata / полного ресинка");
        });
    }

#if FORK_PVS_SEND_DIAG
    private async Task<(int Entities, int Entered, int Created)> LastPvsDiag(ICommonSession session)
    {
        var entities = 0;
        var entered = 0;
        var created = 0;
        var ok = false;

        await Pair.Server.WaitPost(() =>
        {
            var gsm = Pair.Server.ResolveDependency<IServerGameStateManager>();
            ok = gsm.TryGetPvsSendDiag(session, out entities, out entered, out created);
        });

        Assert.That(ok, Is.True, "у сессии нет PVS-диагностики — клиент не в игре?");
        return (entities, entered, created);
    }
#endif
}
