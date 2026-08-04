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
        TryGetString(args, "near", out var near);

        // A longer timeout than the default: GetView is the one genuinely expensive call in the
        // whole tool surface, and upstream says as much in a comment.
        return OnMainAsync(s, "look", () =>
        {
            var seen = GetVisibleEntities(s.Brain, 8.5f + expand * 4f, out var failure);
            if (failure != null)
                return ToolResult.Fail(ToolError.Carded, failure);

            // Mint handles first and for everything worth naming, so the anchor can be looked up by
            // name against the same set the listing is built from.
            var interesting = new List<EntityUid>();
            foreach (var uid in seen)
            {
                if (KindOf(uid) == "thing")
                    continue;

                s.Handles.GetOrCreate(uid, KindOf(uid));
                interesting.Add(uid);
            }

            // "Рядом со мной", "надо мной", "на которую я смотрю" are all relative to a person, not
            // to the eye. Radio hands the AI a voice name and nothing else — deliberately, that is
            // all a player gets — so the anchor accepts a name as well as a handle, resolved only
            // among entities the cameras can currently see. That is exactly what a human player
            // does: finds the speaker on screen, then clicks the thing beside them.
            EntityUid? anchor = null;
            if (!string.IsNullOrWhiteSpace(near))
            {
                if (!TryResolveVisibleName(s, near!, interesting, out var found, out var why))
                    return why!;

                anchor = found;
            }

            var eye = _stationAi.TryGetCore(s.Brain, out var core) && core.Comp?.RemoteEntity != null
                ? core.Comp.RemoteEntity.Value
                : s.Brain;

            var originUid = anchor ?? eye;
            var origin = _xform.GetMapCoordinates(originUid).Position;

            var rows = new List<(float Dist, string Text)>();

            foreach (var uid in interesting)
            {
                if (uid == anchor)
                    continue;

                var handle = s.Handles.GetOrCreate(uid, KindOf(uid));
                var pos = _xform.GetMapCoordinates(uid).Position;
                var dist = (pos - origin).Length();

                var state = ShortState(uid);
                if (HasComp<MobStateComponent>(uid))
                    state += $", смотрит на {FacingRu(uid)}";

                var where = anchor != null
                    ? BearingFrom(origin, pos)
                    : string.Create(CultureInfo.InvariantCulture, $"{dist:F0} тайлов");

                rows.Add((dist, $"{handle} | {Identity.Name(uid, EntityManager)} | {state} | {where}"));
            }

            // Nearest first when anchored — "the door next to me" is the first door in the list, and
            // that ordering is what makes the answer obvious instead of a search. Ties break on the
            // row text so identical world states still produce identical bytes: the broadphase
            // enumeration order must never leak into the prompt and perturb the cache.
            rows.Sort((a, b) => a.Dist != b.Dist
                ? a.Dist.CompareTo(b.Dist)
                : string.CompareOrdinal(a.Text, b.Text));

            var result = new Dictionary<string, object?>
            {
                ["count"] = rows.Count,
                ["seen"] = rows.Select(r => r.Text).Take(60).ToList(),
            };

            if (anchor != null)
            {
                result["near"] = Identity.Name(anchor.Value, EntityManager);
                result["near_facing"] = FacingRu(anchor.Value);
                result["note"] = "расстояния и стороны света отсчитаны от него; север — вверх экрана";
            }

            return ToolResult.Success(result);
        }, ct, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Resolve a <c>near</c> argument to something the cameras can see right now.
    ///
    /// Accepts a handle or a name because those are the two things the AI legitimately has: handles
    /// come from its own previous look, names come off the radio. Restricting the name search to
    /// currently visible entities is what keeps this at parity — it is a search of the screen, not
    /// of the entity manager.
    /// </summary>
    private bool TryResolveVisibleName(
        AgentSession s,
        string query,
        List<EntityUid> visible,
        out EntityUid found,
        out ToolResult? failure)
    {
        found = default;
        failure = null;

        if (s.Handles.TryResolve(query, out var byHandle) && visible.Contains(byHandle))
        {
            found = byHandle;
            return true;
        }

        var named = visible
            .Select(uid => (Uid: uid, Name: Identity.Name(uid, EntityManager)))
            .ToList();

        var exact = named.Where(n => string.Equals(n.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        var partial = exact.Count > 0
            ? exact
            : named.Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (partial.Count == 1)
        {
            found = partial[0].Uid;
            return true;
        }

        if (partial.Count > 1)
        {
            failure = ToolResult.Fail(ToolError.BadArgs,
                $"'{query}' подходит нескольким — уточни",
                retry: "other_target",
                alternatives: partial.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).Take(5).ToList());
            return false;
        }

        // Not a bug and not a refusal — the person is simply somewhere the cameras do not reach.
        // The way out is the crew monitor, so say so instead of leaving the model to guess.
        failure = ToolResult.Fail(ToolError.NotVisible,
            $"'{query}' не видно ни одной камерой — узнай координаты через crew_status и " +
            $"перемести глаз через move_camera, потом повтори",
            retry: "later",
            alternatives: named.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).Take(5).ToList());

        return false;
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

                // The vanilla console paints these as blips on its nav map, so the position is
                // information a human Station AI already has — and it is the only way to point the
                // eye at someone who is nowhere near a camera the AI is currently watching.
                // Sensors below SensorCords mode simply carry no coordinates, exactly as upstream.
                var where = "";
                if (sensor.Coordinates != null)
                {
                    var map = _xform.ToMapCoordinates(GetCoordinates(sensor.Coordinates.Value));
                    where = string.Create(CultureInfo.InvariantCulture, $" | ({map.X:F0},{map.Y:F0})");
                }

                rows.Add($"{sensor.Name} | {sensor.Job} | {dept} | {alive}{dmg}{where}");
            }

            rows.Sort(StringComparer.Ordinal);

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["count"] = rows.Count,
                ["crew"] = rows.Take(60).ToList(),
                ["note"] = "видны только те, у кого включён датчик костюма; координаты — только у тех, " +
                           "у кого он выставлен на передачу координат. По координатам можно навести глаз: move_camera x,y",
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
