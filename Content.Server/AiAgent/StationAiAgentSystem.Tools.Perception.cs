using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Power.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Electrocution;
using Content.Shared.IdentityManagement;
using Content.Server.Medical.CrewMonitoring;
using Content.Shared.Mobs.Components;
using Content.Shared.Silicons.Laws.Components;
using Content.Shared.Silicons.StationAi;
using Content.Shared.StationRecords;
using Content.Shared.StatusIcon.Components;

namespace Content.Server.AiAgent;

/// <summary>Read-only tools: look, inspect, crew_status, identify, records, laws, station_status.</summary>
public sealed partial class StationAiAgentSystem
{
    // ----------------------------------------------------------------------- look

    private Task<ToolResult> LookAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var expand = Math.Clamp(GetInt(args, "expand", 0), 0, 3);

        // A longer timeout than the default: GetView is the one genuinely expensive call in the
        // whole tool surface, and upstream says as much in a comment.
        return OnMainAsync(s, "look", () =>
        {
            var seen = GetVisibleEntities(s.Brain, 8.5f + expand * 4f, out var failure);
            if (failure != null)
                return ToolResult.Fail(ToolError.Carded, failure);

            var origin = _xform.GetMapCoordinates(
                _stationAi.TryGetCore(s.Brain, out var core) && core.Comp?.RemoteEntity != null
                    ? core.Comp.RemoteEntity.Value
                    : s.Brain);

            var rows = new List<string>();

            foreach (var uid in seen)
            {
                var kind = KindOf(uid);
                if (kind == "thing")
                    continue;

                var handle = s.Handles.GetOrCreate(uid, kind);
                var pos = _xform.GetMapCoordinates(uid);
                var dist = (pos.Position - origin.Position).Length();

                rows.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{handle} | {Identity.Name(uid, EntityManager)} | {ShortState(uid)} | {dist:F0} тайлов"));
            }

            // Sorted so an identical world state always produces identical bytes — otherwise the
            // enumeration order of the broadphase leaks into the prompt and perturbs the cache.
            rows.Sort(StringComparer.Ordinal);

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["count"] = rows.Count,
                ["seen"] = rows.Take(60).ToList(),
            });
        }, ct, TimeSpan.FromSeconds(10));
    }

    /// <summary>One-line state for the look listing; the full picture is what inspect is for.</summary>
    private string ShortState(EntityUid uid)
    {
        if (TryComp<DoorComponent>(uid, out var door))
        {
            var state = door.State;
            var bolted = TryComp<DoorBoltComponent>(uid, out var b) && b.BoltsDown ? ", болты" : "";
            return $"{state}{bolted}";
        }

        if (TryComp<MobStateComponent>(uid, out var mob))
        {
            var mobState = mob.CurrentState;
            return mobState.ToString();
        }

        if (TryComp<ApcComponent>(uid, out var apc))
            return apc.MainBreakerEnabled ? "рубильник вкл" : "рубильник выкл";

        return _power.IsPowered(uid) ? "запитано" : "обесточено";
    }

    // -------------------------------------------------------------------- inspect

    private Task<ToolResult> InspectAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        return OnMainAsync(s, "inspect", () =>
        {
            if (!TryResolve(s, args, out var uid, out var failure))
                return failure!;

            var d = new Dictionary<string, object?>
            {
                ["name"] = Identity.Name(uid, EntityManager),
                ["kind"] = KindOf(uid),
                ["visible"] = IsVisibleToAi(s.Brain, uid),
                ["powered"] = _power.IsPowered(uid),
            };

            // Whether the AI's own control wire has been cut is genuinely useful and genuinely
            // knowable: vanilla shows it on the airlock's wire panel indicator.
            if (TryComp<StationAiWhitelistComponent>(uid, out var whitelist))
                d["ai_control"] = whitelist.Enabled ? "есть" : "провод перерезан";
            else
                d["ai_control"] = "нет";

            if (TryComp<DoorComponent>(uid, out var door))
            {
                var doorState = door.State;
                d["door_state"] = doorState.ToString();
                d["bolted"] = TryComp<DoorBoltComponent>(uid, out var bolt) && bolt.BoltsDown;
                d["bolt_wire_cut"] = TryComp<DoorBoltComponent>(uid, out var bw) && bw.BoltWireCut;
                d["electrified"] = TryComp<ElectrifiedComponent>(uid, out var el) && el.Enabled;
            }

            if (TryComp<AirlockComponent>(uid, out var airlock))
                d["emergency_access"] = airlock.EmergencyAccess;

            if (TryComp<ApcComponent>(uid, out var apc))
                d["main_breaker"] = apc.MainBreakerEnabled;

            if (TryComp<AirAlarmComponent>(uid, out var alarm))
            {
                var alarmMode = alarm.CurrentMode;
                d["air_alarm_mode"] = alarmMode.ToString();
            }

            if (TryComp<MobStateComponent>(uid, out var mob))
            {
                var mobState = mob.CurrentState;
                d["mob_state"] = mobState.ToString();
            }

            // Tell the model what it may actually do here, so it does not have to probe.
            d["actions"] = AvailableActions(uid);

            return ToolResult.Success(d);
        }, ct);
    }

    private List<string> AvailableActions(EntityUid uid)
    {
        var actions = new List<string>();

        if (HasComp<DoorComponent>(uid))
        {
            actions.Add("open");
            actions.Add("close");
        }

        if (HasComp<DoorBoltComponent>(uid))
        {
            actions.Add("bolt");
            actions.Add("unbolt");
        }

        if (HasComp<ElectrifiedComponent>(uid))
        {
            actions.Add("electrify");
            actions.Add("unelectrify");
        }

        if (HasComp<AirlockComponent>(uid))
        {
            actions.Add("emergency_access_on");
            actions.Add("emergency_access_off");
        }

        if (HasComp<ApcComponent>(uid))
        {
            actions.Add("apc_breaker_on");
            actions.Add("apc_breaker_off");
        }

        if (HasComp<AirAlarmComponent>(uid))
            actions.Add("air_alarm_mode");

        return actions;
    }

    // ---------------------------------------------------------------- crew_status

    private Task<ToolResult> CrewStatusAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        TryGetString(args, "filter", out var filter);

        return OnMainAsync(s, "crew_status", () =>
        {
            // Read straight off the AI entity: the AiHeld bundle gives it an intrinsic
            // CrewMonitoringConsole, so no UI and no console entity is involved.
            if (!TryComp<CrewMonitoringConsoleComponent>(s.Brain, out var monitor))
                return ToolResult.Fail(ToolError.Carded, "монитор экипажа недоступен");

            var rows = new List<string>();

            foreach (var sensor in monitor.ConnectedSensors.Values)
            {
                if (!string.IsNullOrWhiteSpace(filter)
                    && !Match(sensor.Name, filter!)
                    && !Match(sensor.Job, filter!)
                    && !sensor.JobDepartments.Any(dep => Match(dep, filter!)))
                    continue;

                var dept = sensor.JobDepartments.Count > 0 ? string.Join('/', sensor.JobDepartments) : "—";
                var alive = sensor.IsAlive ? "жив" : "МЁРТВ";
                var dmg = sensor.TotalDamage.HasValue
                    ? $", урон {sensor.TotalDamage.Value}"
                    : "";

                rows.Add($"{sensor.Name} | {sensor.Job} | {dept} | {alive}{dmg}");
            }

            rows.Sort(StringComparer.Ordinal);

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["count"] = rows.Count,
                ["crew"] = rows.Take(60).ToList(),
                ["note"] = "видны только те, у кого включён датчик костюма",
            });
        }, ct);
    }

    private static bool Match(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------- identify

    /// <summary>
    /// What the AI legitimately knows about a person it can see.
    ///
    /// Three independent channels, all of them forgeable and all of them surfaced side by side so
    /// the model can notice when they disagree — which is exactly the signal that someone is
    /// wearing a mask or carrying an agent ID:
    ///
    ///   presented — <c>Identity.Name</c>, the name shown to anyone looking
    ///   id_card   — <c>IdentitySystem.GetIdentityShortInfo</c>, "Name (Job)" read off the ID
    ///   job_icon  — <c>JobStatusComponent</c>, the icon the AI's HUD paints over the mob
    ///
    /// Cross-referencing against the official <c>records</c> is left to the model, deliberately:
    /// spotting the discrepancy is the interesting part of the role.
    /// </summary>
    private Task<ToolResult> IdentifyAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        return OnMainAsync(s, "identify", () =>
        {
            if (!TryResolve(s, args, out var uid, out var failure))
                return failure!;

            if (!IsVisibleToAi(s.Brain, uid))
                return ToolResult.Fail(ToolError.NotVisible, "этого существа не видно ни одной камерой",
                    retry: "other_target");

            var d = new Dictionary<string, object?>
            {
                ["presented"] = Identity.Name(uid, EntityManager),
                ["id_card"] = _identity.GetIdentityShortInfo(uid) ?? "нет ID-карты",
            };

            if (TryComp<JobStatusComponent>(uid, out var job))
            {
                d["job_icon"] = job.JobStatusIcon?.Id ?? "нет";
                d["is_crew"] = job.IsCrew;
            }
            else
            {
                d["job_icon"] = "нет";
                d["is_crew"] = false;
            }

            if (TryComp<MobStateComponent>(uid, out var mob))
            {
                var mobState = mob.CurrentState;
                d["state"] = mobState.ToString();
            }

            d["note"] = "и имя, и ID-карта, и значок должности подделываются независимо друг от друга";

            return ToolResult.Success(d);
        }, ct);
    }

    // -------------------------------------------------------------------- records

    private Task<ToolResult> RecordsAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        TryGetString(args, "query", out var query);

        return OnMainAsync(s, "records", () =>
        {
            var station = _station.GetOwningStation(s.Brain);
            if (station == null)
            {
                // Not an error: a shuttle, a lone grid or a test map genuinely has no records
                // database. Reporting "internal" here would send the model hunting for a fault
                // that does not exist.
                return ToolResult.Success(new Dictionary<string, object?>
                {
                    ["count"] = 0,
                    ["records"] = new List<string>(),
                    ["note"] = "здесь нет базы учётных записей — этот грид не станция",
                });
            }

            var rows = new List<string>();

            foreach (var (_, record) in _records.GetRecordsOfType<GeneralStationRecord>(station.Value))
            {
                if (!string.IsNullOrWhiteSpace(query)
                    && !Match(record.Name, query!)
                    && !Match(record.JobTitle, query!))
                    continue;

                rows.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{record.Name} | {record.JobTitle} | {record.Species} | {record.Age} лет"));
            }

            rows.Sort(StringComparer.Ordinal);

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["count"] = rows.Count,
                ["records"] = rows.Take(60).ToList(),
            });
        }, ct);
    }

    // ----------------------------------------------------------------------- laws

    private Task<ToolResult> LawsAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        return OnMainAsync(s, "laws", () =>
        {
            var lawset = _laws.GetLaws(s.Brain);
            var rows = lawset.Laws
                .Select(l => $"{l.LawIdentifierOverride ?? l.Order.ToString()}. {Loc.GetString(l.LawString)}")
                .ToList();

            var version = TryComp<SiliconLawBoundComponent>(s.Brain, out var bound) ? bound.LastLawProvider : null;

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["laws"] = rows,
                ["count"] = rows.Count,
                ["provider"] = version != null ? Name(version.Value) : "—",
            });
        }, ct);
    }

    // -------------------------------------------------------------- station_status

    private Task<ToolResult> StationStatusAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        return OnMainAsync(s, "station_status", () =>
        {
            var d = new Dictionary<string, object?>
            {
                ["round_time"] = Perception.ObservationFormatter.FormatRoundTime(RoundTime()),
            };

            var station = _station.GetOwningStation(s.Brain);
            if (station != null && TryComp<Content.Shared.AlertLevel.AlertLevelComponent>(station.Value, out var alert))
            {
                d["alert_level"] = alert.CurrentAlertLevel;
            }

            if (_stationAi.TryGetCore(s.Brain, out var core) && core.Comp != null)
            {
                d["core_powered"] = _power.IsPowered(core.Owner);
                d["core_mode"] = core.Comp.Remote ? "камеры" : "голопад";
            }

            d["mode"] = s.Mode.ToString().ToLowerInvariant();
            d["known_handles"] = s.Handles.Count;

            return ToolResult.Success(d);
        }, ct);
    }
}
