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
/// Раскладка экранирования АМЭ: девять клеток и порядок, в котором их занимать.
///
/// <para>
/// Инструмент существует потому, что геометрия здесь — единственное, чего модель не может
/// проверить наблюдением, а ошибка в ней стоит всей смены. Три живых прогона подряд кончились
/// по-разному, и каждый раз причина была в раскладке:
/// </para>
/// <list type="number">
/// <item>кольцо по восьми соседям пульта — ядром становится клетка ПУЛЬТА, которая щитом не
/// станет никогда, и <c>CoreCount</c> остаётся нулём;</item>
/// <item>правильный квадрат, но подход к пульту застроен — реактор собран, а впрыск включить
/// нечем, потому что до консоли не дойти;</item>
/// <item>правильный квадрат с правильным подходом, но робот клал щиты, стоя ВНУТРИ, и девятым
/// закрыл себе выход: «I'm trapped at (29,-40) surrounded by shields».</item>
/// </list>
/// <para>
/// Все три условия проверяемы заранее и не требуют ни одного хода модели, поэтому проверяются
/// здесь. Модель получает готовый список и ведёт по нему цикл; думать ей остаётся о том, чего
/// код не знает, — о людях, авариях и приоритетах.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>Сторона квадрата экранирования. Три на три — одно ядро, и это минимальный рабочий.</summary>
    private const int SquareSide = 3;

    /// <summary>Насколько далеко от пульта имеет смысл искать место под квадрат, тайлы.</summary>
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

            // Пульт ищем сами: спрашивать хендл значит требовать, чтобы модель сначала сделала
            // look и не перепутала АМЭ с ТЭГ — а это ровно тот шаг, на котором она путается.
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

                // Пульт внутри квадрата — та самая ошибка с кольцом: он щитом не станет.
                if (cells.Contains(ctrlTile))
                    continue;

                if (!cells.All(Free))
                    continue;

                // Одной стороной квадрат обязан касаться пульта, иначе они не в одной узловой сети.
                if (!cells.Any(c => side.Any(d => c + d == ctrlTile)))
                    continue;

                var inside = cells.ToHashSet();

                // Подход к пульту: клетка рядом с ним, которой квадрат не займёт. Без неё реактор
                // соберётся, а впрыск включить будет неоткуда.
                var approaches = side
                    .Select(d => ctrlTile + d)
                    .Where(t => !inside.Contains(t) && Free(t))
                    .ToList();

                if (approaches.Count == 0)
                    continue;

                // Выход: клетка квадрата, из которой можно шагнуть наружу. По ней робот и будет
                // отступать, укладывая щиты от дальнего края.
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

            // Порядок: дальние от выхода — первыми. Тогда на каждом шаге дорога к выходу ещё
            // свободна, а последняя клетка та, с которой робот сразу шагает наружу.
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
