using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vision;
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
/// Where look's time actually goes.
///
/// "look is slow" is not a diagnosis. There's a two-order-of-magnitude gap between upstream's
/// shadow-cast and gathering entities, and without a breakdown, the argument about what to fix is
/// settled by reasoning rather than by a number. A live server gave 111 calls and 111 budget
/// overruns in a day, a median of 98 ms and a max of 1908 — but said not a word about where exactly
/// that time went.
///
/// <see cref="Queries"/> is here for reasons other than timing. It's a counter of trips into
/// broadphase, and it must equal one no matter how many tiles the view returned. This is the one the
/// test guards: milliseconds on the build machine measure the hardware and are noisy, while the
/// count of one measures the algorithm and isn't noisy at all.
/// </summary>
internal struct LookProfile
{
    /// <summary>Phase A1: upstream's shadow-cast, <c>StationAiVisionSystem.GetView</c>.</summary>
    public double ViewMs;

    /// <summary>Phase A2: turning tiles into entities.</summary>
    public double GatherMs;

    /// <summary>Phases B..E: filtering, handles, building the lines, sorting.</summary>
    public double RowsMs;

    /// <summary>How many visible tiles the view returned.</summary>
    public int Tiles;

    /// <summary>How many entities broadphase handed back before filtering.</summary>
    public int Candidates;

    /// <summary>How many survived <c>IsOnScreen</c> and the visible-tile membership check.</summary>
    public int OnScreen;

    /// <summary>How many lines went to the model.</summary>
    public int Rows;

    /// <summary>Trips into broadphase. Invariant for the fast path: 1.</summary>
    public int Queries;
}

