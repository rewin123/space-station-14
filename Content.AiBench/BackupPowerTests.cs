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
/// Резервное питание для смены без инженеров.
///
/// <para>
/// Стенд — настоящая станция (<see cref="AiStation"/>, карта Box с настоящей энергосетью), и
/// раунд она стартует БЕЗ подключённых игроков. То есть условие «инженеров на смене нет»
/// выполняется само собой, и проверять надо не его, а то, что генератор реально попал в сеть.
/// </para>
/// <para>
/// <b>Главное утверждение файла — про энергосеть, а не про факт спавна.</b> Генератор
/// подключается через <c>CableDeviceNode</c>, а тот соединяется только с кабелем в своём тайле.
/// Поставленный мимо кабеля он спокойно стоит, гудит и не даёт ничего — <b>без единой ошибки</b>.
/// «Сущность заспавнилась» такую поломку пропускает целиком, поэтому здесь сравниваются
/// <see cref="Node.NodeGroup"/> генератора и SMES.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BackupPowerTests
{
    private const string GeneratorProto = "AiAgentBackupGenerator";

    /// <summary>Тот же департамент, что по умолчанию читает <c>ai.backup_power_department</c>.</summary>
    private static readonly ProtoId<DepartmentPrototype> EngineeringDepartment = "Engineering";

    /// <summary>
    /// Генератор поставлен и находится в ТОЙ ЖЕ энергосети, что SMES станции.
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

            // Вот это и есть смысл теста. Спавн мимо кабеля выглядит как успех и молчит.
            Assert.That(matched, Is.EqualTo(generators),
                $"из {generators} генераторов в сети SMES оказалось {matched} — " +
                "остальные стоят вне энергосети и не дают ничего");
        });
    }

    /// <summary>
    /// Суммарная заказанная мощность выставлена на самих машинах, а не осталась прототипной.
    /// </summary>
    /// <remarks>
    /// Мощность пишется в <c>PowerSupplier.MaxSupply</c> после спавна, чтобы
    /// <c>ai.backup_power_watts</c> можно было крутить на живом сервере. Если этот шаг потеряется,
    /// сервер будет выдавать прототипные 60 кВт независимо от настройки — и заметить это можно
    /// будет только по тому, что ручка «не работает».
    /// </remarks>
    [Test]
    public async Task BackupGenerator_SuppliesTheConfiguredTotal()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var ent = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        // Ожидаемая мощность — из таблицы по станции, если запись есть, и только иначе из CVar'а.
        // Стенд грузит карту Box, а у неё в таблице своё число; сравнивать с CVar'ом значило бы
        // проверять, что таблица НЕ работает.
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
    /// Таблица по станциям: все карты ротации на месте, числа осмысленны, маяки не ловушки.
    /// </summary>
    /// <remarks>
    /// Записи составлялись разбором каждой карты, и опечатка в них — самый тихий вид поломки:
    /// прототип просто не найдётся, система молча уйдёт на общий путь с усреднённой мощностью, и
    /// заметить это будет нечем. Поэтому список карт проверяется против <c>gameMap</c>, а не
    /// против самого себя.
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
                // Ключ обязан быть настоящей картой: иначе запись никогда не сработает, а выглядит
                // как рабочая.
                Assert.That(protoMan.HasIndex<Content.Shared.Maps.GameMapPrototype>(tuning.ID), Is.True,
                    $"'{tuning.ID}' — не id существующей карты, запись мертва");

                // 15 кВт — мощность одной машины; ниже неё запись не даёт ни одного генератора.
                Assert.That(tuning.Watts, Is.InRange(15000, 300000),
                    $"{tuning.ID}: {tuning.Watts}Вт вне разумного диапазона");

                foreach (var anchor in tuning.Anchors)
                {
                    Assert.That(anchor, Is.Not.Empty, $"{tuning.ID}: пустой маяк в списке");

                    // Сравнение идёт по вхождению подстроки, поэтому короткие имена ловят чужое:
                    // «CE» попадает в «Science». Два символа — уже ловушка.
                    Assert.That(anchor.Trim().Length, Is.GreaterThan(2),
                        $"{tuning.ID}: маяк '{anchor}' слишком короток для поиска по подстроке");
                }
            }
        });
    }

    /// <summary>
    /// Условие: пустая смена — инженеров нет; выданная инженерная должность — есть.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Проверяется само решение системы, а не спавн. Поднять
    /// <c>RulePlayerJobsAssignedEvent</c> повторно нельзя: на него подписан
    /// <c>AntagSelectionSystem</c>, и вне последовательности старта раунда он валится с
    /// «_postSpawnRules was null» — первая версия этого теста ровно так и падала, причём не по
    /// вине проверяемой системы.
    /// </para>
    /// <para>
    /// Должность выдаётся настоящим <c>StationJobsSystem.TryAssignJob</c> — тем же путём, которым
    /// идёт живой <c>GameTicker</c>. Писать в <c>PlayerJobs</c> напрямую нельзя: поле закрыто
    /// атрибутом <c>[Access]</c>, и правильно закрыто — при выдаче должности должен уменьшаться её
    /// слот.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EngineeringOnDuty_FollowsTheAssignedJobs()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        var power = server.System<BackupPowerSystem>();

        // Раунд на этом стенде стартует без подключённых клиентов, значит должностей не выдано.
        var beforeAnyJobs = await w.Read(() => power.EngineeringOnDuty(w.Station));

        var assigned = await w.Read(() =>
        {
            var jobs = server.System<Content.Server.Station.Systems.StationJobsSystem>();
            // Через ProtoId, а не строкой: анализатор RA0033 запрещает литерал в Index — иначе
            // переименование прототипа в апстриме не поймал бы никто.
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
