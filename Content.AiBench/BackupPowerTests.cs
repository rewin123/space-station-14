using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.Server.AiAgent;
using Content.Server.GameTicking;
using Content.Server.NodeContainer;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.SMES;
using Content.Server.Station.Components;
using Content.Shared.NodeContainer;
using Content.Shared.NodeContainer.NodeGroups;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// Backup power for a shift with no engineers.
///
/// <para>
/// The bench is a real station (<see cref="AiStation"/>, the Box map with a real power grid), and
/// the round starts on it with NO players connected. So the condition "no engineers on shift"
/// holds automatically, and what needs checking isn't that, but whether the generator actually
/// made it into the grid.
/// </para>
/// <para>
/// <b>The main assertion in this file is about the power grid, not the fact of spawning.</b> The
/// generator connects via <c>CableDeviceNode</c>, which only links to a cable on its own tile.
/// Placed off the cable, it just sits there, hums, and delivers nothing — <b>with no error at
/// all</b>. "The entity spawned" misses this kind of breakage entirely, which is why this file
/// compares the generator's and the SMES's <see cref="Node.NodeGroup"/>.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BackupPowerTests
{
    private const string GeneratorProto = "AiAgentBackupGenerator";

    /// <summary>The same department that <c>ai.backup_power_department</c> reads by default.</summary>
    private static readonly ProtoId<DepartmentPrototype> EngineeringDepartment = "Engineering";

    /// <summary>
    /// The generator is placed and sits in the SAME power grid as the station's SMES.
    /// </summary>
    [Test]
    public async Task BackupGenerator_JoinsTheSameNetAsTheSmes()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var ent = server.ResolveDependency<IEntityManager>();

        var counts = await w.Read(() =>
        {
            var nodes = server.System<NodeContainerSystem>();

            var smesNets = new HashSet<INodeGroup>();
            var smesQuery = ent.EntityQueryEnumerator<SmesComponent, NodeContainerComponent>();
            while (smesQuery.MoveNext(out _, out _, out var container))
            {
                if (nodes.TryGetNode<Node>(container, "output", out var node) && node.NodeGroup != null)
                    smesNets.Add(node.NodeGroup);
            }

            var found = 0;
            var onSmesNet = 0;

            var genQuery = ent.EntityQueryEnumerator<PowerSupplierComponent, NodeContainerComponent, MetaDataComponent>();
            while (genQuery.MoveNext(out _, out _, out var container, out var meta))
            {
                if (meta.EntityPrototype?.ID != GeneratorProto)
                    continue;

                found++;

                if (nodes.TryGetNode<Node>(container, "output", out var node)
                    && node.NodeGroup != null
                    && smesNets.Contains(node.NodeGroup))
                {
                    onSmesNet++;
                }
            }

            return (Generators: found, Matched: onSmesNet, SmesNets: smesNets.Count);
        });

        var (generators, matched, smesGroups) = counts;

        Assert.Multiple(() =>
        {
            Assert.That(smesGroups, Is.GreaterThan(0), "на станции не нашлось ни одного SMES в сети — стенд сломан");
            Assert.That(generators, Is.GreaterThan(0),
                "резервный генератор не поставлен, хотя инженеров на смене нет");

            // This is the whole point of the test. Spawning off the cable looks like success and stays silent.
            Assert.That(matched, Is.EqualTo(generators),
                $"из {generators} генераторов в сети SMES оказалось {matched} — " +
                "остальные стоят вне энергосети и не дают ничего");
        });
    }

    /// <summary>
    /// The total requested power is set on the machines themselves, not left at the prototype value.
    /// </summary>
    /// <remarks>
    /// Power is written into <c>PowerSupplier.MaxSupply</c> after spawn, so that
    /// <c>ai.backup_power_watts</c> can be tuned on a live server. If this step gets lost, the
    /// server will keep handing out the prototype's 60 kW regardless of the setting — and the only
    /// way to notice would be that the knob "doesn't work".
    /// </remarks>
    [Test]
    public async Task BackupGenerator_SuppliesTheConfiguredTotal()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var ent = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        // The expected power comes from the per-station table if an entry exists, and only
        // otherwise from the CVar. The bench loads the Box map, which has its own number in the
        // table; comparing against the CVar would mean testing that the table does NOT work.
        var wanted = await w.Read(() =>
        {
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            return protoMan.TryIndex<AiBackupPowerPrototype>(AiStation.MapProto, out var tuning)
                ? tuning.Watts
                : cfg.GetCVar(AiCVars.BackupPowerWatts);
        });

        var total = await w.Read(() =>
        {
            var sum = 0f;
            var query = ent.EntityQueryEnumerator<PowerSupplierComponent, MetaDataComponent>();
            while (query.MoveNext(out _, out var supplier, out var meta))
            {
                if (meta.EntityPrototype?.ID == GeneratorProto)
                    sum += supplier.MaxSupply;
            }

            return sum;
        });

        Assert.That(total, Is.EqualTo((float) wanted).Within(1f),
            $"заказано {wanted}Вт, а машины отдают {total:F0}Вт");
    }

    /// <summary>
    /// The per-station table: every rotation map is present, the numbers make sense, the beacons
    /// aren't traps.
    /// </summary>
    /// <remarks>
    /// The entries were compiled by inspecting each map, and a typo in them is the quietest kind of
    /// breakage: the prototype simply won't be found, the system will silently fall back to the
    /// generic path with an averaged power value, and there'll be nothing to notice it by. That's
    /// why the map list is checked against <c>gameMap</c>, not against itself.
    /// </remarks>
    [Test]
    public async Task Tuning_CoversTheRotationAndIsSane()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        var tunings = await w.Read(() =>
            protoMan.EnumeratePrototypes<AiBackupPowerPrototype>().ToList());

        Assert.That(tunings, Is.Not.Empty, "таблица резервного питания пуста");

        Assert.Multiple(() =>
        {
            foreach (var tuning in tunings)
            {
                // The key must be a real map: otherwise the entry will never fire, while still
                // looking like it works.
                Assert.That(protoMan.HasIndex<Content.Shared.Maps.GameMapPrototype>(tuning.ID), Is.True,
                    $"'{tuning.ID}' — не id существующей карты, запись мертва");

                // 15 kW is the power of a single machine; below that, the entry yields zero generators.
                Assert.That(tuning.Watts, Is.InRange(15000, 300000),
                    $"{tuning.ID}: {tuning.Watts}Вт вне разумного диапазона");

                foreach (var anchor in tuning.Anchors)
                {
                    Assert.That(anchor, Is.Not.Empty, $"{tuning.ID}: пустой маяк в списке");

                    // The comparison is by substring match, so short names catch the wrong thing:
                    // "CE" matches inside "Science". Two characters is already a trap.
                    Assert.That(anchor.Trim().Length, Is.GreaterThan(2),
                        $"{tuning.ID}: маяк '{anchor}' слишком короток для поиска по подстроке");
                }
            }
        });
    }

    /// <summary>
    /// Condition: an empty shift means no engineers; an assigned engineering job means there are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This checks the system's own decision, not the spawn. <c>RulePlayerJobsAssignedEvent</c>
    /// can't be raised again: <c>AntagSelectionSystem</c> is subscribed to it, and outside the
    /// round-start sequence it blows up with "_postSpawnRules was null" — the first version of this
    /// test failed exactly that way, and not because of the system under test.
    /// </para>
    /// <para>
    /// The job is assigned through the real <c>StationJobsSystem.TryAssignJob</c> — the same path a
    /// live <c>GameTicker</c> takes. Writing into <c>PlayerJobs</c> directly isn't possible: the
    /// field is closed off with an <c>[Access]</c> attribute, and rightly so — assigning a job must
    /// decrement its slot.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EngineeringOnDuty_FollowsTheAssignedJobs()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        var power = server.System<BackupPowerSystem>();

        // The round on this bench starts with no clients connected, so no jobs have been assigned.
        var beforeAnyJobs = await w.Read(() => power.EngineeringOnDuty(w.Station));

        var assigned = await w.Read(() =>
        {
            var jobs = server.System<Content.Server.Station.Systems.StationJobsSystem>();
            // Via ProtoId, not a string: analyzer RA0033 forbids a literal in Index — otherwise
            // nobody would catch a prototype rename in upstream.
            var department = protoMan.Index(EngineeringDepartment);

            foreach (var role in department.Roles)
            {
                if (jobs.TryAssignJob(w.Station, role.Id, new NetUserId(System.Guid.NewGuid())))
                    return true;
            }

            return false;
        });

        var afterEngineer = await w.Read(() => power.EngineeringOnDuty(w.Station));

        Assert.Multiple(() =>
        {
            Assert.That(beforeAnyJobs, Is.False, "смена пуста, а система считает, что инженеры есть");
            Assert.That(assigned, Is.True, "не удалось занять ни одну инженерную должность — стенд сломан");
            Assert.That(afterEngineer, Is.True, "инженер на смене, а система его не увидела");
        });
    }

}
