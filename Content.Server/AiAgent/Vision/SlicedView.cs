using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Content.Server.AiAgent.Threading;
using Content.Shared.Power.EntitySystems;
using Content.Shared.StationAi;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;
using Content.Shared.Silicons.StationAi;

namespace Content.Server.AiAgent.Vision;

/// <summary>
/// The station AI's view, computed IN SLICES — as much per frame as the budget allows.
///
/// <para>
/// <b>Why a private copy of upstream's <c>StationAiVisionSystem.GetView</c> was needed.</b>
/// That one computes everything in one indivisible call. As long as each <c>look</c> cost a round
/// trip through the model, this was tolerable: one call per agent roughly every fourteen seconds,
/// and our own comment right next to it noted that calls were "bounded by the agent's tick and
/// can't be spammed". Lua mode removed that assumption — a script calls <c>look()</c> in a loop —
/// and in a live round on 2026-08-20 the main thread took 280 budget overruns: an average of 28 ms
/// per call against a 33 ms frame, worst case 85.
/// </para>
/// <para>
/// A per-phase profile left no choice about where to fix it: <c>view=18.4 gather=3.4 rows=2.4</c> —
/// three quarters of the time sits in shadowcasting, not row construction. Limiting to "no more
/// than one view per frame" is pointless when a single view IS the whole frame; the cut has to be
/// made INSIDE it. That is exactly why <see cref="IWorldJob"/> has a <c>Step(JobBudget)</c>, which
/// until now had no implementation other than the atomic one.
/// </para>
/// <para>
/// <b>Why a copy, not a cache.</b> A visibility cache would have to be invalidated on every opened
/// door and every broken window, and a miss in that invalidation means an agent that doesn't see
/// someone who just walked in and behaves plausibly anyway. A copy of the algorithm is more
/// expensive to write and hides nothing: the same input gives the same output, just spread across
/// several frames.
/// </para>
/// <para>
/// <b>The algorithm was ported verbatim</b> from <c>Content.Shared/Silicons/StationAi/StationAiVisionSystem.cs</c>
/// (which was itself ported from OpenDream's <c>ViewAlgorithm.cs</c>). It must not be changed here:
/// diverging from upstream would mean the AI sees something different from what a player would see
/// in this role, which is a direct parity violation. Equivalence is guarded by a test that runs
/// both implementations against the same station in the same frame and requires the resulting tile
/// sets to match tile for tile.
/// </para>
/// </summary>
public sealed class SlicedView
{
    /// <summary>How many tiles we check for opacity between budget checks.</summary>
    /// <remarks>
    /// The budget isn't checked on every tile: <c>Stopwatch.GetTimestamp()</c> isn't free by
    /// itself, and a single <c>IsOccluded</c> is a trip into broadphase, i.e. tens of microseconds.
    /// Thirty-two per check gives a grain of about a millisecond: finer is pointless, coarser starts
    /// overrunning the budget.
    /// </remarks>
    private const int OcclusionGrain = 32;

    private enum Phase : byte
    {
        Seeds,
        Occlusion,
        Cast,
        Done,
    }

    private enum CastStage : byte
    {
        Prepare,
        Diagonal,
        Straight,
        Finish,
    }

    private readonly IEntityManager _ents;
    private readonly EntityLookupSystem _lookup;
    private readonly SharedMapSystem _maps;
    private readonly SharedTransformSystem _xforms;
    private readonly SharedPowerReceiverSystem _power;
    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<TransformComponent> _xformQuery;

    // --------------------------------------------------------------- input

    private Entity<BroadphaseComponent, MapGridComponent> _grid;
    private Box2 _localAabb;
    private Box2 _expandedAabb;

    // --------------------------------------------- state between slices

    private Phase _phase = Phase.Seeds;

    private readonly List<Entity<StationAiVisionComponent>> _data = new();
    private readonly HashSet<Entity<StationAiVisionComponent>> _seeds = new();

    /// <summary>Tiles that actually fall within the view frame. Only these end up in the result.</summary>
    private readonly HashSet<Vector2i> _viewportTiles = new();

    /// <summary>Opaque tiles. Computed in chunks — this is the most expensive phase after casting.</summary>
    private readonly HashSet<Vector2i> _opaque = new();

    /// <summary>List of tiles pending an opacity check, and the position within it.</summary>
    private readonly List<Vector2i> _pending = new();
    private int _pendingAt;

