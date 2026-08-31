using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.AiAgent.Borg;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Что происходит, пока робот ИДЁТ.
///
/// <para>
/// Файл заведён по двум жалобам с боевого сервера, которые оказались одной поломкой. Первая: «как
/// только борг начинает двигаться, fps в игре ложится». Вторая: «борги жалуются, что не могут до
/// меня дойти». Обе давал счётчик заторов в <c>WatchForStall</c>, который мерил не застой, а
/// сдвиг за один тик, и сравнивал его с порогом 0.15 тайла — при том что шасси идёт спринтом
/// 4.5 тайла в секунду, а тикрейт 30, то есть ровно 0.15 тайла за тик.
/// </para>
/// <para>
/// Идущий робот получался стоящим КАЖДЫЙ тик. Дальше по накатанной: раз в тридцать тиков он
/// объявлял непроходимым тайл, по которому шёл, и перекладывал маршрут. Перепланировка — это
/// полный A* по станции, и идёт он прямо в <c>Update</c>, мимо шины мира и мимо её профиля.
/// Отсюда и лаги, которых не видно в профиле, и удлиняющийся от попытки к попытке путь, который
/// в конце концов кончается «дороги нет».
/// </para>
/// <para>
/// Поэтому здесь два сторожа с разных сторон: один смотрит на сам счётчик, другой — на его
/// последствия. Ни один не спрашивает у часов, сколько миллисекунд: миллисекунды на сборочной
/// машине меряют железо, а число перепланировок на чистой дороге обязано быть нулём независимо
/// от того, где тест гоняется.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class RouteCostTests
{
    /// <summary>Поставить робота, занять его и дождаться готовности чужого навмеша.</summary>
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

        // Навмеш рулевого строится асинхронно, а проверка проходимости у нас идёт по нему.
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
    /// На ходу счётчик заторов регулярно обнуляется.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Главное утверждение файла и единственное, которое смотрит прямо на поломку. Всё остальное —
    /// её последствия, и они зависят от карты, дверей и того, кто стоит в коридоре.
    /// </para>
    /// <para>
    /// Проверяется не величина счётчика, а то, что он ОБНУЛЯЕТСЯ. Просто «счётчик мал» здесь не
    /// годится: робот на настоящей станции честно встаёт у каждой закрытой створки на десяток
    /// тиков, пока её не нажмут, и счётчик в этот момент обязан расти — за этим он и заведён.
    /// Сломанный счётчик отличается не высотой, а тем, что не опускается никогда: на крейсерском
    /// ходу он рос на единицу каждый тик и за короткий проход добирался до сотни.
    /// </para>
    /// <para>
    /// Замер на стенде, ради которого написана эта формулировка: на разгоне сдвиг за тик идёт
    /// 0.0667, 0.1130, 0.1335, 0.1427 и упирается в 0.1500 — ровно в старый порог, ни разу его не
    /// перешагнув. Это и есть 4.5 тайла в секунду при тикрейте 30.
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

        // Крейсерский ход: тик, на котором робот прошёл почти полный шаг. Именно это состояние
        // старый счётчик и записывал в заторы.
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
                // Робот действительно стоит — у двери, в толпе, на разгоне. Счётчик здесь обязан
                // расти, и серию мы прерываем, а не засчитываем.
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

        // Уход на полтайла занимает три-четыре тика, плюс запас на разгон после двери. Серия в
        // десятки тиков означает, что обнуления не происходит вовсе, то есть счётчик снова мерит
        // сдвиг за один тик.
        Assert.That(worstRun, Is.LessThan(12),
            $"на крейсерском ходу счётчик не обнулялся {worstRun} тиков подряд — " +
            "он снова мерит сдвиг за тик, а не застой");
    }

    /// <summary>
    /// Робот не перекладывает маршрут, по которому спокойно идёт.
    /// </summary>
    /// <remarks>
    /// Сторож последствия. Перепланировка стоит полного A* по станции и идёт на главном потоке
    /// мимо бюджета шины, а вдобавок травит собственный коридор робота: перед ней
    /// <c>WatchForStall</c> объявляет непроходимым тайл, к которому робот шёл. На боевом раунде
    /// это выглядело как путь до Tools, растущий от попытки к попытке — 6, 18, 35, 43, 64 тайла —
    /// и кончающийся «дороги нет» в трёх шагах от цели.
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
            // Один — это сам goto. Всё сверх него на чистой дороге и есть перепланировка.
            Assert.That(searches, Is.EqualTo(1),
                $"на чистой дороге маршрут строился {searches} раз вместо одного");

            Assert.That(blocked, Is.Zero,
                $"робот объявил непроходимыми {blocked} тайлов, идя по ним же");
        });
    }
}