/// <summary>
/// The vision bridge and the device gate chain — the two things every phase-2 tool stands on.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// A reusable candidate buffer.
    ///
    /// Not a fresh <c>HashSet</c> per call: the view is only ever built on the main thread and only
    /// from <c>LookAsync</c>, so there's no reentrancy by construction. On a big station this can
    /// hold close to a thousand entities, and throwing them away along with the set on every call
    /// means handing the GC work it doesn't need to do.
    /// </summary>
    private readonly HashSet<EntityUid> _lookCandidates = new();

    /// <summary>
    /// How many tiles wide and tall we're willing to scan for the sake of one multi-tile entity.
    ///
    /// A ceiling, not a typical size: real machines on the station take up one or two tiles, three
    /// is the singularity with its field. Eight was picked with an order of magnitude of headroom.
    /// If something doesn't fit, it's better to give up on it than to scan half the station for one
    /// entity and drag the tick right back to the thing this whole exercise was meant to fix.
    /// </summary>
    private const int MaxSpanTiles = 8;

    /// <summary>
    /// What the AI can actually see, as entities.
    ///
    /// Upstream gives us half of this: <c>StationAiVisionSystem.GetView</c> returns the visible
    /// <em>tile indices</em> using its shadowcasting algorithm, and there is no "entities I can
    /// see" API anywhere. The other half used to be
    /// <c>EntityLookupSystem.GetLocalEntitiesIntersecting(grid, IEnumerable&lt;Vector2i&gt;)</c>,
    /// which happens to accept exactly a set of grid indices — so the two composed into a single
    /// bridge with no approximation in between.
    ///
    /// The composition looked elegant and cost a second of the tick. Why, in detail, is in
    /// <see cref="GatherByBounds"/>; in short: that overload's own comment promises "Faster than
    /// doing each tile individually" and then literally does each tile individually, re-walking the
    /// entire accumulated set on each one.
    ///
    /// Runs on the main thread inside the tick, and <c>GetView</c> carries an upstream comment
    /// reading "yes this is expensive. Yes it needs optimising", so callers are rate-limited by
    /// the agent's tick rather than being free to spam it.
    /// </summary>
    /// <param name="fastOverride">
    /// Force a specific gathering path instead of whatever <see cref="AiCVars.LookFast"/> dictates.
    /// Only for the equivalence test: it needs to run both paths within ONE frame, otherwise it's
    /// comparing two different seconds of the station's life and catching someone's footsteps
    /// instead of geometry.
    /// </param>
    private List<EntityUid> GetVisibleEntities(
        EntityUid brain,
        float expansion,
        out string? failure,
        ref LookProfile profile,
        bool? fastOverride = null)
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

        var viewStart = Stopwatch.GetTimestamp();
        _vision.GetView((gridUid.Value, broadphase, mapGrid), bounds, tiles);
        profile.ViewMs = Stopwatch.GetElapsedTime(viewStart).TotalMilliseconds;
        profile.Tiles = tiles.Count;

        if (tiles.Count == 0)
            return result;

        var gatherStart = Stopwatch.GetTimestamp();

        if (fastOverride ?? _cfg.GetCVar(AiCVars.LookFast))
            GatherByBounds(gridUid.Value, mapGrid, tiles, eye, brain, core.Owner, result, ref profile);
        else
            GatherByTile(gridUid.Value, tiles, eye, brain, core.Owner, result, ref profile);

        profile.GatherMs = Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;
        profile.OnScreen = result.Count;

        return result;
    }

    /// <summary>
    /// A view pass stretched over several frames.
    ///
    /// Holds what's computed once at the start (the eye, the grid, the core) and survives the slices
    /// together with the <see cref="SlicedView"/> itself. It's its own object rather than fields on
    /// the system because there can be several passes in flight at once: each agent has its own, and
    /// stashing them on the system would mean catching someone else's tiles once
    /// <c>ai.max_agents &gt; 1</c>.
    /// </summary>
    private sealed class ViewPass
    {
        public required SlicedView View;
        public required EntityUid Grid;
        public required MapGridComponent MapGrid;
        public required EntityUid Eye;
        public required EntityUid Core;
        public double ViewMs;
    }

    /// <summary>
    /// Resolve the eye and the grid. Shared code for the atomic and the sliced paths — so they don't
    /// drift apart on what counts as "the eye"; a divergence here would be a divergence in parity.
    /// </summary>
    private bool TryResolveEye(
        EntityUid brain,
        out EntityUid eye,
        out EntityUid core,
        out EntityUid grid,
        out BroadphaseComponent broadphase,
        out MapGridComponent mapGrid,
        out string? failure)
    {
        eye = default;
        core = default;
        grid = default;
        broadphase = default!;
        mapGrid = default!;

        if (!_stationAi.TryGetCore(brain, out var found) || found.Comp?.RemoteEntity == null)
        {
            failure = "нет доступа к ядру — камеры недоступны";
            return false;
        }

        core = found.Owner;
        eye = found.Comp.RemoteEntity.Value;

        var gridUid = Transform(eye).GridUid;

        if (gridUid == null
            || !TryComp(gridUid, out MapGridComponent? foundGrid)
            || !TryComp(gridUid, out BroadphaseComponent? foundBroadphase))
        {
            failure = "глаз не на станции";
            return false;
        }

        grid = gridUid.Value;
        mapGrid = foundGrid;
        broadphase = foundBroadphase;
        failure = null;
        return true;
    }

    /// <summary>
    /// Start a view pass. There's nothing heavy here: just resolving the eye and the bounds.
    /// </summary>
    private ViewPass? BeginSlicedView(EntityUid brain, float expansion, out string? failure)
    {
        if (!TryResolveEye(brain, out var eye, out var core, out var grid, out var broadphase, out var mapGrid, out failure))
            return null;

        var worldPos = _xform.GetWorldPosition(Transform(eye));
        var bounds = new Box2Rotated(
            new Box2(worldPos.X - expansion, worldPos.Y - expansion, worldPos.X + expansion, worldPos.Y + expansion),
            Angle.Zero,
            worldPos);

        var view = new SlicedView(EntityManager, _lookup, _mapSystem, _xform, _power);
        view.Begin((grid, broadphase, mapGrid), bounds, expansion);

        return new ViewPass
        {
            View = view,
            Grid = grid,
            MapGrid = mapGrid,
            Eye = eye,
            Core = core,
        };
    }

    /// <summary>
    /// Gather entities from already-computed tiles. The same gathering step as the atomic path —
    /// there's no reason to slice it: per the profile it's 3-4 ms against 18-22 for the cast.
    /// </summary>
    private List<EntityUid> GatherFromPass(ViewPass pass, EntityUid brain, ref LookProfile profile)
    {
        var result = new List<EntityUid>();

        profile.ViewMs = pass.ViewMs;
        profile.Tiles = pass.View.VisibleTiles.Count;

        if (pass.View.VisibleTiles.Count == 0)
            return result;

        var gatherStart = Stopwatch.GetTimestamp();

        if (_cfg.GetCVar(AiCVars.LookFast))
            GatherByBounds(pass.Grid, pass.MapGrid, pass.View.VisibleTiles, pass.Eye, brain, pass.Core, result, ref profile);
        else
            GatherByTile(pass.Grid, pass.View.VisibleTiles, pass.Eye, brain, pass.Core, result, ref profile);

        profile.GatherMs = Stopwatch.GetElapsedTime(gatherStart).TotalMilliseconds;
        profile.OnScreen = result.Count;

        return result;
    }

    /// <summary>
    /// The fast path: one tree traversal over the overall bounds, then filtering by tile membership.
    ///
    /// <para>
    /// The slow path asked broadphase "who intersects this specific tile" and did that once per
    /// visible tile — anywhere from 289 at <c>expand:0</c> to 1681 at <c>expand:3</c>. For each tile
    /// that's up to four tree traversals, a narrow-phase check per candidate, and a call to
    /// <c>AddContained</c>, which re-walks the ENTIRE accumulated set, asks every element for its
    /// <c>ContainerManagerComponent</c>, and recursively adds the contents of every container — with
    /// a fresh list on every single tile. With T tiles and a set of E entities that's O(T·E) in time
    /// and O(T) allocations inside the tick. That's where the 1908 ms came from: T and E grow
    /// together, so the product scales as the fourth power of the radius.
    /// </para>
    /// <para>
    /// Here it's the other way around: candidates are gathered all at once, and only then is each
    /// one asked where it stands. One trip into the tree instead of a thousand, followed by a linear
    /// pass.
    /// </para>
    /// <para>
    /// <b>The answer isn't poorer for it, and that's provable, not eyeballed.</b> Upstream tested
    /// the fixture against a tile shrunk by <c>TileEnlargementRadius</c> (a negative value) — we test
    /// the entity's bounding box against the unshrunk tile. The box ⊇ the fixture, the unshrunk tile
    /// ⊇ the shrunk one, so the new set is a superset of the old one. The only possible error is in
    /// the direction of "an extra one got included at the boundary", and for parity that direction is
    /// the safe one: a sprite on the player's screen is drawn from its position, not its fixture, so
    /// a bounding-box check is actually closer to "what a human sees" than the narrow phase is. The
    /// equivalence test runs both paths and requires strict inclusion.
    /// </para>
    /// </summary>
    private void GatherByBounds(
        EntityUid gridUid,
        MapGridComponent mapGrid,
        HashSet<Vector2i> tiles,
        EntityUid eye,
        EntityUid brain,
        EntityUid coreUid,
        List<EntityUid> result,
        ref LookProfile profile)
    {
        var min = new Vector2i(int.MaxValue, int.MaxValue);
        var max = new Vector2i(int.MinValue, int.MinValue);

        foreach (var tile in tiles)
        {
            min = Vector2i.ComponentMin(min, tile);
            max = Vector2i.ComponentMax(max, tile);
        }

        var size = mapGrid.TileSize;
        var box = new Box2(min.X * size, min.Y * size, (max.X + 1) * size, (max.Y + 1) * size);

        // Contained is dropped deliberately, and that removes nothing from the answer: IsOnScreen
        // rejects everything sitting in a container anyway — backpacks, lockers, machine innards. We
        // were paying a quadratic cost for something that never once made it into the answer.
        //
        // Approximate is also deliberate. A narrow phase against the overall bounds is pointless:
        // membership in a specific tile is something we check ourselves anyway, and more strictly.
        _lookCandidates.Clear();
        _lookup.GetLocalEntitiesIntersecting(gridUid, box, _lookCandidates,
            LookupFlags.Uncontained | LookupFlags.Approximate);
        profile.Queries++;
        profile.Candidates = _lookCandidates.Count;

        // Computed once for the whole view: it will have to be applied to every candidate.
        var invGrid = _xform.GetInvWorldMatrix(gridUid);

        foreach (var uid in _lookCandidates)
        {
            if (uid == eye || uid == brain || uid == coreUid)
                continue;

            // IsOnScreen BEFORE the geometry, not after. The bulk of candidates on a station are
            // cables, pipes and disposal lines under the plating: they're SubFloorHide, get cut by
            // one comparison, and never reach the tile arithmetic at all.
            if (!IsOnScreen(uid))
                continue;

            if (!CoversVisibleTile(uid, size, invGrid, tiles))
                continue;

            result.Add(uid);
        }
    }

    /// <summary>
    /// The slow path. Exists for the equivalence test and for a switch-flip rollback in production —
    /// see <see cref="AiCVars.LookFast"/>. Do not add new code here.
    /// </summary>
    private void GatherByTile(
        EntityUid gridUid,
        HashSet<Vector2i> tiles,
        EntityUid eye,
        EntityUid brain,
        EntityUid coreUid,
        List<EntityUid> result,
        ref LookProfile profile)
    {
        var entities = _lookup.GetLocalEntitiesIntersecting(gridUid, tiles);

        profile.Queries += tiles.Count;
        profile.Candidates = entities.Count;

        foreach (var uid in entities)
        {
            if (uid == eye || uid == brain || uid == coreUid)
                continue;

            // IsOnScreen lives here, not at the call site, so both paths hand back the SAME kind of
            // set and the equivalence test compares like with like. This filter used to live in
            // LookAsync; moving it here changes nothing about the result (the check is pure and
            // idempotent), but it makes the claim "the fast path lost nothing" verifiable in one
            // line instead of reassembling the conditions inside the test.
            if (!IsOnScreen(uid))
                continue;

            result.Add(uid);
        }
    }

    /// <summary>
    /// Does the entity land on even one tile of the visible set.
    ///
    /// First the transform's own tile — for the overwhelming majority that's the only one, and it's
    /// a single matrix multiplication. The bounding box is only computed for misses, and a miss here
    /// means one of two things: either a multi-tile machine at the edge of visibility whose center
    /// happens to land on a tile hidden behind a wall — which must not be lost, it's on the player's
    /// screen — or honest junk behind a wall that should indeed be filtered out. Misses are rare, so
    /// the expensive branch is almost never taken.
    /// </summary>
    private bool CoversVisibleTile(EntityUid uid, ushort size, Matrix3x2 invGrid, HashSet<Vector2i> tiles)
    {
        var world = _xform.GetWorldPosition(uid);
        var local = Vector2.Transform(world, invGrid);

        var tile = new Vector2i(
            (int) MathF.Floor(local.X / size),
            (int) MathF.Floor(local.Y / size));

        if (tiles.Contains(tile))
            return true;

        // All FOUR corners, not two opposite ones.
        //
        // The bounding box arrives axis-aligned to the WORLD, while the station sits at an angle —
        // Box, for instance, is rotated, and that's visible to the naked eye just from the tile count
        // in the view. After converting to grid coordinates, the rectangle turns into a rhombus whose
        // extreme points are not the same two corners that were extreme in world space. The
        // two-corner version was losing a lamp sitting right on a tile seam: the range came out
        // skewed and didn't cover the one tile that was actually visible. One lost item out of two
        // and a half thousand — exactly the kind of breakage a test catches and a human doesn't.
        var aabb = _lookup.GetWorldAABB(uid);

        var c0 = Vector2.Transform(aabb.BottomLeft, invGrid);
        var c1 = Vector2.Transform(aabb.BottomRight, invGrid);
        var c2 = Vector2.Transform(aabb.TopLeft, invGrid);
        var c3 = Vector2.Transform(aabb.TopRight, invGrid);

        var minX = MathF.Min(MathF.Min(c0.X, c1.X), MathF.Min(c2.X, c3.X));
        var minY = MathF.Min(MathF.Min(c0.Y, c1.Y), MathF.Min(c2.Y, c3.Y));
        var maxX = MathF.Max(MathF.Max(c0.X, c1.X), MathF.Max(c2.X, c3.X));
        var maxY = MathF.Max(MathF.Max(c0.Y, c1.Y), MathF.Max(c2.Y, c3.Y));

        var x0 = (int) MathF.Floor(minX / size);
        var y0 = (int) MathF.Floor(minY / size);
        var x1 = (int) MathF.Floor(maxX / size);
        var y1 = (int) MathF.Floor(maxY / size);

        if (x1 - x0 >= MaxSpanTiles || y1 - y0 >= MaxSpanTiles)
            return false;

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            if (tiles.Contains(new Vector2i(x, y)))
                return true;
        }

        return false;
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
            failure = ToolResult.Fail(ToolError.Internal,
                "твой глаз сейчас не на станции — вернись к ядру через jump_to_core", retry: "later");
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
                    $"в точку ({x:F0},{y:F0}) {DeviceGateExt.NoCameraDetail}"),
                retry: "other_target");
            return false;
        }

        coords = candidate;
        return true;
    }

    /// <summary>
    /// Cardinal directions in the agent's prompt language, keyed the way the crew actually talks.
    ///
    /// North is up the screen. That equivalence is what makes "открой дверь надо мной" answerable
    /// at all: the client renders the station world-aligned with no eye rotation, so world north
    /// and screen up are the same thing for both the player and the AI.
    /// </summary>
    private static string DirectionWord(Direction dir, Locale.AgentLocale loc) => loc.Dir(dir);

    /// <summary>
    /// Where <paramref name="target"/> lies as seen from <paramref name="from"/>: "север 3" / "north 3".
    ///
    /// Within half a tile there is no meaningful bearing — the two are on the same square, which is
    /// what "прямо рядом" means to a person describing their surroundings.
    /// </summary>
    private static string BearingFrom(Vector2 from, Vector2 target, Locale.AgentLocale? loc = null)
    {
        loc ??= Locale.AgentLocale.Ru;
        var delta = target - from;
        var dist = delta.Length();

        if (dist < 0.5f)
            return loc.Adjacent;

        return string.Create(CultureInfo.InvariantCulture, $"{DirectionWord(delta.GetDir(), loc)} {dist:F0}");
    }

    /// <summary>
    /// Where a thing is, as two pairs of numbers: the offset from whatever the listing is measured
    /// from, and the absolute position.
    ///
    /// The offset replaces an eight-way bearing with a rounded distance, which threw away exactly
    /// the part that settles "which door am I standing at" — <c>(3,1)</c> and <c>(1,3)</c> both
    /// printed as "северо-восток 3", and on a station those are two doors in two different walls.
    /// One live request returned fifty-five doors, four of them tied at the same bearing and
    /// distance and most named "secure windoor"; the agent opened the wrong one twice, then gave up.
    ///
    /// The absolute pair is there because it is what <c>move_camera</c> takes. Without it, looking
    /// at something and then going to see it meant a separate <c>map</c> call and a guess.
    /// </summary>
    private static string PositionFrom(Vector2 from, Vector2 target) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Δ({target.X - from.X:F0},{target.Y - from.Y:F0}) ({target.X:F0},{target.Y:F0})");

    /// <summary>Which way a mob is facing, for "открой дверь, на которую я смотрю".</summary>
    private string FacingWord(EntityUid uid, Locale.AgentLocale loc) =>
        DirectionWord(_xform.GetWorldRotation(uid).GetDir(), loc);

    /// <summary>Classify an entity into a handle kind. Order matters: most specific first.</summary>
    public string KindOf(EntityUid uid)
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

        // Everything below this line is scenery the AI cannot operate — and it used to be dropped
        // from look entirely. That made the agent blind to exactly what the crew asks about: it
        // told an engineer standing beside the SMES bank that no such device was in view. A player
        // sees the whole room; withholding the uncontrollable half is not parity, it is a handicap.
        if (HasComp<Content.Server.Power.Components.PowerNetworkBatteryComponent>(uid)
            || HasComp<Content.Shared.Power.Components.BatteryComponent>(uid))
            return "power";
        if (HasComp<Content.Shared.Atmos.Piping.Unary.Components.GasCanisterComponent>(uid))
            return "canister";
        if (HasComp<Content.Server.Construction.Components.ComputerComponent>(uid))
            return "computer";
        if (HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(uid))
            return "locker";

        return "obj";
    }

    /// <summary>
    /// Would a player looking at this tile see the thing at all?
    ///
    /// Handing the model literally every entity in the broadphase is not "what a player sees": it
    /// is also the cable under the floor, the pipe beneath it and the spare pen inside someone's
    /// backpack. Those are invisible on screen, and listing them buries the room the crew is
    /// actually asking about.
    /// </summary>
    private bool IsOnScreen(EntityUid uid)
    {
        // Under the plating: cables, pipes, disposals. Hidden from players too.
        if (HasComp<Content.Shared.SubFloor.SubFloorHideComponent>(uid))
            return false;

        // Inside a bag, a locker or a machine — including the AI's own brain in its core.
        if (_container.IsEntityInContainer(uid))
            return false;

        // Markers, spawn points and other invisible bookkeeping carry no name worth reporting.
        return !string.IsNullOrWhiteSpace(Name(uid));
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
