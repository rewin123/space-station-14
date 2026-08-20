using Robust.Shared.Prototypes;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Station.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Preferences;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Station.Components;
using Content.Shared.Turrets;
using Content.Shared.TurretController;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server.AiAgent.RogueAi;

/// <summary>
/// Режим «злой ИИ»: скрытый и открытый — <c>RogueAiHiddenRule</c> и <c>RogueAiOpenRule</c>.
///
/// <para>
/// <b>Что режим меняет.</b> Три вещи, и все три — на старте раунда: агент получает свой лоусет и
/// свой файл личности вместо штатных, станция раздаёт ему доступ к оборудованию, которого он в
/// обычной смене не касается, а в открытом варианте вдобавок весь экипаж заступает ассистентами.
/// </para>
/// <para>
/// <b>Это сознательное нарушение паритета с живым игроком</b> — того самого правила, на котором
/// стоит весь остальной модуль. Смысл режима ровно в нарушении, и живёт оно только внутри режима:
/// компоненты доступа навешиваются на сущности раунда, а те исчезают вместе с картой при
/// рестарте. Ничего чистить не нужно и нечем — <see cref="ActiveRule"/> обнуляется, и следующий
/// раунд начинается с обычного ИИ.
/// </para>
/// <para>
/// <b>Порядок старта раунда, на который всё опирается</b> (<c>GameTicker.RoundFlow.cs</c>):
/// <c>LoadMaps</c> → <c>StartGamePresetRules</c> (здесь срабатывает <see cref="Started"/>) →
/// <c>SpawnPlayers</c> (внутри: <c>RulePlayerSpawningEvent</c> → раздача должностей →
/// <c>RulePlayerJobsAssignedEvent</c>) → <c>RunLevel = InRound</c>, и только на последнем шаге
/// агент занимает ядро. То есть и должности, и доступ успевают лечь до того, как соберётся
/// замороженный системный промпт, а законы ставятся уже по факту существования мозга — в
/// <c>StationAiAgentSystem.TryClaimAnyCore</c>, см. <see cref="Lawset"/>.
/// </para>
/// </summary>
public sealed partial class RogueAiRuleSystem : GameRuleSystem<RogueAiRuleComponent>
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private StationJobsSystem _stationJobs = default!;
    [Dependency] private Borg.AiBorgSystem _borgs = default!;

    private ISawmill _sawmill = default!;

    /// <summary>
    /// Активное правило режима, либо null — обычная смена.
    ///
    /// Единственный источник истины «мы в режиме злого ИИ»; его спрашивают и промпт, и захват
    /// ядра. Держится полем, а не поиском по сущностям правил, потому что спрашивают его из
    /// горячих мест и на каждом захвате.
    /// </summary>
    public RogueAiRuleComponent? ActiveRule { get; private set; }

    public bool TryGetActive([NotNullWhen(true)] out RogueAiRuleComponent? rule)
    {
        rule = ActiveRule;
        return rule != null;
    }

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai.rogue");

        // Момент перед раздачей должностей: список должностей ещё можно переписать.
        SubscribeLocalEvent<RulePlayerSpawningEvent>(OnPlayerSpawning);

        // Момент, когда игроки уже стоят на станции. Образец — BackupPowerSystem.
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnJobsAssigned);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
    }

    /// <summary>Станции, которым доступ на этот раунд уже роздан.</summary>
    private readonly HashSet<EntityUid> _served = new();

    protected override void Started(EntityUid uid,
        RogueAiRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        ActiveRule = component;

        _sawmill.Info(
            $"режим злого ИИ включён: законы {component.Lawset}, личность {component.SoulFile}, " +
            $"экипаж ассистентами: {component.AllJobsPassenger}");
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent ev)
    {
        ActiveRule = null;
        _served.Clear();
    }

    // ------------------------------------------------------------------ должности

    private void OnPlayerSpawning(RulePlayerSpawningEvent ev)
    {
        if (ActiveRule is not { AllJobsPassenger: true })
            return;

        foreach (var station in _station.GetStations())
            ForcePassengerJobs(station);

        ForceOverflowPreference(ev);
    }

    /// <summary>
    /// Закрыть на станции все должности, кроме overflow (то есть кроме ассистента).
    /// </summary>
    /// <remarks>
    /// Это же само собой закрывает и поздние заходы: список доступных должностей у лейтджоинера —
    /// тот самый <c>JobList</c>, который мы здесь обнуляем.
    ///
    /// <para>
    /// Публичный ради теста, по той же причине, что <c>BackupPowerSystem.EngineeringOnDuty</c>:
    /// поднять <c>RulePlayerJobsAssignedEvent</c> руками нельзя — на него подписан
    /// <c>AntagSelectionSystem</c> и вне последовательности старта раунда он падает сам.
    /// </para>
    /// </remarks>
    public void ForcePassengerJobs(EntityUid station)
    {
        if (!HasComp<Content.Server.Station.Components.StationJobsComponent>(station))
            return;

        var overflow = _stationJobs.GetOverflowJobs(station);
        var closed = 0;

        // ToList обязателен: TrySetJobSlot пишет в тот же словарь, который отдаёт GetJobs.
        foreach (var job in _stationJobs.GetJobs(station).Keys.ToList())
        {
            if (overflow.Contains(job))
                continue;

            if (_stationJobs.TrySetJobSlot(station, job, 0))
                closed++;
        }

        // Ноль здесь значит, что экипаж заступит как обычно, — то есть режим наполовину не
        // состоялся. В игре это выглядит как «капитан почему-то капитан», и объяснить это можно
        // только отсюда.
        _sawmill.Info(
            $"должности закрыты на {ToPrettyString(station)}: {closed}, оставлено overflow: " +
            $"{string.Join(", ", overflow.Select(j => j.Id))}");
    }

    /// <summary>
    /// Выдать overflow тем, кто просил «оставить в лобби, если должность занята».
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем.</b> При закрытых должностях <c>StationJobsSystem.AssignOverflowJobs</c> даёт
    /// ассистента только игрокам с <see cref="PreferenceUnavailableMode.SpawnAsOverflow"/>;
    /// остальные не спавнятся вовсе и получают в чат «job-not-available-wait-in-lobby». На игровом
    /// вечере это выглядит как «меня не пустило на сервер», причём избирательно и без объяснения.
    /// </para>
    /// <para>
    /// <b>Почему это безопасно для профилей.</b> Словарь, который событие отдаёт как
    /// <c>IReadOnlyDictionary</c>, — тот же объект, который <c>SpawnPlayers</c> следом передаёт в
    /// <c>AssignJobs</c> (<c>GameTicker.Spawning.cs</c>), и живёт он ровно один раунд.
    /// <c>WithPreferenceUnavailable</c> возвращает копию профиля, а не правит исходный, так что до
    /// базы предпочтений это не доходит никак.
    /// </para>
    /// <para>
    /// Приведение типа <b>guarded</b> намеренно: разойдись апстрим с этим предположением, мы
    /// получим предупреждение в журнале и обычный путь, а не исключение посреди старта раунда.
    /// Снимается целиком через <c>ai.rogue_force_overflow</c>.
    /// </para>
    /// </remarks>
    private void ForceOverflowPreference(RulePlayerSpawningEvent ev)
    {
        if (!_cfg.GetCVar(AiCVars.RogueForceOverflow))
            return;

        if (ev.Profiles is not Dictionary<NetUserId, HumanoidCharacterProfile> profiles)
        {
            _sawmill.Warning(
                "профили игроков пришли не словарём — часть экипажа с настройкой «остаться в " +
                "лобби» не заспавнится. Проверь RulePlayerSpawningEvent в апстриме");
            return;
        }

        var changed = 0;

        foreach (var (userId, profile) in profiles.ToList())
        {
            if (profile.PreferenceUnavailable == PreferenceUnavailableMode.SpawnAsOverflow)
                continue;

            profiles[userId] = profile.WithPreferenceUnavailable(PreferenceUnavailableMode.SpawnAsOverflow);
            changed++;
        }

        if (changed > 0)
            _sawmill.Info($"выдан ассистент вместо лобби: {changed} игрок(ов)");
    }

    // -------------------------------------------------------------------- доступ

    private void OnJobsAssigned(RulePlayerJobsAssignedEvent ev)
    {
        if (ActiveRule is not { } rule)
            return;

        foreach (var station in _station.GetStations())
        {
            if (!_served.Add(station))
                continue;

            GrantAccess(station, rule);
        }

        SpawnSupportBorgs(rule);

        if (rule is { AnnounceOnStart: true, Announcement: { } announcement })
        {
            var text = Loc.GetString(announcement);
            _chat.DispatchGlobalAnnouncement(text, playSound: true);

            // Объявление уходит в чат, а чат не пишет ни строчки в журнал. Без этого «объявили ли
            // вообще» проверяется только глазами игрока, которого на стенде нет. Первые слова, а
            // не весь текст: он на пять строк.
            _sawmill.Info($"объявление режима отправлено: «{Truncate(text, 60)}»");
        }
    }

    /// <summary>
    /// Раздать ИИ доступ к оборудованию станции: навесить <c>StationAiWhitelist</c> тому, у чего
    /// его нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Отбор по компонентам, а не по списку прототипов.</b> Список id устарел бы на первой же
    /// новой карте или новом прототипе двери, и устарел бы молча — «эту бластдверь ИИ почему-то не
    /// открывает» не отличить от «так и задумано». Компонент же есть у любой двери независимо от
    /// того, кто её добавил.
    /// </para>
    /// <para>
    /// <b>Два ограничителя обязательны.</b> Только гриды станции — иначе доступ налипнет на
    /// Центком, эвакуационный шаттл и аванпост ядерщиков, то есть на всё, до чего ИИ не должен
    /// дотягиваться ни в каком режиме. Только заякоренное — иначе в набор попадут переносимые
    /// приборы, содержимое ящиков и вещи в руках у людей.
    /// </para>
    /// <para>
    /// Уже размеченное не трогаем вовсе, и это не оптимизация: у <c>StationAiWhitelist</c> есть
    /// поле <c>Enabled</c>, которое экипаж гасит, перерезав провод управления. Переналожить
    /// компонент значило бы чинить перерезанный провод — то есть отбирать у экипажа единственную
    /// контригру, которая работает молча и точечно.
    /// </para>
    /// </remarks>
    public RogueAiGrant GrantAccess(EntityUid station, RogueAiRuleComponent rule)
    {
        var grant = new RogueAiGrant();

        if (!TryComp<StationDataComponent>(station, out var data) || data.Grids.Count == 0)
            return grant;

        var grids = data.Grids;

        if (rule.GrantDoors && _cfg.GetCVar(AiCVars.RogueGrantDoors))
            grant.Doors = Grant<DoorComponent>(grids);

        // Сеть «всё с интерфейсом» выбрана потому, что ровно она соответствует инструменту
        // device_ui: тот строит контракт рефлексией по типам BUI-сообщений и работает с любой
        // консолью, как только у неё появился доступ. Сузить до списка типов консолей значило бы
        // отдать агенту меньше, чем он умеет.
        if (rule.GrantConsoles && _cfg.GetCVar(AiCVars.RogueGrantConsoles))
            grant.Consoles = Grant<UserInterfaceComponent>(grids);

        if (rule.GrantTurrets && _cfg.GetCVar(AiCVars.RogueGrantTurrets))
        {
            grant.Turrets = Grant<DeployableTurretComponent>(grids)
                + Grant<DeployableTurretControllerComponent>(grids);
        }

        rule.GrantedDoors += grant.Doors;
        rule.GrantedConsoles += grant.Consoles;
        rule.GrantedTurrets += grant.Turrets;

        // Строка обязана быть: обход, выродившийся в ноль, и обход, захвативший полстанции, в игре
        // выглядят одинаково — «ИИ ведёт себя странно», — а различаются только здесь.
        _sawmill.Info(
            $"доступ роздан на {ToPrettyString(station)}: дверей {grant.Doors}, " +
            $"консолей {grant.Consoles}, турелей {grant.Turrets}");

        return grant;
    }

    private static string Truncate(string text, int max)
    {
        var oneLine = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private int Grant<TComp>(IReadOnlySet<EntityUid> grids) where TComp : IComponent
    {
        var granted = 0;

        var query = EntityQueryEnumerator<TComp, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!xform.Anchored)
                continue;

            if (xform.GridUid is not { } grid || !grids.Contains(grid))
                continue;

            if (HasComp<StationAiWhitelistComponent>(uid))
                continue;

            AddComp<StationAiWhitelistComponent>(uid);
            granted++;
        }

        return granted;
    }

    /// <summary>
    /// Поставить на станцию киборгов поддержки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь, на раздаче должностей, а не на старте правила: правило стартует до
    /// <c>LoadMaps</c>-соседей и до того, как навигационная карта грида готова, а место под
    /// робота ищется именно по ней. К моменту <c>RulePlayerJobsAssignedEvent</c> станция уже
    /// собрана целиком.
    /// </para>
    /// <para>
    /// Роботы поднимаются ДО того, как мозг займёт ядро (это происходит на
    /// <c>RunLevel = InRound</c>), и это нормально: автозахват тел висит на том же переходе, а
    /// общий потолок числа агентов проверяется в <c>StartSession</c> и одинаков для всех.
    /// </para>
    /// <para>
    /// Неполный набор — не отказ раунда. Свободного пола у маяка может не найтись, и играть
    /// с двумя роботами вместо трёх лучше, чем не начать смену вовсе; расхождение видно
    /// строкой в журнале и в разборе раунда.
    /// </para>
    /// </remarks>
    public int SpawnSupportBorgs(RogueAiRuleComponent rule)
    {
        if (rule.SupportBorgs.Count == 0)
            return 0;

        // Аварийный тормоз того же вида, что у раздачи доступа: выключить включённое в прототипе
        // отсюда можно, включить выключенное — нет.
        if (!_cfg.GetCVar(AiCVars.RogueSupportBorgs))
        {
            _sawmill.Info("киборги поддержки отключены через ai.rogue_support_borgs");
            return 0;
        }

        foreach (var proto in rule.SupportBorgs)
        {
            if (TrySpawnAtPreferredBeacon(rule, proto, out var reason))
                rule.SpawnedBorgs++;
            else
                _sawmill.Warning($"киборг поддержки {proto.Id} не поставлен: {reason}");
        }

        _sawmill.Info($"киборгов поддержки: {rule.SpawnedBorgs} из {rule.SupportBorgs.Count}");
        return rule.SpawnedBorgs;
    }

    /// <summary>
    /// Поставить робота у первого маяка из списка предпочтений, какой найдётся.
    /// </summary>
    /// <remarks>
    /// Падать в «любой маяк», когда ни один из названных не нашёлся, — сознательное решение, но
    /// с громкой строкой в журнале. Раунд без роботов хуже раунда с роботами не там, где хотелось;
    /// а молчаливый откат — это ровно та поломка, которую этот список и чинит: в прошлый раз
    /// «любой» означал запертую комнату ядра, и понять это удалось только по игре.
    /// </remarks>
    private bool TrySpawnAtPreferredBeacon(RogueAiRuleComponent rule, EntProtoId proto, out string reason)
    {
        reason = string.Empty;

        foreach (var beacon in rule.SupportBorgBeacons)
        {
            if (_borgs.TrySpawnBorg(beacon, out _, out reason, proto))
                return true;
        }

        if (rule.SupportBorgBeacons.Count > 0)
        {
            _sawmill.Warning(
                $"ни одного из маяков [{string.Join(", ", rule.SupportBorgBeacons)}] не нашлось — " +
                "ставлю робота у любого подходящего; проверь, что он не оказался в запертом отсеке");
        }

        return _borgs.TrySpawnBorg(null, out _, out reason, proto);
    }

    // ------------------------------------------------------------- разбор раунда

    protected override void AppendRoundEndText(EntityUid uid,
        RogueAiRuleComponent component,
        GameRuleComponent gameRule,
        ref RoundEndTextAppendEvent args)
    {
        args.AddLine(Loc.GetString(component.AnnounceOnStart
            ? "rogue-ai-round-end-open"
            : "rogue-ai-round-end-hidden"));

        args.AddLine(Loc.GetString("rogue-ai-round-end-access",
            ("doors", component.GrantedDoors),
            ("consoles", component.GrantedConsoles),
            ("turrets", component.GrantedTurrets)));

        args.AddLine(Loc.GetString("rogue-ai-round-end-laws", ("lawset", component.Lawset.Id)));

        if (component.SupportBorgs.Count > 0)
        {
            args.AddLine(Loc.GetString("rogue-ai-round-end-borgs",
                ("borgs", component.SpawnedBorgs),
                ("wanted", component.SupportBorgs.Count)));
        }
    }
}

/// <summary>Сколько чего получило доступ за один обход. Нужен тестам и журналу.</summary>
public sealed class RogueAiGrant
{
    public int Doors;
    public int Consoles;
    public int Turrets;
}
