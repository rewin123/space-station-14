using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Access.Components;
using Content.Shared.Pinpointer;
using Content.Server.Atmos.Monitor.Components;
using Robust.Shared.Map;
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
        TryGetString(args, "kind", out var kind);

        // A longer timeout than the default: GetView is the one genuinely expensive call in the
        // whole tool surface, and upstream says as much in a comment.
        return OnMainAsync(s, "look", () =>
        {
            var profile = new LookProfile();

            var seen = GetVisibleEntities(s.Brain, 8.5f + expand * 4f, out var failure, ref profile);
            if (failure != null)
                return ToolResult.Fail(ToolError.Internal, failure, retry: "later");

            var rowsStart = Stopwatch.GetTimestamp();

            // Mint handles first and for everything worth naming, so the anchor can be looked up by
            // name against the same set the listing is built from. The kind filter is applied to the
            // LISTING, not here: an anchor named by the crew may well be of a different kind than
            // the things being asked about ("какие двери рядом с Иваном").
            //
            // Вид считается ОДИН раз на сущность и едет рядом с ней. Раньше KindOf звался до трёх
            // раз: `Handles.GetOrCreate(uid, KindOf(uid))` вычисляет аргумент всегда, даже когда
            // хендл уже есть и метод сразу выходит, — а это цепочка из тринадцати HasComp.
            var interesting = new List<(EntityUid Uid, string Kind)>(seen.Count);
            foreach (var uid in seen)
            {
                var kindOf = KindOf(uid);
                s.Handles.GetOrCreate(uid, kindOf);
                interesting.Add((uid, kindOf));
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

            foreach (var (uid, kindOf) in interesting)
            {
                if (uid == anchor)
                    continue;

                // Фильтр по виду — ДО построчной работы, а не после неё. Раньше `look {"kind":"door"}`
                // платил полную цену за все шестьсот сущностей в поле зрения, чтобы напечатать
                // двадцать девять дверей.
                if (!string.IsNullOrWhiteSpace(kind)
                    && !string.Equals(kindOf, kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                var handle = s.Handles.GetOrCreate(uid, kindOf);
                var pos = _xform.GetMapCoordinates(uid).Position;
                var dist = (pos - origin).Length();

                var state = ShortState(uid);

                // Второй HasComp<MobStateComponent> тут не нужен: вид уже это знает — KindOf
                // ставит "crew" ровно по наличию этого компонента и ставит его первым.
                if (kindOf == "crew")
                    state += $", смотрит на {FacingRu(uid)}";

                // Say which of these the AI can actually operate.
                //
                // Without it the model has to inspect doors one by one to find one that answers,
                // and on a real station that is a real cost: a scenario at Atmospherics found
                // twenty-nine doors in view, and the nearest one was a firelock the AI may never
                // touch. A player sees this instantly — the radial menu either appears or it does
                // not — so making the model probe for it was a handicap, not parity.
                if (TryComp<StationAiWhitelistComponent>(uid, out var aiWire))
                    state += aiWire.Enabled ? ", управляю" : ", провод перерезан";

                // Same shape whether the listing is measured from a person or from the eye: the
                // offset answers "which one", the absolute pair feeds move_camera.
                var where = PositionFrom(origin, pos);

                rows.Add((dist, $"{handle} | {Identity.Name(uid, EntityManager)} | {state} | {where}"));
            }

            // Nearest first when anchored — "the door next to me" is the first door in the list, and
            // that ordering is what makes the answer obvious instead of a search. Ties break on the
            // row text so identical world states still produce identical bytes: the broadphase
            // enumeration order must never leak into the prompt and perturb the cache.
            rows.Sort((a, b) => a.Dist != b.Dist
                ? a.Dist.CompareTo(b.Dist)
                : string.CompareOrdinal(a.Text, b.Text));

            // Nearest first, so a cut always removes the far half of the room rather than something
            // standing next to the person who asked.
            var limit = _cfg.GetCVar(AiCVars.LookLimit);

            var result = new Dictionary<string, object?>
            {
                ["count"] = rows.Count,
                ["seen"] = rows.Select(r => r.Text).Take(limit).ToList(),
            };

            // Silent truncation is the worst of both worlds: the model reports "there is no SMES
            // here" with total confidence about a list that was cut before the SMES. If the list is
            // short, say so out loud and say what to do about it.
            if (rows.Count > limit)
            {
                result["обрезано"] = rows.Count - limit;

                // The old advice led with "expand поменьше", which is impossible: expand defaults to
                // 0, its minimum, so in the overwhelmingly common case the first remedy offered
                // could not be taken. The kind filter is the one that actually works.
                result["как_увидеть_остальное"] =
                    "список обрезан по расстоянию, дальнее не показано. Сузь его: " +
                    "look {\"kind\":\"door\"} покажет только двери, look {\"near\":\"<имя>\"} — то, " +
                    "что вокруг человека. Либо переведи глаз ближе к цели";
            }

            if (anchor != null)
            {
                result["near"] = Identity.Name(anchor.Value, EntityManager);
                result["near_handle"] = s.Handles.GetOrCreate(anchor.Value, KindOf(anchor.Value));
                result["near_facing"] = FacingRu(anchor.Value);
                result["note"] =
                    "Δ отсчитана ОТ него. Список отсортирован от ближнего, «дверь рядом со мной» — " +
                    "первая строка. В одном проёме часто стоят две створки, шлюз и файрлок, с " +
                    "одинаковой Δ: не прошёл после открытия — открывай вторую, а не ищи другую дверь";
            }

            profile.RowsMs = Stopwatch.GetElapsedTime(rowsStart).TotalMilliseconds;
            profile.Rows = rows.Count;
            _lastLook = profile;
            ReportLookCost(expand, profile);

            return ToolResult.Success(result);
        }, ct, TimeSpan.FromSeconds(10));
    }

    /// <summary>Профиль последнего обзора. Только для тестов и консоли — на решения не влияет.</summary>
    private LookProfile _lastLook;

    /// <summary>
    /// Сказать, куда ушло время, если его ушло много.
    ///
    /// Предупреждение диспетчера называет операцию, но не фазу, а без фазы «look 496 мс» одинаково
    /// хорошо объясняется и стенами, и содержимым чужих рюкзаков — притом что чинить это разные
    /// вещи. Отдельная строка стоит трёх обращений к таймеру и снимает весь спор.
    ///
    /// Порог тот же, что у диспетчера: две строки об одном событии должны появляться вместе, иначе
    /// в журнале заводится «предупреждение без объяснения» и наоборот.
    /// </summary>
    private void ReportLookCost(int expand, LookProfile p)
    {
        var total = p.ViewMs + p.GatherMs + p.RowsMs;

        if (total <= _cfg.GetCVar(AiCVars.MainThreadBudgetMs))
            return;

        _sawmill.Warning(string.Create(CultureInfo.InvariantCulture,
            $"look expand={expand} итого={total:F1}мс view={p.ViewMs:F1} gather={p.GatherMs:F1} " +
            $"rows={p.RowsMs:F1} tiles={p.Tiles} cand={p.Candidates} scr={p.OnScreen} " +
            $"rows={p.Rows} queries={p.Queries}"));
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
        List<(EntityUid Uid, string Kind)> visible,
        out EntityUid found,
        out ToolResult? failure)
    {
        found = default;
        failure = null;

        // Список приходит полным, ДО фильтра по kind, и это не оплошность: анкер может быть
        // другого вида, чем то, о чём спрашивают («какие двери рядом с Иваном» — Иван не дверь).
        if (s.Handles.TryResolve(query, out var byHandle) && visible.Any(v => v.Uid == byHandle))
        {
            found = byHandle;
            return true;
        }

        var named = visible
            .Select(v => (Uid: v.Uid, Name: Identity.Name(v.Uid, EntityManager)))
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

    // ------------------------------------------------------------------------ map

    /// <summary>
    /// The station's named places and where they are.
    ///
    /// The agent's problem was never seeing — <c>look</c> works — it was orientation. It knew there
    /// was a door two tiles north and had no idea whether it was staring at the bridge or at a
    /// maintenance closet, so "открой дверь в инженерный" had nowhere to start and every answer
    /// ended in "назовите, где вы находитесь".
    ///
    /// This is the same data the crew monitoring console draws as labels on its navigation map, and
    /// the AI carries that console intrinsically as part of the AiHeld bundle. Reading the labels is
    /// not new power; it is the map the role already ships with.
    ///
    /// Coordinates come out in map space so they feed straight into <c>move_camera {x,y}</c>. That
    /// closes the loop that was open all day: name a place, point the eye at it, look around.
    /// </summary>
    private Task<ToolResult> MapAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        TryGetString(args, "query", out var query);

        return OnMainAsync(s, "map", () =>
        {
            if (!_stationAi.TryGetCore(s.Brain, out var core) || core.Comp?.RemoteEntity == null)
                return ToolResult.Fail(ToolError.Internal, "карта недоступна — у тебя сейчас нет ядра",
                    retry: "later");

            var eye = core.Comp.RemoteEntity.Value;
            var gridUid = Transform(eye).GridUid;

            if (gridUid == null || !TryComp<NavMapComponent>(gridUid, out var navMap))
                // Not not_visible: the prompt teaches that code as a camera problem, so the model
                // would move the eye and retry forever against a station that simply has no nav map.
                return ToolResult.Fail(ToolError.Internal,
                    "у этой станции нет навигационной карты — названий мест не будет", retry: "none");

            // Centring on a given point matters because the model does not do geometry: handed a
            // crewman's coordinates and a list of places measured from its own camera, it will
            // happily report the two as if they were the same neighbourhood. Let the server measure
            // from whatever point the question is actually about.
            var haveX = TryGetFloat(args, "x", out var cx);
            var haveY = TryGetFloat(args, "y", out var cy);

            var origin = haveX && haveY
                ? new Vector2(cx, cy)
                : _xform.GetMapCoordinates(eye).Position;

            var rows = new List<(float Dist, string Text)>();

            foreach (var beacon in navMap.Beacons.Values)
            {
                if (!string.IsNullOrWhiteSpace(query) && !Match(beacon.Text, query!))
                    continue;

                var pos = _xform.ToMapCoordinates(new EntityCoordinates(gridUid.Value, beacon.Position)).Position;
                var dist = (pos - origin).Length();

                rows.Add((dist, string.Create(CultureInfo.InvariantCulture,
                    $"{beacon.Text} | ({pos.X:F0},{pos.Y:F0}) | {BearingFrom(origin, pos)}")));
            }

            rows.Sort((a, b) => a.Dist != b.Dist
                ? a.Dist.CompareTo(b.Dist)
                : string.CompareOrdinal(a.Text, b.Text));

            var d = new Dictionary<string, object?>
            {
                ["note"] = haveX && haveY
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"направления и расстояния отсчитаны от точки ({cx:F0},{cy:F0}); координаты идут в move_camera {{x,y}}")
                    : "направления и расстояния — от ТВОЕГО глаза, не от собеседника. Чтобы узнать, " +
                      "что рядом с человеком, передай сюда его координаты: map {\"x\":…,\"y\":…}. " +
                      "Координаты идут в move_camera {x,y}",
            };

            AddRows(d, "places", rows.Select(r => r.Text).ToList(), 80,
                "карта обрезана. Сузь её: map {\"query\":\"<часть названия>\"} — подписи английские");

            if (rows.Count == 0 && !string.IsNullOrWhiteSpace(query))
                d["note"] = $"по запросу '{query}' ничего нет — вызови map без query, чтобы увидеть все места";

            return ToolResult.Success(d);
        }, ct);
    }

    /// <summary>Nearest named place to an entity, for the SELF line.</summary>
    private string PlaceAt(EntityUid uid)
    {
        return _navMap.TryGetNearestBeacon(uid, out var beacon, out _) && beacon != null
            ? beacon.Value.Comp.Text ?? Name(beacon.Value.Owner)
            : "неизвестно";
    }

    /// <summary>Nearest named place to a bare position — for people the AI locates by coordinates.</summary>
    private string PlaceNear(MapCoordinates coords)
    {
        return _navMap.TryGetNearestBeacon(coords, out var beacon, out _) && beacon != null
            ? beacon.Value.Comp.Text ?? Name(beacon.Value.Owner)
            : "неизвестно";
    }

    /// <summary>
    /// Every named place on the eye's grid, in map coordinates, snapshotted once.
    ///
    /// <c>NavMapSystem.TryGetNearestBeacon</c> walks every beacon on the station per call, so asking
    /// it once per crew member turns a fifty-strong shift into thousands of transform resolutions
    /// inside a single marshalled delegate — against a five-millisecond budget. One pass, then a
    /// linear scan per person over a list of at most a couple of hundred entries.
    /// </summary>
    private List<(Vector2 Pos, string Text)> SnapshotPlaces(EntityUid eye)
    {
        var places = new List<(Vector2, string)>();
        var gridUid = Transform(eye).GridUid;

        if (gridUid == null || !TryComp<NavMapComponent>(gridUid, out var navMap))
            return places;

        foreach (var beacon in navMap.Beacons.Values)
        {
            if (string.IsNullOrWhiteSpace(beacon.Text))
                continue;

            var pos = _xform.ToMapCoordinates(new EntityCoordinates(gridUid.Value, beacon.Position)).Position;
            places.Add((pos, beacon.Text!));
        }

        return places;
    }

    private static string NearestPlace(List<(Vector2 Pos, string Text)> places, Vector2 at)
    {
        var best = "неизвестно";
        var bestDist = float.MaxValue;

        foreach (var (pos, text) in places)
        {
            var dist = (pos - at).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            best = text;
        }

        return best;
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

            var visible = IsVisibleToAi(s.Brain, uid);

            var d = new Dictionary<string, object?>
            {
                ["name"] = Identity.Name(uid, EntityManager),
                ["kind"] = KindOf(uid),
                ["visible"] = visible,
            };

            // Whether the AI's own control wire has been cut is genuinely useful and genuinely
            // knowable: vanilla shows it on the airlock's wire panel indicator.
            if (TryComp<StationAiWhitelistComponent>(uid, out var whitelist))
                d["ai_control"] = whitelist.Enabled ? "есть" : "провод перерезан";
            else
                d["ai_control"] = "нет";

            // Handles live for the whole shift, and this used to report live bolt, breaker, charge
            // and pressure readings for anything the AI had ever laid eyes on — from the other end
            // of the station, through walls. identify already refuses on the same grounds and the
            // access half of this very tool goes through VisibleOrExplain; only the device half was
            // exempt. It is not refused outright, because knowing what a thing IS and what can be
            // done with it costs nothing and refusing burns a turn — but the live readings go.
            if (!visible)
            {
                d["устарело"] = "сейчас ты этого не видишь ни одной камерой — текущее состояние " +
                                "неизвестно, это только то, что ты знал раньше. Наведи камеру, " +
                                "если состояние важно";
                d["actions"] = AvailableActions(uid);
                AddAccessInfo(s, args, uid, d);
                return ToolResult.Success(d);
            }

            d["powered"] = _power.IsPowered(uid);

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

            AddReadableState(uid, d);
            AddAccessInfo(s, args, uid, d);

            // Tell the model what it may actually do here, so it does not have to probe.
            d["actions"] = AvailableActions(uid);

            return ToolResult.Success(d);
        }, ct);
    }

    /// <summary>
    /// State the AI can read off a thing it cannot operate.
    ///
    /// Looking and controlling are different rights, and the gate chain only ever governed the
    /// second one. A SMES bank wears its charge on its face — five indicator lamps, readable across
    /// the room — and a gas canister has a pressure gauge on the side. A player standing at a camera
    /// reads both without touching anything, so refusing to report them is a handicap, not parity.
    ///
    /// Deliberately coarse. The AI has no power-monitoring console in its AiHeld bundle (radar,
    /// crew monitor, records, laws and comms — that is the whole list), so it has no telemetry
    /// feed: it is reading lamps. Rounding to the steps the sprite actually shows keeps the answer
    /// honest instead of inventing three decimal places the role could never see.
    /// </summary>
    private void AddReadableState(EntityUid uid, Dictionary<string, object?> d)
    {
        if (TryComp<Content.Shared.Power.Components.BatteryComponent>(uid, out var battery)
            && battery.MaxCharge > 0)
        {
            var fraction = _battery.GetCharge((uid, battery)) / battery.MaxCharge;
            var steps = Math.Clamp((int)Math.Round(fraction * 5f), 0, 5);

            d["заряд"] = string.Create(CultureInfo.InvariantCulture, $"{steps * 20}% (по индикатору)");
        }

        if (TryComp<Content.Shared.Atmos.Piping.Unary.Components.GasCanisterComponent>(uid, out var canister))
        {
            d["давление"] = string.Create(CultureInfo.InvariantCulture,
                $"{canister.Air.Pressure:F0} кПа (по манометру)");
        }
    }

    /// <summary>
    /// What the door demands, and — when asked about a specific person — whether it would let them
    /// through.
    ///
    /// "Ии, пусти меня в инженерный" is one of the most common things said to a Station AI, and the
    /// honest answer often is "your card already opens it, just walk up to it". Without this the
    /// agent either guesses from the job title or opens the door needlessly, and both are wrong:
    /// access does not follow job reliably once anyone has visited the ID console.
    ///
    /// The verdict comes from <c>AccessReaderSystem.IsAllowed</c> — the very call the game makes
    /// when that person touches that door. It is a simulation of their own attempt, not a private
    /// oracle: the same answer arrives a second later if they simply try the handle.
    ///
    /// The person must be visible on camera, exactly as <c>identify</c> requires. Answering about
    /// someone the AI cannot see would mean reading ID cards over the radio, which no player can do.
    /// </summary>
    private void AddAccessInfo(AgentSession s, JsonElement args, EntityUid uid, Dictionary<string, object?> d)
    {
        // Not TryComp: an airlock's own AccessReader is a shell with ContainerAccessProvider set,
        // and the requirements that actually decide anything live on the door electronics board
        // inside it. Reading the shell reports a list the game never consults — it looked right and
        // was pure fiction, which is worse than reporting nothing.
        if (!_access.GetMainAccessReader(uid, out var readerEnt))
            return;

        var reader = readerEnt.Value.Comp;

        if (!reader.Enabled)
        {
            d["access_required"] = "замок отключён — пускает всех";
            return;
        }

        // Each inner set is one sufficient combination; any single one of them opens the door.
        d["access_required"] = reader.AccessLists.Count == 0
            ? new List<string> { "свободный проход" }
            : reader.AccessLists
                .Select(set => string.Join('+', set.Select(t => t.Id).OrderBy(t => t, StringComparer.Ordinal)))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

        if (!TryGetString(args, "by", out var by) || string.IsNullOrWhiteSpace(by))
            return;

        if (!TryResolvePerson(s, by!, out var person, out var why))
        {
            // A separate key, not access_by. Putting a Russian error sentence in the field where
            // the model expects a name, while access_allowed silently vanishes, reads to a
            // mediocre model as "no access" — and it goes and tells the person so.
            d["access_by_ошибка"] = why;
            return;
        }

        var allowed = _access.IsAllowed(person, uid);

        d["access_by"] = Identity.Name(person, EntityManager);
        d["access_allowed"] = allowed;

        if (!allowed)
        {
            var held = _access.FindAccessTags(person).Select(t => t.Id).OrderBy(t => t, StringComparer.Ordinal).ToList();
            d["access_held"] = held.Count > 0 ? held : new List<string> { "нет карты или доступов" };
        }
    }

    /// <summary>
    /// Resolve a person by handle or by name, among people the AI has already seen, and only while
    /// it can still see them.
    ///
    /// Searching the handle registry rather than sweeping vision again is deliberate: a fresh
    /// <c>GetView</c> is the single most expensive call in the tool surface, and everything the AI
    /// legitimately knows about is in the registry already because that is where looking puts it.
    /// </summary>
    private bool TryResolvePerson(AgentSession s, string query, out EntityUid uid, out string? failure)
    {
        failure = null;

        if (s.Handles.TryResolve(query, out uid) && Exists(uid) && !TerminatingOrDeleted(uid))
            return VisibleOrExplain(s, uid, ref failure);

        var known = s.Handles.HandlesOfKind("crew")
            .Select(h => s.Handles.TryResolve(h, out var u) ? u : EntityUid.Invalid)
            .Where(u => u.IsValid() && Exists(u) && !TerminatingOrDeleted(u))
            .Select(u => (Uid: u, Name: Identity.Name(u, EntityManager)))
            .ToList();

        var exact = known.Where(n => string.Equals(n.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = exact.Count > 0
            ? exact
            : known.Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hits.Count == 1)
        {
            uid = hits[0].Uid;
            return VisibleOrExplain(s, uid, ref failure);
        }

        failure = hits.Count > 1
            ? $"'{query}' подходит нескольким: {string.Join(", ", hits.Select(h => h.Name).OrderBy(n => n, StringComparer.Ordinal).Take(5))}"
            : $"'{query}' не найден — сначала посмотри на него: look near";

        return false;
    }

    private bool VisibleOrExplain(AgentSession s, EntityUid uid, ref string? failure)
    {
        if (IsVisibleToAi(s.Brain, uid))
            return true;

        failure = $"{Identity.Name(uid, EntityManager)} сейчас не на камерах — карту в его руках ты не видишь";
        return false;
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
                return ToolResult.Fail(ToolError.Internal, "монитор экипажа сейчас недоступен",
                    retry: "later");

            var rows = new List<string>();

            // One pass over the station's labels, reused for everyone below.
            var places = _stationAi.TryGetCore(s.Brain, out var core) && core.Comp?.RemoteEntity != null
                ? SnapshotPlaces(core.Comp.RemoteEntity.Value)
                : new List<(Vector2 Pos, string Text)>();

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

                    // The nearest landmark to THEM, not to the eye. Without it the model does the
                    // geometry itself and gets it wrong: on the first live round it read the places
                    // nearest its own camera and told a crewman he was standing in the AI core,
                    // seventy tiles from where he actually was.
                    where = string.Create(CultureInfo.InvariantCulture,
                        $" | ({map.X:F0},{map.Y:F0}) | у {NearestPlace(places, map.Position)}");
                }

                rows.Add($"{sensor.Name} | {sensor.Job} | {dept} | {alive}{dmg}{where}");
            }

            rows.Sort(StringComparer.Ordinal);

            var d = new Dictionary<string, object?>
            {
                ["note"] = "видны только те, у кого включён датчик костюма; координаты — только у тех, " +
                           "у кого он выставлен на передачу координат. По координатам можно навести глаз: move_camera x,y",
            };

            AddRows(d, "crew", rows, 60,
                "список обрезан. Сузь его: crew_status {\"filter\":\"<имя, должность или отдел>\"}");

            return ToolResult.Success(d);
        }, ct);
    }

    private static bool Match(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Put a listing in the answer and, if it was cut, say so and say what to do about it.
    ///
    /// <c>look</c> has always done this; <c>map</c>, <c>crew_status</c> and <c>records</c> reported a
    /// <c>count</c> that quietly exceeded the array beside it. A real station has more beacons than
    /// the cap and often more crew, so the model would scan a truncated list and tell somebody their
    /// department was not on the map — with complete confidence, because nothing said otherwise.
    /// </summary>
    private static void AddRows(
        Dictionary<string, object?> d,
        string key,
        List<string> rows,
        int limit,
        string howToNarrow)
    {
        d["count"] = rows.Count;
        d[key] = rows.Take(limit).ToList();

        if (rows.Count <= limit)
            return;

        d["обрезано"] = rows.Count - limit;
        d["как_увидеть_остальное"] = howToNarrow;
    }

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

            var d = new Dictionary<string, object?>();
            AddRows(d, "records", rows, 60,
                "список обрезан. Сузь его: records {\"query\":\"<имя или должность>\"}");

            return ToolResult.Success(d);
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
            if (station != null)
            {
                // Как станция называется. Экипаж зовёт её по имени постоянно — в объявлениях, по
                // рации, в позывных Центрального командования, — а узнать его агенту было неоткуда
                // ни одним инструментом. Он мог отработать всю смену, ни разу не поняв, что «Аксиома»
                // это то место, где он находится.
                d["station"] = Name(station.Value);

                if (TryComp<Content.Shared.AlertLevel.AlertLevelComponent>(station.Value, out var alert))
                    d["alert_level"] = alert.CurrentAlertLevel;
            }

            if (_stationAi.TryGetCore(s.Brain, out var core) && core.Comp != null)
            {
                d["core_powered"] = _power.IsPowered(core.Owner);

                // Same vocabulary as the SELF line's core= field. Two words for one fact is two
                // things to learn, and the model has no way to know they mean the same.
                d["core"] = core.Comp.Remote ? "remote" : "projected";
            }

            d["mode"] = s.Mode.ToString().ToLowerInvariant();

            // known_handles used to be reported here: an internal registry counter that means
            // nothing in the world and that the model cannot act on.

            return ToolResult.Success(d);
        }, ct);
    }
}
