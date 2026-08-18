using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Power.Components;
using Content.Shared.Atmos.Monitor;
using Content.Shared.Chat;
using Content.Server.Communications;
using Content.Shared.Atmos.Monitor.Components;
using Content.Shared.Communications;
using Content.Shared.Doors.Components;
using Content.Shared.Electrocution;
using Content.Shared.Radio;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>Acting tools: say, radio, announce, move_camera, jump_to_core, device_action, device_ui.</summary>
public sealed partial class StationAiAgentSystem
{
    private UiActionIndex? _uiIndex;

    /// <summary>
    /// Built on first use and never off the main thread: every tool body runs inside
    /// <c>OnMainAsync</c>, so there is no second caller to race with. Constructing it eagerly would
    /// mean binding reflection against an event bus the benchmarks replace between worlds.
    /// </summary>
    private UiActionIndex UiIndex => _uiIndex ??= new UiActionIndex(EntityManager, _sawmill);

    /// <summary>
    /// Refuse to broadcast a line the agent has just broadcast.
    ///
    /// This model fills silence: on a live station it put "Экипаж, Аксиома на связи" on the common
    /// channel every eight seconds for minutes. A player would never; the crew reads it as a stuck
    /// machine. Refusing at the tool is the only place the habit reliably breaks, and the message
    /// says what to do instead rather than just saying no.
    /// </summary>
    private static ToolResult RepeatRefusal() =>
        ToolResult.Fail(ToolError.BadArgs,
            "ты только что это говорил. Не повторяйся: скажи что-то новое или промолчи — " +
            "молчание нормально, если добавить нечего.",
            retry: "none");

    // ----------------------------------------------------------------------- noop

    /// <summary>
    /// Явное «ничего не делаю». Закрывает ход — см. <see cref="AiTool.EndsTurn"/>.
    ///
    /// Единственный инструмент без единой причины отказать, и это намеренно: он не трогает мир,
    /// не маршалится на главный поток и работает во всех режимах, включая разбор и интелликарту.
    /// Отказ здесь означал бы «ты обязан что-то сделать», а сказать «нечего» агент должен уметь
    /// всегда.
    ///
    /// До него закрыть ход можно было только перестав звать инструменты, то есть ответив прозой.
    /// А проза при любом радиотрафике поднимает owed — <c>Addressed</c> истинно от ЛЮБОЙ строки
    /// рации, не только обращённой к ИИ, — и тянет за собой напоминание «этого никто не услышал»
    /// и лишний запрос к модели. То есть агента подталкивали высказаться ровно там, где правильный
    /// ответ молчание, и он высказывался: наблюдённая привычка ставить «Экипаж, Аксиома на связи»
    /// в общий канал растёт отсюда же.
    /// </summary>
    private Task<ToolResult> NoopAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        _ = s;
        _ = ct;

        TryGetString(args, "reason", out var reason);

        // Debug, а не Info: на спокойной станции это самый частый вызов, и в Info он затопил бы
        // журнал сервера ровно тем, что означает «ничего не произошло».
        _sawmill.Debug($"[LLM] noop{(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}");

