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
/// Тонкий мир: чужому клиенту не приезжает то, что он всё равно не рисует.
///
/// <para>
/// Разбор — <c>AiBorgSystem.Replication.cs</c> и <c>docs/problems.md</c> №19. Коротко: дельта
/// сущности, которой у клиента нет, стоит ему полного состояния на 250 КБ, а ванильный клиент
/// подтверждает буфер, а не применение мира — то есть сервер об этой дыре не узнаёт. Лечится
/// составом мира: внутренности занятого робота чужому экрану не нужны никогда.
/// </para>
/// <para>
/// <b>Корень шасси при этом остаётся видимым.</b> Это половина требования, а не оговорка: по
/// спрятанному роботу нельзя щёлкнуть, ударить его, заговорить с ним или дать ему предмет.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgPvsTests
{
    /// <summary>
    /// Захват вешает <see cref="VisibilityFlags.Internal"/> на детей и не вешает на само шасси.
    /// </summary>
    /// <remarks>
    /// Проверка серверная, по маске видимости, а не по клиенту, и это осознанно: маска — то самое,
    /// на что смотрит <c>PvsSystem.ToSendSet</c>, а поднимать подключённого клиента вместе с
    /// настоящим захватом значит тащить в тест про PVS ещё и петлю хода с моделью. Что маска
    /// действительно убирает сущность у клиента, проверяет <see cref="BorgPvsClientTests"/>.
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

        // Освобождение возвращает шасси в обычное состояние: незанятый корпус ничем не отличается
        // от любого другого предмета на станции, и прятать его внутренности незачем.
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
    /// Сервер по-прежнему видит ствол в слоте: прячется чужой PVS, а не мир.
    /// </summary>
    /// <remarks>
    /// Отдельным тестом от <see cref="Claim_HidesTheInsides_ButNotTheChassis"/>, потому что это
    /// другой вопрос и другой способ упасть. Сокрытие, сделанное через удаление или через
    /// <c>Undetachable</c>, прошло бы проверку масок и сломало бы <c>shoot</c>.
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
/// То же требование, но с настоящим подключённым клиентом.
/// </summary>
/// <remarks>
/// <para>
/// Отдельная фикстура, а не тест рядом: <see cref="AiStation"/> поднимает пару БЕЗ подключённого
/// клиента, и спросить «что доехало» там просто некому.
/// </para>
/// <para>
/// Захват здесь не зовётся — только само сокрытие через
/// <c>AiBorgSystem.SetSubtreeHiddenForTest</c>. Захват требует включённой <c>ai.enabled</c> и живой
/// модели; проверять здесь ещё и его значило бы валить тест про состав пакета на любой заминке
/// петли хода. Что захват сокрытие включает — предмет <see cref="BorgPvsTests"/>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class BorgPvsClientTests : GameTest
{
    /// <summary>
    /// Настоящая станция с настоящим клиентом — как у <c>PvsResyncTests</c>.
    /// </summary>
    /// <remarks>
    /// Карта обязана быть настоящей: на пустой в зоне видимости десяток сущностей, и вопрос «что
    /// доехало» не имеет смысла.
    /// </remarks>
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        DummyTicker = false,
        Map = "Box",
        Dirty = true,
    };

    /// <summary>
    /// Спрятанное поддерево уходит у клиента из PVS, а шасси остаётся.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>net.pvs</c> включается руками: пул тестов ставит его в <c>false</c>
    /// (<c>Robust.UnitTesting/Pool/PoolManager.cs</c>), а с выключенным PVS сервер шлёт всё всем
    /// через <c>GetAllEntityStates</c> — маску видимости никто не спрашивает, и тест был бы
    /// красным при исправном коде.
    /// </para>
    /// <para>
    /// Уход из PVS — это <b>отсоединение</b>, а не удаление: клиент оставляет сущность у себя и
    /// ставит ей <c>MetaDataFlags.Detached</c> (см. апстримовый <c>ActionPvsDetachTest</c>).
    /// Поэтому проверяется флаг, а не <c>TryGetEntity</c>: дельты и полные состояния на
    /// отсоединённую сущность не уходят, а это и есть цель.
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

        // Рядом с игроком, а не где придётся: сущность за пределами дальности PVS не доедет и без
        // всякого сокрытия, и тест мерил бы собственную небрежность.
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

        // Возврат. Незанятое шасси обязано снова быть обычным предметом, иначе освобождённый
        // корпус навсегда остаётся половинчатым.
        await server.WaitPost(() =>
            server.System<AiBorgSystem>().SetSubtreeHiddenForTest(borg, false));
        await pair.RunTicksSync(10);

        Assert.That(IsInPvs(netLaser), Is.True, "ствол не вернулся в PVS после снятия сокрытия");

        await server.WaitPost(() => server.EntMan.DeleteEntity(borg));
    }

    /// <summary>
    /// Держит ли клиент сущность в поле зрения — то есть не отсоединена ли она.
    /// </summary>
    private bool IsInPvs(NetEntity netEntity)
    {
        if (!Client.EntMan.TryGetEntity(netEntity, out var uid))
            return false;

        return !Client.EntMan.GetComponent<MetaDataComponent>(uid.Value).Flags
            .HasFlag(MetaDataFlags.Detached);
    }
}
