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
/// Where to recharge. Search across the WHOLE grid, not just what's visible.
///
/// <para>
/// Sight is fundamentally useless here: the cyborg charging station sits in robotics, while the
/// robot runs out of charge wherever it was working, and it has no way to see the station from
/// there. In round 137 this cost a shift — it went around the AME, engineering, the substation,
/// Atmos Storage, and the TEG, reported "couldn't find a BorgCharger anywhere, task impossible",
/// and shut down at zero, even though there are three charging stations on the map. A component
/// query knows about all of them at once and costs one call.
/// </para>
/// <para>
/// Filtering is by the station's own whitelist, not by prototype name: the robot's question isn't
/// "where is a BorgCharger", it's "where can I fit". Battery chargers and xenoborg chargers get
/// filtered out on their own this way, with no exclusion list that would need fixing after every
/// upstream merge.
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

                // "Can I fit" is the only honest criterion. The cyborg station has an
                // entity_storage slot and a whitelist on the chassis; a desktop charger has a
                // slot for a battery.
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
