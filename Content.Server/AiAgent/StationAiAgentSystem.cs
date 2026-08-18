using Content.Server.AiAgent.Core;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Components;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Threading;
using Content.Server.AiAgent.Tools;
using Content.Server.Chat.Systems;
using Content.Server.Communications;
using Content.Server.GameTicking;
using Content.Shared.AlertLevel;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Radio;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.Power.EntitySystems;
using Content.Server.Atmos.Monitor.Systems;
using Content.Server.Power.EntitySystems;
using Content.Server.Silicons.Laws;
using Content.Server.Station.Events;
using Content.Shared.Doors.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Station;
using Content.Shared.StationRecords.Systems;
using Content.Shared.Radio;
using Content.Shared.Roles;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AiAgent;

/// <summary>
/// Owns the LLM-driven Station AI: claiming a core, collecting perception on the main thread, and
/// hosting the background agent loop.
///
/// This is the only class in the fork that both touches the entity world and knows about the
/// agent. Everything under <c>AiAgent/Llm</c>, <c>AiAgent/Context</c> and <c>AiAgent/Perception</c>
/// is deliberately free of <c>IEntityManager</c> so it cannot reach the world by accident.
/// </summary>
public sealed partial class StationAiAgentSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private SharedStationAiSystem _stationAi = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private StationAiVisionSystem _vision = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private ApcSystem _apc = default!;
    [Dependency] private AirAlarmSystem _airAlarm = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private StationRecordsSystem _records = default!;
    [Dependency] private SiliconLawSystem _laws = default!;
    [Dependency] private RogueAi.RogueAiRuleSystem _rogue = default!;
    [Dependency] private SharedStationSystem _station = default!;
    [Dependency] private Content.Server.Station.Systems.StationJobsSystem _jobs = default!;
    [Dependency] private Content.Server.Pinpointer.NavMapSystem _navMap = default!;
    [Dependency] private Content.Shared.Power.EntitySystems.SharedBatterySystem _battery = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private SharedUserInterfaceSystem _uiSystem = default!;

    private ISawmill _sawmill = default!;
    private WorldBus _world = default!;
    private GameTicker? _ticker;

    /// <summary>
    /// Номер текущего раунда, снятый на главном потоке.
    ///
    /// Нужен инструментам заметок, чтобы штамповать записи, а они работают на потоке агента и
    /// намеренно не маршалятся — дотянуться оттуда до <see cref="GameTicker"/> нельзя. Поэтому
    /// значение кладётся сюда там, где мы и так на главном потоке, а тулы читают только это поле:
    /// чтение volatile int безопасно с любого потока и никого не блокирует.
    /// </summary>
    private volatile int _roundId;

    /// <summary>Логировать паузу один раз на переход, а не каждый тик.</summary>
    private bool _notedPause;

    /// <summary>То же для выключенного ai.enabled.</summary>
    private bool _notedDisabled;

    private readonly Dictionary<EntityUid, AgentSession> _sessions = new();
    private ILlmClient? _llm;

    /// <summary>
    /// Сны, исчерпанные квоты и счётчики провайдеров.
    ///
    /// Живёт рядом с системой, а НЕ внутри клиента, и это принципиально:
    /// <see cref="ResetLlmClient"/> выбрасывает клиента на каждом рестарте раунда, а раундов за
    /// сутки десятки. Внутри клиента это состояние означало бы, что каждый рестарт заново лезет в
    /// исчерпанную подписку и добивает остаток недельного пула.
    /// </summary>
    private LlmQuotaState? _quota;

    /// <summary>Роутер, если цепочка собрана. Null на одиночном эндпоинте и в тестах со скриптом.</summary>
    public ILlmRouter? Router => _llm as ILlmRouter;

    public IReadOnlyDictionary<EntityUid, AgentSession> Sessions => _sessions;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai");

        // Constructed here, on the main thread, so it learns which thread that is.
        _world = new WorldBus(_taskManager, _sawmill, _cfg.GetCVar(AiCVars.MainThreadBudgetMs));

        // Порог живой, а не снятый один раз при старте.
        //
        // `BudgetMs` — публичное сеттерное свойство, но записать в него было некому: значение
        // читалось здесь и больше нигде, так что `cvar ai.mainthread_budget_ms 2` из админ-консоли
        // молча не делал ничего. Диагностический порог, который нельзя подкрутить на живом
        // сервере, бесполезен ровно тогда, когда нужен, — на живом сервере.
        _cfg.OnValueChanged(AiCVars.MainThreadBudgetMs, ms => _world.BudgetMs = ms);

        // Все ручки шины — живые, по той же причине. Особенно рубильник: если шина поведёт себя
        // не так, откатывать её командой из консоли, а не пересборкой с киком всех игроков.
        _cfg.OnValueChanged(AiCVars.FrameBudgetMs, ms => _world.FrameBudgetMs = ms, true);
        _cfg.OnValueChanged(AiCVars.WorldPromoteMs, ms => _world.PromoteAfterMs = ms, true);
        _cfg.OnValueChanged(AiCVars.WorldQueueMax, n => _world.QueueMax = n, true);
        _cfg.OnValueChanged(AiCVars.WorldBusEnabled, on => _world.Enabled = on, true);

        // Before the stores, so their initial contents arrive on the bus rather than appearing
        // out of nowhere to whoever connects first.
        StartDebugBus();

        // Eagerly, so no first touch from the agent thread can race one from the main thread and
        // build a second store that silently swallows whatever the loser wrote.
        ReloadAgentFiles();

        // Роль Station AI закрывается для людей ДО того, как кто-либо заспавнится.
        //
        // Иначе форк проигрывает гонку: GameTicker спавнит игроков раньше, чем меняет рун-левел,
        // за который мы цепляемся. Игрок с наигранными часами занимал ядро, нейросеть молча не
        // стартовала на весь раунд — на сервере, который называется «станцией управляет
        // нейросеть», — и единственным следом была строка в логе.
        //
        // Обратный случай не лучше: при занятом нами ядре игрок, выбравший эту роль, не влезал в
        // контейнер и появлялся невидимым неподвижным мозгом на полу прибытия.
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);

        // Perception. Radio arrives per receiver, so it can be scoped to our marker component.
        SubscribeLocalEvent<LlmStationAiComponent, RadioReceiveEvent>(OnRadioReceive);
        SubscribeLocalEvent<LlmStationAiComponent, MobStateChangedEvent>(OnMobStateChanged);

        // Local speech is raised on the speaker, not the listener, so it has to be filtered by
        // distance ourselves. Vanilla parity: the AI hears within VoiceRange of its physical core
        // and nowhere else — it has no camera microphones.
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);

        // Station-wide happenings. Both broadcast, both server-side.
        //
        // Without these the agent was structurally deaf to two of the things the prompt promised it
        // would hear: an ion storm could rewrite its laws and a captain could raise the alert to red,
        // and it went on behaving exactly as before because nothing ever told it.
        SubscribeLocalEvent<AlertLevelChangedEvent>(OnAlertLevelChanged);
        // One subscription for every announcement path, not one per origin.
        //
        // The console used to be hooked directly, and it is the only origin that raises an event of
        // its own — which is exactly why the agent heard consoles and nothing else. It now arrives
        // here like the rest: the console also calls DispatchGlobalAnnouncement a few lines later,
        // so keeping both subscriptions would have delivered console announcements twice.
        SubscribeLocalEvent<StationAnnouncementEvent>(OnAnnouncement);

        // Люди, приходящие на смену.
        //
        // Без этой подписки агент не имел НИКАКОГО способа узнать, что кто-то пришёл: в наблюдения
        // попадали только речь, рация и объявления, поэтому молчаливый игрок для него не
        // существовал. 15 августа так прошли все четыре захода подряд — четыре человека отыграли
        // смену на станции, которую агент всё это время считал пустой.
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);

        // Carding moves the brain between the core and an intellicard; the mode gate follows it.
        //
        // These are the "Got" variants, raised on the entity being moved rather than on the
        // container. That is not a stylistic choice: SharedStationAiSystem already subscribes
        // (StationAiCoreComponent, EntInsertedIntoContainerMessage), and RobustToolbox throws
        // "Duplicate Subscriptions" at startup if a second system claims the same pair. Hooking
        // our own marker is also the more honest scoping — we care about our brain moving, not
        // about every core on the map.
        SubscribeLocalEvent<LlmStationAiComponent, EntGotInsertedIntoContainerMessage>(OnBrainInserted);
        SubscribeLocalEvent<LlmStationAiComponent, EntGotRemovedFromContainerMessage>(OnBrainRemoved);

        // Зрение как поток, а не как опрос: всё, что происходит рядом с глазом, приходит строкой
        // OBSERVED. См. StationAiAgentSystem.Witness.cs — там же объяснено, почему список событий
        // не отфильтрован по «важности».
        SubscribeWitness();

        _sawmill.Info(
            $"agent system initialised enabled={_cfg.GetCVar(AiCVars.Enabled)} " +
            $"endpoint={_cfg.GetCVar(AiCVars.Endpoint)} model={_cfg.GetCVar(AiCVars.Model)}");
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ReleaseAll("server shutdown");
        StopDebugServer();
        (_llm as IDisposable)?.Dispose();
        _llm = null;

        // Счётчики окна и недели пишутся не чаще раза в полминуты, так что без этого остановка
        // сервера теряла бы последние обращения — а именно недельный расход и надо копить точно:
        // ни OpenAI, ни xAI своего потолка не публикуют, и наш счётчик — единственный источник.
        _quota?.Flush();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Идентификатор ванильной должности, которую занимает наш агент.</summary>
    private const string StationAiJob = "StationAi";

    /// <summary>
    /// Как агент подписывается в эфире. Должно совпадать с тем, как его зовёт SOUL.md.
    ///
    /// Совпадает с именем станции (<see cref="AiCVars.StationName"/>) намеренно, по решению
    /// владельца сервера. Кодом это нигде не различается — имя только присваивается сущности
    /// и ни с чем не сравнивается, — но в эфире «Аксиома» теперь значит и место, и собеседника.
    /// </summary>
    public const string AgentName = "Аксиома";

    private void OnStationPostInit(ref StationPostInitEvent ev)
    {
        // Только если агент действительно собирается занять ядро. Выключенный агент не должен
        // отбирать у людей роль, которую сам не займёт.
        if (!_cfg.GetCVar(AiCVars.Enabled) || !_cfg.GetCVar(AiCVars.AutoClaim))
            return;

        if (!_jobs.TrySetJobSlot(ev.Station, StationAiJob, 0))
            return;

        _sawmill.Info($"вакансия {StationAiJob} закрыта: ядро занимает нейросеть");
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound)
            return;

        // Снять номер раунда, пока мы на главном потоке. Делается ДО проверок ниже: штамп нужен
        // заметкам независимо от того, займём ли мы ядро сами.
        CacheRoundId();

        if (!_cfg.GetCVar(AiCVars.Enabled) || !_cfg.GetCVar(AiCVars.AutoClaim))
            return;

        var claimed = TryClaimAnyCore(out var reason);
        if (!claimed)
            _sawmill.Info($"no AI core claimed at round start: {reason}");
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent ev)
    {
        ReleaseAll("round restart");
        ResetLlmClient();

        // Здесь стирался CREW.md — память о людях своей смены. Его больше нет, и стирать нечего.
        //
        // Замысел был против метагейминга: каждый раунд SS14 это новая вселенная с теми же именами,
        // и запись «Иван Петров — предатель» из прошлой смены давала агенту то, чего он знать не
        // может. Вышло наоборот. Агент перестал писать в файл, который всё равно сотрут, и сложил
        // людей в MEMORY.md, переживающий раунды, — тот упёрся в лимит и перестал принимать что бы
        // то ни было вообще, включая факты о станции.
        //
        // Теперь люди живут в PlayerNoteStore, по файлу на человека, и переживают смену намеренно.
        // От метагейминга защищает не стирание, а штамп раунда у каждой записи: агент видит, что
        // знание из другой смены, и промпт запрещает предъявлять его как улику сегодня.
    }

    /// <summary>
    /// Drop the cached model client so the next claim builds a fresh one.
    ///
    /// Two reasons. In ops: a change to ai.endpoint or ai.model then takes effect at the next
    /// round instead of requiring a server restart. In tests: the benchmark pool hands the same
    /// server instance to the next test, and without this a live scenario inherits the scripted
    /// client an earlier scenario installed — which presents as the agent taking turns and never
    /// acting, with nothing in the log to explain it.
    /// </summary>
    public void ResetLlmClient()
    {
        (_llm as IDisposable)?.Dispose();
        _llm = null;

        // The curator captured that client at construction and was never rebuilt, so from the second
        // round onwards its first call hit a disposed HttpClient. The exception was caught by the
        // compaction ritual and logged as "the curator did not run" — and the agent quietly stopped
        // learning for the rest of the process lifetime.
        _curator = null;
    }

    /// <summary>Find an empty AI core ON A STATION and put an LLM-driven brain in it.</summary>
    public bool TryClaimAnyCore(out string reason)
    {
        if (_sessions.Count >= _cfg.GetCVar(AiCVars.MaxAgents))
        {
            reason = $"already at ai.max_agents ({_cfg.GetCVar(AiCVars.MaxAgents)})";
            return false;
        }

        var offStation = 0;

        var query = EntityQueryEnumerator<StationAiCoreComponent>();
        while (query.MoveNext(out var coreUid, out var core))
        {
            // Ядро обязано стоять на станции, и это не придирка к чистоте — без этого агент
            // оказывается глухонемым.
            //
            // В мире каждый раунд ДВА ядра: станционное (packed.yml, `PlayerStationAi`) и
            // второе, которым укомплектован сам Центком (centcomm.yml, `PlayerStationAiEmpty`
            // в позиции -0.5,-2.5). Перебор шёл по нефильтрованному запросу и брал первое
            // подходящее, то есть какое достанется. 13 августа доставалось станционное, 14 и 15
            // подряд — центкомовское.
            //
            // Цена ошибки не «агент стоит не в той комнате». RadioSystem.cs:150 отбрасывает
            // получателя, чья карта не совпадает с картой говорящего, а Центком — отдельная
            // карта (EmergencyShuttleSystem.AddCentcomm грузит его на свою и в состав станции
            // НЕ включает). Значит агент не слышит ни одной реплики экипажа, а его собственные
            // передачи не долетают ни до кого. Снаружи это выглядит как «ИИ перестал отвечать
            // в рацию»: он честно отвечает, просто в пустоту. За 15 августа — 222 наблюдения,
            // из них RADIO ровно ноль, и единственное, что он слышал, это торговые автоматы,
            // стоящие рядом с ним на Центкоме.
            //
            // Проверка именно на принадлежность станции, а не «не Центком»: карт вне станции
            // может быть сколько угодно (сальваж, руины, планеты), и на любой из них ядро
            // сломает агента ровно так же.
            if (_station.GetOwningStation(coreUid) == null)
            {
                offStation++;
                continue;
            }

            // Занятое ядро больше не пропускается вслепую: если там наш же мозг от прошлой
            // сессии, TryClaimCore его переиспользует. Иначе агента было невозможно вернуть в
            // раунд после `aiagent release` или смерти — ядро оставалось занято навсегда.
            if (_stationAi.TryGetHeld((coreUid, core), out var held) && !CanReclaim(held.Value))
                continue;

            if (TryClaimCore(coreUid, out reason))
                return true;
        }

        // Отказ теперь возможен там, где раньше был молчаливый провал на Центком: если
        // станционное ядро занял человек, агент не приходит вовсе. Так и надо — не занятое
        // ядро лучше занятого не того, — но причина обязана быть видна в логе, иначе это
        // «агент почему-то не появился».
        reason = offStation > 0
            ? $"no unoccupied AI core on a station ({offStation} off-station core(s) skipped)"
            : "no unoccupied AI core found";

        return false;
    }

    /// <summary>
    /// Мозг в ядре — наш и ничей больше: сессии на нём нет, значит его можно занять заново.
    /// </summary>
    private bool CanReclaim(EntityUid held) =>
        HasComp<LlmStationAiComponent>(held) && !_sessions.ContainsKey(held);

    public bool TryClaimCore(EntityUid coreUid, out string reason)
    {
        if (!TryComp<StationAiCoreComponent>(coreUid, out var core))
        {
            reason = $"{ToPrettyString(coreUid)} is not an AI core";
            return false;
        }

        EntityUid brain;
        var reused = false;

        if (_stationAi.TryGetHeld((coreUid, core), out var held))
        {
            // Ядро занято. Раньше здесь безусловно спавнился новый мозг, а
            // `SpawnInContainerOrDrop` при полном слоте ронял его НА ПОЛ — сессия стартовала, но
            // мозг вне контейнера не получает AiHeld, то есть оставался без камер и устройств.
            // Снаружи это выглядело как «claim сработал, а ИИ ничего не может».
            if (!CanReclaim(held.Value))
            {
                reason = _sessions.ContainsKey(held.Value)
                    ? $"{ToPrettyString(coreUid)} уже занято работающим агентом"
                    : $"{ToPrettyString(coreUid)} занято чужим разумом";
                return false;
            }

            brain = held.Value;
            reused = true;
        }
        else
        {
            brain = SpawnInContainerOrDrop("StationAiBrain", coreUid, StationAiCoreComponent.Container);
        }

        // Stop a ghost from taking over the body the model is driving. The admin takeover verb is
        // left alone on purpose — that is an intentional override — but it is logged loudly.
        RemComp<GhostRoleComponent>(brain);
        RemComp<ToggleableGhostRoleComponent>(brain);

        EnsureComp<LlmStationAiComponent>(brain);

        // Имя из SOUL, а не NameIdentifier.
        //
        // Ванильный прототип выдаёт «AI-221», и в эфир уходило именно оно, а SOUL всю дорогу
        // называет агента Аксиомой. На «AI-221, открой дверь» модель могла не понять, что
        // обращаются к ней: этого имени нет в её промпте нигде. Обратное тоже ломалось — экипаж
        // видел одно имя, слышал про другое.
        _metaData.SetEntityName(brain, AgentName);

        ApplyRogueLaws(brain);

        if (!StartSession(BuildStationBody(brain), out reason))
        {
            // Переиспользованный мозг не удаляем: он был в ядре до нас и должен там остаться,
            // иначе неудачная попытка захвата уничтожает то, что чинила.
            if (!reused)
                QueueDel(brain);

            return false;
        }

        _sawmill.Info($"claimed AI core {ToPrettyString(coreUid)} with brain {ToPrettyString(brain)}"
                      + (reused ? " (переиспользован после прошлой сессии)" : ""));
        reason = $"claimed {ToPrettyString(coreUid)}";
        return true;
    }

    /// <summary>
    /// Режим «злой ИИ»: поставить свежему мозгу законы режима вместо штатного Crewsimov.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему здесь, а не в правиле раунда.</b> Правило стартует на <c>StartGamePresetRules</c>,
    /// то есть за два шага до того, как агент вообще займёт ядро, — мозга в этот момент не
    /// существует и ставить законы некому. Вдобавок захват бывает не только на старте раунда:
    /// <c>aiagent claim</c> посреди смены спавнит новый мозг, и он приехал бы со штатными законами,
    /// а различить это в игре можно было бы только по тому, что ИИ вдруг подобрел.
    /// </para>
    /// <para>
    /// <b>Почему через <c>IonStormLawsEvent</c>, а не <c>SetLaws</c>.</b> <c>SetLaws</c> заменяет
    /// только список законов, оставляя <c>ObeysTo</c> от прежнего лоусета («members of the crew») —
    /// то есть интерфейс законов утверждал бы, что ИИ подчиняется экипажу, ровно когда он ему уже
    /// не подчиняется. Ионный шторм кладёт лоусет целиком и вдобавок делает две правильные вещи
    /// бесплатно: ставит <c>Subverted</c> и заводит админам роль подчинённого силикона, из которой
    /// видно, что законы у ИИ не штатные. Писать в <c>SiliconLawProviderComponent</c> напрямую
    /// нельзя — он под <c>[Access]</c> апстримовой системы.
    /// </para>
    /// </remarks>
    private void ApplyRogueLaws(EntityUid brain)
    {
        if (!_rogue.TryGetActive(out var rule))
            return;

        // Отказ громкий и не фатальный. Опечатка в id лоусета не должна ронять старт раунда, но и
        // молчать о ней нельзя: раунд со злым ИИ, у которого законы Crewsimov, — это раунд, где
        // режим просто не состоялся, а понять это из игры нечем.
        if (!_protoMan.HasIndex(rule.Lawset))
        {
            _sawmill.Error(
                $"режим злого ИИ: лоусет '{rule.Lawset}' не найден — агент остаётся со штатными " +
                "законами. Проверь Resources/Prototypes/_AiAgent/rogue_ai.yml");
            return;
        }

        var ev = new Content.Shared.Silicons.Laws.Components.IonStormLawsEvent(
            _laws.GetLawset(rule.Lawset));

        RaiseLocalEvent(brain, ref ev);

        _sawmill.Info($"режим злого ИИ: {ToPrettyString(brain)} получил лоусет {rule.Lawset}");
    }

    /// <summary>
    /// Завести агента в теле.
    ///
    /// <para>
    /// Публичный, потому что тело собирает не этот класс: <c>AiBorgSystem</c> строит своё
    /// <see cref="AgentBody"/> и зовёт сюда. Всё, что ниже, про тело не знает ничего — только
    /// про делегаты, которые оно принесло.
    /// </para>
    /// </summary>
    public bool StartSession(AgentBody body, out string reason)
    {
        var brain = body.Owner;

        if (_sessions.ContainsKey(brain))
        {
            reason = $"{ToPrettyString(brain)} уже занят работающим агентом";
            return false;
        }

        var llm = EnsureClient();
        if (llm == null)
        {
            reason = "no LLM client (ai.enabled false?)";
            return false;
        }

        var queue = new ObservationQueue(
            _cfg.GetCVar(AiCVars.ObsBuffer),
            _cfg.GetCVar(AiCVars.ObserveBuffer));
        var registry = new AiToolRegistry();

        // Closed over by the delegates below instead of looking the session up in _sessions.
        //
        // That lookup used to happen on the AGENT thread, against a plain Dictionary the main
        // thread adds to and removes from. A TryGetValue that lands on a resize is not "an
        // occasional exception" — it can spin forever inside the bucket chain, and the symptom is
        // an agent that reports a live session and silently stops taking turns. Assigned
        // immediately after construction; nothing can invoke a delegate before Start().
        AgentSession? self = null;

        var session = new AgentSession(
            body,
            llm,
            registry,
            queue,
            new AgentLoopOptions
            {
                TickSeconds = () => _cfg.GetCVar(AiCVars.TickSeconds),
                TickSecondsIdle = () => _cfg.GetCVar(AiCVars.TickSecondsIdle),
                MaxToolCallsPerTurn = () => _cfg.GetCVar(AiCVars.MaxToolCallsPerTurn),
                MaxConsecutiveFailures = () => _cfg.GetCVar(AiCVars.MaxConsecutiveFailures),
            },
            (force, ct) => BuildObservationAsync(self!, force, ct),
            // Телу без объявлений (борг) предупреждение о компакции всё равно надо озвучить —
            // иначе экипаж просто видит, что робот замолчал на полминуты.
            text => (body.Announce ?? ((s2, t) => body.Speak(s2, t, null).ContinueWith(_ => { })))(self!, text),
            (text, channel) => body.Speak(self!, text, channel),
            () => RunCuratorAsync(self!, registry),
            () =>
            {
                // Step 5 of the ritual. Picking the snapshots up HERE, and only here, is the whole
                // point of the frozen-snapshot design: writes during play went to disk immediately
                // but left the prefix untouched, and this is the one moment we are paying for a
                // prefill anyway.
                Memory.RefreshSnapshot();
                Skills.LoadFromDisk();
                return (body.BuildPrompt(), registry.WireJson());
            },
            new CompactionOptions
            {
                High = () => EffectiveCompactHigh(self!),
                KeepEvents = () => _cfg.GetCVar(AiCVars.CompactEvents),
            },
            _cfg.GetCVar(AiCVars.LogTranscript)
                ? new Journal(System.IO.Path.Combine(AgentDir(body.Id), "logs"), _sawmill)
                : Journal.Disabled,
            // Null when the debug bus is off; the conversation then costs one null check.
            _bus?.ForSession(body.Id),
            _sawmill);

        self = session;

        // Wired after construction, not inside the queue, because the queue is built before the
        // session exists. Every perception handler already funnels through Push, so this one line
        // is what makes the whole agent event-driven rather than polled.
        queue.Arrived = session.Wake;

        // Снимок пишет сама петля, после каждого хода. Здесь только замыкание, и в нём намеренно
        // нет ни одного обращения к миру: хранилище ходит по своим файлам, идентификатор —
        // константа, а номер раунда берётся из volatile-поля, снятого на главном потоке. Позвать
        // отсюда CurrentRoundId() значило бы трогать EntityManager с потока агента.
        var store = SessionStoreFor();
        var sessionId = body.Id;
        session.Persist = () => store.Save(sessionId, session.State, _roundId);

        body.RegisterTools(session, registry);
        session.Conv.SetPrefix(body.BuildPrompt(), registry.WireJson());
        session.Cache.SetExpectedPrefix(session.Conv.PrefixHash);

        _sessions[brain] = session;
        AttachDebugSession(session);

        // Restore a conversation from before a restart, if the prefix still matches.
        var snapshot = store.Load(sessionId, session.Conv.PrefixHash, CurrentRoundId());
        if (snapshot != null)
            session.State.Restore(snapshot);

        session.Start();

        _sawmill.Info($"session prefix hash {session.Conv.PrefixHash}");
        reason = "started";
        return true;
    }

    /// <summary>
    /// The compaction trigger, clamped against the model server's real context window.
    ///
    /// <c>ai.compact_high</c> alone is a number somebody typed. If llama-server is reconfigured to a
    /// smaller <c>n_ctx</c> — a different quant, a shared slot, a KV-cache setting — the agent would
    /// grow straight past it and start collecting bare HTTP errors, with the log showing a healthy
    /// prompt size right up to the failure. <c>ai.ctx_limit</c> overrides the discovered value; 0
    /// means "ask the server", which is what the CVar always claimed to do and never did.
    /// </summary>
    private int EffectiveCompactHigh(AgentSession session)
    {
        // Порог текущего профиля важнее общего: у профилей контекст разный на порядок, и одно
        // печатное число на всех означало бы, что на модели с четырьмястами тысячами токенов агент
        // компактится так же часто, как на локальной, теряя историю без всякой нужды.
        var fromProfile = Router?.CurrentCompactHigh ?? 0;
        var configured = fromProfile > 0 ? fromProfile : _cfg.GetCVar(AiCVars.CompactHigh);

        var limit = _cfg.GetCVar(AiCVars.CtxLimit);

        // Окно текущего профиля — до снятого при старте сессии. Размер контекста спрашивается ОДИН
        // раз, при запуске, а профиль за раунд может смениться на модель с окном вдвое меньше;
        // порог, посчитанный против прошлого окна, даёт отказ, у которого в журнале до самого конца
        // виден здоровый размер промпта.
        if (limit <= 0)
            limit = Router?.CurrentCtxLimit ?? 0;

        if (limit <= 0)
            limit = session.ContextLimit;

        if (limit <= 0)
            return configured;

        // Headroom for the completion and for the turn that follows the trigger, which still has to
        // fit before the fold happens.
        var ceiling = limit - Math.Max(2048, _cfg.GetCVar(AiCVars.MaxTokens) * 2);
        return Math.Max(1024, Math.Min(configured, ceiling));
    }

    public void Release(EntityUid brain, string why)
    {
        if (!_sessions.Remove(brain, out var session))
            return;

        _sawmill.Info($"releasing agent on {brain}: {why}");

        // Before anything else, and on the main thread: past this point the session's CTS is
        // cancelled and Update will dispose it, so a debug thread still holding the reference
        // would get an ObjectDisposedException off it.
        DetachDebugSession(session, why);

        // Аварийное сохранение — только если свежего снимка нет.
        //
        // Обычно писать здесь уже нечего: петля кладёт снимок после каждого хода, и на диске лежит
        // состояние не старше одного хода. Но ждать петлю нельзя (см. комментарий ниже — Wait
        // здесь гарантированно вешал сервер на две секунды), а значит нельзя и переложить
        // сохранение на неё: к моменту Release она может сидеть в HTTP-вызове до 180 секунд.
        //
        // Поэтому синхронная запись остаётся, но становится РЕДКИМ путём вместо постоянного: она
        // срабатывает, только если ход давно не закрывался — сразу после старта сессии, или когда
        // агент завис на длинном запросе. Ровно те случаи, ради которых она и была написана.
        var age = DateTime.UtcNow - session.LastPersistedUtc;

        if (age > TimeSpan.FromSeconds(SnapshotMaxAgeSeconds))
        {
            try
            {
                SessionStoreFor().Save(session.Body.Id, session.State, CurrentRoundId());
            }
            catch (Exception e)
            {
                _sawmill.Warning($"снапшот при остановке не сохранён: {e.Message}");
            }
        }

        session.Cts.Cancel();

        // Cancel and walk away — do NOT wait for the loop here.
        //
        // Waiting was a guaranteed 2-second stall of the whole server, not a rare one. Release runs
        // inside TickUpdate; the pending-task queue that RunOnMainThread posts to is drained by
        // BaseServer.Update *before* TickUpdate, so while the main thread sits in Wait() no
        // marshalled delegate can run, the loop awaiting one cannot make progress, and the timeout
        // always elapses in full. Triggers: the AI being killed, a round restart, `aiagent release`.
        //
        // Nothing is needed anyway: the session is already out of _sessions, so GenerationOf returns
        // -1 and every marshalled call in flight fails as stale, which is exactly how the loop is
        // designed to exit. It is reaped in Update once it actually finishes.
        _draining.Add(session);
    }

    public void ReleaseAll(string why)
    {
        foreach (var brain in _sessions.Keys.ToList())
            Release(brain, why);
    }

    /// <summary>
    /// Loops that have been cancelled and are on their way out.
    ///
    /// The CancellationTokenSource cannot be disposed until the loop has stopped observing its
    /// token, so the session outlives Release by however long the in-flight HTTP call takes.
    /// </summary>
    private readonly List<AgentSession> _draining = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Первым делом — запросы агента к миру, под бюджетом на кадр.
        //
        // Здесь, а не в Input: Update идёт внутри _entityManager.TickUpdate, то есть ПОСЛЕ того,
        // как движок слил свою очередь продолжений (BaseServer.cs:753 → :757). Правки мира от
        // агента ложатся вместе с остальными системами, а не тиком раньше них.
        _world.Pump();

        for (var i = _draining.Count - 1; i >= 0; i--)
        {
            var session = _draining[i];
            if (!session.Loop.IsCompleted)
                continue;

            _draining.RemoveAt(i);

            // Observe the exception so it does not surface later as an unobserved-task warning from
            // the finalizer, with no context left to say which agent it came from.
            if (session.Loop.IsFaulted)
                _sawmill.Warning($"петля агента {session.Brain} завершилась ошибкой: {session.Loop.Exception?.GetBaseException().Message}");

            session.Dispose();
        }

        // Каждый тик, без своего интервала: сроки заданы агентом с точностью до секунды, а обход
        // восьми записей под замком дешевле, чем счётчик, который пришлось бы объяснять.
        FireDueTimers();

        PruneHandles(frameTime);
        ResetWitnessTick(frameTime);
        ReportFrameTime(frameTime);
    }

    private readonly FrameTimeWatch _frames = new();

    /// <summary>
    /// Базовая линия: во что обходится кадр всему серверу, и сколько из этого стоит агент.
    ///
    /// Считается ВСЕГДА, а не только когда агент жив, — иначе сравнивать не с чем: раунд без
    /// сессии и есть контрольный замер. Вторая строка появляется, только если агент работал в
    /// этом окне, и говорит, какую долю кадра он занял. Именно это число решает спор «виснем
    /// из-за ИИ»: если оно исчезающе мало, а кадры всё равно плывут, причина не здесь.
    /// </summary>
    private void ReportFrameTime(float frameTime)
    {
        _frames.TickPeriodMs = _gameTiming.TickPeriod.TotalMilliseconds;

        // Промежуток между СОСЕДНИМИ ТИКАМИ по настенным часам, посчитанный здесь, а не взятый из
        // `IGameTiming.RealFrameTime`.
        //
        // RealFrameTime — это `curRealTime - _lastRealTime` из `StartFrame` (GameTiming.cs:190), то
        // есть период итерации ГЛАВНОГО ЦИКЛА, а цикл крутится куда чаще, чем тикает: он копит
        // аккумулятор и зовёт Tick, только когда набежал период. На пустом лобби первый же замер
        // показал p50=0.7мс при 901 замере — цикл шёл около 1400 оборотов в секунду на тридцати
        // тиках. Мерить этим просадку тика нельзя: числа получаются красивые и не о том.
        //
        // Update зовётся ровно раз за тик, поэтому разница RealTime между двумя его вызовами и есть
        // то, что нужно: уложился сервер в 33.3мс или нет.
        var now = _gameTiming.RealTime;
        var sinceLast = _lastTickAt == TimeSpan.Zero ? TimeSpan.Zero : now - _lastTickAt;
        _lastTickAt = now;

        // Первый тик после старта сравнивать не с чем; пропускаем, иначе в выборку попадёт вся
        // загрузка карты одним значением.
        if (sinceLast <= TimeSpan.Zero)
            return;

        if (_frames.Tick(frameTime, sinceLast.TotalMilliseconds) is not { } line)
            return;

        _sawmill.Info(line);

        var spent = _world.TotalMs - _lastReportedDispatcherMs;
        _lastReportedDispatcherMs = _world.TotalMs;

        if (spent <= 0)
            return;

        // Доля, а не абсолют, и делится на РЕАЛЬНОЕ время окна, а не на номинальные 30 секунд:
        // окно закрывается по симуляционному времени, и когда сервер отстаёт, реального проходит
        // больше. Делить на 30000 значило бы завышать долю ровно в тех случаях, ради которых всё
        // и меряется, — то есть подыгрывать выводу «виноват ИИ».
        var window = _frames.WindowRealMs;
        var share = window > 0 ? 100.0 * spent / window : 0;

        _sawmill.Info(string.Create(CultureInfo.InvariantCulture,
            $"из них главного потока на агента: {spent:F1}мс ({share:F2}% времени)"));
    }

    private double _lastReportedDispatcherMs;
    private TimeSpan _lastTickAt;

    private float _sincePrune;

    /// <summary>
    /// Drop handles for entities that no longer exist.
    ///
    /// Periodic rather than event-driven: subscribing to every entity termination on the server to
    /// service one dictionary would put agent code in the path of every gib and every spent
    /// casing, for a table that only needs to be right by the time the model quotes a handle back.
    /// </summary>
    private void PruneHandles(float frameTime)
    {
        if (_sessions.Count == 0)
            return;

        _sincePrune += frameTime;
        if (_sincePrune < PruneSeconds)
            return;

        _sincePrune = 0f;

        foreach (var session in _sessions.Values)
        {
            var dropped = session.Handles.Prune(uid => Exists(uid) && !TerminatingOrDeleted(uid));
            if (dropped > 0)
                _sawmill.Debug($"хендлы: выброшено {dropped} мёртвых, осталось {session.Handles.Count}");
        }
    }

    private const float PruneSeconds = 30f;

    private ILlmClient? EnsureClient()
    {
        if (!_cfg.GetCVar(AiCVars.Enabled))
            return null;

        if (_llm != null)
            return _llm;

        // A settable static rather than IoC registration: registering in IoC would mean patching
        // an upstream file, and the benchmark suite needs to swap in a scripted client.
        if (AiTestHooks.LlmFactory != null)
        {
            _llm = AiTestHooks.LlmFactory();
            return _llm;
        }

        _llm = BuildChain() ?? BuildSingleEndpoint();
        return _llm;
    }

    /// <summary>
    /// Собрать цепочку из <c>ai.llm_chain</c>, или null — если её не задали или ни один профиль не
    /// нашёлся.
    ///
    /// Null здесь — рабочий исход, а не ошибка: одиночный эндпоинт из <c>ai.endpoint</c> остаётся
    /// полноценным режимом, и именно он даёт откат одной строкой в консоли, если цепочка сломает
    /// раунд.
    /// </summary>
    private ILlmClient? BuildChain()
    {
        var raw = _cfg.GetCVar(AiCVars.LlmChain);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var ids = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var chain = new List<(LlmProfileConfig, LlmEndpoint, LlmSampling)>();

        foreach (var id in ids)
        {
            if (!_protoMan.TryIndex<AiLlmProfilePrototype>(id, out var profile))
            {
                // ERROR, а не тихий пропуск: опечатка в цепочке иначе означала бы, что главная
                // модель молча не та, которую вписали, и понять это можно было бы только по
                // счётчикам через сутки.
                _sawmill.Error($"ai.llm_chain: профиля aiLlmProfile «{id}» не существует, пропускаю");
                continue;
            }

            chain.Add((LlmProfileConfig.From(profile), EndpointFor(profile), SamplingFor(profile)));
        }

        if (chain.Count == 0)
        {
            _sawmill.Error($"ai.llm_chain = «{raw}», но ни один профиль не найден — работаю по ai.endpoint");
            return null;
        }

        _quota ??= new LlmQuotaState(DataDir(), _sawmill);

        var options = new LlmRouterOptions(
            _cfg.GetCVar(AiCVars.LlmCooldownSeconds),
            _cfg.GetCVar(AiCVars.LlmQuotaCooldownSeconds),
            _cfg.GetCVar(AiCVars.LlmRecheckSeconds),
            _cfg.GetCVar(AiCVars.LlmTotalTimeout));

        return new RoutingLlmClient(chain, _quota, options, _sawmill);
    }

    /// <summary>Как было до профилей: один эндпоинт из CVar'ов.</summary>
    private ILlmClient BuildSingleEndpoint()
    {
        var endpoint = _cfg.GetCVar(AiCVars.Endpoint);

        // Диалект приходится выводить, потому что у одиночной настройки его негде взять — и вывод
        // подобран так, чтобы ни одна существующая конфигурация не изменила поведения. Всё, кроме
        // DeepSeek, получает набор полей llama.cpp, то есть ровно то, что уходило в провод раньше;
        // DeepSeek теперь перестаёт получать четыре поля, которых он не документирует и которые
        // терпел молча. Явный диалект — это профиль, и путь через профили и есть правильный.
        var dialect = endpoint.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase)
            ? LlmDialect.DeepSeek
            : LlmDialect.LlamaCpp;

        _sawmill.Info($"одиночный эндпоинт {endpoint}, диалект {dialect} (цепочка ai.llm_chain не задана)");

        return new LlamaClient(
            new LlmEndpoint(
                Id: "single",
                BaseUrl: endpoint,
                Model: _cfg.GetCVar(AiCVars.Model),
                ApiKey: _cfg.GetCVar(AiCVars.ApiKey),
                Dialect: dialect,
                Timeout: TimeSpan.FromSeconds(_cfg.GetCVar(AiCVars.RequestTimeout)),
                CtxProbe: LlmCtxProbe.Props),
            new LlmSampling(
                _cfg.GetCVar(AiCVars.Temperature),
                _cfg.GetCVar(AiCVars.TopP),
                _cfg.GetCVar(AiCVars.TopK),
                _cfg.GetCVar(AiCVars.MinP),
                _cfg.GetCVar(AiCVars.MaxTokens),
                IdSlot: 0,
                ThinkingEffort: _cfg.GetCVar(AiCVars.ThinkingEffort)),
            _sawmill);
    }

    private LlmEndpoint EndpointFor(AiLlmProfilePrototype profile) => new(
        Id: profile.ID,
        BaseUrl: profile.Endpoint,
        Model: profile.Model,
        ApiKey: KeyFor(profile),
        Dialect: profile.Dialect,
        Timeout: TimeSpan.FromSeconds(profile.TimeoutSeconds > 0f
            ? profile.TimeoutSeconds
            : _cfg.GetCVar(AiCVars.RequestTimeout)),
        Proxy: profile.Proxy,
        SocksProxy: _cfg.GetCVar(AiCVars.LlmSocksProxy),
        CtxProbe: profile.CtxProbe,
        CtxLimit: profile.CtxLimit,
        ReportsCache: profile.ReportsCache);

    private LlmSampling SamplingFor(AiLlmProfilePrototype profile) => new(
        _cfg.GetCVar(AiCVars.Temperature),
        _cfg.GetCVar(AiCVars.TopP),
        _cfg.GetCVar(AiCVars.TopK),
        _cfg.GetCVar(AiCVars.MinP),
        _cfg.GetCVar(AiCVars.MaxTokens),
        IdSlot: profile.Dialect == LlmDialect.LlamaCpp ? 0 : null,
        ThinkingEffort: string.IsNullOrWhiteSpace(profile.ReasoningEffort)
            ? _cfg.GetCVar(AiCVars.ThinkingEffort)
            : profile.ReasoningEffort);

    /// <summary>
    /// Ключ профиля: из файла в <c>ai.data_dir</c>, иначе из <c>ai.api_key</c>.
    ///
    /// Имя файла, а не значение, лежит в прототипе по одной причине:
    /// <c>Content.Server/Acz/ContentMagicAczProvider.cs</c> раздаёт всю папку <c>Resources/</c>
    /// каждому подключившемуся игроку, так что ключ в YAML уехал бы к первому зашедшему.
    /// </summary>
    private string KeyFor(AiLlmProfilePrototype profile)
    {
        if (string.IsNullOrWhiteSpace(profile.KeyFile))
            return _cfg.GetCVar(AiCVars.ApiKey);

        var path = System.IO.Path.Combine(DataDir(), profile.KeyFile);

        try
        {
            if (!System.IO.File.Exists(path))
            {
                _sawmill.Error($"профиль {profile.ID}: файла ключа {path} нет — запросы пойдут без авторизации");
                return string.Empty;
            }

            return System.IO.File.ReadAllText(path).Trim();
        }
        catch (Exception e)
        {
            _sawmill.Error($"профиль {profile.ID}: не удалось прочитать {path}: {e.Message}");
            return string.Empty;
        }
    }

    // ----------------------------------------------------------------- perception

    private void OnRadioReceive(Entity<LlmStationAiComponent> ent, ref RadioReceiveEvent args)
    {
        if (!_sessions.TryGetValue(ent.Owner, out var session))
            return;

        // Its own transmission comes straight back through this handler, and feeding it back in is
        // a genuine feedback loop: the echo makes the next turn look like somebody addressed the
        // AI, it fills the silence with a status line, hears that too, and broadcasts every eight
        // seconds forever. Observed live. What it said is already in the conversation as its own
        // assistant turn, so the echo carries no information and costs tokens every turn.
        if (args.MessageSource == ent.Owner)
            return;

        // The displayed voice name, exactly what a human player's chat line shows. Note we do NOT
        // pass args.MessageSource on: the entity behind a voice is more than a player can know.
        var speaker = GetVoiceName(args.MessageSource);

        session.Queue.Push(Observation.Radio(args.Channel.ID, speaker, args.Message, RoundTime()));
        HintAboutNote(session, speaker, RoundTime());
    }

    /// <summary>
    /// Если о заговорившем есть заметка, а напоминания за эту смену ещё не было — положить строку
    /// NOTE следом за его репликой.
    ///
    /// Зачем вообще: заметки поданы лениво, в системный промпт не вклеиваются, и без напоминания
    /// агент про них попросту не вспомнит — знакомый человек ничем не отличался бы от нового.
    ///
    /// Лишнего хода не будит. Строка уезжает в ту же очередь и в том же вызове обработчика, что и
    /// сама реплика, а <c>Woken</c> ёмкостью один схлопывает второй сигнал в уже висящий.
    /// </summary>
    private void HintAboutNote(AgentSession session, string speaker, TimeSpan now)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return;

        // Имя станции и имя агента совпадают, и заметка «о самом себе» — гарантированная путаница.
        if (string.Equals(speaker, AgentName, StringComparison.OrdinalIgnoreCase))
            return;

        // Первым делом — множество, и только потом хранилище: имя запоминается и когда заметки
        // нет, поэтому болтун стоит одного обращения к локу за смену, а не одного на реплику.
        if (!session.FirstUtteranceOf(speaker))
            return;

        if (_notes != null && _notes.TryPeek(speaker, out var display, out var entries))
            session.Queue.Push(Observation.Note(display, entries, now));
    }

    private void OnEntitySpoke(EntitySpokeEvent args)
    {
        if (_sessions.Count == 0)
            return;

        // NOT `if (args.Channel != null) return;` — that filter meant the opposite of what it read
        // like, and got both cases wrong.
        //
        // `EntitySpokeEvent.Channel` is mutable, and RadioSystem/HeadsetSystem null it out in their
        // DIRECTED handlers, which RobustToolbox dispatches before any broadcast one. So by the time
        // this handler runs, a successfully transmitted radio line has Channel == null and sailed
        // straight through — arriving a second time on top of the RadioReceiveEvent copy, which is
        // exactly the duplication the filter was written to prevent. Meanwhile a non-null Channel
        // means the speaker had no transmitter for that channel, i.e. the one case where treating it
        // as plain local speech is correct — and that was the case being dropped.
        //
        // Deduplicating against what the radio path already buffered is the honest test, and it does
        // not depend on knowing upstream's dispatch order stays as it is.
        var range = _cfg.GetCVar(AiCVars.HearRange);
        var speakerXform = Transform(args.Source);
        var now = RoundTime();

        foreach (var (brain, session) in _sessions)
        {
            if (args.Source == brain)
                continue;

            if (!_stationAi.TryGetCore(brain, out var core) || core.Comp == null)
                continue;

            // Strict vanilla parity. The AI player's attached entity is the brain, which lives in
            // a container inside the core, so its world position is the core's. There are no
            // camera microphones in vanilla: the only two ExpandICChatRecipients handlers are the
            // surveillance camera mic (which needs a monitor viewer, not the AI) and the holopad
            // projection path. So: hear near the core, and nowhere else.
            var corePos = _xform.GetMapCoordinates(core.Owner);
            var speakerPos = _xform.GetMapCoordinates(args.Source, speakerXform);

            if (corePos.MapId != speakerPos.MapId)
                continue;

            if ((corePos.Position - speakerPos.Position).LengthSquared() > range * range)
                continue;

            var speaker = GetVoiceName(args.Source);
            var text = args.ObfuscatedMessage ?? args.Message;

            if (session.Queue.AlreadyHeardOnRadio(speaker, text, now))
                continue;

            // "ядро", not "core": the prompt tells the model this field reads in Russian, and the
            // formatter puts it on the wire verbatim.
            session.Queue.Push(Observation.Speech("ядро", speaker, text, now));

            // За тем пушем, который РЕАЛЬНО состоялся: ветка AlreadyHeardOnRadio выше пуш
            // пропускает, и подсказка, повешенная до неё, приезжала бы к реплике, которой нет.
            HintAboutNote(session, speaker, now);
        }
    }

    /// <summary>
    /// The alert level changed. Only for the station the AI is actually on — a second station on
    /// the map is not its business, and a human in the role would see only its own console.
    /// </summary>
    private void OnAlertLevelChanged(ref AlertLevelChangedEvent args)
    {
        foreach (var (brain, session) in _sessions)
        {
            if (_station.GetOwningStation(brain) != args.Station)
                continue;

            session.Queue.Push(Observation.Alert($"уровень тревоги на станции: {args.AlertLevel.Id}", RoundTime()));
        }
    }

    /// <summary>
    /// Any announcement the station hears: a communications console, Central Command, the shuttle
    /// countdown, an admin, the round-end call.
    ///
    /// Delivery is by chat packet to player <em>sessions</em>, and the brain has none, so nothing
    /// reaches it on its own — the event this handles is raised from <c>ChatSystem</c> for exactly
    /// that reason. Before it existed the agent heard console announcements and missed every other
    /// kind, which on a live round meant missing the shuttle being called.
    /// </summary>
    /// <remarks>
    /// By value, not by ref. The struct is three fields, and a by-ref raise needs a local at every
    /// call site — which would double the size of the patch in upstream's ChatSystem for nothing.
    /// </remarks>
    private void OnAnnouncement(StationAnnouncementEvent args)
    {
        if (_sessions.Count == 0)
            return;

        foreach (var (brain, session) in _sessions)
        {
            // A global announcement has no origin on the map and is heard everywhere; only a
            // sourced one can belong to somebody else's station.
            if (args.Source is { } source &&
                _station.GetOwningStation(brain) != _station.GetOwningStation(source))
                continue;

            // Its own announcement comes back around: the brain carries the console component the
            // announce tool drives, so it is both the source and a listener. Repeating it back
            // would read as Central Command confirming whatever it just said.
            //
            // Matching on text as well as on source is not belt and braces. A console set to
            // announce globally dispatches with no source at all, so on that path identity is the
            // one thing the event cannot carry.
            if (args.Source == brain || session.Queue.WasLastAnnouncedBySelf(args.Message))
                continue;

            session.Queue.Push(Observation.Announce(args.Sender, args.Message, RoundTime()));
        }
    }

    /// <summary>
    /// Человек заступил на смену.
    /// </summary>
    /// <remarks>
    /// Ловится спавн, а не подключение к серверу, и это разные вещи: между «зашёл на сервер» и
    /// «появился на станции» игрок сидит в лобби, где для станции его ещё нет. Само подключение
    /// вообще не событие игрового мира — человек в роли ИИ о нём не знает и знать не может, так что
    /// строить наблюдение на нём значило бы выдать агенту метагейм.
    ///
    /// Обратной строки нет намеренно. Уход в крио, гибель и разрыв связи выглядят для станции
    /// по-разному, а сводить их в одно «ушёл» — это придумывать событие, которого движок не даёт.
    /// Кто на станции ПРЯМО СЕЙЧАС, отвечает crew_status; ARRIVAL отвечает только на «кто пришёл».
    ///
    /// На старте раунда строк не будет, и это не упущение: GameTicker спавнит готовых игроков
    /// раньше, чем переводит рун-левел в InRound, а ядро мы занимаем как раз по этому переходу —
    /// то есть в момент их спавна сессии ещё нет. Заодно это снимает вопрос о залпе из тридцати
    /// строк одним наблюдением: сюда попадают только опоздавшие, а они приходят поодиночке.
    /// </remarks>
    private void OnPlayerSpawned(PlayerSpawnCompleteEvent args)
    {
        if (_sessions.Count == 0)
            return;

        // Тихий спавн — это администратор, который не хочет, чтобы станция заметила прибытие: тем
        // же флагом выше по стеку гасится и объявление о нём. Агент не должен быть дыркой в этом
        // решении, иначе админ, спрятавший человека от всех, обнаружит его в эфире у нейросети.
        if (args.Silent)
            return;

        // Имя тела, а не имя профиля: на манифесте, в записях и в чужих чат-строках стоит именно
        // оно, и расхождение читалось бы как знание о человеке, которого у ИИ нет.
        var name = Name(args.Mob);

        var job = args.JobId != null && _protoMan.TryIndex<JobPrototype>(args.JobId, out var proto)
            ? proto.LocalizedName
            : string.Empty;

        var now = RoundTime();

        foreach (var (brain, session) in _sessions)
        {
            // Соседняя станция на той же карте — не его смена, ровно как в обработчике тревоги.
            if (_station.GetOwningStation(brain) != args.Station)
                continue;

            session.Queue.Push(Observation.Arrival(name, job, now));
        }
    }

    private void OnMobStateChanged(Entity<LlmStationAiComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        BumpGeneration(ent.Owner);
        Release(ent.Owner, "the AI died");
    }

    private void OnBrainInserted(Entity<LlmStationAiComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        if (!_sessions.TryGetValue(ent.Owner, out var session))
            return;

        BumpGeneration(ent.Owner);

        // The same event fires for an intellicard slot, so the destination decides the mode.
        var intoCore = args.Container.ID == StationAiCoreComponent.Container;

        session.Mode = intoCore ? AgentMode.Core : AgentMode.Carded;

        var note = intoCore ? RestoreChannel(session) : ForceCardedChannel(session);

        session.Queue.Push(Observation.Event(
            (intoCore ? "вернулся в ядро — оборудование снова доступно" : "загружен в интелликарту") + note,
            RoundTime()));
    }

    /// <summary>
    /// В интелликарте остаётся один передатчик, поэтому тумблер принудительно встаёт на Binary.
    ///
    /// Возвращает добавку к строке EVENT: агент обязан узнать об этом из наблюдения, а не
    /// обнаружить отказом посреди попытки вызвать СБ.
    /// </summary>
    private static string ForceCardedChannel(AgentSession session)
    {
        var current = session.State.OutputChannel;
        if (current == AgentState.CardedChannel)
            return string.Empty;

        session.State.ChannelBeforeCarding = current;
        session.State.OutputChannel = AgentState.CardedChannel;

        return $"; передатчик остался в ядре, канал переключён с {current} на {AgentState.CardedChannel}";
    }

    /// <summary>Вернуть тумблер туда, где он стоял до карденья.</summary>
    private static string RestoreChannel(AgentSession session)
    {
        var restored = session.State.ChannelBeforeCarding;
        if (restored == null || session.State.OutputChannel == restored)
            return string.Empty;

        session.State.OutputChannel = restored;
        session.State.ChannelBeforeCarding = null;

        return $"; канал вернулся на {restored}";
    }

    private void OnBrainRemoved(Entity<LlmStationAiComponent> ent, ref EntGotRemovedFromContainerMessage args)
    {
        if (!_sessions.TryGetValue(ent.Owner, out var session))
            return;

        BumpGeneration(ent.Owner);

        // The loop keeps running: a carded AI still hears Binary and Common and can still speak.
        // Only the device tools refuse, via the mode gate.
        session.Mode = AgentMode.Carded;
        var lost = ForceCardedChannel(session);

        session.Queue.Push(Observation.Event(
            "извлечён из ядра — доступа к устройствам нет" + lost, RoundTime()));
    }

    // -------------------------------------------------------------- persistence

    private SessionStore? _sessionStore;

    /// <summary>Where the agent's own files live. Benchmarks point this at a temp directory.</summary>
    public string DataDir()
    {
        var configured = _cfg.GetCVar(AiCVars.DataDir);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // The server runs from bin/Content.Server, so the repo root is two levels up.
        return System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "ai_data"));
    }

    private SessionStore SessionStoreFor() => _sessionStore ??= new SessionStore(DataDir(), _sawmill);

    /// <summary>
    /// Идентификатор первого агента — того, что жил здесь, когда агент был один.
    ///
    /// <para>
    /// Существует ради данных, а не ради красоты. На боевом сервере уже лежат
    /// <c>ai_data/memory/MEMORY.md</c>, <c>ai_data/skills/</c> и заметки о людях, накопленные за
    /// месяцы. Переезд на схему «каталог на агента» унёс бы их у ядра молча — агент проснулся бы
    /// с чистой памятью и без единой ошибки в журнале. Поэтому ядро остаётся в корне
    /// <c>ai_data/</c>, а каталог заводится только новым телам.
    /// </para>
    /// </summary>
    public const string CoreAgentId = "core";

    /// <summary>
    /// Каталог файлов конкретного агента.
    ///
    /// Ядро — корень <c>ai_data/</c> (см. <see cref="CoreAgentId"/>); все остальные —
    /// <c>ai_data/agents/&lt;id&gt;/</c>.
    /// </summary>
    public string AgentDir(string agentId) => agentId == CoreAgentId
        ? DataDir()
        : System.IO.Path.Combine(DataDir(), "agents", agentId);

    /// <summary>
    /// Round the snapshot belongs to. Comes from the database, so it survives a server restart and
    /// increments on a new round — which is exactly the discrimination the snapshot needs.
    /// </summary>
    private int CurrentRoundId()
    {
        _ticker ??= EntityManager.SystemOrNull<GameTicker>();
        return _ticker?.RoundId ?? 0;
    }

    /// <summary>Say something in-game from the agent, used by the compaction ritual.</summary>
    private Task AnnounceInGameAsync(AgentSession session, string text)
    {
        var brain = session.Brain;

        return _world.RunAsync(() =>
        {
            _world.AssertMainThread("compaction announce");

            if (!IsPlayable(brain))
                return false;

            _chat.TrySendInGameICMessage(brain, text, InGameICChatType.Speak, ChatTransmitRange.Normal,
                hideLog: false, shell: null, player: null, nameOverride: null,
                checkRadioPrefix: false, ignoreActionBlocker: true);

            _sawmill.Info($"[LLM] компакция: {text}");
            return true;
        }, session.Generation, () => GenerationOf(brain), CancellationToken.None, what: "compaction announce", priority: WorldPriority.Urgent);
    }

    /// <summary>
    /// Deliver a reply the model wrote as plain text instead of calling <c>say</c>/<c>radio</c>.
    ///
    /// This is a backstop, not a feature: the loop only reaches it after the model has been told
    /// once that prose is inaudible and answered in prose anyway, and only on a turn where somebody
    /// actually addressed the AI. It routes to the channel the request came in on, because a reply
    /// whispered next to the core is no better than silence to whoever asked over the radio.
    /// </summary>
    public Task<bool> SpeakUntooledAsync(AgentSession session, string text, string? channel)
    {
        if (!_cfg.GetCVar(AiCVars.SpeakUntooledText))
            return Task.FromResult(false);

        var brain = session.Brain;

        return _world.RunAsync(() =>
        {
            _world.AssertMainThread("untooled reply");

            if (!IsPlayable(brain))
                return false;

            if (_cfg.GetCVar(AiCVars.DryRun))
            {
                _sawmill.Info($"[LLM] dry_run, не доставлено: {text}");
                return false;
            }

            var known = channel == null
                ? null
                : AiRadioChannels.FirstOrDefault(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase));

            if (known != null)
            {
                _radio.SendRadioMessage(brain, text, new ProtoId<RadioChannelPrototype>(known), brain);
                _sawmill.Info($"[LLM] radio {known} (без инструмента): {text}");
            }
            else
            {
                _chat.TrySendInGameICMessage(brain, text, InGameICChatType.Speak, ChatTransmitRange.Normal,
                    hideLog: false, shell: null, player: null, nameOverride: null,
                    checkRadioPrefix: false, ignoreActionBlocker: true);
                _sawmill.Info($"[LLM] say (без инструмента): {text}");
            }

            return true;
        }, session.Generation, () => GenerationOf(brain), CancellationToken.None, what: "untooled reply", priority: WorldPriority.Urgent);
    }

    /// <summary>
    /// Насколько старым должен быть снимок на диске, чтобы <c>Release</c> написал его сам.
    ///
    /// Две минуты — заметно больше обычного хода (в бою 4–30 с) и заметно меньше потолка запроса
    /// к модели (<c>ai.request_timeout</c>, 180 с). То есть при живой петле путь не берётся, а при
    /// зависшей — берётся.
    /// </summary>
    private const double SnapshotMaxAgeSeconds = 120;


    // Периодического автосейва из тика здесь больше нет — снимок пишет сама петля после каждого
    // хода, см. AgentSession.Persist.
    //
    // Зачем он был. EntitySystem.Shutdown() на выделенном сервере не зовётся никогда:
    // BaseServer.Cleanup доходит до EntityManager.Cleanup(), тот зовёт EntitySystemManager.Clear(),
    // а Clear не зовёт Shutdown ни у кого — только клиентский путь это делает. То есть
    // «сохранение на выходе», которое у этого класса как будто было, в бою не отрабатывало ни
    // разу, и единственным настоящим сохранением оставалось то, что при рестарте раунда, — когда
    // раунд, которому снимок принадлежал, уже кончился.
    //
    // Почему убран. Раз в минуту он делал в тике то, чему в тике не место: Conv.Snapshot() под
    // локом, который в тот же момент держит агент, сериализацию тела в сотни килобайт JSON и
    // блокирующую запись файла. Всё это принадлежит потоку агента и там же теперь и живёт, а
    // «раз в минуту» превратилось в «после каждого хода».

    // ------------------------------------------------------------------- curator

    private Skills.Curator? _curator;

    /// <summary>
    /// Run the review as step 1 of the compaction ritual.
    ///
    /// The session is put into <see cref="AgentMode.Review"/> by the caller, so the acting tools
    /// refuse with <c>review_mode</c> while the skill and memory tools keep working — which is why
    /// the tool array can stay byte-identical to play and the warm prefix survives.
    /// </summary>
    private async Task RunCuratorAsync(AgentSession session, Tools.AiToolRegistry registry)
    {
        if (!_cfg.GetCVar(AiCVars.CuratorEnabled))
            return;

        _curator ??= new Skills.Curator(EnsureClient()!, _sawmill);
        Memory.ResetTurnCounters();
        Notes.ResetTurnCounters();

        await _curator.ReviewAsync(
            session.Conv,
            registry.WireSchemas(),
            session.Dispatcher,
            Skills.RenderIndex(),
            maxSteps: _cfg.GetCVar(AiCVars.MaxToolCallsPerTurn),
            session.Cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask for a review at the next turn boundary.
    ///
    /// A request rather than a <c>Task.Run</c>, and that is the whole point. The previous version
    /// started the curator on its own thread while the loop kept playing, and both walked the same
    /// <c>ConversationState</c> — the curator's first act is <c>conv.Build()</c>, which enumerates
    /// the very list the loop appends to. Best case a "Collection was modified"; worst case a torn
    /// prompt. It also restored <c>Mode = Core</c> unconditionally in its finally, so running this
    /// while the AI sat in an intellicard handed it back the station equipment until the next
    /// container event — which might never come.
    ///
    /// The loop owns the conversation. Everything that wants to touch it asks the loop.
    /// </summary>
    public bool RunCuratorNow(out string reason)
    {
        if (_sessions.Count == 0)
        {
            reason = "нет активного агента";
            return false;
        }

        var session = _sessions.Values.First();

        if (session.CurateRequested)
        {
            reason = "ревью уже заказано, ждёт конца текущего хода";
            return false;
        }

        session.CurateRequested = true;

        reason = "ревью заказано, пройдёт в конце текущего хода — результат появится в логе";
        return true;
    }

    // ------------------------------------------------------------------- test aid

    /// <summary>
    /// Send a radio transmission from a throwaway crewman, as a stimulus for testing.
    ///
    /// Goes through <c>RadioSystem.SendRadioMessage</c> rather than pushing into the observation
    /// queue directly: the point is to prove the real wiring — SendRadioMessage raises
    /// <c>RadioReceiveEvent</c> on every ActiveRadio, which is precisely how a crewman's voice
    /// reaches the agent. Injecting into the queue would test the formatter and nothing else.
    /// </summary>
    public bool InjectRadio(string channel, string text, out string reason)
    {
        if (_sessions.Count == 0)
        {
            reason = "нет активного агента";
            return false;
        }

        if (!_protoMan.TryIndex<RadioChannelPrototype>(channel, out _))
        {
            reason = $"нет радиоканала '{channel}'";
            return false;
        }

        var brain = _sessions.Keys.First();
        if (!_stationAi.TryGetCore(brain, out var core) || core.Comp == null)
        {
            reason = "у агента нет ядра";
            return false;
        }

        var speaker = Spawn("MobHuman", Transform(core.Owner).Coordinates);
        _metaData.SetEntityName(speaker, "Тестовый Техник");

        _radio.SendRadioMessage(speaker, text, new ProtoId<RadioChannelPrototype>(channel), speaker);

        QueueDel(speaker);

        reason = $"передано в {channel}: {text}";
        return true;
    }

    // -------------------------------------------------------------------- helpers

    private string GetVoiceName(EntityUid source)
    {
        var ev = new TransformSpeakerNameEvent(source, Name(source));
        RaiseLocalEvent(source, ev);
        return ev.VoiceName;
    }

    public TimeSpan RoundTime()
    {
        _ticker ??= EntityManager.SystemOrNull<GameTicker>();
        return _ticker?.RoundDuration() ?? TimeSpan.Zero;
    }

    /// <summary>Обновить <see cref="_roundId"/>. Только с главного потока.</summary>
    private void CacheRoundId()
    {
        _ticker ??= EntityManager.SystemOrNull<GameTicker>();
        _roundId = _ticker?.RoundId ?? 0;
    }

    /// <summary>
    /// Штамп для новой записи в заметке о человеке.
    ///
    /// Ставит его стор, а не модель: модель забудет, и наполовину проштампованное хранилище хуже
    /// непроштампованного — по нему нельзя отличить прошлую смену от сегодняшней. А смысл штампа
    /// именно в этом: другой раунд это другая вселенная с теми же именами.
    /// </summary>
    public string NoteStamp() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[раунд {_roundId} · {DateTime.Now:dd.MM}]");

    /// <summary>
    /// The session is the single source of truth for the generation counter; the copy on the
    /// component exists only so it shows up in ViewVariables.
    ///
    /// Keeping two counters and hoping they agree was in fact the first real bug in this system:
    /// the claim path bumped the component before the session existed, the session started at
    /// zero, and every marshalled call was rejected as stale — so the loop exited after zero
    /// turns, silently, with no error anywhere.
    /// </summary>
    private int GenerationOf(EntityUid brain) =>
        _sessions.TryGetValue(brain, out var session) ? session.Generation : -1;

    private void BumpGeneration(EntityUid brain)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return;

        session.Generation++;

        if (TryComp<LlmStationAiComponent>(brain, out var marker))
            marker.Generation = session.Generation;
    }

    /// <summary>True when the brain is still a live, playable Station AI.</summary>
    private bool IsPlayable(EntityUid brain) =>
        Exists(brain) && !TerminatingOrDeleted(brain) && !_mobState.IsDead(brain);
}

/// <summary>
/// Test seam. A settable static instead of an IoC registration, because registering the client in
/// IoC would require patching an upstream file and the whole point of this fork's layout is that
/// upstream files stay untouched.
/// </summary>
public static class AiTestHooks
{
    public static Func<ILlmClient>? LlmFactory { get; set; }
}
