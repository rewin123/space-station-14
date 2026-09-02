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
// Замер, а не регрессия: числа здесь — ресинки на 1000 тиков на игрока, и они зависят от машины,
// от её загрузки и от того, что ещё на ней крутится. В CI такому месту не место — зелёная сборка
// не может зависеть от соседнего процесса. Гонять руками на свободной машине.
[Category("Load")]
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

    /// <summary>
    /// Петля раунда 205: ходячая сущность + один ресинк не должны давать ВТОРОЙ ресинк.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Что воспроизводим.</b> Боевой раунд 205 (24.08.2026): клиент из локальной сети — то есть
    /// БЕЗ большого пинга и без потерь — вошёл в петлю «запрос полного состояния каждые 11–31
    /// тиков» на шесть минут, и каждый запрос называл одну и ту же сущность: шагающего киборга.
    /// Сервер при этом считал, что сущность у клиента есть (EntityLastAcked свежий,
    /// LastLeftView=0), и слал голые дельты. Внутрипроцессная пара — тот же случай: доставка
    /// мгновенная, потерь нет. Если петля системная, она обязана воспроизвестись здесь.
    /// </para>
    /// <para>
    /// Ходьба — SetLocalPosition каждый тик: сущность грязная каждый тик и регулярно пересекает
    /// границы чанков (ChunkSize = 8), как киборг на маршруте. Двое ходоков, как в раунде.
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

        // Боевой сервер работает с net.pvs_async = true (умолчание движка); стенд по своим
        // причинам ставит false. Петля может жить в гонке асинхронного расчёта — проверяем оба.
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
            // Прилёт: раунд 205 вошёл в петлю в момент стыковки шаттла прибытия, когда в поле
            // зрения клиента разом вошла станция и вход растянулся бюджетом на десятки тиков.
            // Здесь тот же профиль дешевле: игрока уносит в пустоту за пределы дальности PVS,
            // мир у клиента пустеет, возврат — массовый вход, и ресинк бьёт ровно в его середину.
            var player = ServerSession!.AttachedEntity!.Value;
            Vector2 home = default;
            await server.WaitPost(() =>
            {
                home = server.EntMan.GetComponent<TransformComponent>(player).LocalPosition;
                xformSys.SetLocalPosition(player, home + new Vector2(120f, 0));
            });
            await pair.RunTicksSync(30);
            await server.WaitPost(() => xformSys.SetLocalPosition(player, home));
            await pair.RunTicksSync(3); // вход начался, бюджет 200/50 ещё далеко не выбран
        }

        // Один ресинк — как MissingMetadataException на бою: клиент просит полный мир.
        await client.ExecuteCommand("fullstatereset");

        // Ходьба: туда-обратно на ±10 тайлов, 0.25 тайла за тик. Пересечение границы чанка
        // каждые ~32 тика — период, с которым в раунде 205 и шли запросы.
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
            // Первая потеря — сам ресинк (PartialStateReset может на тик снести, пока полное в
            // пути). Всё, что после первых 60 тиков, — та самая петля.
            Assert.That(lossesA.FindAll(x => x > 60), Is.Empty,
                "ходок A потерян клиентом ПОСЛЕ восстановления от ресинка — петля раунда 205");
            Assert.That(lossesB.FindAll(x => x > 60), Is.Empty,
                "ходок B потерян клиентом ПОСЛЕ восстановления от ресинка — петля раунда 205");
            Assert.That(presentA && presentB, Is.True, "к концу прогона ходоки так и не вернулись");
        });
    }

    /// <summary>
    /// Возврат в зону видимости без ack не должен копить полные состояния всей станции в одном пакете.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Журнал 26.08.2026: ресинков ноль, сервер 33 мс/такт, клиент из локальной сети замерзал.
    /// <c>IsEnteringPvsRange</c> держал <c>entering=true</c> для каждой сущности с
    /// <c>EntityLastAcked &lt; fromTick</c>, даже если её слали прошлый такт, и бюджет на это не
    /// брался. Полное состояние копилось: 200 → 2098 сущностей за три секунды.
    /// </para>
    /// <para>
    /// Стенд: прогреть зону, увести игрока (сущности уходят, ack ещё живой), заморозить ack
    /// через <c>DropStates</c>, вернуть игрока. Стены не грязные — в пакет они попадают только
    /// веткой входа. С поломкой они остаются в каждом следующем пакете; с патчем №14 — только
    /// в тике появления, дальше бюджет входа 200 за тик и без накопления.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ReentryWithoutAck_DoesNotAccumulateFullStates()
    {
// Тесту нужны сразу две вещи: ClientGameStateManager.DropStates (он есть только под DEBUG) и
// серверная диагностика TryGetPvsSendDiag, которой в ванильном движке нет — она была нашим
// дополнением и уехала вместе с откатом на v286.0.0. Тело сохранено, а не удалено: вернётся
// диагностика — вернётся и тест, символ надо будет объявить в csproj.
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

        // С поломкой пакет за несколько тиков дорастает до всей зоны. С патчем №14 в каждом
        // пакете не больше бюджета входа (200) плюс немного грязного — далеко от полной зоны.
        Assert.That(maxEntities, Is.LessThan(settled * 2 / 3),
            $"возврат без ack набрал {maxEntities} сущностей в одном пакете при зоне {settled}. " +
            "Полные состояния входящих копятся из тика в тик — патч №14 не сработал");
#endif
    }

    /// <summary>
    /// Грязная сущность, которую клиент никогда не подтверждал, не должна уходить дельтой без метадаты.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Боевая подпись Клина: после полного слепка <c>LastSeen</c> свежий, <c>LastLeftView=0</c>,
    /// <c>EntityLastAcked=0</c>, сущность грязная каждый тик, сервер шлёт дельту от
    /// <c>fromTick</c> без MetaData. Клиент сущности не создавал — MissingMetadata — новое полное.
    /// Внутрипроцессный ack мгновенный, поэтому <see cref="WalkingEntity_SingleResync_DoesNotLoop"/>
    /// этот хвост не ловит: к моменту ходьбы клиент сущность уже имеет.
    /// </para>
    /// <para>
    /// Стенд: полный ресинк (EntityLastAcked обнулён, LastSeen проставлен), сразу после применения
    /// полного слепка сущность у клиента стираем — как PartialStateReset / потеря фрагментов —
    /// и грязним каждый тик. Без патча №21 CreateNewEntity бросает MissingMetadataException.
    /// С патчем приезжает полное состояние сущности, клиент создаёт её снова, второго ресинка нет.
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

        // Клиент слепок применил, сервер EntityLastAcked ещё не сдвинул (аванс LastReceivedAck
        // без PendingAcks, патч №13). Стираем сущность у клиента — дальше она для него «новая».
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
