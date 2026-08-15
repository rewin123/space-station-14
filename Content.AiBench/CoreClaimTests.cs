using System.Threading.Tasks;
using Content.Shared.Silicons.StationAi;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Автозахват берёт ядро НА СТАНЦИИ и никакое другое.
///
/// Живая авария 14–15 августа. В мире каждый раунд два ядра: станционное и то, которым
/// укомплектован сам Центком (<c>centcomm.yml</c>, <c>PlayerStationAiEmpty</c> в позиции
/// -0.5,-2.5). Перебор шёл по нефильтрованному запросу и брал первое подходящее — два дня
/// подряд доставалось центкомовское.
///
/// Само по себе это выглядело безобидно: агент жил, ходил, говорил в рацию, лог был чист.
/// Но <c>RadioSystem.cs:150</c> выбрасывает получателя, чья карта не совпадает с картой
/// говорящего, а Центком — отдельная карта. Значит агент не слышал ни одной реплики экипажа
/// и ни одна его собственная передача не долетала. За 15 августа: 222 наблюдения, RADIO —
/// ровно ноль, слышны только торговые автоматы, стоявшие рядом с ним на Центкоме.
///
/// Тест держит именно инвариант «ядро принадлежит станции», а не «ядро не на Центкоме»:
/// карт вне станции сколько угодно — сальваж, руины, планеты, — и ядро на любой из них
/// ломает агента ровно так же.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class CoreClaimTests
{
    /// <summary>Снять агента и убрать ядро, с которым стенд поднимает мир.</summary>
    private static async Task ClearTheWorldsCore(AiWorld w)
    {
        await w.Post(() =>
        {
            w.System.ReleaseAll("core claim test");
            w.Ent.DeleteEntity(w.Core);
        });

        await w.Pair.Server.WaitRunTicks(3);
    }

    [Test]
    public async Task ACoreOffStationIsNotClaimed()
    {
        await using var w = await AiWorld.Create();
        await ClearTheWorldsCore(w);

        // Вторая карта без станции — ровно то, чем Центком и является: EmergencyShuttleSystem
        // грузит его на собственную карту и в состав станции не включает.
        var elsewhere = await w.Pair.CreateTestMap();

        await w.Post(() => w.Ent.SpawnEntity("PlayerStationAiEmpty", elsewhere.GridCoords));
        await w.Pair.Server.WaitRunTicks(3);

        var (claimed, reason) = await w.Read(() =>
        {
            var ok = w.System.TryClaimAnyCore(out var why);
            return (ok, why);
        });

        Assert.Multiple(() =>
        {
            Assert.That(claimed, Is.False,
                "агент занял ядро вне станции — он не услышит рацию и его самого не услышат");
            Assert.That(reason, Does.Contain("off-station"),
                "отказ обязан называть причину: иначе в логе это «агент почему-то не появился»");
        });
    }

    [Test]
    public async Task StationCoreWinsOverAnEarlierOffStationOne()
    {
        // Порядок важен: чужое ядро создаётся ПЕРВЫМ, то есть до исправления перебор дошёл бы
        // до него раньше и остановился. Без этой расстановки тест был бы зелёным и на баге.
        await using var w = await AiWorld.Create();
        await ClearTheWorldsCore(w);

        var elsewhere = await w.Pair.CreateTestMap();

        var stray = await w.Read(() => w.Ent.SpawnEntity("PlayerStationAiEmpty", elsewhere.GridCoords));
        await w.Pair.Server.WaitRunTicks(3);

        var onStation = await w.Read(() => w.Ent.SpawnEntity("PlayerStationAiEmpty", w.Map.GridCoords));
        await w.Pair.Server.WaitRunTicks(3);

        var claimed = await w.Read(() => w.System.TryClaimAnyCore(out _));

        var stationAi = w.Pair.Server.System<SharedStationAiSystem>();

        var (stationHolds, strayHolds) = await w.Read(() => (
            stationAi.TryGetHeld(new Entity<StationAiCoreComponent?>(onStation, null), out _),
            stationAi.TryGetHeld(new Entity<StationAiCoreComponent?>(stray, null), out _)));

        Assert.Multiple(() =>
        {
            Assert.That(claimed, Is.True, "станционное ядро было свободно — занять его было обязано");
            Assert.That(stationHolds, Is.True, "мозг положен не в станционное ядро");
            Assert.That(strayHolds, Is.False, "мозг ушёл в ядро вне станции");
        });
    }
}
