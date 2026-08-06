using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.Station.Events;
using Content.Shared.Station.Components;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Имя станции: подмена на всех картах и то, что агент про него знает.
///
/// В ваниле имя собирает генератор из прототипа карты, поэтому оно меняется вместе с ротацией:
/// «TG Box Station 14-Alpha», потом что-то ещё. Для сервера, где станция — часть его лица, это
/// значит, что лица нет.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class StationNameTests
{
    [Test]
    public async Task OverridesTheGeneratedName()
    {
        await using var w = await AiWorld.Create();
        var cfg = w.Pair.Server.ResolveDependency<IConfigurationManager>();
        var ent = w.Ent;

        await w.Post(() => cfg.SetCVar(AiCVars.StationName, "Аксиома"));

        // Тот самый путь, которым имя приезжает на живом сервере: событие после инициализации
        // станции. Тестовая станция создаётся напрямую, поэтому событие поднимаем сами.
        await w.Post(() =>
        {
            var ev = new StationPostInitEvent(
                new Entity<StationDataComponent>(w.Station, ent.GetComponent<StationDataComponent>(w.Station)));
            ent.EventBus.RaiseLocalEvent(w.Station, ref ev, true);
        });

        var name = await w.Read(() => ent.GetComponent<MetaDataComponent>(w.Station).EntityName);

        Assert.That(name, Is.EqualTo("Аксиома"));
    }

    [Test]
    public async Task EmptyCVarLeavesTheVanillaName()
    {
        // Выключенная подмена — не «переименовать в пустую строку». Именно так работают бенчмарки
        // и тесты, которым нужно ванильное поведение, и станция без имени сломала бы половину из них.
        await using var w = await AiWorld.Create();
        var cfg = w.Pair.Server.ResolveDependency<IConfigurationManager>();
        var ent = w.Ent;

        await w.Post(() => cfg.SetCVar(AiCVars.StationName, ""));

        // Имя ставим сами: тестовая станция создаётся напрямую и генератор имён к ней не
        // применялся, так что без этого «не изменилось» означало бы «как было пусто, так и есть».
        const string vanilla = "TG Box Station 14-Alpha";
        await w.Post(() => ent.System<Content.Server.Station.Systems.StationSystem>()
            .RenameStation(w.Station, vanilla, loud: false));

        await w.Post(() =>
        {
            var ev = new StationPostInitEvent(
                new Entity<StationDataComponent>(w.Station, ent.GetComponent<StationDataComponent>(w.Station)));
            ent.EventBus.RaiseLocalEvent(w.Station, ref ev, true);
        });

        var after = await w.Read(() => ent.GetComponent<MetaDataComponent>(w.Station).EntityName);

        Assert.That(after, Is.EqualTo(vanilla),
            "пустой ai.station_name обязан оставлять ванильное имя в покое, а не затирать его");
    }

    [Test]
    public async Task StationStatus_TellsTheAgentWhereItIs()
    {
        // Экипаж зовёт станцию по имени постоянно — в объявлениях, по рации, в позывных
        // Центрального командования. Узнать его агенту было неоткуда ни одним инструментом:
        // он мог отработать смену, ни разу не поняв, что «Аксиома» — это где он находится.
        await using var w = await AiWorld.Create();
        var cfg = w.Pair.Server.ResolveDependency<IConfigurationManager>();
        var ent = w.Ent;

        await w.Post(() => cfg.SetCVar(AiCVars.StationName, "Аксиома"));
        await w.Post(() =>
        {
            var ev = new StationPostInitEvent(
                new Entity<StationDataComponent>(w.Station, ent.GetComponent<StationDataComponent>(w.Station)));
            ent.EventBus.RaiseLocalEvent(w.Station, ref ev, true);
        });

        var result = await w.Invoke("station_status", "{}");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("Аксиома"),
            $"station_status не сообщает имя станции: {result.ToJson()}");
    }
}
