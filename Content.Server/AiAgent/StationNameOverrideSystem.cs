using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Robust.Shared.Configuration;

namespace Content.Server.AiAgent;

/// <summary>
/// One station name across all maps — <c>ai.station_name</c>.
///
/// In vanilla, the name is assembled by <see cref="StationNameSystem"/> from a template in the map
/// prototype: "TG Box Station 14-Alpha", and it changes with every map in the rotation. For a server
/// where the station's name is part of its face, that means there is no face: the crew flies in
/// somewhere different every shift, and Central Command's announcements are addressed to a new
/// place each time.
///
/// Patched here rather than in the map prototypes, per the fork's rule: no upstream file gets
/// modified. Otherwise every map in the pool would need touching, and again for any new one.
///
/// An empty value disables the override entirely, and that matters more than it looks: that's the
/// behaviour benchmarks and tests rely on, since they need vanilla behaviour.
/// </summary>
// partial — required by the RA0049 analyzer for types with [Dependency]. In Debug it's a warning,
// in Release it's an error: without it, the release-configuration build doesn't compile at all.
public sealed partial class StationNameOverrideSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private StationSystem _station = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(ref StationPostInitEvent ev)
    {
        var name = _cfg.GetCVar(AiCVars.StationName);
        if (string.IsNullOrWhiteSpace(name))
            return;

        // loud: false — otherwise the first thing the crew hears at the briefing would be an
        // announcement "station X renamed to Y" about a station that never existed for a second.
        _station.RenameStation(ev.Station, name.Trim(), loud: false);
    }
}
