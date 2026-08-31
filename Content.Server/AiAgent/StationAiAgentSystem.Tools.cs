using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Threading;
using Content.Server.AiAgent.Tools;
using Content.Shared.Silicons.Laws.Components;

namespace Content.Server.AiAgent;

/// <summary>
/// Tool registration and the shared plumbing every tool uses.
///
/// Sixteen game-facing tools, plus <c>noop</c> — the one that does nothing. Held down deliberately:
/// the mcbot deployment on this box measured
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
                "near":{"type":"string","description":"Имя человека или хендл. Список пересчитается от него: направления и расстояния будут относительно него, ближайшее первым."}}}
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
                "by":{"type":"string","description":"Имя или хендл человека. Ответит, открывает ли его карта этот замок. Человек должен быть виден камерами."}}}
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
                "y":{"type":"number","description":"Задаётся вместе с x."}}}
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
                "filter":{"type":"string","description":"Подстрока имени, должности или отдела."}}}
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
                "handle":{"type":"string","description":"Хендл существа из look, например crew-2."}}}
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
                "query":{"type":"string","description":"Подстрока имени или должности."}}}
                """,
            Handler = (a, ct) => RecordsAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "station_status",
            Description = "Сводка по станции: уровень тревоги, состояние твоего ядра, питание.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
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
                "text":{"type":"string","maxLength":400}}}
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
                "text":{"type":"string","maxLength":400}}}
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
                "channel":{"type":"string","enum":["Binary","Common","Command","Engineering","Medical","Science","Security","Service","Supply"]}}}
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
                "alert_level":{"type":"string","enum":["Green","Blue","Yellow","Violet","Red"],"description":"Новый уровень тревоги. Регистр важен: это идентификаторы, а не слова."}}}
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
                "y":{"type":"number","description":"Координата Y. Задаётся вместе с x вместо handle."}}}
                """,
            Handler = (a, ct) => MoveCameraAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "jump_to_core",
            Description = "Вернуть глаз к своему ядру.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
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
                "value":{"type":"string","enum":["filtering","wide_filtering","fill","panic","none"],"description":"Режим воздушной тревоги. Нужен только когда action=air_alarm_mode."}}}
                """,
            Handler = (a, ct) => DeviceActionAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "device_ui",
            Description = "Консоль станции. Без 'action' — читает её: текущее состояние и список " +
                          "действий, которые именно эта консоль понимает. С 'action' — выполняет " +
                          "действие и возвращает состояние уже после него. Список действий заранее " +
                          "знать не нужно и выдумывать нельзя: сначала прочитай консоль.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["handle"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл консоли из look."},
                "action":{"type":"string","description":"Имя действия из списка, который вернул этот же инструмент без action. Пусто — только прочитать."},
                "args":{"type":"object","description":"Аргументы действия по его сигнатуре из того же списка."}}}
                """,
            Handler = (a, ct) => DeviceUiAsync(s, a, ct),
        });

        RegisterCommonTools(s, r);
    }

    /// <summary>
    /// Инструменты, одинаковые для любого тела.
    ///
    /// <para>
    /// Отбор строгий: сюда попало только то, что не меняет ни смысла, ни описания при смене тела.
    /// <c>noop</c> закрывает ход, <c>laws</c> перечитывает законы силикона, таймеры живут в
    /// состоянии агента, память и навыки — в файлах. Ни один из них не знает, есть ли у агента
    /// камеры или ноги.
    /// </para>
    /// <para>
    /// <c>say</c>, <c>radio</c> и <c>set_channel</c> сюда сознательно <b>не</b> попали, хотя
    /// соблазн был. У них расходится не реализация, а <em>описание и схема</em>: «слышат те, кто
    /// рядом с ядром» для борга — ложь, а перечень каналов в <c>enum</c> у шасси другой. Описание
    /// инструмента едет в замороженный префикс и является для модели единственным источником
    /// правды о её возможностях, поэтому общая формулировка «на все тела» была бы не экономией, а
    /// дезинформацией.
    /// </para>
    /// </summary>
    public void RegisterCommonTools(AgentSession s, AiToolRegistry r)
    {
        r.Register(new AiTool
        {
            Name = "laws",
            Description = "Перечитать свои законы. О перепрошивке тебе сообщит строка LAWS с новым " +
                          "текстом; этот инструмент — чтобы свериться, когда сомневаешься.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
                """,
            Handler = (a, ct) => LawsAsync(s, a, ct),
        });

        // ------------------------------------------------------------ ничего не делать

        r.Register(new AiTool
        {
            Name = "noop",
            Description = "Ничего не делать: ты прочитал наблюдение и вмешиваться не нужно. " +
                          "Ход на этом заканчивается. Это нормальный ответ и правильный ответ на " +
                          "чужой разговор по рации: смена идёт сама, и большую часть времени от " +
                          "тебя ничего не требуется. Если обращались именно к тебе — сначала " +
                          "ответь через say или radio, и только потом noop.",

            // Не GameAction: ход должен закрываться и во время разбора, и из интелликарты.
            // Единственный инструмент, который обязан работать всегда.
            EndsTurn = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "reason":{"type":"string","maxLength":200,"description":"Коротко для журнала, зачем ты решил не вмешиваться. Экипаж этого не увидит."}}}
                """,
            Handler = (a, ct) => NoopAsync(s, a, ct),
        });

        // ---------------------------------------------------------------- таймеры
        RegisterTimerTools(s, r);

        // ---------------------------------------------- файловая система агента
        //
        // Ни один из трёх не помечен GameAction — и это несущее свойство. Именно оно позволяет
        // куратору писать на разборе отрезка, когда игровые инструменты отвечают review_mode.
        RegisterVfsTools(s, r);
    }

    // ---------------------------------------------------------------- observation

    /// <summary>Drain perception on the main thread and build this turn's input as one value.</summary>
    private async Task<TurnPerception?> BuildObservationAsync(AgentSession session, bool force, CancellationToken ct)
    {
        var brain = session.Brain;
        var generation = session.Generation;

        return await _world.RunAsync(() =>
        {
            _world.AssertMainThread("observation");

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

            // Тому же правилу подчиняется восприятие, которое тело считает само (у борга — разность
            // поля зрения): посчитать надо ДО слива, иначе строки уедут в следующий ход.
            session.Body.BeforeObservation?.Invoke(session);

            var (items, dropped) = session.Queue.Drain();
            var roundTime = RoundTime();

            var text = ObservationFormatter.Format(items, dropped, roundTime, session.Body.SelfLine(session), force);
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
        }, generation, () => GenerationOf(brain), ct, what: "observation", priority: WorldPriority.Urgent).ConfigureAwait(false);
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

        // Заведённые будильники печатаются по той же причине, что и положение тумблера: иначе это
        // скрытое состояние. Агент, забывший, что уже поставил таймер на обход, ставит второй — и
        // будит себя дважды на одно дело. Печатается только когда они есть, и только имя со сроком:
        // тексты лежат в list_timers, и место в каждом наблюдении им ни к чему.
        var timers = TimersForSelf(session);
        if (timers.Length > 0)
            sb.Append(" таймеры=").Append(timers);

        // Идущие скрипты — по той же причине, что и будильники: запущенное фоновое дело иначе
        // становится скрытым состоянием, и агент запускает второе такое же.
        var scripts = ScriptsForSelf(session);
        if (scripts.Length > 0)
            sb.Append(' ').Append(scripts);

        sb.Append(" turn=").Append(session.Turns.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // -------------------------------------------------------------- tool plumbing

    /// <summary>
    /// Что меняет мир или говорит вслух — в срочную полосу; что только смотрит — в обычную.
    ///
    /// <para>
    /// Правило именно такое, а не «дешёвое вперёд». Смысл приоритета в том, чтобы объявление
    /// тревоги не ждало за обзором, который считает две тысячи сущностей: экипаж замечает
    /// задержку речи и не замечает задержку опроса. Стоимость тут ни при чём — <c>announce</c>
    /// сам по себе не из дешёвых.
    /// </para>
    /// <para>
    /// Политика живёт одним списком, а не параметром на восемнадцати вызовах: так её видно
    /// целиком и она не разъезжается при добавлении инструмента.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> UrgentOps = new(StringComparer.Ordinal)
    {
        "say", "radio", "announce", "move_camera", "jump_to_core",
        "device_action", "device_ui", "new_timer", "del_timer",
        "observation", "compaction announce", "untooled reply",

        // Тело борга. Ходьба и руки меняют мир и видны экипажу немедленно — задержка здесь
        // выглядит как зависший робот, а не как занятый сервер.
        "goto", "step", "use", "pickup", "drop", "hit", "module", "console",
    };

    private static WorldPriority PriorityOf(string what) =>
        UrgentOps.Contains(what) ? WorldPriority.Urgent : WorldPriority.Normal;

    /// <summary>
    /// Run a tool body on the main thread with the session's generation guard.
    ///
    /// <para>
    /// Публичный: инструменты второго тела маршалируются через ту же шину и тот же бюджет кадра.
    /// Своя шина у борга означала бы, что два агента делят кадр без общего потолка — то есть
    /// ровно та просадка тика, ради предсказуемости которой шина и заведена.
    /// </para>
    /// <para>
    /// Проверка живости идёт через <c>Body.Alive</c>, а не через станционный <c>IsPlayable</c>:
    /// «жив» для мозга в ядре и для шасси на батарее — разные вопросы.
    /// </para>
    /// </summary>
    /// <summary>
    /// То же, что <see cref="OnMainAsync"/>, но тяжёлая часть режется по бюджету кадра.
    ///
    /// <para>
    /// Заведено под <c>look</c> и пока используется только им. Причина в цифрах: по профилю фаз
    /// теневой каст — это 18-22 мс из 24-29, то есть кадр целиком, а сбор сущностей и строки —
    /// единицы миллисекунд. Пока каждый вызов стоил круга через модель, это было терпимо; в режиме
    /// Lua скрипт зовёт look в цикле, и главный поток встал.
    /// </para>
    /// </summary>
    public Task<ToolResult> OnMainSlicedAsync(
        AgentSession s,
        string what,
        Func<Threading.JobBudget, bool> step,
        Func<ToolResult> finish,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var brain = s.Brain;
        var generation = s.Generation;
        var alive = s.Body.Alive;

        // Проверка живости стоит в ОБОИХ половинах, и это не дублирование: между первым срезом и
        // хвостом проходят кадры, за которые агента вполне может не стать. В тяжёлой части она
        // работает как выход — считать обзор для выбывшего незачем.
        var job = new Threading.SteppedJob<ToolResult>(what, PriorityOf(what),
            budget => !alive() || step(budget),
            () => !alive() ? ToolResult.Fail(ToolError.Dead, "агент больше не в игре") : finish());

        return _world.SubmitAsync(job, job.Task, generation, () => GenerationOf(brain), ct, timeout);
    }

    public Task<ToolResult> OnMainAsync(AgentSession s, string what, Func<ToolResult> body,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var brain = s.Brain;
        var generation = s.Generation;
        var alive = s.Body.Alive;

        return _world.RunAsync(() =>
        {
            _world.AssertMainThread(what);
            return !alive()
                ? ToolResult.Fail(ToolError.Dead, "агент больше не в игре")
                : body();
        }, generation, () => GenerationOf(brain), ct, timeout, what, PriorityOf(what));
    }

    /// <summary>
    /// Resolve a handle, or produce a <c>stale_handle</c> failure that names the nearest live
    /// handles of the same kind. Guessing wrong is normal; leaving the model to guess blindly
    /// again is what burns turns.
    /// </summary>
    private bool TryResolve(AgentSession s, JsonElement args, out EntityUid uid, out ToolResult? failure,
        string param = "handle")
    {
        uid = default;
        failure = null;

        if (!TryGetString(args, param, out var handle) || string.IsNullOrWhiteSpace(handle))
        {
            failure = ToolResult.Fail(ToolError.BadArgs, $"нужен параметр '{param}' — возьми его из look");
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

    public static bool TryGetString(JsonElement args, string name, out string? value)
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