        return Task.FromResult(ToolResult.Effected("self", new Dictionary<string, object?>
        {
            ["ход_окончен"] = true,
        }));
    }

    // ------------------------------------------------------------------------ say

    public Task<ToolResult> SayAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "text", out var text) || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, "say: нужен непустой 'text'"));

        if (s.AlreadySaid(text!))
            return Task.FromResult(RepeatRefusal());

        return OnMainAsync(s, "say", () =>
        {
            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected("self", new Dictionary<string, object?> { ["dry_run"] = true, ["said"] = text });

            // checkRadioPrefix: false on purpose — a stray ":c" typed by the model must not
            // silently become a station-wide Command broadcast. Radio goes through the radio tool,
            // where the channel is an explicit enum it has to choose.
            _chat.TrySendInGameICMessage(s.Brain, text!, InGameICChatType.Speak, ChatTransmitRange.Normal,
                hideLog: false, shell: null, player: null, nameOverride: null,
                checkRadioPrefix: false, ignoreActionBlocker: true);

            _sawmill.Info($"[LLM] say: {text}");
            return ToolResult.Effected("self", new Dictionary<string, object?> { ["said"] = text });
        }, ct);
    }

    // ---------------------------------------------------------------------- radio

    public Task<ToolResult> RadioAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "text", out var text) || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, "radio: нужен непустой 'text'"));

        if (s.AlreadySaid(text!))
            return Task.FromResult(RepeatRefusal());

        var allowed = s.Body.ChannelsFor(s.Mode);

        // Канал не назван — говорим в тот, на который настроен тумблер. Разовое обращение в другой
        // канал тумблер НЕ двигает: это ровно та же механика, что префикс у живого игрока.
        var explicitChannel = TryGetString(args, "channel", out var channel) && !string.IsNullOrWhiteSpace(channel);

        if (!explicitChannel)
        {
            channel = s.State.OutputChannel;

            // Тумблер может указывать на канал, недоступный в текущем режиме: карденье случается
            // между ходами, а состояние живёт дольше хода. Отказывать здесь нельзя — модель
            // получила бы отказ про канал, которого она не называла, и пошла бы искать в нём
            // опечатку. Молча съезжаем на доступный и говорим об этом в ответе.
            if (!allowed.Contains(channel, StringComparer.OrdinalIgnoreCase))
            {
                s.State.ChannelBeforeCarding ??= channel;
                s.State.OutputChannel = allowed[0];
                channel = allowed[0];
            }
        }

        var match = allowed.FirstOrDefault(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            // Канал существует, но не в этом режиме — это другой отказ, и говорить о нём надо
            // иначе, иначе модель будет искать опечатку там, где её нет.
            if (s.Body.ChannelsFor(AgentMode.Core).Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(ToolResult.Fail(ToolError.Carded,
                    $"из интелликарты канал '{channel}' недоступен — передатчик остался в ядре",
                    retry: "other_target", alternatives: allowed));
            }

            var near = allowed
                .OrderBy(c => AiToolRegistry.Distance(c.ToLowerInvariant(), channel!.ToLowerInvariant()))
                .Take(3).ToList();

            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, $"radio: нет канала '{channel}'",
                retry: "other_target", alternatives: near));
        }

        return OnMainAsync(s, "radio", () =>
        {
            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected("self",
                    new Dictionary<string, object?> { ["dry_run"] = true, ["channel"] = match, ["said"] = text });

            _radio.SendRadioMessage(s.Brain, text!, new ProtoId<RadioChannelPrototype>(match), s.Brain);

            _sawmill.Info($"[LLM] radio {match}: {text}");
            return ToolResult.Effected("self", new Dictionary<string, object?> { ["channel"] = match, ["said"] = text });
        }, ct);
    }

    // ------------------------------------------------------------ переключатель

    /// <summary>
    /// Выбрать канал, в который уходит речь по умолчанию.
    ///
    /// Не трогает мир и потому не маршалится: это внутренняя настройка агента, как положение
    /// тумблера на пульте. Ход, назвавший канал прямо в <c>radio</c>, тумблер не двигает.
    /// </summary>
    public Task<ToolResult> SetChannelAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        _ = ct;

        var allowed = ChannelsFor(s.Mode);

        if (!TryGetString(args, "channel", out var channel) || string.IsNullOrWhiteSpace(channel))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, "set_channel: нужен 'channel'",
                retry: "other_target", alternatives: allowed));

        var match = allowed.FirstOrDefault(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            if (AiRadioChannels.Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(ToolResult.Fail(ToolError.Carded,
                    $"из интелликарты канал '{channel}' недоступен — передатчик остался в ядре",
                    retry: "other_target", alternatives: allowed));
            }

            var near = allowed
                .OrderBy(c => AiToolRegistry.Distance(c.ToLowerInvariant(), channel!.ToLowerInvariant()))
                .Take(3).ToList();

            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, $"нет канала '{channel}'",
                retry: "other_target", alternatives: near));
        }

        var previous = s.State.OutputChannel;
        s.State.OutputChannel = match;

        _sawmill.Info($"[LLM] канал вывода: {previous} -> {match}");

        return Task.FromResult(ToolResult.Effected("self", new Dictionary<string, object?>
        {
            ["канал_был"] = previous,
            ["канал_стал"] = match,
        }));
    }

    // ------------------------------------------------------------------- announce

    /// <summary>
    /// Station-wide announcement and alert level, driven through the AI's own intrinsic
    /// communications console by raising its BUI messages directly with <c>Actor = brain</c> —
    /// byte for byte what <c>SharedUserInterfaceSystem.OnMessageReceived</c> does for a real click.
    ///
    /// Calling the shuttle is correctly impossible: the intrinsic console is declared with
    /// <c>canShuttle: false</c>, so <c>CanCallOrRecall</c> refuses — exactly as it does for a human
    /// Station AI.
    /// </summary>
    private Task<ToolResult> AnnounceAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        TryGetString(args, "text", out var text);
        TryGetString(args, "alert_level", out var level);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(level))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs,
                "announce: нужен 'text', или 'alert_level', или оба"));

        return OnMainAsync(s, "announce", () =>
        {
            if (s.Mode == AgentMode.Carded)
                return ToolResult.Fail(ToolError.Carded, DeviceGate.Carded.ToDetail(),
                    DeviceGate.Carded.Retry());

            if (!HasComp<CommunicationsConsoleComponent>(s.Brain))
                return ToolResult.Fail(ToolError.Internal, "консоль связи сейчас недоступна",
                    retry: "later");

            var effect = new Dictionary<string, object?>();

            if (_cfg.GetCVar(AiCVars.DryRun))
            {
                effect["dry_run"] = true;
                effect["text"] = text;
                effect["alert_level"] = level;
                return ToolResult.Effected("self", effect);
            }

            if (!string.IsNullOrWhiteSpace(level))
            {
                var msg = new CommunicationsConsoleSelectAlertLevelMessage(new ProtoId<Content.Shared.AlertLevel.AlertLevelPrototype>(level!))
                {
                    Actor = s.Brain,
                    UiKey = CommunicationsConsoleUiKey.Key,
                };
                RaiseLocalEvent(s.Brain, (object)msg, true);
                effect["alert_level_requested"] = level;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Noted BEFORE the message goes out, because the echo comes back synchronously:
                // the console dispatches the announcement inside this same RaiseLocalEvent, and the
                // agent's own observation handler runs before control returns here. Recording it
                // afterwards would be recording it too late.
                s.Queue.NoteSelfAnnouncement(text!);

                var msg = new CommunicationsConsoleAnnounceMessage(text!)
                {
                    Actor = s.Brain,
                    UiKey = CommunicationsConsoleUiKey.Key,
                };
                RaiseLocalEvent(s.Brain, (object)msg, true);
                effect["announced"] = text;
            }

            // Read the level back rather than trusting the request: the console has a cooldown and
            // an access check, and a refused announcement looks identical to a successful one from
            // the caller's side.
            var station = _station.GetOwningStation(s.Brain);
            if (station != null && TryComp<Content.Shared.AlertLevel.AlertLevelComponent>(station.Value, out var alert))
            {
                effect["alert_level_now"] = alert.CurrentAlertLevel;

                // …and COMPARE it. Reading it back and not looking was the whole bug: prototype ids
                // are Green/Blue/Red, the schema offered green/blue/red, ProtoId resolution is
                // ordinal, and SetLevel returns early on an unknown id without raising anything. So
                // every alert level the model could request was a silent no-op that answered ok:true
                // — and it went and told the crew the station was on red.
                if (!string.IsNullOrWhiteSpace(level)
                    && !string.Equals(alert.CurrentAlertLevel, level, StringComparison.Ordinal))
                {
                    effect["alert_level_отказано"] =
                        $"уровень не сменился и остался {alert.CurrentAlertLevel}: либо консоль на " +
                        "кулдауне, либо такого уровня нет. Не объявляй экипажу смену, которой не было";
                }
            }

            _sawmill.Info($"[LLM] announce text='{text}' level='{level}'");
            return ToolResult.Effected("self", effect);
        }, ct);
    }

    // ------------------------------------------------------------------ move_camera

    /// <summary>
    /// Move the eye to a target it can already see.
    ///
    /// A teleport within existing camera coverage rather than a simulated drag: the eye has no
    /// collision and vision — not physics — is the real constraint, so this is behaviourally what
    /// a human does by dragging, without needing a pathfinder or a multi-tick state machine. The
    /// clamp is what keeps it honest: the destination must be somewhere the AI can already see.
    /// </summary>
    private Task<ToolResult> MoveCameraAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var hasHandle = TryGetString(args, "handle", out var handleArg) && !string.IsNullOrWhiteSpace(handleArg);
        // Evaluated separately rather than with &&: short-circuiting would leave y definitely
        // unassigned and the compiler is right to say so.
        var hasX = TryGetFloat(args, "x", out var px);
        var hasY = TryGetFloat(args, "y", out var py);
        var hasPoint = hasX && hasY;

        if (!hasHandle && !hasPoint)
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs,
                "move_camera: нужен 'handle' из look, либо пара 'x' и 'y' — например координаты из crew_status"));

        return OnMainAsync(s, "move_camera", () =>
        {
            if (s.Mode == AgentMode.Carded)
                return ToolResult.Fail(ToolError.Carded, DeviceGate.Carded.ToDetail(),
                    DeviceGate.Carded.Retry());

            if (!_stationAi.TryGetCore(s.Brain, out var core) || core.Comp?.RemoteEntity == null)
                return ToolResult.Fail(ToolError.Internal, "у тебя сейчас нет глаза", retry: "later");

            var eye = core.Comp.RemoteEntity.Value;

            EntityCoordinates destination;
            string at;

            if (hasHandle)
            {
                if (!TryResolve(s, args, out var uid, out var failure))
                    return failure!;

                if (!IsVisibleToAi(s.Brain, uid))
                    return ToolResult.Fail(ToolError.NotVisible, DeviceGateExt.NoCameraDetail,
                        retry: "other_target");

                destination = Transform(uid).Coordinates;
                at = Name(uid);
            }
            else
            {
                // Jumping to a bare point is what makes a crew_status position actionable. It is
                // still gated by camera coverage on the destination tile, so this buys reach the AI
                // did not have, never sight it should not have: a point with no camera refuses.
                if (!TryPointOnGrid(eye, px, py, out destination, out var why))
                    return why!;

                // Name the place, not just the numbers. "at": "точка (112,-40)" told the model
                // nothing it could repeat to the crew, and every other tool answers with a landmark.
                var place = PlaceNear(_xform.ToMapCoordinates(destination));
                at = string.Create(CultureInfo.InvariantCulture, $"точка ({px:F0},{py:F0})");

                if (place != "неизвестно")
                    at += $", у {place}";
            }

            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected("self", new Dictionary<string, object?> { ["dry_run"] = true, ["at"] = at });

            _xform.SetCoordinates(eye, destination);

            var pos = _xform.GetMapCoordinates(eye);
            return ToolResult.Effected("self", new Dictionary<string, object?>
            {
                ["eye_x"] = (int)pos.X,
                ["eye_y"] = (int)pos.Y,
                ["at"] = at,
            });
        }, ct);
    }

    private Task<ToolResult> JumpToCoreAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        return OnMainAsync(s, "jump_to_core", () =>
        {
            if (s.Mode == AgentMode.Carded)
                return ToolResult.Fail(ToolError.Carded, DeviceGate.Carded.ToDetail(),
                    DeviceGate.Carded.Retry());

            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected("self", new Dictionary<string, object?> { ["dry_run"] = true });

            RaiseLocalEvent(s.Brain, new JumpToCoreEvent());

            if (_stationAi.TryGetCore(s.Brain, out var core) && core.Comp?.RemoteEntity != null)
            {
                var pos = _xform.GetMapCoordinates(core.Comp.RemoteEntity.Value);
                return ToolResult.Effected("self", new Dictionary<string, object?>
                {
                    ["eye_x"] = (int)pos.X,
                    ["eye_y"] = (int)pos.Y,
                    ["at"] = "ядро",
                });
            }

            return ToolResult.Success();
        }, ct);
    }

    // -------------------------------------------------------------- device_action

    /// <summary>
    /// One dispatch table instead of fifteen tools.
    ///
    /// Every entry runs the full gate chain first and reports which link refused, then performs the
    /// mutation, then reads the resulting state back off the server. That read-back is the whole
    /// point of the <c>effect</c> field: the transcript ends up holding what actually happened
    /// rather than the model's account of what it intended, which is what later makes skill
    /// learning trustworthy.
    /// </summary>
    private Task<ToolResult> DeviceActionAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "action", out var action) || string.IsNullOrWhiteSpace(action))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, "device_action: нужен 'action'",
                alternatives: KnownActions.Take(6).ToList()));

        if (!KnownActions.Contains(action!))
        {
            var near = KnownActions
                .OrderBy(a => AiToolRegistry.Distance(a, action!))
                .Take(3).ToList();

            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs,
                $"device_action: нет действия '{action}'", retry: "other_target", alternatives: near));
        }

        TryGetString(args, "value", out var value);

        return OnMainAsync(s, "device_action", () =>
        {
            if (!TryResolve(s, args, out var uid, out var failure))
                return failure!;

            var gate = CheckGate(s.Brain, uid, s.Mode);
            if (gate != DeviceGate.Ok)
                return ToolResult.Fail(gate.ToError(), gate.ToDetail(), gate.Retry());

            var handle = s.Handles.TryGetHandle(uid, out var h) ? h : "device";

            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected(handle,
                    new Dictionary<string, object?> { ["dry_run"] = true, ["action"] = action });

            var applied = ApplyDeviceAction(s.Brain, uid, action!, value, out var why);
            if (!applied)
                return ToolResult.Fail(ToolError.BadArgs, why ?? "устройство не поддерживает это действие",
                    retry: "other_target", alternatives: AvailableActions(uid));

            _sawmill.Info($"[LLM] device_action {action} on {ToPrettyString(uid)}");
            return ToolResult.Effected(handle, ReadBack(uid));
        }, ct);
    }

    private static readonly HashSet<string> KnownActions = new()
    {
        "open", "close", "bolt", "unbolt", "electrify", "unelectrify",
        "emergency_access_on", "emergency_access_off",
        "apc_breaker_on", "apc_breaker_off", "air_alarm_mode",
    };

    private bool ApplyDeviceAction(EntityUid brain, EntityUid uid, string action, string? value, out string? why)
    {
        why = null;

        switch (action)
        {
            case "open":
                if (!TryComp<DoorComponent>(uid, out var od))
                    return Fail(out why, "это не дверь");
                _doors.TryOpen(uid, od, brain);
                return true;

            case "close":
                if (!TryComp<DoorComponent>(uid, out var cd))
                    return Fail(out why, "это не дверь");
                _doors.TryClose(uid, cd, brain);
                return true;

            // Bolts, electrification and emergency access go through the radial-action events
            // rather than the underlying systems. Those handlers carry upstream's own access
            // checks, admin logging and "device not responding" popups — reimplementing the
            // effect directly would quietly bypass all three.
            case "bolt":
            case "unbolt":
                if (!HasComp<DoorBoltComponent>(uid))
                    return Fail(out why, "у этой двери нет болтов");
                RaiseLocalEvent(uid, (object)new StationAiBoltEvent { Bolted = action == "bolt", User = brain });
                return true;

            case "electrify":
            case "unelectrify":
                if (!HasComp<ElectrifiedComponent>(uid))
                    return Fail(out why, "это устройство нельзя электризовать");
                RaiseLocalEvent(uid, (object)new StationAiElectrifiedEvent { Electrified = action == "electrify", User = brain });
                return true;

            case "emergency_access_on":
            case "emergency_access_off":
                if (!HasComp<AirlockComponent>(uid))
                    return Fail(out why, "это не шлюз");
                RaiseLocalEvent(uid, (object)new StationAiEmergencyAccessEvent
                {
                    EmergencyAccess = action == "emergency_access_on",
                    User = brain,
                });
                return true;

            // light_on / light_off used to live here, and were the only branch with no component
            // check: on anything other than an ItemTogglePointLight the event went nowhere and the
            // tool still answered ok:true with a door's state attached. Removed rather than guarded,
            // because on this fork no prototype carries both ItemTogglePointLight and
            // StationAiWhitelist — so the action was unreachable as well as unchecked, and
            // advertising a verb that can never work costs the model a turn every time it tries.
            case "apc_breaker_on":
            case "apc_breaker_off":
            {
                if (!TryComp<ApcComponent>(uid, out var apc))
                    return Fail(out why, "это не APC");

                var want = action == "apc_breaker_on";
                if (apc.MainBreakerEnabled != want)
                    _apc.ApcToggleBreaker(uid, apc, user: brain);

                return true;
            }

            case "air_alarm_mode":
            {
                if (!HasComp<AirAlarmComponent>(uid))
                    return Fail(out why, "это не воздушная тревога");

                // "replace" was offered here and in the schema and is not a member of the enum, so
                // the error text handed the model back the same invalid value it had just been
                // refused for — a guaranteed retry loop. The real set is below.
                var wanted = value?.Trim().Replace("_", "", StringComparison.Ordinal);

                if (!Enum.TryParse<AirAlarmMode>(wanted, ignoreCase: true, out var mode))
                    return Fail(out why,
                        "нужен 'value': filtering, wide_filtering, fill, panic или none");

                _airAlarm.SetMode(uid, Name(brain), mode, uiOnly: false);
                return true;
            }

            default:
                return Fail(out why, $"действие '{action}' не реализовано");
        }

        static bool Fail(out string? w, string msg)
        {
            w = msg;
            return false;
        }
    }

    /// <summary>Post-mutation world state, read on the main thread. Never the model's word for it.</summary>
    private Dictionary<string, object?> ReadBack(EntityUid uid)
    {
        var d = new Dictionary<string, object?>();

        if (TryComp<DoorComponent>(uid, out var door))
        {
            var doorState = door.State;
            d["state"] = doorState.ToString();
        }

        if (TryComp<DoorBoltComponent>(uid, out var bolt))
            d["bolted"] = bolt.BoltsDown;

        if (TryComp<ElectrifiedComponent>(uid, out var el))
            d["electrified"] = el.Enabled;

        if (TryComp<AirlockComponent>(uid, out var airlock))
            d["emergency_access"] = airlock.EmergencyAccess;

        if (TryComp<ApcComponent>(uid, out var apc))
            d["main_breaker"] = apc.MainBreakerEnabled;

        if (TryComp<AirAlarmComponent>(uid, out var alarm))
        {
            var alarmMode = alarm.CurrentMode;
            d["air_alarm_mode"] = alarmMode.ToString();
        }

        if (d.Count == 0)
            d["powered"] = _power.IsPowered(uid);

        return d;
    }

    // ------------------------------------------------------------------ device_ui

    /// <summary>
    /// Console documentation, fetched on demand, and the one verb that acts on it.
    ///
    /// Called with just a handle it reads: the console's current state and the list of actions it
    /// accepts, both derived by reflecting the client/server data contract
    /// (<see cref="UiContract"/>, <see cref="UiActionIndex"/>). Called with an action it presses
    /// that action and reads the console again, so the response is always the new state rather
    /// than a claim about it.
    ///
    /// This replaces a hand-written table of two commands. The table was the reason the agent could
    /// talk to exactly one console in the game: everything else in the AI whitelist — atmospherics
    /// alerts, power monitoring, criminal records, robotics, cargo — was reachable and undescribed.
    /// Nothing about a console is spelled out here, so nothing here goes stale when a console
    /// changes, and a console added upstream works the day it lands.
    ///
    /// The description never enters the frozen prefix: the tool schema is four fields, and the
    /// hundred consoles behind it cost nothing until one is opened.
    /// </summary>
    /// <param name="param">Имя аргумента с хендлом: у ядра «handle», у борга «target».</param>
    /// <param name="gate">
    /// Чем проверяется право трогать консоль. <c>null</c> — станционные ворота (вайтлист ИИ,
    /// питание, видимость через камеры). Тело с руками передаёт своё: «я рядом и могу дотянуться».
    ///
    /// <para>
    /// Параметр, а не копия метода, потому что различие ровно здесь: сам драйвер консолей —
    /// отражение по типам BUI-сообщений — про тело не знает ничего и работает одинаково для
    /// любого, кто имеет право нажать.
    /// </para>
    /// </param>
    public Task<ToolResult> DeviceUiAsync(AgentSession s, JsonElement args, CancellationToken ct,
        string param = "handle", Func<AgentSession, EntityUid, ToolResult?>? gate = null)
    {
        TryGetString(args, "action", out var action);
        var hasArgs = args.ValueKind == JsonValueKind.Object &&
                      args.TryGetProperty("args", out var rawArgs) &&
                      rawArgs.ValueKind != JsonValueKind.Null;

        var callArgs = hasArgs ? args.GetProperty("args") : (JsonElement?)null;

        return OnMainAsync(s, "device_ui", () =>
        {
            if (!TryResolve(s, args, out var uid, out var failure, param))
                return failure!;

            if (gate != null)
            {
                if (gate(s, uid) is { } refused)
                    return refused;
            }
            else
            {
                var station = CheckGate(s.Brain, uid, s.Mode);
                if (station != DeviceGate.Ok)
                    return ToolResult.Fail(station.ToError(), station.ToDetail(), station.Retry());
            }

            var handle = s.Handles.TryGetHandle(uid, out var h) ? h : "device";
            var index = UiIndex;
            var keys = index.KeysFor(uid);

            if (keys.Count == 0)
                return ToolResult.Fail(ToolError.NotControllable,
                    "у этого устройства нет консоли, которую можно открыть — управляй им через device_action",
                    retry: "other_target");

            var actions = index.ActionsFor(uid);

            // No action named: this is the read half, the "documentation" call.
            if (string.IsNullOrWhiteSpace(action))
                return ToolResult.Success(Snapshot(uid, handle, keys, actions));

            if (!actions.TryGetValue(action!, out var chosen))
            {
                var near = actions.Keys
                    .OrderBy(a => AiToolRegistry.Distance(a, action!))
                    .Take(3).ToList();

                return ToolResult.Fail(ToolError.BadArgs,
                    $"у '{handle}' нет действия '{action}'. Открой device_ui без action, чтобы увидеть список.",
                    retry: "other_target", alternatives: near);
            }

            var message = UiContract.Build(chosen, callArgs, out var error);
            if (message == null)
                return ToolResult.Fail(ToolError.BadArgs, error, retry: "other_target",
                    alternatives: new[] { chosen.Signature });

            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected(handle, new Dictionary<string, object?>
                {
                    ["dry_run"] = true,
                    ["action"] = chosen.Signature,
                });

            // Stamped and raised exactly as SharedUserInterfaceSystem.OnMessageReceived does for a
            // real click. The key matters: Subs.BuiEvents filters on it, and a message carrying the
            // wrong one is silently dropped by every handler.
            //
            // With several interfaces on one entity there is no way to tell from outside which key
            // a handler wants — the filter is inside the subscription closure — so each is tried in
            // turn. Handlers for the other keys see a message they do not match and ignore it.
            foreach (var key in keys)
            {
                message.Actor = s.Brain;
                message.UiKey = key;
                RaiseLocalEvent(uid, (object)message, true);
            }

            _sawmill.Info($"[LLM] device_ui {chosen.Name} on {ToPrettyString(uid)}");

            return ToolResult.Effected(handle, Snapshot(uid, handle, keys, actions));
        }, ct);
    }

    /// <summary>
    /// What the console looks like right now: one state block per interface, plus the callable
    /// actions.
    ///
    /// State is read back after acting rather than predicted, for the same reason every other tool
    /// here returns <c>effect</c> — what the server recorded, not what the agent intended.
    /// </summary>
    private Dictionary<string, object?> Snapshot(
        EntityUid uid,
        string handle,
        IReadOnlyList<Enum> keys,
        IReadOnlyDictionary<string, UiContract.UiAction> actions)
    {
        var state = new Dictionary<string, object?>();

        foreach (var key in keys)
        {
            // The state object first, then the console's own component. Two sources because the
            // engine has two conventions: the older one pushes a state object, the newer one
            // networks a component and lets the client read it directly. A console using the newer
            // one is not a console without readings — it is a console whose readings are somewhere
            // else, and reading only the first source showed it as empty.
            if (_uiSystem.TryGetUiState<BoundUserInterfaceState>((uid, null), key, out var raw))
                state[key.ToString()] = UiContract.Describe(raw);
            else if (UiIndex.StateComponentFor(uid, key) is { } component)
                state[key.ToString()] = UiContract.Describe(component);
        }

        var result = new Dictionary<string, object?>
        {
            ["handle"] = handle,
            ["actions"] = actions.Values
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => a.Signature)
                .ToList(),
        };

        if (state.Count > 0)
        {
            // Several consoles carry one interface; unwrapping the single case keeps the common
            // response from being a dictionary with one meaningless key in it.
            result["state"] = state.Count == 1 ? state.Values.First() : state;
        }
        else
        {
            // Not a malfunction. Newer interfaces push their data through networked components
            // instead of the legacy state object, and some fill the state only once a client has
            // opened them. Saying so stops the model reading an empty console as a broken one.
            result["state_note"] =
                "состояние этой консоли не передаётся отдельным блоком — читай его через inspect, " +
                "а здесь пользуйся списком действий";
        }

        return result;
    }
}
