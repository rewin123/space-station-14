using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Ame.Components;
using Content.Server.AiAgent.Tools;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// AME shielding layout: nine cells and the order in which to occupy them.
///
/// <para>
/// This tool exists because the geometry here is the one thing the model can't verify by
/// observation, and a mistake in it costs the whole shift. Three live runs in a row ended
/// differently, and each time the cause was the layout:
/// </para>
/// <list type="number">
/// <item>a ring over the controller's eight neighbors — the core becomes the CONTROLLER's own
/// cell, which will never become a shield, and <c>CoreCount</c> stays zero;</item>
/// <item>a correct square, but the approach to the controller is built over — the reactor is
/// assembled, but there's nothing to turn on the injection with, because the console can't be
/// reached;</item>
/// <item>a correct square with a correct approach, but the robot placed shields while standing
/// INSIDE, and the ninth one sealed off its own exit: "I'm trapped at (29,-40) surrounded by
/// shields."</item>
/// </list>
/// <para>
/// All three conditions can be checked in advance and require no move from the model, so they're
/// checked here. The model gets a ready-made list and runs a loop over it; what's left for it to
/// think about is what the code doesn't know — people, emergencies, and priorities.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// Side of the shielding square. Three by three gives one core, and that's the minimal working setup.
    /// </summary>
    private const int SquareSide = 3;

    /// <summary>
    /// How far from the controller it makes sense to search for a spot for the square, in tiles.
    /// </summary>
    private const int SearchRadius = 4;

    private Task<ToolResult> AmePlanAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "ame_plan", () =>
        {
            if (Transform(borg).GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
                return ToolResult.Fail(ToolError.NotVisible, "я не на сетке станции");

            var toGrid = _xform.GetInvWorldMatrix(grid);
            var here = ToTile(Vector2.Transform(_xform.GetMapCoordinates(borg).Position, toGrid));

            // We find the controller ourselves: asking for a handle would mean requiring the model to
            // do a look first and not mix up the AME with the TEG — and that's exactly the step
            // where it gets confused.
            var ctrl = EntityUid.Invalid;
            var ctrlTile = Vector2i.Zero;
            var best = float.MaxValue;

            var query = EntityQueryEnumerator<AmeControllerComponent>();

            while (query.MoveNext(out var uid, out _))
            {
                if (Transform(uid).GridUid != grid)
                    continue;

                var tile = ToTile(Vector2.Transform(_xform.GetMapCoordinates(uid).Position, toGrid));
                var d = (tile - here).Length;

                if (d >= best)
                    continue;

                best = d;
                ctrl = uid;
                ctrlTile = tile;
            }

            if (!ctrl.IsValid())
                return ToolResult.Fail(ToolError.NotVisible, "пульта АМЭ на этой сетке нет");

            bool Free(Vector2i tile)
            {
                if (!_maps.TryGetTileRef(grid, gridComp, tile, out var tileRef))
                    return false;

                return !_turf.IsSpace(tileRef) && !_turf.IsTileBlocked(tileRef, CollisionGroup.Impassable);
            }

            var side = new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };

            List<Vector2i>? chosen = null;
            Vector2i exit = default;
            Vector2i approach = default;
            var chosenScore = float.MaxValue;

            for (var ox = -SearchRadius; ox <= SearchRadius; ox++)
            for (var oy = -SearchRadius; oy <= SearchRadius; oy++)
            {
                var corner = ctrlTile + new Vector2i(ox, oy);
                var cells = new List<Vector2i>(SquareSide * SquareSide);

                for (var x = 0; x < SquareSide; x++)
                for (var y = 0; y < SquareSide; y++)
                    cells.Add(corner + new Vector2i(x, y));

                // The controller inside the square — that's the same ring mistake: it won't become a shield.
                if (cells.Contains(ctrlTile))
                    continue;

                if (!cells.All(Free))
                    continue;

                // The square must touch the controller with one side, otherwise they're not on the
                // same node network.
                if (!cells.Any(c => side.Any(d => c + d == ctrlTile)))
                    continue;

                var inside = cells.ToHashSet();

                // Approach to the controller: a cell next to it that the square won't occupy. Without
                // it, the reactor would get assembled but there'd be nowhere to turn on the injection from.
                var approaches = side
                    .Select(d => ctrlTile + d)
                    .Where(t => !inside.Contains(t) && Free(t))
                    .ToList();

                if (approaches.Count == 0)
                    continue;

                // Exit: a cell of the square from which you can step outside. The robot will retreat
                // along it, laying shields starting from the far edge.
                var exits = cells
                    .SelectMany(c => side.Select(d => (Cell: c, Out: c + d)))
                    .Where(p => !inside.Contains(p.Out) && p.Out != ctrlTile && Free(p.Out))
                    .ToList();

                if (exits.Count == 0)
                    continue;

                var gate = exits.OrderBy(p => (p.Out - here).Length).First();
                var score = (gate.Out - here).Length + (corner - here).Length * 0.1f;

                if (score >= chosenScore)
                    continue;

                chosenScore = score;
                chosen = cells;
                exit = gate.Out;
                approach = approaches.OrderBy(t => (t - here).Length).First();
            }

            if (chosen == null)
            {
                return ToolResult.Fail(ToolError.NotVisible,
                    $"рядом с пультом ({ctrlTile.X},{ctrlTile.Y}) нет девяти свободных клеток, из которых " +
                    "остаётся и подход к нему, и выход наружу. Разбери, что мешает, или собирай квадрат дальше");
            }

            // Order: farthest from the exit first. That way the path to the exit is still clear at
            // every step, and the last cell is the one from which the robot steps straight outside.
            var order = chosen
                .OrderByDescending(c => (c - exit).Length)
                .ThenBy(c => (c - here).Length)
                .ToList();

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["пульт"] = $"{ctrlTile.X},{ctrlTile.Y}",
                ["порядок"] = order.Select(c => $"{c.X},{c.Y}").ToList(),
                ["выход"] = $"{exit.X},{exit.Y}",
                ["подход_к_пульту"] = $"{approach.X},{approach.Y}",
                ["как_класть"] = "иди на клетку из «порядок», брось флэтпак, прозвони мультитулом, " +
                                 "потом на следующую. Порядок уже отступает к «выход» — не меняй его местами. " +
                                 "Поставив последнюю, сразу шагни на «выход», иначе останешься внутри",
            });
        }, ct);
    }
}
