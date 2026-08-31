using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Во что обходится один <c>look</c> и не удерживает ли он тик.
///
/// <para>
/// Поломка, ради которой этот файл заведён, полтора месяца жила в бою и ни разу не показалась на
/// стенде. Журнал живого сервера за сутки: 111 вызовов <c>look</c> и 111 перерасходов бюджета,
/// медиана 98 мс, максимум 1908 — при тикрейте 30, то есть 33 мс на тик. Худший вызов сжирал
/// пятьдесят семь тиков подряд, и игроки видели это как «сервер завис на секунду».
/// </para>
/// <para>
/// Проспал это существующий <c>MainThread_NeverStallsUnderRealLoad</c>, и не по недосмотру: он
/// гоняется на <see cref="AiWorld"/> — тринадцать тайлов плитки и один шлюз. Стоимость обзора
/// росла как произведение числа тайлов на число сущностей, а на стенде оба множителя были
/// однозначными. Поэтому эти тесты живут на <see cref="AiStation"/>: настоящая карта, настоящая
/// сеть камер, настоящие шкафы с настоящим барахлом внутри.
/// </para>
/// <para>
/// Главный тест здесь — не про миллисекунды. Миллисекунды на сборочной машине меряют железо и
/// шумят от холодного JIT, соседнего процесса и сборки мусора; порог по ним приходится ставить
/// таким щедрым, что он ловит только катастрофу. Число походов в broadphase не шумит вовсе:
/// оно обязано быть единицей независимо от того, сколько тайлов вернул обзор, и регрессия к
/// поштучному запросу ломает его мгновенно и однозначно.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class LookCostTests
{
    /// <summary>
    /// Один обзор — один поход в broadphase, сколько бы тайлов в нём ни было.
    ///
    /// Это и есть сторож. Медленный путь делал по запросу на тайл — от 289 при <c>expand:0</c> до
    /// 1681 при <c>expand:3</c>, — и каждый из них вдобавок заново обходил весь уже накопленный
    /// набор в поисках контейнеров. Проверка на единицу ловит возврат к этому, не спрашивая у
    /// часов.
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

                // Без этого тест самоудовлетворяется: одна проверка прошла бы и на пустом обзоре,
                // где сущностей нет вовсе и квадрату не из чего вырасти.
                Assert.That(cost.Tiles, Is.GreaterThan(200),
                    "обзор вернул слишком мало тайлов — тест ничего не доказал, глаз стоит не там");
            });
        }
    }

    /// <summary>
    /// Быстрый путь не потерял ничего из того, что видел медленный.
    ///
    /// Утверждается включение, а не равенство, и это не компромисс, а следствие геометрии. Апстрим
    /// проверял фикстуру против тайла, сжатого на <c>TileEnlargementRadius</c> (величина
    /// отрицательная); мы проверяем рамку сущности против несжатого. Рамка ⊇ фикстуры, несжатый
    /// тайл ⊇ сжатого — значит новый набор обязан быть надмножеством. Если тест упал, сломана
    /// именно геометрия, а не «слегка разъехалось».
    ///
    /// Лишние на границе печатаются, но не роняют: направление ошибки паритетно безопасное —
    /// спрайт на экране игрока рисуется по позиции, а не по фикстуре.
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

            // Маяк может оказаться в мёртвой зоне камер — это нормальный ответ станции, а не
            // поломка. Такую точку просто пропускаем.
            if (!moved.Ok)
                continue;

            for (var expand = 0; expand <= 3; expand++)
            {
                // Оба пути — в одном кадре. Иначе между замерами проходит тик, и разошедшаяся на
                // одну сущность пара читается как потеря, хотя это чей-то шаг.
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
    /// Стоимость не взрывается по <c>expand</c>.
    ///
    /// Отношение, а не абсолют: оно сокращает скорость машины, и порог не приходится задирать до
    /// бессмысленного. По журналу боя отношение было ×12…×19 — ровно подпись квадратичного роста,
    /// потому что число тайлов и число сущностей росли вместе. После снятия квадрата остаётся рост
    /// самого обзора апстрима, а он линеен по площади: ждём около ×3.
    ///
    /// Порог щедрый (×8), потому что сборка мусора отношение не сокращает.
    /// </summary>
    [Test]
    public async Task Look_CostDoesNotExplodeWithExpand()
    {
        await using var w = await AiStation.Create();

        // Прогрев: первый вызов оплачивает JIT всей цепочки, и мерить по нему — мерить компилятор.
        await w.Invoke("look");

        var t0 = await Measure(w, 0);
        var t3 = await Measure(w, 3);

        TestContext.Out.WriteLine($"expand=0: {t0:F1}мс, expand=3: {t3:F1}мс, отношение {t3 / t0:F1}");

        Assert.That(t3 / t0, Is.LessThan(8.0),
            $"стоимость растёт по expand в {t3 / t0:F1} раз — похоже на возврат квадратичного сбора");
    }

    /// <summary>
    /// Абсолютный потолок — дымовая сетка, а не цель.
    ///
    /// Настенные часы на сборочной машине флапают: холодный JIT, шумный раннер, сборка мусора
    /// поверх только что загруженной карты. Поэтому порог стоит там, где он ловит катастрофу
    /// (секунда удержания тика), а не там, где мы хотим видеть результат. Цель стережёт
    /// <see cref="Look_MakesExactlyOneBroadphaseQuery"/>.
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

    /// <summary>Суммарное время одного обзора по профилю, а не по внешнему секундомеру теста.</summary>
    private static async Task<double> Measure(AiStation w, int expand)
    {
        var result = await w.Invoke("look", $"{{\"expand\":{expand}}}");
        Assert.That(result.Ok, Is.True, $"look expand={expand} отказал: {result.Detail}");

        var cost = await w.Read(() => w.System.LastLookCost());
        return cost.ViewMs + cost.GatherMs + cost.RowsMs;
    }
    /// <summary>
    /// Нарезаемый обзор видит РОВНО то же, что апстримовый, — тайл в тайл, при любой мелкости среза.
    ///
    /// <para>
    /// Это единственное, что оправдывает существование своей копии теневого каста. Копия чужого
    /// алгоритма расходится с оригиналом молча: ИИ начинает видеть на тайл больше или меньше, чем
    /// увидел бы игрок на этой роли, и в игре это неотличимо от «модель так решила». Утверждение
    /// «перенесли точно» либо проверяемо, либо это обещание.
    /// </para>
    /// <para>
    /// Гоняется дважды. Целым срезом — проверка, что порт верен сам по себе. Срезом с нулевым
    /// бюджетом, то есть с разрывом при первой возможности, — проверка, что состояние переживает
    /// границу кадра: именно здесь всплыло бы забытое поле или счётчик, сброшенный не там.
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

                    // При нулевом зерне резаться обязано хотя бы раз: иначе тест «переживает границу
                    // кадра» ничего не проверил, а просто прогнал всё одним куском.
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
