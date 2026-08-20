using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Whitelist;
using Robust.Shared.Map.Components;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Где зарядиться. Поиск по ВСЕЙ сетке, а не по тому, что видно.
///
/// <para>
/// Зрение здесь бесполезно принципиально: станция для киборгов стоит в робототехнике, а садится
/// робот там, где работал, и увидеть её он не может ниоткуда. В раунде 137 это стоило смены — он
/// обошёл АМЭ, инженерию, подстанцию, Atmos Storage и ТЭГ, сообщил «BorgCharger не нашёл нигде,
/// задача невозможна» и сел на нуле, хотя на карте станций три штуки. Поиск по компонентам знает
/// про них всё сразу и стоит один вызов.
/// </para>
/// <para>
/// Отбор по whitelist самой станции, а не по имени прототипа: вопрос у робота не «где стоит
/// BorgCharger», а «куда влезаю я». Зарядки для батареек и ксеноборгов при этом отсеиваются сами,
/// без списка исключений, который пришлось бы чинить после каждого апстрима.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private SharedPowerReceiverSystem _powered = default!;

    private Task<ToolResult> FindChargerAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "find_charger", () =>
        {
            if (Transform(borg).GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out _))
                return ToolResult.Fail(ToolError.NotVisible, "я не на сетке станции");

            var toGrid = _xform.GetInvWorldMatrix(grid);
            var here = Vector2.Transform(_xform.GetMapCoordinates(borg).Position, toGrid);

            var found = new List<(Vector2i Tile, float Distance, bool Powered)>();

            var query = EntityQueryEnumerator<ChargerComponent>();

            while (query.MoveNext(out var uid, out var charger))
            {
                if (Transform(uid).GridUid != grid)
                    continue;

                // «Влезаю ли я» — единственный честный признак. У станции для киборгов слот
                // entity_storage и вайтлист на шасси; у настольной зарядки — слот под батарейку.
                if (charger.Whitelist == null || !_whitelist.IsValid(charger.Whitelist, borg))
                    continue;

                var there = Vector2.Transform(_xform.GetMapCoordinates(uid).Position, toGrid);

                found.Add((ToTile(there), (there - here).Length(), _powered.IsPowered(uid)));
            }

            if (found.Count == 0)
                return ToolResult.Fail(ToolError.NotVisible,
                    "на этой сетке нет ни одной станции, в которую я влезаю");

            var rows = found
                .OrderBy(f => f.Distance)
                .Select(f => $"{f.Tile.X},{f.Tile.Y} | {f.Distance:F0} тайлов | " +
                             (f.Powered ? "запитана" : "ОБЕСТОЧЕНА"))
                .ToList();

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["станций"] = rows.Count,
                ["зарядки"] = rows,
                ["как_дойти"] = "координаты подставляй в goto как есть; они в той же сетке, что и " +
                                "твоё «я=(x,y)». Обесточенная станция не зарядит — иди к запитанной",
            });
        }, ct);
    }
}