    // ------------------------------------------------- state of one cast

    private int _seedAt;
    private CastStage _stage = CastStage.Prepare;
    private int _d;
    private int _maxDepthMax;
    private int _sumDepthMax;
    private Vector2i _eyePos;
    private readonly Dictionary<Vector2i, int> _vis1 = new();
    private readonly Dictionary<Vector2i, int> _vis2 = new();
    private readonly HashSet<Vector2i> _seedTiles = new();
    private readonly HashSet<Vector2i> _boundary = new();

    /// <summary>Reusable buffer for one tile's occluders. See <see cref="IsOccluded"/>.</summary>
    private readonly HashSet<Entity<OccluderComponent>> _occluders = new();

    /// <summary>Where the result is stored. The same contract as upstream's.</summary>
    public HashSet<Vector2i> VisibleTiles { get; } = new();

    /// <summary>How many slices the computation took. For the log and for the "slicing actually works" test.</summary>
    public int Slices { get; private set; }

    /// <summary>
    /// How much time the view computation actually SPENT on the main thread, excluding pauses between frames.
    ///
    /// A wall clock is fundamentally unfit here: a sliced view lives for a second, of which about
    /// twenty milliseconds is spent on the main thread. Measuring it with an external stopwatch
    /// means declaring the fix a regression, which is exactly what happened on the very first run.
    /// </summary>
    public double BusyMs { get; private set; }

    public SlicedView(
        IEntityManager ents,
        EntityLookupSystem lookup,
        SharedMapSystem maps,
        SharedTransformSystem xforms,
        SharedPowerReceiverSystem power)
    {
        _ents = ents;
        _lookup = lookup;
        _maps = maps;
        _xforms = xforms;
        _power = power;
        _occluderQuery = ents.GetEntityQuery<OccluderComponent>();
        _xformQuery = ents.GetEntityQuery<TransformComponent>();
    }

    /// <summary>
    /// Set the input and start over. Called once before the first <see cref="Step"/>.
    /// </summary>
    public void Begin(Entity<BroadphaseComponent, MapGridComponent> grid, Box2Rotated worldBounds, float expansionSize)
    {
        _grid = grid;

        var invMatrix = _xforms.GetInvWorldMatrix(grid.Owner);
        _localAabb = invMatrix.TransformBox(worldBounds);
        _expandedAabb = invMatrix.TransformBox(worldBounds.Enlarged(expansionSize));

        _phase = Phase.Seeds;
        _data.Clear();
        _seeds.Clear();
        _viewportTiles.Clear();
        _opaque.Clear();
        _pending.Clear();
        _pendingAt = 0;
        _seedAt = 0;
        _stage = CastStage.Prepare;
        VisibleTiles.Clear();
        Slices = 0;
    }

