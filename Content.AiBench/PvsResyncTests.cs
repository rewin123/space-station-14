using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Content.IntegrationTests;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Robust.Client.GameStates;
using Robust.Shared;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Стенд под петлю полных ресинков PVS.
///
/// <para>
/// <b>Что воспроизводим.</b> На боевом сервере клиент получает дельту сущности, которой у него нет,
/// бросает <c>MissingMetadataException</c>, просит полное состояние — и через полсотни тиков всё
/// повторяется на той же сущности. Замер по журналам: у одного игрока 1.1 ресинка на тысячу тиков
/// в хорошем раунде и 17.4 в плохом, период повтора 40–56 тиков ОДИНАКОВЫЙ и для наших роботов, и
/// для ванильных питомцев. Одинаковый период у несвязанных сущностей означает, что цикл системный,
/// а не про конкретное тело.
/// </para>
/// <para>
/// <b>Почему стенд вообще возможен в одном процессе.</b> Внутрипроцессный <c>IntegrationNetManager</c>
/// не Lidgren: он не знает MTU, ничего не теряет и не переупорядочивает. Казалось бы, ветку
/// «состояние тяжелее порога — шлём надёжно и считаем доставленным» здесь не достать. Но
/// <c>ServerSendMessage</c> честно зовёт <c>WriteToBuffer</c>, то есть <c>MsgState.MsgSize</c>
/// выставлен к моменту проверки <c>ShouldSendReliably()</c>, и затирание подтверждения происходит
/// как на бою. Не хватает только потери — её даёт <c>ClientGameStateManager.DropStates</c>.
/// </para>
/// <para>
/// <b>Что здесь НЕ проверяется.</b> Настоящие MTU, фрагментация, переупорядочивание надёжного и
/// ненадёжного каналов. Для этого нужен второй ярус — два процесса и безголовый клиент; настройки
/// <c>net.fakeloss</c> и соседи в этом стенде молча ничего не делают, их читает только настоящий
/// <c>NetManager</c>.
/// </para>
/// </summary>
[TestFixture]
public sealed class PvsResyncTests : GameTest
{
    /// <summary>
    /// Настоящая станция с настоящим подключённым клиентом.
    /// </summary>
    /// <remarks>
    /// Карта обязана быть настоящей. На пустой карте в зоне видимости десяток сущностей, и
    /// бюджет входа — тот самый, вокруг которого весь разбор, — никогда не исчерпается. Стенд на
    /// «Empty» был бы зелёным при любой поломке.
    /// </remarks>
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        DummyTicker = false,
        Map = "Box",
        Dirty = true,
    };

    /// <summary>
    /// Привести пару к боевым сетевым настройкам.
    /// </summary>
    /// <remarks>
    /// Пул тестов переопределяет и то, и другое: <c>net.pvs</c> он ставит в <c>false</c>
    /// (<c>Robust.UnitTesting/Pool/PoolManager.cs</c>), а <c>net.buffer_size</c> в ноль. С
    /// выключенным PVS сервер шлёт всё всем через <c>GetAllEntityStates</c> — ни бюджета, ни
    /// чанков, ни входа в зону видимости, то есть ровно тех механизмов, которые мы и проверяем.
    /// </remarks>
    private async Task ProductionNetSettings(int newEntityBudget = 50)
    {
        await OverrideCVar(Side.Server, CVars.NetPVS, true);
        // Бюджеты входа — на КЛИЕНТЕ, и это не придирка к стороне.
        //
        // `net.pvs_budget` и `net.pvs_enter_budget` объявлены `CVar.REPLICATED | CVar.CLIENT`:
        // авторитетно клиентское значение, оно уезжает на сервер, а серверное игнорируется.
        // Первый прогон стенда я поставил их на сервере, получил «с бюджетом 50 и с бюджетом в
        // миллион одинаково» и чуть не похоронил верную версию. Опыт был недействителен.
        await OverrideCVar(Side.Client, CVars.NetPVSEntityBudget, newEntityBudget);
        await OverrideCVar(Side.Client, CVars.NetPVSEntityEnterBudget, Math.Max(200, newEntityBudget));
        await OverrideCVar(Side.Server, CVars.NetPvsAsync, false);
        await OverrideCVar(Side.Server, CVars.ThreadParallelCount, 0);

        // Ванильная дальность. Урезание до 17 было обходом петли ресинков и больше не нужно:
        // стенд обязан ловить регрессию на том же поле зрения, что и апстримовый клиент.
        await OverrideCVar(Side.Server, CVars.NetMaxUpdateRange, 25f);

        // Ванильный порог принудительного подтверждения. 15 было обходом взрыва стоимости
        // такта (PreviouslySent на 20 тиков не видел честный ack) и снова подтверждало
        // авансом клиента с пингом ~20 тиков. Стенд проверяет, что 60 снова работает.
        await OverrideCVar(Side.Server, CVars.NetForceAckThreshold, 60);

        // Буфер состояний у клиента — как на бою. С нулём клиент применяет состояние в тот же
        // тик, и «клиент отстаёт» перестаёт быть воспроизводимым вовсе.
        await OverrideCVar(Side.Client, CVars.NetBufferSize, 2);

        await Pair.RunTicksSync(20);
    }

    /// <summary>
    /// ГЛАВНЫЙ ТЕСТ: полное состояние обязано быть полным.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Разбор, который этот тест проверяет. В <c>PvsSystem.ToSendSet.cs</c> проверка бюджета входа
    /// стоит РАНЬШЕ ветки <c>session.RequestedFull</c>. При полном состоянии <c>fromTick</c> равен
    /// нулю, поэтому <c>IsEnteringPvsRange</c> считает входящей каждую сущность, а
    /// <c>ForceFullState</c> перед этим обнулила <c>EntityLastAcked</c> у всех — значит каждая
    /// попадает ещё и в счётчик «новых». Потолок, стало быть, не <c>net.pvs_enter_budget</c> (200),
    /// а <c>net.pvs_budget</c> — пятьдесят сущностей на всё полное состояние.
    /// </para>
    /// <para>
    /// А на клиенте <c>ApplyGameState</c> при <c>FromSequence == 0</c> зовёт
    /// <c>PartialStateReset(curState, true)</c>, которая УДАЛЯЕТ каждую сетевую сущность,
    /// отсутствующую в этом состоянии. То есть каждое полное состояние стирает клиенту почти весь
    /// мир, сервер досылает остаток по пятьдесят штук в такт, и каждое из этих состояний снова
    /// тяжелее порога, то есть снова подтверждается авансом. Петля кормит сама себя, и ровный
    /// период в журнале — это её оборот.
    /// </para>
    /// <para>
    /// Меряем минимум по тикам, а не значение в конце: через десяток тиков мир доедет обратно, и
    /// замер «после» ничего не покажет. Проваливается именно провал.
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

        // ПЕРВЫЙ полный сброс — не замер, а приведение клиента в чистое состояние.
        //
        // Пул тестов поднимает пару с выключенным PVS, поэтому к началу теста у клиента лежит вся
        // карта целиком — 38 тысяч сущностей. Замерять от этого числа бессмысленно: первый же
        // честный полный слепок оставит только то, что в зоне видимости, и любая правка покажет
        // «потеряно 99.9%». Стоило одного прогона, чтобы это понять: с бюджетом 50 и с бюджетом в
        // миллион результат был одинаковым — 54 и 53 сущности.
        await client.ExecuteCommand("fullstatereset");
        await pair.RunTicksSync(60);

        var before = client.EntMan.EntityCount;
        TestContext.Out.WriteLine($"сущностей у клиента в зоне видимости: {before}");

        Assert.That(before, Is.GreaterThan(20),
            "у клиента почти пусто — станция не в зоне видимости, мерить нечего");

        await client.ExecuteCommand("fullstatereset");

        // По одному тику, потому что нужен МИНИМУМ, а он живёт ровно один тик — тот, в котором
        // применилось полное состояние.
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
    /// Клиент не должен терять сущность навсегда из-за потерянных состояний.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь воспроизводится вторая половина беды: сервер объявляет состояние доставленным в момент
    /// отправки, если оно тяжелее порога (<c>PvsSystem.Send.cs</c>,
    /// <c>data.LastReceivedAck = CurTick</c> внутри <c>ShouldSendReliably()</c>). Настоящее
    /// подтверждение клиента после этого отбрасывается как устаревшее, и всё, что было в
    /// неприменённом состоянии, сервер считает у клиента имеющимся — навсегда.
    /// </para>
    /// <para>
    /// Порог, к слову, вовсе не 1388 байт, как написано в комментариях к боевому конфигу:
    /// <c>MsgState.ReliableThreshold = kDefaultMTU - 20</c>, а вендоренный Lidgren в этом дереве
    /// объявляет <c>kDefaultMTU = 508</c>. То есть <b>488 байт</b> — почти каждое состояние
    /// населённой станции.
    /// </para>
    /// <para>
    /// Метрика — минимальное окно потери, после которого клиент не восстанавливается. Она сравнима
    /// между прогонами и не зависит от длины теста, в отличие от «сколько раз упало».
    /// </para>
    /// <para>
    /// Кейс 25 тиков больше старого <c>DirtyBufferSize</c> (20) и меньше
    /// <c>force_ack_threshold</c> (60). На истории в 20 тиков настоящий ack уже не находил
    /// sent-set, EntityLastAcked застывал, и каждое тело в зоне сериализовалось как входящее.
    /// Если он зелёный — размер истории связан с порогом, а не с кольцом грязных сущностей.
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

        // Сущность, за которой следим. Человек, а не наш робот: сначала надо убедиться, что
        // ломается сам движок, и только потом мерить, насколько наши тела это усугубляют.
        //
        // Ставится ВОЗЛЕ ИГРОКА, и это обязательное условие, а не удобство: при включённом PVS
        // сущность, заспавненная где придётся, в зону видимости клиента не попадает вовсе, и тест
        // измеряет не потерю состояний, а собственную небрежность.
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

        // Окно потери. Сервер продолжает слать и продолжает считать отправленное доставленным.
        await client.WaitPost(() => stateMan.DropStates = true);
        await pair.RunTicksSync(dropTicks);
        await client.WaitPost(() => stateMan.DropStates = false);

        // Даём вдесятеро больше времени на восстановление, чем длилась потеря: если за это время
        // клиент не догнал, он не догонит уже никогда.
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
}
