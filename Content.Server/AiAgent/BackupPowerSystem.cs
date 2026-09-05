using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Content.Server.Power.Components;
using Content.Server.Power.SMES;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.NodeContainer;
using Content.Shared.NodeContainer;
using Content.Shared.NodeContainer.NodeGroups;
using Content.Shared.Physics;
using Content.Shared.Power;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>
/// Backup power for a shift with no engineers — <c>ai.backup_power</c>.
///
/// <para>
/// <b>Why.</b> This server has 0-2 people online, and that is normal, not an outage. Nobody staffs
/// an engineering shift at that population, and the station is left running on whatever the
/// batteries stored: an SMES holds 8MJ (<c>smes.yml</c>), and the upstream guidebook says outright
/// "this will at most last 5-10 minutes". After that the round goes dark, and the AI, whose core is
/// also fed by this grid, goes silent.
/// </para>
/// <para>
/// <b>Why not solar panels, even though they are free and endless.</b> They are not connected. A
/// walk of the HV cable graph across all thirteen rotation maps: the arrays sit on their own
/// isolated cable islands, with no SMES and no path to the main grid — on <b>eleven of thirteen
/// maps</b> solar power on the main grid is EXACTLY ZERO. There are two exceptions, both partial:
/// Oasis (70 of 230 panels) and Tram2 (20 of 244). Upstream does not hide this: "At the start of
/// the shift solar panels are misaligned and disconnected from the grid"
/// (<c>Guidebook/Engineering/SolarPanels.xml</c>). To get them producing current on the Packed map
/// would take laying roughly a hundred and fifty tiles of cable — that is, rewriting the world in
/// code, which is far more intrusive than placing one machine. On top of that, nobody aims the
/// panels: <c>PowerSolarSystem</c> forces the same rotation onto every panel, and only a human at
/// the console writes it, so an unaimed array produces zero for half of every solar cycle.
/// </para>
/// <para>
/// <b>Why not a game rule.</b> The list of round-start rules lives in
/// <c>Resources/Prototypes/game_presets.yml</c> — an upstream file, and adding a rule of our own
/// there cannot be done additively. So this is an ordinary system with a subscription, following
/// the pattern of <see cref="StationNameOverrideSystem"/>, set up here for the same reason: not a
/// single upstream file changed.
/// </para>
/// <para>
/// <b>Why on job assignment rather than <c>StationPostInitEvent</c>.</b> Whether there are
/// engineers on shift is unknown before players spawn. <c>StationPostInitEvent</c> fires earlier
/// and would always answer "no engineers".
/// </para>
/// </summary>
public sealed partial class BackupPowerSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private Content.Server.Maps.IGameMapManager _maps = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private Content.Server.NodeContainer.EntitySystems.NodeContainerSystem _nodes = default!;
    [Dependency] private Content.Shared.Construction.EntitySystems.AnchorableSystem _anchorable = default!;

    private const string GeneratorProto = "AiAgentBackupGenerator";

    /// <summary>Power of a single unit from the prototype. More than that — we just place several.</summary>
    private const int WattsPerUnit = 60000;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai.power");

        // The moment when jobs are already assigned and players are spawned. Analysis pattern
        // borrowed from Content.Server/Access/Systems/PresetIdCardSystem.cs.
        //
        // The event fires ALWAYS, even when there are zero ready players: GameTicker.Spawning.
        // SpawnPlayers has no early exit for an empty list. That is exactly what's needed — the AI
        // core needs power even on a shift with no crew.
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnJobsAssigned);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
    }

    /// <summary>
    /// Stations that already have generators deployed for this round.
    ///
    /// The job-assignment event can in principle fire more than once (rules that finish out spawning
    /// also raise it), and <see cref="Deploy"/> is not itself idempotent — it would place a second
    /// batch on top of the first. Silently doubled power looks like "it works" and would only be
    /// noticed by the power becoming unkillable.
    /// </summary>
    private readonly HashSet<EntityUid> _served = new();

    private void OnRoundCleanup(RoundRestartCleanupEvent ev) => _served.Clear();

    private void OnJobsAssigned(RulePlayerJobsAssignedEvent ev)
    {
        if (!_cfg.GetCVar(AiCVars.BackupPower))
            return;

        // This station's own tuning, if it is known; otherwise the generic path.
        //
        // A missing entry is a normal path, not a failure: upstream adds maps, and a new one will
        // enter rotation before it gets a row here. In that case we take the power from the CVar
        // and look for a spot the same way as on any unfamiliar station.
        var tuning = SelectedMapTuning();

        var baseWatts = tuning?.Watts ?? _cfg.GetCVar(AiCVars.BackupPowerWatts);

        // A multiplier on top of any source — the only way to tweak power on a live server: the
        // station table lives in a prototype, and prototypes are read at process start.
        var scale = Math.Max(0f, _cfg.GetCVar(AiCVars.BackupPowerScale));
        var watts = (int) MathF.Round(baseWatts * scale);

        if (watts <= 0)
            return;

        foreach (var station in _station.GetStations())
        {
            if (_served.Contains(station))
                continue;

            if (EngineeringOnDuty(station))
                continue;

            _served.Add(station);

            // Do NOT deploy right away. The power grid does not exist yet at this point.
            //
            // Node groups are assembled by NodeGroupSystem in its own Update, via a deferred queue
            // (QueueReflood -> _toReflood -> FloodFillNode). At job assignment, that is, on the same
            // tick as round start, cables' Node.NodeGroup is still null, and a search over the main
            // grid honestly finds zero tiles. The first version of this change broke exactly this
            // way: tests showed zero generators with live search logic.
            _pending.Add(new Pending(station, watts, tuning?.Anchors));
        }
    }

    /// <summary>Stations waiting for placement until the engine assembles the power grids.</summary>
    private readonly List<Pending> _pending = new();

    private sealed record Pending(EntityUid Station, int Watts, List<string>? Anchors)
    {
        public float Waited;
    }

    /// <summary>
    /// How long to wait for the power grid to appear before declaring a failure.
    ///
    /// Ten seconds is deliberately more than the engine needs for its first flood fill, and
    /// deliberately less than the time it takes someone to notice the dark. Once it expires we log
    /// an error: a silent "the grid never appeared" would look exactly like "generators are not
    /// needed".
    /// </summary>
    private const float DeployTimeoutSeconds = 10f;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pending.Count == 0)
            return;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];

            if (Deploy(pending.Station, pending.Watts, pending.Anchors))
            {
                _pending.RemoveAt(i);
                continue;
            }

            pending.Waited += frameTime;

            if (pending.Waited < DeployTimeoutSeconds)
                continue;

            _pending.RemoveAt(i);
            _sawmill.Error(
                $"резервное питание не поставлено на {ToPrettyString(pending.Station)}: за " +
                $"{DeployTimeoutSeconds:F0}с не нашлось ни одной энергосети с SMES");
        }
    }

    /// <summary>
    /// Tuning for the map selected for this round, if one is set up.
    /// </summary>
    private AiBackupPowerPrototype? SelectedMapTuning()
    {
        var mapId = _maps.GetSelectedMap()?.ID;

        if (mapId == null)
            return null;

        return _protoMan.TryIndex<AiBackupPowerPrototype>(mapId, out var tuning) ? tuning : null;
    }

    /// <summary>
    /// Whether there is at least one player on this station with a job from the engineering
    /// department.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The department's roster is taken from the department prototype rather than a role-by-role
    /// list in code: a fork that adds its own engineering job gets counted automatically. For
    /// upstream that is <c>AtmosphericTechnician</c>, <c>ChiefEngineer</c>, <c>StationEngineer</c>,
    /// <c>TechnicalAssistant</c> (<c>Roles/Jobs/departments.yml</c>).
    /// </para>
    /// <para>
    /// <b>Public for the sake of the test, and that is deliberate.</b> The condition cannot be
    /// checked by raising <c>RulePlayerJobsAssignedEvent</c> manually: <c>AntagSelectionSystem</c>
    /// is subscribed to it, and outside the round-start sequence it blows up with
    /// "_postSpawnRules was null". So the only way to test the decision is to ask about it
    /// directly.
    /// </para>
    /// </remarks>
    public bool EngineeringOnDuty(EntityUid station)
    {
        var departmentId = _cfg.GetCVar(AiCVars.BackupPowerDepartment);

        if (!_protoMan.TryIndex<DepartmentPrototype>(departmentId, out var department))
        {
            // The failure is loud. Silently assuming there are no engineers would mean deploying a
            // generator every shift, including fully staffed ones, and finding out about it from
            // the players.
            _sawmill.Error(
                $"департамент '{departmentId}' не найден — резервное питание не ставится. " +
                "Проверь ai.backup_power_department");
            return true;
        }

        // No jobs component — nothing to count. That happens for non-stations (the nuke ops
        // outpost, CentCom): they don't need backup power.
        if (!TryComp<StationJobsComponent>(station, out var stationJobs))
            return true;

        var roles = department.Roles;

        foreach (var assigned in stationJobs.PlayerJobs.Values)
        {
            foreach (var job in assigned)
            {
                if (roles.Contains(job))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Place generators on the station.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The location is only a tile that ALREADY has a high-voltage cable on it, and that is a
    /// requirement, not a convenience. <c>PowerSupplier</c> connects through
    /// <c>CableDeviceNode</c>, and that (<c>Content.Server/Power/Nodes/CableDeviceNode.cs</c>)
    /// connects exclusively to a <c>CableNode</c> on the same tile, and only for an anchored
    /// entity. A generator placed off the cable is dead metal that hums and provides nothing, with
    /// no error whatsoever.
    /// </para>
    /// <para>
    /// Tiles are taken around an SMES, not random ones. First, an SMES by definition sits on the
    /// MAIN grid — landing on an isolated solar island is impossible. Second, this is an
    /// engineering space: the generator ends up where it belongs, next to what it powers. A random
    /// tile (<c>TryFindRandomTileOnStation</c>, used by variation passes) will almost never turn
    /// out to be a cable tile.
    /// </para>
    /// </remarks>
    private bool Deploy(EntityUid station, int watts, List<string>? anchors)
    {
        var wanted = Math.Max(1, (int) Math.Ceiling(watts / (double) WattsPerUnit));
        var perUnit = watts / (float) wanted;

        var placed = 0;

        foreach (var coords in PlacementTiles(station, anchors))
        {
            if (placed >= wanted)
                break;

            var uid = Spawn(GeneratorProto, coords);

            // Power is set here, not only in the prototype: otherwise ai.backup_power_watts could
            // not be tweaked on a live server, and a rebuild kicks everyone playing.
            if (TryComp<PowerSupplierComponent>(uid, out var supplier))
                supplier.MaxSupply = perUnit;

            placed++;
        }

        // Zero here most often means "the grid isn't assembled yet", not "no room": the caller will
        // retry on the next tick and only declare a failure on timeout.
        if (placed == 0)
            return false;

        // A shortfall is called out loud. A silent shortfall reads as "it works" even though power
        // is half of what was ordered, and it would only get sorted out from complaints about the
        // dark. Map analysis showed this is not theoretical: on several stations the number of
        // suitable tiles is exactly what's needed, and one catwalk from a mapper turns two
        // generators into one.
        if (placed < wanted)
        {
            _sawmill.Warning(
                $"резервное питание: поставлено {placed} из {wanted} машин — " +
                $"{placed * perUnit:F0}Вт вместо {watts}Вт, не хватило подходящих тайлов");
        }
        else
        {
            _sawmill.Info(
                $"резервное питание: {placed}×{perUnit:F0}Вт на станции {ToPrettyString(station)} " +
                "(инженерной смены нет)");
        }

        return true;
    }

    /// <summary>
    /// Free tiles with a high-voltage cable, adjacent to this station's SMES units.
    /// </summary>
    /// <summary>
    /// Tiles on the station's main power grid where a generator can be placed, in preference order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first version searched only near an SMES, and that was a mistake.</b> Analysing all
    /// thirteen rotation maps showed that SMES banks are surrounded by cable terminals, catwalks
    /// and substations almost wall-to-wall: on Sushi the number of suitable tiles came out to
    /// <b>zero</b> — meaning the system would have silently placed nothing — one on Oasis, two on
    /// Marathon, and on Bagel four out of five ended up in distant solar bays instead of
    /// engineering. Proximity to an SMES is not where the free tiles are.
    /// </para>
    /// <para>
    /// So we search the <b>entire main grid</b> instead. "Main" is the one with the most SMES
    /// units; this fixes the second mistake of that same version: it did not distinguish grids and
    /// could place a generator on an isolated solar island with not a single consumer on it (Packed
    /// has exactly such an SMES, with thirty-one panels and no APC).
    /// </para>
    /// <para>
    /// The grid is taken from the runtime rather than by walking the cable graph: every node has a
    /// <c>Node.NodeGroup</c>, and the engine has already computed connectivity for us.
    /// </para>
    /// </remarks>
    private List<EntityCoordinates> PlacementTiles(EntityUid station, List<string>? anchors)
    {
        var result = new List<EntityCoordinates>();

        if (MainNet(station) is not { } net)
            return result;

        var targets = anchors is { Count: > 0 } ? AnchorPositions(station, anchors) : new List<Vector2>();
        if (targets.Count == 0)
            targets = SmesPositions(station);

        var seen = new HashSet<(EntityUid Grid, Vector2i Tile)>();
        var candidates = new List<(EntityCoordinates Coords, float Score)>();

        var query = EntityQueryEnumerator<CableComponent, NodeContainerComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var cable, out var container, out var xform))
        {
            if (cable.CableType != CableType.HighVoltage)
                continue;

            if (!_nodes.TryGetNode<Node>(container, "power", out var node) || node.NodeGroup != net)
                continue;

            if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
                continue;

            var tile = _map.CoordinatesToTile(gridUid, grid, xform.Coordinates);

            if (!seen.Add((gridUid, tile)))
                continue;

            // Upstream's own standard check, not a custom one. The previous version considered any
            // anchored neighbour as occupying the tile, including a catwalk or a pipe — physically
            // a generator sits fine next to those, whereas a wall or a machine genuinely blocks it.
            if (!_anchorable.TileFree((gridUid, grid), tile,
                    (int) CollisionGroup.MachineLayer, (int) CollisionGroup.MachineMask))
            {
                continue;
            }

            var coords = _map.ToCoordinates(gridUid, tile, grid);
            candidates.Add((coords, Score(_xform.ToMapCoordinates(coords).Position)));
        }

        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));

        foreach (var (coords, _) in candidates)
            result.Add(coords);

        return result;

        float Score(Vector2 at)
        {
            if (targets.Count == 0)
                return 0f;

            var best = float.MaxValue;

            foreach (var target in targets)
                best = MathF.Min(best, (target - at).LengthSquared());

            return best;
        }
    }

    /// <summary>
    /// The station's main power grid — the one with the most SMES units.
    /// </summary>
    /// <remarks>
    /// "The most SMES units", not "the first one found": every map has solar islands with their own
    /// SMES, and landing a generator there means powering a grid with no consumers.
    /// </remarks>
    private INodeGroup? MainNet(EntityUid station)
    {
        var votes = new Dictionary<INodeGroup, int>();
        var query = EntityQueryEnumerator<SmesComponent, NodeContainerComponent>();

        while (query.MoveNext(out var smes, out _, out var container))
        {
            if (_station.GetOwningStation(smes) != station)
                continue;

            if (!_nodes.TryGetNode<Node>(container, "output", out var node) || node.NodeGroup == null)
                continue;

            votes[node.NodeGroup] = votes.GetValueOrDefault(node.NodeGroup) + 1;
        }

        INodeGroup? best = null;
        var bestCount = 0;

        foreach (var (group, count) in votes)
        {
            if (count <= bestCount)
                continue;

            best = group;
            bestCount = count;
        }

        return best;
    }

    /// <summary>
    /// World positions of the station's beacons whose name matches one of the named ones.
    /// </summary>
    /// <remarks>
    /// A case-insensitive substring match: the list has "Engineering", but on the map a beacon might
    /// be named "Engineering Storage". Requiring an exact match would mean the preference silently
    /// fails to work on half the maps.
    /// </remarks>
    private List<Vector2> AnchorPositions(EntityUid station, List<string> anchors)
    {
        var result = new List<Vector2>();
        var query = EntityQueryEnumerator<Content.Shared.Pinpointer.NavMapComponent, TransformComponent>();

        while (query.MoveNext(out var gridUid, out var navMap, out _))
        {
            if (_station.GetOwningStation(gridUid) != station)
                continue;

            foreach (var beacon in navMap.Beacons.Values)
            {
                foreach (var wanted in anchors)
                {
                    if (string.IsNullOrWhiteSpace(wanted))
                        continue;

                    if (beacon.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(_xform.ToMapCoordinates(new EntityCoordinates(gridUid, beacon.Position)).Position);
                        break;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>World positions of the station's SMES units — the fallback preference when there are no beacons.</summary>
    private List<Vector2> SmesPositions(EntityUid station)
    {
        var result = new List<Vector2>();
        var query = EntityQueryEnumerator<SmesComponent, TransformComponent>();

        while (query.MoveNext(out var smes, out _, out var xform))
        {
            if (_station.GetOwningStation(smes) == station)
                result.Add(_xform.GetWorldPosition(xform));
        }

        return result;
    }
}