    /// <summary>
    /// Process one slice. <c>true</c> means the view is fully computed, <see cref="VisibleTiles"/> is ready.
    /// </summary>
    public bool Step(JobBudget budget)
    {
        Slices++;
        var started = Stopwatch.GetTimestamp();

        try
        {
            switch (_phase)
            {
                case Phase.Seeds:
                    return StepSeeds();

                case Phase.Occlusion:
                    return StepOcclusion(budget);

                case Phase.Cast:
                    return StepCast(budget);

                default:
                    return true;
            }
        }
        finally
        {
            BusyMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    // ------------------------------------------------------------ seed phase

    /// <summary>
    /// Gather cameras and lay out the frame's tiles. One slice: there's exactly one broadphase
    /// traversal here, and enumerating tiles without checking opacity is cheap — we pay for
    /// <c>IsOccluded</c>.
    /// </summary>
    private bool StepSeeds()
    {
        _lookup.GetLocalEntitiesIntersecting(_grid.Owner, _expandedAabb, _seeds,
            flags: LookupFlags.All | LookupFlags.Approximate);

        foreach (var seed in _seeds)
        {
            if (!seed.Comp.Enabled)
                continue;

            if (seed.Comp.NeedsPower && !_power.IsPowered(seed.Owner))
                continue;

            if (seed.Comp.NeedsAnchoring && !_xformQuery.GetComponent(seed.Owner).Anchored)
                continue;

            _data.Add(seed);
        }

        if (_data.Count == 0)
        {
            _phase = Phase.Done;
            return true;
        }

        // The same order as upstream: first the frame's tiles, then the expanded bounding box minus
        // whatever's already in the frame. The distinction between the two lists carries meaning —
        // only the frame's tiles end up in the result, but opacity is also needed outside it, or
        // else a wall past the screen edge wouldn't cast a shadow inward.
        var viewport = _maps.GetLocalTilesEnumerator(_grid.Owner, _grid.Comp2, _localAabb, ignoreEmpty: false);
        while (viewport.MoveNext(out var tileRef))
        {
            _viewportTiles.Add(tileRef.GridIndices);
            _pending.Add(tileRef.GridIndices);
        }

        var expanded = _maps.GetLocalTilesEnumerator(_grid.Owner, _grid.Comp2, _expandedAabb, ignoreEmpty: false);
        while (expanded.MoveNext(out var tileRef))
        {
            if (_viewportTiles.Contains(tileRef.GridIndices))
                continue;

            _pending.Add(tileRef.GridIndices);
        }

        _phase = Phase.Occlusion;
        return false;
    }

    // --------------------------------------------------- opacity phase

    private bool StepOcclusion(JobBudget budget)
    {
        while (_pendingAt < _pending.Count)
        {
            var end = Math.Min(_pendingAt + OcclusionGrain, _pending.Count);

            for (; _pendingAt < end; _pendingAt++)
            {
                if (IsOccluded(_pending[_pendingAt]))
                    _opaque.Add(_pending[_pendingAt]);
            }

            if (budget.Exhausted)
                return false;
        }

        _phase = Phase.Cast;
        return false;
    }

    /// <summary>
    /// A word-for-word port of upstream's <c>IsOccluded</c>.
    ///
    /// The set is reused rather than created per tile, exactly as upstream does. In the first
    /// version it was created inside, and that cost thousands of allocations per view: the
    /// tick-budget test caught it immediately, with the worst call going past 150 ms.
    /// </summary>
    private bool IsOccluded(Vector2i tile)
    {
        var tileBounds = _lookup.GetLocalBounds(tile, _grid.Comp2.TileSize).Enlarged(-0.05f);

        var occluders = _occluders;
        occluders.Clear();
        _lookup.GetLocalEntitiesIntersecting((_grid.Owner, _grid.Comp1), tileBounds, occluders,
            query: _occluderQuery, flags: LookupFlags.Static | LookupFlags.Approximate);

        foreach (var occluder in occluders)
        {
            if (occluder.Comp.Enabled)
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------- cast phase

    private bool StepCast(JobBudget budget)
    {
        while (_seedAt < _data.Count)
        {
            if (StepOneSeed(budget))
            {
                _seedAt++;
                _stage = CastStage.Prepare;
            }

            if (budget.Exhausted)
                return false;
        }

        _phase = Phase.Done;
        return true;
    }

    /// <summary>
    /// One casting step from one camera. <c>true</c> means this camera is fully computed.
    ///
    /// Within a camera, slicing proceeds by depth <c>d</c>: both shadow loops are O(depth × tiles),
    /// and at long range a single loop by itself would overrun the frame.
    /// </summary>
    private bool StepOneSeed(JobBudget budget)
    {
        var seed = _data[_seedAt];

        if (_stage == CastStage.Prepare)
        {
            var seedXform = _xformQuery.GetComponent(seed.Owner);

            // Upstream's fast path: a camera without occlusion sees the whole circle. There's
            // nothing to slice here, and complicating the state for just this one case is pointless.
            if (!seed.Comp.Occluded)
            {
                var squircles = _maps.GetLocalTilesIntersecting(_grid.Owner, _grid.Comp2,
                    new Circle(_xforms.GetWorldPosition(seedXform), seed.Comp.Range), ignoreEmpty: false);

                foreach (var tile in squircles)
                    VisibleTiles.Add(tile.GridIndices);

                return true;
            }

            _vis1.Clear();
            _vis2.Clear();
            _seedTiles.Clear();
            _boundary.Clear();

            _maxDepthMax = 0;
            _sumDepthMax = 0;
            _eyePos = _maps.GetTileRef(_grid.Owner, _grid.Comp2, seedXform.Coordinates).GridIndices;

            var range = seed.Comp.Range;

            for (var x = Math.Floor(_eyePos.X - range); x <= _eyePos.X + range; x++)
            {
                for (var y = Math.Floor(_eyePos.Y - range); y <= _eyePos.Y + range; y++)
                {
                    var tile = new Vector2i((int)x, (int)y);
                    var delta = tile - _eyePos;
                    var xDelta = Math.Abs(delta.X);
                    var yDelta = Math.Abs(delta.Y);

                    _maxDepthMax = Math.Max(_maxDepthMax, Math.Max(xDelta, yDelta));
                    _sumDepthMax = Math.Max(_sumDepthMax, xDelta + yDelta);
                    _seedTiles.Add(tile);
                }
            }

            _d = 0;
            _stage = CastStage.Diagonal;
            return false;
        }

        if (_stage == CastStage.Diagonal)
        {
            for (; _d < _maxDepthMax; _d++)
            {
                foreach (var tile in _seedTiles)
                {
                    if (MaxDelta(tile, _eyePos) == _d + 1 && NeighborsVis(_vis2, tile, _d))
                        _vis2[tile] = _opaque.Contains(tile) ? -1 : _d + 1;
                }

                if (budget.Exhausted)
                {
                    _d++;
                    return false;
                }
            }

            _d = 0;
            _stage = CastStage.Straight;
            return false;
        }

        if (_stage == CastStage.Straight)
        {
            for (; _d < _sumDepthMax; _d++)
            {
                foreach (var tile in _seedTiles)
                {
                    if (SumDelta(tile, _eyePos) != _d + 1 || !NeighborsVis(_vis1, tile, _d))
                        continue;

                    if (_opaque.Contains(tile))
                        _vis1[tile] = -1;
                    else if (_vis2.GetValueOrDefault(tile) != 0)
                        _vis1[tile] = _d + 1;
                }

                if (budget.Exhausted)
                {
                    _d++;
                    return false;
                }
            }

            _stage = CastStage.Finish;
            return false;
        }

        // Upstream's tail: the eye sees itself, wall corners are revealed, and only what fell
        // within the frame ends up in the result. All together this is a linear pass over the
        // camera's tiles, no need to slice it.
        _vis1[_eyePos] = 1;

        foreach (var tile in _seedTiles)
            _vis2[tile] = _vis1.GetValueOrDefault(tile, 0);

        foreach (var tile in _seedTiles)
        {
            if (!_opaque.Contains(tile))
                continue;

            if (_vis1.GetValueOrDefault(tile) != 0)
                continue;

            if (IsCorner(tile, Vector2i.UpRight) || IsCorner(tile, Vector2i.UpLeft)
                || IsCorner(tile, Vector2i.DownLeft) || IsCorner(tile, Vector2i.DownRight))
            {
                _boundary.Add(tile);
            }
        }

        foreach (var tile in _boundary)
            _vis1[tile] = -1;

        foreach (var tile in _seedTiles)
        {
            if (!_viewportTiles.Contains(tile))
                continue;

            if (_vis1.GetValueOrDefault(tile, 0) != 0)
                VisibleTiles.Add(tile);
        }

        return true;
    }

    // ------------------------------------------------------------ helpers

    private static int MaxDelta(Vector2i tile, Vector2i center)
    {
        var delta = tile - center;
        return Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
    }

    private static int SumDelta(Vector2i tile, Vector2i center)
    {
        var delta = tile - center;
        return Math.Abs(delta.X) + Math.Abs(delta.Y);
    }

    private static bool NeighborsVis(Dictionary<Vector2i, int> vis, Vector2i index, int d)
    {
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                    continue;

                if (vis.GetValueOrDefault(index + new Vector2i(x, y)) == d)
                    return true;
            }
        }

        return false;
    }

    private bool IsCorner(Vector2i index, Vector2i delta)
    {
        var diagonalIndex = index + delta;

        if (!_seedTiles.TryGetValue(diagonalIndex, out var diagonal))
            return false;

        var cardinal1 = new Vector2i(index.X, diagonal.Y);
        var cardinal2 = new Vector2i(diagonal.X, index.Y);

        return _vis1.GetValueOrDefault(diagonal) != 0
               && _vis1.GetValueOrDefault(cardinal1) != 0
               && _vis1.GetValueOrDefault(cardinal2) != 0
               && _opaque.Contains(cardinal1)
               && _opaque.Contains(cardinal2)
               && !_opaque.Contains(diagonal);
    }
}
