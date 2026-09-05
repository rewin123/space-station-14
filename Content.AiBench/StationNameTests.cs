using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.Station.Events;
using Content.Shared.Station.Components;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Station name: the override across all maps, and what the agent knows about it.
///
/// In vanilla the name is assembled by a generator from the map prototype, so it changes with every
/// rotation: "TG Box Station 14-Alpha", then something else. For a server where the station is part
/// of its identity, that means it has none.
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

        // The exact path the name travels on a live server: an event after station init. The test
        // station is created directly, so we raise the event ourselves.
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
        // A disabled override does not mean "rename to an empty string". That is exactly how the
        // benchmarks and tests that need vanilla behaviour operate, and a nameless station would
        // break half of them.
        await using var w = await AiWorld.Create();
        var cfg = w.Pair.Server.ResolveDependency<IConfigurationManager>();
        var ent = w.Ent;

        await w.Post(() => cfg.SetCVar(AiCVars.StationName, ""));

        // We set the name ourselves: the test station is created directly and the name generator
        // was never applied to it, so without this "unchanged" would just mean "empty as it always
        // was".
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
        // The crew calls the station by name constantly — in announcements, over the radio, in
        // Central Command's callsigns. There was no tool that could tell the agent this: it could
        // work a whole shift without ever realising that "Axiom" was where it was.
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
