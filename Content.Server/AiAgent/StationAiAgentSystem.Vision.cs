using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.AiAgent.Tools;
using Content.Shared.Access.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Power.Components;
using Content.Shared.Silicons.StationAi;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Power.Components;
using Content.Shared.SurveillanceCamera.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Server.AiAgent;

/// <summary>
/// The vision bridge and the device gate chain — the two things every phase-2 tool stands on.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// What the AI can actually see, as entities.
    ///
    /// Upstream gives us half of this: <c>StationAiVisionSystem.GetView</c> returns the visible
    /// <em>tile indices</em> using its shadowcasting algorithm, and there is no "entities I can
    /// see" API anywhere. The other half is <c>EntityLookupSystem.GetLocalEntitiesIntersecting</c>,
    /// which happens to accept exactly an <c>IEnumerable&lt;Vector2i&gt;</c> of grid indices — so
    /// the two compose into a single bridge with no approximation in between.
    ///
    /// Runs on the main thread inside the tick, and <c>GetView</c> carries an upstream comment
    /// reading "yes this is expensive. Yes it needs optimising", so callers are rate-limited by
    /// the agent's tick rather than being free to spam it.
    /// </summary>
    private List<EntityUid> GetVisibleEntities(EntityUid brain, float expansion, out string? failure)
    {
        failure = null;
        var result = new List<EntityUid>();

        if (!_stationAi.TryGetCore(brain, out var core) || core.Comp?.RemoteEntity == null)
        {
            failure = "нет доступа к ядру — камеры недоступны";
            return result;
        }

        var eye = core.Comp.RemoteEntity.Value;
        var eyeXform = Transform(eye);
        var gridUid = eyeXform.GridUid;

        if (gridUid == null
            || !TryComp<MapGridComponent>(gridUid, out var mapGrid)
            || !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
        {
            failure = "глаз не на станции";
            return result;
        }

        var worldPos = _xform.GetWorldPosition(eyeXform);
        var half = expansion;
        var bounds = new Box2Rotated(
            new Box2(worldPos.X - half, worldPos.Y - half, worldPos.X + half, worldPos.Y + half),
            Angle.Zero,
            worldPos);

        var tiles = new HashSet<Vector2i>();
        _vision.GetView((gridUid.Value, broadphase, mapGrid), bounds, tiles);

        if (tiles.Count == 0)
            return result;

        var entities = _lookup.GetLocalEntitiesIntersecting(gridUid.Value, tiles);

        foreach (var uid in entities)
        {
            if (uid == eye || uid == brain || uid == core.Owner)
                continue;

            result.Add(uid);
        }

        return result;
    }

    /// <summary>
    /// Turn a map position into grid coordinates the eye may legally occupy.
    ///
    /// Same camera-coverage test the entity path uses, so reaching a point by number is no more
    /// permissive than reaching it by handle — a tile no camera watches refuses either way.
    /// </summary>
    private bool TryPointOnGrid(EntityUid eye, float x, float y, out EntityCoordinates coords, out ToolResult? failure)
    {
        coords = default;
        failure = null;

        var eyeXform = Transform(eye);
        var gridUid = eyeXform.GridUid;

        if (gridUid == null
            || !TryComp<MapGridComponent>(gridUid, out var mapGrid)
            || !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
        {
            failure = ToolResult.Fail(ToolError.NotVisible, "глаз не на станции");
            return false;
        }

        var point = new MapCoordinates(new Vector2(x, y), eyeXform.MapID);
        var candidate = _xform.ToCoordinates(gridUid.Value, point);
        var tile = _mapSystem.LocalToTile(gridUid.Value, mapGrid, candidate);

        // A point over open space is not a camera problem, it is a different structure entirely —
        // the arrivals shuttle, a salvage wreck, someone drifting outside. Saying "no cameras there"
        // sends the model hunting for a camera that could never exist, and the crew gets told to
        // walk somewhere that will not help.
        if (!_mapSystem.TryGetTileRef(gridUid.Value, mapGrid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
        {
            failure = ToolResult.Fail(ToolError.NotVisible,
                string.Create(CultureInfo.InvariantCulture,
                    $"точка ({x:F0},{y:F0}) не на твоей станции — это другой корабль, обломок или открытый космос; " +
                    $"туда ты не видишь в принципе"),
                retry: "other_target");
            return false;
        }

        if (!_vision.IsAccessible((gridUid.Value, broadphase, mapGrid), tile, fastPath: false))
        {
            failure = ToolResult.Fail(ToolError.NotVisible,
                string.Create(CultureInfo.InvariantCulture,
                    $"в точку ({x:F0},{y:F0}) не добивают камеры"),
                retry: "other_target");
            return false;
        }

        coords = candidate;
        return true;
    }

    /// <summary>
    /// Cardinal directions in Russian, keyed the way the crew actually talks.
    ///
    /// North is up the screen. That equivalence is what makes "открой дверь надо мной" answerable
    /// at all: the client renders the station world-aligned with no eye rotation, so world north
    /// and screen up are the same thing for both the player and the AI.
    /// </summary>
    private static string DirectionRu(Direction dir) => dir switch
    {
        Direction.North => "север",
        Direction.NorthEast => "северо-восток",
        Direction.East => "восток",
        Direction.SouthEast => "юго-восток",
        Direction.South => "юг",
        Direction.SouthWest => "юго-запад",
        Direction.West => "запад",
        Direction.NorthWest => "северо-запад",
        _ => "рядом",
    };

    /// <summary>
    /// Where <paramref name="target"/> lies as seen from <paramref name="from"/>: "север 3".
    ///
    /// Within half a tile there is no meaningful bearing — the two are on the same square, which is
    /// what "прямо рядом" means to a person describing their surroundings.
    /// </summary>
    private static string BearingFrom(Vector2 from, Vector2 target)
    {
        var delta = target - from;
        var dist = delta.Length();

        if (dist < 0.5f)
            return "вплотную";

        return string.Create(CultureInfo.InvariantCulture, $"{DirectionRu(delta.GetDir())} {dist:F0}");
    }

    /// <summary>Which way a mob is facing, for "открой дверь, на которую я смотрю".</summary>
    private string FacingRu(EntityUid uid) => DirectionRu(_xform.GetWorldRotation(uid).GetDir());

    /// <summary>Classify an entity into a handle kind. Order matters: most specific first.</summary>
    private string KindOf(EntityUid uid)
    {
        if (HasComp<MobStateComponent>(uid))
            return "crew";
        if (HasComp<DoorComponent>(uid))
            return "door";
        if (HasComp<ApcComponent>(uid))
            return "apc";
        if (HasComp<SurveillanceCameraComponent>(uid))
            return "camera";
        if (HasComp<Content.Shared.Holopad.HolopadComponent>(uid))
            return "holopad";
        if (HasComp<Content.Shared.TurretController.DeployableTurretControllerComponent>(uid))
            return "turretctl";
        if (HasComp<Content.Shared.Turrets.DeployableTurretComponent>(uid))
            return "turret";
        if (HasComp<AirAlarmComponent>(uid))
            return "airalarm";
        if (HasComp<StationAiWhitelistComponent>(uid))
            return "device";

        return "thing";
    }

    /// <summary>
    /// Walk the same gate chain a human player's click walks, and report which link refused.
    ///
    /// The order is not arbitrary — it mirrors <c>SharedStationAiSystem.Held.cs::OnHeldInteraction</c>
    /// followed by <c>OnAiBuiCheck</c> and the per-device <c>AccessReaderSystem.IsAllowed</c> call.
    /// Skipping any of it would make the LLM strictly more capable than a player, which is the one
    /// thing this whole design is trying not to do.
    /// </summary>
    private DeviceGate CheckGate(EntityUid brain, EntityUid target, AgentMode mode, bool needAccess = true)
    {
        if (!IsPlayable(brain))
            return DeviceGate.Dead;

        if (mode == AgentMode.Carded)
            return DeviceGate.Carded;

        if (!TryComp<StationAiWhitelistComponent>(target, out var whitelist))
            return DeviceGate.NotWhitelisted;

        if (!whitelist.Enabled)
            return DeviceGate.WireCut;

        if (!_power.IsPowered(target))
            return DeviceGate.Unpowered;

        if (!IsVisibleToAi(brain, target))
            return DeviceGate.NotVisible;

        if (needAccess && !_access.IsAllowed(brain, target))
            return DeviceGate.NoAccess;

        return DeviceGate.Ok;
    }

    /// <summary>
    /// Same-grid plus camera coverage — the gate a human player's click passes through.
    ///
    /// <b>Deliberately not <c>fastPath: true</c>.</b> That branch of <c>ViewJob</c> is broken for
    /// any grid that is not sitting at the world origin: it builds the seed's coverage circle from
    /// <c>GetWorldPosition</c> and hands it to <c>GetLocalTilesIntersecting</c>, which expects grid
    /// coordinates. On a station loaded at, say, (-570, 86) the circle lands hundreds of tiles away
    /// in empty space, no tile is ever visible, and every gated tool refuses — a door one tile from
    /// the eye reports "no cameras". It costs nothing on a test grid at the origin, which is exactly
    /// why the benchmarks were green while the live station could not open a single door.
    ///
    /// The slow path derives the seed tile with <c>GetTileRef(..., seedXform.Coordinates)</c> —
    /// grid-local, correct — and is what upstream's own interaction check (<c>OnAiInRange</c>) uses.
    /// It also runs the occluder sweep, so the answer respects walls, which is the honest reading of
    /// "can the AI see this" anyway.
    /// </summary>
    private bool IsVisibleToAi(EntityUid brain, EntityUid target)
    {
        if (!_stationAi.TryGetCore(brain, out var core) || core.Comp?.RemoteEntity == null)
            return false;

        var eyeXform = Transform(core.Comp.RemoteEntity.Value);
        var targetXform = Transform(target);

        if (eyeXform.GridUid == null || eyeXform.GridUid != targetXform.GridUid)
            return false;

        var gridUid = eyeXform.GridUid.Value;
        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid) ||
            !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
            return false;

        var tile = _mapSystem.LocalToTile(gridUid, mapGrid, targetXform.Coordinates);
        return _vision.IsAccessible((gridUid, broadphase, mapGrid), tile, fastPath: false);
    }
}
