using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Tools;
using Content.Shared.Access.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Power.Components;
using Content.Shared.Silicons.StationAi;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Power.Components;
using Content.Shared.SurveillanceCamera.Components;
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
    /// Same-grid plus camera coverage, matching <c>OnAiBuiCheck</c>'s check exactly — including
    /// its <c>fastPath: true</c>, which skips occlusion and tests range only.
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
        return _vision.IsAccessible((gridUid, broadphase, mapGrid), tile, fastPath: true);
    }
}
