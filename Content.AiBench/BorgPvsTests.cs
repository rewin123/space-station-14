using System;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.AiAgent.Borg;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Eye;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Thin world: a foreign client is not sent something it wouldn't draw anyway.
///
/// <para>
/// Background — <c>AiBorgSystem.Replication.cs</c> and <c>docs/problems.md</c> #19. In short: a
/// delta for an entity the client doesn't have costs it a full 250 KB state, and the vanilla
/// client acknowledges the buffer, not the world application — meaning the server never learns
/// about this hole. The fix is in world composition: the innards of an occupied robot should
/// never be needed on a foreign screen.
/// </para>
/// <para>
/// <b>The chassis root stays visible, though.</b> That's half the requirement, not an oversight:
/// a hidden robot must still be clickable, hittable, speakable-to, and able to receive an item.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgPvsTests
{
    /// <summary>
    /// Claiming sets <see cref="VisibilityFlags.Internal"/> on the children and not on the chassis itself.
    /// </summary>
    /// <remarks>
    /// The check is server-side, against the visibility mask, not against the client, and that's
    /// deliberate: the mask is exactly what <c>PvsSystem.ToSendSet</c> looks at, while spinning up a
    /// connected client alongside a real claim would drag the turn loop and the model into a test
    /// that's supposed to be about PVS. That the mask actually removes the entity on the client is
    /// checked by <see cref="BorgPvsClientTests"/>.
    /// </remarks>
    [Test]
    public async Task Claim_HidesTheInsides_ButNotTheChassis()
    {
        await using var w = await AiStation.Create();
        var borg = EntityUid.Invalid;

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(system.TrySpawnBorg(null, out borg, out var placed, "AiBorgCombatChassis"), Is.True, placed);
            Assert.That(system.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(10);

        var (chassisMask, laserMask, laserFound) = await w.Read(() =>
        {
            var laser = w.Pair.Server.System<ItemSlotsSystem>().GetItemOrNull(borg, "gun_slot");

            return (
                w.Ent.GetComponent<MetaDataComponent>(borg).VisibilityMask,
                laser is { } uid ? w.Ent.GetComponent<MetaDataComponent>(uid).VisibilityMask : 0,
                laser != null);
        });

        Assert.That(laserFound, Is.True, "в gun_slot нет ствола — прятать нечего, стенд собран неверно");

        Assert.Multiple(() =>
        {
            Assert.That(chassisMask & (int) VisibilityFlags.Internal, Is.Zero,
                "корень шасси спрятан — по роботу нельзя ни щёлкнуть, ни ударить, ни дать предмет");
            Assert.That(laserMask & (int) VisibilityFlags.Internal, Is.Not.Zero,
                "встроенный лазер по-прежнему уезжает чужому клиенту");
        });

        // Releasing returns the chassis to its normal state: an unoccupied body is no different
        // from any other item on the station, so there's no reason to keep its innards hidden.
        await w.Post(() => w.Pair.Server.System<AiBorgSystem>().ReleaseBody(borg, "конец теста"));
        await w.Pair.Server.WaitRunTicks(5);

        var afterRelease = await w.Read(() =>
        {
            var laser = w.Pair.Server.System<ItemSlotsSystem>().GetItemOrNull(borg, "gun_slot");
            return laser is { } uid ? w.Ent.GetComponent<MetaDataComponent>(uid).VisibilityMask : 0;
        });

        Assert.That(afterRelease & (int) VisibilityFlags.Internal, Is.Zero,
            "освобождённое шасси осталось с невидимыми внутренностями");
    }

    /// <summary>
    /// The server still sees the gun in its slot: what's hidden is a foreign client's PVS, not the world.
    /// </summary>
    /// <remarks>
    /// A separate test from <see cref="Claim_HidesTheInsides_ButNotTheChassis"/>, because it's a
    /// different question and a different way to fail. Hiding done via deletion or via
    /// <c>Undetachable</c> would pass the mask check and would break <c>shoot</c>.
    /// </remarks>
    [Test]
    public async Task Hiding_DoesNotBlindTheServer()
    {
        await using var w = await AiStation.Create();
        var borg = EntityUid.Invalid;

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(system.TrySpawnBorg(null, out borg, out var placed, "AiBorgCombatChassis"), Is.True, placed);
            Assert.That(system.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(10);

        var shot = await w.InvokeOn(borg, "shoot", "{\"target\":\"obj-999\"}");

        Assert.That(shot.Detail ?? "", Does.Not.Contain("нечем стрелять"),
            $"после сокрытия сервер потерял ствол в слоте: {shot.Detail}");
    }
}

/// <summary>
/// The same requirement, but with a real connected client.
/// </summary>
/// <remarks>
/// <para>
/// A separate fixture rather than a test alongside it: <see cref="AiStation"/> spins up a pair
/// WITHOUT a connected client, and there's simply no one there to ask "what arrived".
/// </para>
/// <para>
/// Claiming is not invoked here — only the hiding itself, via
/// <c>AiBorgSystem.SetSubtreeHiddenForTest</c>. Claiming requires <c>ai.enabled</c> turned on and a
/// live model; checking it here too would make a test about packet composition fail on any hiccup
/// in the turn loop. That claiming does turn on the hiding is the subject of <see cref="BorgPvsTests"/>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class BorgPvsClientTests : GameTest
{
    /// <summary>
    /// A real station with a real client — same as <c>PvsResyncTests</c>.
    /// </summary>
    /// <remarks>
    /// The map must be a real one: an empty map has a dozen entities in view range, and the
    /// question "what arrived" wouldn't mean anything.
    /// </remarks>
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        DummyTicker = false,
        Map = "Box",
        Dirty = true,
    };

    /// <summary>
    /// A hidden subtree leaves the client's PVS while the chassis stays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>net.pvs</c> is turned on by hand: the test pool sets it to <c>false</c>
    /// (<c>Robust.UnitTesting/Pool/PoolManager.cs</c>), and with PVS off the server sends
    /// everything to everyone via <c>GetAllEntityStates</c> — nobody consults the visibility
    /// mask, and the test would be red even with correct code.
    /// </para>
    /// <para>
    /// Leaving PVS is a <b>detachment</b>, not a deletion: the client keeps the entity around and
    /// sets <c>MetaDataFlags.Detached</c> on it (see the upstream <c>ActionPvsDetachTest</c>).
    /// That's why the flag is checked rather than <c>TryGetEntity</c>: deltas and full states for
    /// a detached entity don't get sent, and that's exactly the goal.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HiddenSubtree_LeavesTheClientPvs_ChassisStays()
    {
        var pair = Pair;
        var (server, client) = pair;

        await OverrideCVar(Side.Server, CVars.NetPVS, true);
        await OverrideCVar(Side.Server, CVars.NetPvsAsync, false);
        await OverrideCVar(Side.Server, CVars.NetMaxUpdateRange, 25f);
        await pair.RunTicksSync(20);

        // Next to the player, not wherever happens: an entity outside PVS range wouldn't arrive
        // even without any hiding, and the test would just be measuring its own carelessness.
        EntityUid borg = default;
        EntityUid laser = default;

        await server.WaitPost(() =>
        {
            var player = ServerSession?.AttachedEntity;
            Assert.That(player, Is.Not.Null, "у сессии нет тела — некуда ставить робота");

            var where = server.EntMan.GetComponent<TransformComponent>(player!.Value).Coordinates;
            borg = server.EntMan.SpawnAtPosition("AiBorgCombatChassis", where);
        });

        await pair.RunTicksSync(20);

        await server.WaitPost(() =>
            laser = server.System<ItemSlotsSystem>().GetItemOrNull(borg, "gun_slot") ?? EntityUid.Invalid);

        Assert.That(laser, Is.Not.EqualTo(EntityUid.Invalid), "в gun_slot нет ствола — прятать нечего");

        var netBorg = server.EntMan.GetNetEntity(borg);
        var netLaser = server.EntMan.GetNetEntity(laser);

        Assert.Multiple(() =>
        {
            Assert.That(IsInPvs(netBorg), Is.True, "клиент не получил шасси даже до сокрытия");
            Assert.That(IsInPvs(netLaser), Is.True,
                "клиент не получил ствол и до сокрытия — стенд не воспроизводит то, что мы убираем");
        });

        await server.WaitPost(() =>
            server.System<AiBorgSystem>().SetSubtreeHiddenForTest(borg, true));
        await pair.RunTicksSync(10);

        Assert.Multiple(() =>
        {
            Assert.That(IsInPvs(netLaser), Is.False, "ствол так и остался в PVS клиента");
            Assert.That(IsInPvs(netBorg), Is.True,
                "вместе с внутренностями спрятался и корень шасси — по роботу больше не щёлкнуть");
        });

        // Revert. An unoccupied chassis must once again be an ordinary item, otherwise a released
        // body stays permanently half-hidden.
        await server.WaitPost(() =>
            server.System<AiBorgSystem>().SetSubtreeHiddenForTest(borg, false));
        await pair.RunTicksSync(10);

        Assert.That(IsInPvs(netLaser), Is.True, "ствол не вернулся в PVS после снятия сокрытия");

        await server.WaitPost(() => server.EntMan.DeleteEntity(borg));
    }

    /// <summary>
    /// Whether the client keeps the entity in view — i.e. whether it is not detached.
    /// </summary>
    private bool IsInPvs(NetEntity netEntity)
    {
        if (!Client.EntMan.TryGetEntity(netEntity, out var uid))
            return false;

        return !Client.EntMan.GetComponent<MetaDataComponent>(uid.Value).Flags
            .HasFlag(MetaDataFlags.Detached);
    }
}
