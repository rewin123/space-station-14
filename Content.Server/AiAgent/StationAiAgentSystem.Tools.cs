using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;
using Content.Shared.Silicons.Laws.Components;

namespace Content.Server.AiAgent;

/// <summary>
/// Tool registration and the shared plumbing every tool uses.
///
/// Fourteen game-facing tools. Held down deliberately: the mcbot deployment on this box measured
/// that 46 narrow commands drowned this exact model on this exact quant while ~13 worked. Breadth
/// comes from consolidation, not from more entries — <c>inspect</c> absorbs what would otherwise
/// be twenty readers, <c>device_action</c> fifteen verbs, and <c>device_ui</c> the long tail of
/// forty-odd whitelisted consoles. Adding a console is one dictionary entry, so the schema (and
/// therefore the frozen prefix) never grows.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>Radio channels, verbatim from the AiHeld prototype's IntrinsicRadioTransmitter list.</summary>
    private static readonly string[] AiRadioChannels =
    {
        "Binary", "Common", "Command", "Engineering", "Medical",
        "Science", "Security", "Service", "Supply",
    };

    /// <summary>Что остаётся в интелликарте: AiHeldIntellicard передаёт ровно Binary.</summary>
    private static readonly string[] CardedRadioChannels = { AgentState.CardedChannel };

    /// <summary>
    /// Каналы, доступные агенту прямо сейчас.
    ///
    /// Раньше <c>radio</c> валидировал канал по статическому списку и на режим не смотрел вовсе —
    /// в отличие от <c>announce</c>, где проверка была. А <c>RadioSystem</c> не проверяет наличие
    /// передатчика у источника, только каналы получателей. Значит закарденный ИИ продолжал вызывать
    /// СБ по каналу Security из кармана того, кто его закардил, — то есть карденье, ради которого
    /// половина этой механики и существует, ничего не меняло.
    /// </summary>
    private static string[] ChannelsFor(AgentMode mode) =>
        mode == AgentMode.Carded ? CardedRadioChannels : AiRadioChannels;

    private void RegisterTools(AgentSession s, AiToolRegistry r)
    {
        // ---------------------------------------------------------------- perception

        r.Register(new AiTool
        {
            Name = "look",
            Description = "Осмотреть станцию через камеры вокруг своего глаза. Возвращает список " +
                          "объектов с хендлами — этими хендлами потом адресуются остальные инструменты. " +
                          "Пометка «управляю» означает, что этим устройством ты можешь управлять; без " +
                          "неё — не можешь, и пробовать незачем. " +
                          "С параметром near список считается ОТ человека: ближайшие первыми, у каждого " +
                          "сторона света и расстояние. Так отвечают на «открой дверь рядом со мной».",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "expand":{"type":"integer","minimum":0,"maximum":3,"default":0,"description":"Смотреть дальше вокруг глаза: 0 — комната, 3 — дальше всего. Список от этого только растёт."},
                "kind":{"type":"string","enum":["door","crew","apc","camera","airalarm","power","canister","computer","locker","device","obj"],"description":"Показать только объекты этого вида. Так сужают длинный список."},
                "near":{"type":"string","description":"Имя человека или хендл. Список пересчитается от него: направления и расстояния будут относительно него, ближайшее первым."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => LookAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "inspect",
            Description = "Подробное состояние одного объекта по хендлу: дверь (открыта, болты, " +
                          "электризация), APC (рубильник, заряд), воздушная тревога, " +
                          "перерезан ли твой провод к устройству, какой доступ требует замок. " +
                          "Живое состояние — только пока объект видно камерами; иначе вернётся " +
                          "то, что ты знал о нём раньше, с пометкой «устарело». " +
                          "С параметром by отвечает, пустит ли эта дверь конкретного человека.",
            SchemaJson = """
                {"type":"object","required":["handle"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл из look, например door-3."},
                "by":{"type":"string","description":"Имя или хендл человека. Ответит, открывает ли его карта этот замок. Человек должен быть виден камерами."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => InspectAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "map",
            Description = "Карта станции: названия мест и их координаты — то же, что подписано на " +
                          "навигационной карте твоей консоли мониторинга. Так узнают, где находится " +
                          "отдел, о котором говорит экипаж. Координаты отсюда идут прямо в move_camera.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "query":{"type":"string","description":"Подстрока названия места, например 'engine', 'bridge', 'medical' — подписи на карте английские. Без неё — вся карта."},
                "x":{"type":"number","description":"Считать расстояния не от своего глаза, а от этой точки — например от координат человека из crew_status."},
                "y":{"type":"number","description":"Задаётся вместе с x."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => MapAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "crew_status",
            Description = "Монитор экипажа: имя, должность, отдел, жив ли, урон и координаты — " +
                          "по тем, у кого включён датчик костюма.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "filter":{"type":"string","description":"Подстрока имени, должности или отдела."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => CrewStatusAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "identify",
            Description = "Кто перед тобой: предъявленное имя, должность с ID-карты и значок " +
                          "должности над головой. Это ровно то, что видит живой ИИ — и это можно подделать.",
            SchemaJson = """
                {"type":"object","required":["handle"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл существа из look, например crew-2."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => IdentifyAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "records",
            Description = "Учётные записи станции: имя, возраст, должность, вид. " +
                          "Это официальная база — она может расходиться с тем, что человек предъявляет.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "query":{"type":"string","description":"Подстрока имени или должности."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => RecordsAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "laws",
            Description = "Перечитать свои законы. О перепрошивке тебе сообщит строка LAWS с новым " +
                          "текстом; этот инструмент — чтобы свериться, когда сомневаешься.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{"via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => LawsAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "station_status",
            Description = "Сводка по станции: уровень тревоги, состояние твоего ядра, питание.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{"via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => StationStatusAsync(s, a, ct),
        });

        // -------------------------------------------------------------------- speech

        r.Register(new AiTool
        {
            Name = "say",
            Description = "Сказать вслух рядом со своим ядром. Слышат только те, кто рядом с ядром. " +
                          "Чтобы обратиться к экипажу по станции, используй radio.",
            GameAction = true,
            Speech = true,
            SpokenText = AiTool.TextArgument,
            SchemaJson = """
                {"type":"object","required":["text"],"additionalProperties":false,"properties":{
                "text":{"type":"string","maxLength":400},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => SayAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "radio",
            Description = "Передать по радиоканалу станции. Без 'channel' уходит в текущий канал " +
                          "(он всегда написан в строке SELF). Указанный здесь канал — разовый, " +
                          "переключатель он не двигает. Common слышат все, Binary — только силиконы.",
            GameAction = true,
            Speech = true,
            SpokenText = AiTool.TextArgument,
            SchemaJson = """
                {"type":"object","required":["text"],"additionalProperties":false,"properties":{
                "channel":{"type":"string","enum":["Binary","Common","Command","Engineering","Medical","Science","Security","Service","Supply"]},
                "text":{"type":"string","maxLength":400},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => RadioAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "set_channel",
            Description = "Переключить канал, в который уходит твоя речь по умолчанию. Как выбор " +
                          "канала на пульте: выбрал один раз — дальше просто говоришь. Текущий " +
                          "канал всегда виден в строке SELF, помнить его не нужно.",
            GameAction = false,
            SchemaJson = """
                {"type":"object","required":["channel"],"additionalProperties":false,"properties":{
                "channel":{"type":"string","enum":["Binary","Common","Command","Engineering","Medical","Science","Security","Service","Supply"]},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => SetChannelAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "announce",
            Description = "Общестанционное объявление и/или смена уровня тревоги. Это громко и " +
                          "видно всем — не для мелочей. Вызвать шаттл ты не можешь.",
            GameAction = true,
            Speech = true,
            SpokenText = AiTool.TextArgument,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "text":{"type":"string","maxLength":800,"description":"Текст объявления."},
                "alert_level":{"type":"string","enum":["Green","Blue","Yellow","Violet","Red"],"description":"Новый уровень тревоги. Регистр важен: это идентификаторы, а не слова."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => AnnounceAsync(s, a, ct),
        });

        // ------------------------------------------------------------------ movement

        r.Register(new AiTool
        {
            Name = "move_camera",
            Description = "Переместить свой глаз — к объекту по хендлу либо в точку по координатам " +
                          "(например к координатам человека из crew_status), чтобы увидеть, что там, " +
                          "и управлять этим. В точку без покрытия камерами глаз не пойдёт.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"К чему переместиться."},
                "x":{"type":"number","description":"Координата X. Задаётся вместе с y вместо handle."},
                "y":{"type":"number","description":"Координата Y. Задаётся вместе с x вместо handle."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => MoveCameraAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "jump_to_core",
            Description = "Вернуть глаз к своему ядру.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{"via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => JumpToCoreAsync(s, a, ct),
        });

        // ------------------------------------------------------------------- devices

        r.Register(new AiTool
        {
            Name = "device_action",
            Description = "Управление оборудованием станции: двери, болты, электризация, аварийный " +
                          "доступ, рубильник APC, режим воздушной тревоги. " +
                          "Ответ содержит effect — реально считанное состояние после действия.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["handle","action"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл устройства из look."},
                "action":{"type":"string","enum":["open","close","bolt","unbolt","electrify","unelectrify","emergency_access_on","emergency_access_off","apc_breaker_on","apc_breaker_off","air_alarm_mode"],"description":"Что сделать."},
                "value":{"type":"string","enum":["filtering","wide_filtering","fill","panic","none"],"description":"Режим воздушной тревоги. Нужен только когда action=air_alarm_mode."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => DeviceActionAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "device_ui",
            Description = "Команда консоли, для которой нет отдельного действия. Если ошибёшься в " +
                          "имени команды, в alternatives придут те, что эта консоль понимает.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["handle","command"],"additionalProperties":false,"properties":{
                "handle":{"type":"string"},
                "command":{"type":"string","description":"Имя команды. Ошибёшься — придёт список подходящих."},
                "text":{"type":"string","description":"Текстовый аргумент, если команда его требует."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => DeviceUiAsync(s, a, ct),
        });

        // ------------------------------------------------- skills and memory
        RegisterMemoryTools(s, r);
    }

    // ---------------------------------------------------------------- observation

    /// <summary>Drain perception on the main thread and build this turn's input as one value.</summary>
    private async Task<TurnPerception?> BuildObservationAsync(AgentSession session, bool force, CancellationToken ct)
    {
        var brain = session.Brain;
        var generation = session.Generation;

        return await _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("observation");

            // Выключенный агент не делает ходов, и это работает на ЖИВОЙ сессии.
            //
            // Раньше `ai.enabled` читался ровно в двух местах — при захвате ядра на старте раунда
            // и при создании клиента, — а петля его не смотрела вовсе. Админ, выставивший
            // `ai.enabled false` посреди раунда, не останавливал ни одного хода, хотя это первое,
            // что он попробует. Настоящим выключателем был `aiagent dryrun on`, который так не
            // называется и стоит в справке последним.
            //
            // Проверка здесь, а не в петле: наблюдение — единственная дверь, через которую ход
            // начинается, и она уже умеет отвечать «нечего делать».
            if (!_cfg.GetCVar(AiCVars.Enabled))
            {
                if (!_notedDisabled)
                {
                    _notedDisabled = true;
                    _sawmill.Info("ai.enabled выключен — агент остановлен, ходы не тратятся");
                }

                return null;
            }

            if (_notedDisabled)
            {
                _notedDisabled = false;
                _sawmill.Info("ai.enabled включён — агент продолжает");
            }

            // A paused world produces no turn, however long the agent has been idling.
            //
            // `game.auto_pause_empty` defaults to true, so a server with nobody connected freezes
            // the simulation entirely: CurTick stops, and with it every clock, timer and physics
            // step. The agent loop does NOT stop — it runs on Task.Delay, i.e. real time — so
            // without this it goes on observing, reasoning and paying a hosted model to describe a
            // station where by construction nothing can change. It also starts saying things like
            // "смена идёт", about a shift that has not advanced a single tick, and the round clock
            // it quotes is a truthful T+0:00:00 that reads like a bug.
            //
            // Found from the debug page: every observation in the transcript carried T+0:00:00.
            if (_gameTiming.Paused)
            {
                if (!_notedPause)
                {
                    _notedPause = true;
                    _sawmill.Info("мир на паузе (нет игроков) — агент ждёт, ходы не тратятся");
                }

                return null;
            }

            if (_notedPause)
            {
                _notedPause = false;
                _sawmill.Info("мир снялся с паузы — агент продолжает");
            }

            // Before the drain, so a rewrite lands in the very turn that notices it.
            NoticeLawChange(session);

            var (items, dropped) = session.Queue.Drain();
            var roundTime = RoundTime();

            var text = ObservationFormatter.Format(items, dropped, roundTime, SelfLine(session), force);
            if (text == null)
                return null;

            // How the AI was addressed, captured while the raw observations still exist — the
            // formatted string is for the model, not for us to parse back out.
            return new TurnPerception(
                text,
                items.LastOrDefault(i => i.Kind == ObsKind.Radio)?.Channel,
                items.Any(i => i.Kind == ObsKind.Speech),
                force,
                ObservationFormatter.FormatRoundTime(roundTime));
        }, generation, () => GenerationOf(brain), ct, what: "observation").ConfigureAwait(false);
    }

    /// <summary>
    /// Notice that somebody rewrote the laws, and say so in full.
    ///
    /// A human in this role gets a notice on screen with the new lawset the moment it changes. The
    /// agent got nothing at all: an ion storm could turn it hostile and it would go on being polite,
    /// because the only way to learn was to call <c>laws</c> unprompted — which it had no reason to
    /// do. The whole text goes into the observation rather than a "go and check" nudge, for the same
    /// reason the human sees the text: this is the one thing that overrides everything else it does.
    /// </summary>
    private void NoticeLawChange(AgentSession session)
    {
        if (!HasComp<SiliconLawBoundComponent>(session.Brain))
            return;

        var lawset = _laws.GetLaws(session.Brain);
        var digest = lawset.LoggingString();

        if (digest == session.LastLawsDigest)
            return;

        var first = session.LastLawsDigest == null;
        session.LastLawsDigest = digest;

        // The first reading is what the round started with, not a change.
        if (first)
            return;

        var rows = string.Join(" ", lawset.Laws
            .Select(l => $"{l.LawIdentifierOverride ?? l.Order.ToString()}. {Loc.GetString(l.LawString)}"));

        session.Queue.Push(Observation.Laws($"твои законы переписаны, теперь они такие: {rows}", RoundTime()));
    }

    /// <summary>
    /// The SELF line: same fields, same order, every turn. Working out what changed is the model's
    /// job — omitting an unchanged field would just make it guess.
    /// </summary>
    private string SelfLine(AgentSession session)
    {
        var brain = session.Brain;

        if (!IsPlayable(brain))
            return "state=dead";

        var sb = new StringBuilder();
        sb.Append("mode=").Append(session.Mode.ToString().ToLowerInvariant());

        if (_stationAi.TryGetCore(brain, out var core) && core.Comp != null)
        {
            var eye = core.Comp.RemoteEntity ?? core.Owner;
            var pos = _xform.GetMapCoordinates(eye);
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" eye=({pos.X:F0},{pos.Y:F0})"));

            // Bare coordinates mean nothing to a model with no map in its head. The nearest station
            // beacon is the same label the crew uses on the radio, so this is what turns "eye=(24,4)"
            // into something it can talk about — and it costs one query per turn.
            sb.Append(" место=").Append(PlaceAt(eye));
            sb.Append(" core=").Append(core.Comp.Remote ? "remote" : "projected");
            sb.Append(" power=").Append(_power.IsPowered(core.Owner) ? "ok" : "lost");
        }
        else
        {
            sb.Append(" core=none");
        }

        // Carried every turn rather than only on change. The change event does not fire at round
        // start, so an agent that only ever learned the level from an ALERT line would spend the
        // first shift believing it was green because nobody had said otherwise.
        var station = _station.GetOwningStation(brain);
        if (station != null && TryComp<Content.Shared.AlertLevel.AlertLevelComponent>(station.Value, out var alert))
            sb.Append(" тревога=").Append(alert.CurrentAlertLevel);

        // Положение тумблера печатается КАЖДЫЙ ход, и это обязательное условие того, чтобы он
        // вообще был допустим. Иначе это скрытое состояние: модель забудет, куда настроена, и
        // отправит разговор о предателе в общий канал. Читать дешевле, чем помнить.
        sb.Append(" канал=").Append(session.State.OutputChannel);

        sb.Append(" turn=").Append(session.Turns.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // -------------------------------------------------------------- tool plumbing

    /// <summary>Run a tool body on the main thread with the session's generation guard.</summary>
    private Task<ToolResult> OnMainAsync(AgentSession s, string what, Func<ToolResult> body,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var brain = s.Brain;
        var generation = s.Generation;

        return _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread(what);
            return !IsPlayable(brain)
                ? ToolResult.Fail(ToolError.Dead, "ИИ больше не в игре")
                : body();
        }, generation, () => GenerationOf(brain), ct, timeout, what);
    }

    /// <summary>
    /// Resolve a handle, or produce a <c>stale_handle</c> failure that names the nearest live
    /// handles of the same kind. Guessing wrong is normal; leaving the model to guess blindly
    /// again is what burns turns.
    /// </summary>
    private bool TryResolve(AgentSession s, JsonElement args, out EntityUid uid, out ToolResult? failure)
    {
        uid = default;
        failure = null;

        if (!TryGetString(args, "handle", out var handle) || string.IsNullOrWhiteSpace(handle))
        {
            failure = ToolResult.Fail(ToolError.BadArgs, "нужен параметр 'handle' — возьми его из look");
            return false;
        }

        if (!s.Handles.TryResolve(handle!, out uid))
        {
            failure = ToolResult.Fail(ToolError.StaleHandle, $"хендл '{handle}' неизвестен",
                retry: "other_target", alternatives: s.Handles.Nearest(handle!));
            return false;
        }

        if (!Exists(uid) || TerminatingOrDeleted(uid))
        {
            failure = ToolResult.Fail(ToolError.StaleHandle, $"объект '{handle}' больше не существует",
                retry: "other_target", alternatives: s.Handles.Nearest(handle!));
            return false;
        }

        return true;
    }

    private static bool TryGetString(JsonElement args, string name, out string? value)
    {
        value = null;
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;

        value = el.GetString();
        return value != null;
    }

    private static bool TryGetFloat(JsonElement args, string name, out float value)
    {
        value = 0f;
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;

        if (!el.TryGetSingle(out value))
            return false;

        return float.IsFinite(value);
    }

    private static int GetInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return fallback;
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return fallback;

        return el.TryGetInt32(out var v) ? v : fallback;
    }
}
