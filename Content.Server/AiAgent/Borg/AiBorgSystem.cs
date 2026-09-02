using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Components;
using Content.Server.AiAgent.Core;
using Content.Shared.DoAfter;
using Robust.Shared.Containers;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Content.Server.Mind;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Silicons.Borgs;
using Content.Shared.Silicons.Borgs.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Log;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Языковая модель в теле борга.
///
/// <para>
/// Второе тело агента и первое подвижное. Всё, что делает агента агентом — петля хода, диалог,
/// компакция, память, маршрутизация моделей, — берётся готовым: система собирает
/// <see cref="AgentBody"/> и отдаёт его хосту. Здесь живёт только то, чем борг отличается от
/// неподвижного глаза: как он занимает тело, как ходит, как видит и что делает руками.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem : EntitySystem
{
    [Dependency] private StationAiAgentSystem _host = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private SharedBorgSystem _borg = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    /// <summary>Тела, которые мы уже занимаем. Ключ — сущность шасси.</summary>
    private readonly Dictionary<EntityUid, AiBorgComponent> _claimed = new();

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai.borg");

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);

        InitializeMovement();
        InitializeSight();
        InitializeReplication();
        InitializeHits();
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound || !_cfg.GetCVar(AiCVars.Enabled))
            return;

        var query = EntityQueryEnumerator<AiBorgComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!comp.AutoClaim || _claimed.ContainsKey(uid))
                continue;

            if (!TryClaim(uid, out var reason))
                _sawmill.Warning($"автозахват {ToPrettyString(uid)} не удался: {reason}");
        }
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent ev)
    {
        // Хост сам освобождает сессии; наше дело — не оставить за собой разумов-сирот.
        foreach (var uid in _claimed.Keys.ToList())
            ReleaseBody(uid, "перезапуск раунда");

        ForgetTakenTiles();
    }

    /// <summary>
    /// Посадить агента в шасси.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок здесь не произвольный. <b>Разум ставится первым и он обязателен.</b>
    /// <c>SharedBorgSystem.CanActivate</c> требует <c>TryGetMind</c>, и без разума шасси не
    /// активируется: не встанут модули, не включится доступ по ID
    /// (<c>SharedBorgSystem.OnMindAdded</c>), а скорость останется шаговой. Разум при этом
    /// безголовый — <c>CreateMind(null)</c>, без игрока: <c>TransferTo</c> спотыкается только об
    /// <c>ActorComponent</c>, которого у шасси нет.
    /// </para>
    /// </remarks>
    public bool TryClaim(EntityUid borg, out string reason)
    {
        if (!TryComp<AiBorgComponent>(borg, out var comp))
        {
            reason = $"{ToPrettyString(borg)} — не ИИ-борг";
            return false;
        }

        if (_claimed.ContainsKey(borg))
        {
            reason = $"{ToPrettyString(borg)} уже занят";
            return false;
        }

        if (!TryComp<BorgChassisComponent>(borg, out _))
        {
            reason = $"{ToPrettyString(borg)} — не шасси борга";
            return false;
        }

        // Идентификатор — раньше разума и раньше сессии, потому что именно он выбирает каталог,
        // куда лягут журнал и файл диалога. Ошибиться здесь дороже всего: два робота с одним id
        // не падают, а тихо пишут друг поверх друга.
        if (!TryAssignAgentId(comp, out reason))
            return false;

        ApplyAgentName(comp);

        // Разум — условие активации, а не украшение. См. remarks.
        if (!_mind.TryGetMind(borg, out var existing, out _))
        {
            var mind = _mind.CreateMind(null, comp.AgentName);
            comp.Mind = mind.Owner;
            _mind.TransferTo(mind.Owner, borg, ghostCheckOverride: true);
        }
        else
        {
            comp.Mind = existing;
        }

        // Маркер «здесь живёт LLM-агент». Назван по первому телу, но значит именно это, и боргу
        // он нужен не для порядка: на пару (маркер, RadioReceiveEvent) повешен приём рации, и без
        // него робот полностью ГЛУХ к эфиру. На бою это выглядело так — приказ ушёл в Common,
        // Station AI ответил, борг взял ноль ходов и остался стоять в баре.
        EnsureComp<LlmStationAiComponent>(borg);

        // Имя из настроек агента, а не ванильный NameIdentifier.
        //
        // Прототип выдаёт «Le Borgue (Si-6785)», и в эфир уходило бы именно оно, тогда как SOUL
        // всю дорогу зовёт агента иначе. На обращение по своему настоящему имени модель тогда не
        // отзывается: этого имени нет в её промпте нигде. Ровно та же причина, по которой мозг в
        // ядре переименовывается при захвате.
        _metaData.SetEntityName(borg, comp.AgentName);

        if (!_host.StartSession(BuildBody(borg, comp), out reason))
        {
            ReleaseMind(comp);
            return false;
        }

        _claimed[borg] = comp;

        // Тело поехало — значит начнёт входить в чужие зоны видимости. См. AiBorgSystem.Replication.cs.
        HideSubtree(borg);
        HoldInPvs(borg);

        var active = TryComp<BorgChassisComponent>(borg, out var chassis) && chassis.Active;
        _sawmill.Info(
            $"агент {comp.AgentId} занял {ToPrettyString(borg)}; шасси активно: {active}");

        if (!active)
        {
            // Не отказ: борг без батареи ездит и говорит, просто без модулей. Но знать об этом
            // надо сразу, иначе «руки не работают» будет расследоваться как баг инструментов.
            _sawmill.Warning(
                $"{ToPrettyString(borg)} не активировался — нет заряда или он в крите. " +
                "Модули и доступ по ID будут недоступны, пока это не исправится.");
        }

        reason = "занято";
        return true;
    }

    /// <summary>Освободить тело и убрать за собой разум.</summary>
    public void ReleaseBody(EntityUid borg, string why)
    {
        if (!_claimed.Remove(borg, out var comp))
            return;

        StopSteering(borg);
        ForgetSight(borg);
        ForgetHits(borg);
        ShowSubtree(borg);
        ReleaseFromPvs(borg);
        _host.Release(borg, why);
        ReleaseMind(comp);

        _sawmill.Info($"агент {comp.AgentId} освободил {ToPrettyString(borg)}: {why}");
    }

    private void ReleaseMind(AiBorgComponent comp)
    {
        if (comp.Mind is not { } mind)
            return;

        comp.Mind = null;

        // Разум заводился ради активации шасси и никому больше не принадлежит: игрока за ним нет.
        // Оставить его — значит копить сущности на каждый раунд.
        if (!TerminatingOrDeleted(mind))
            QueueDel(mind);
    }

    /// <summary>
    /// Выдать роботу идентификатор агента, если прототип не назвал его явно.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Занятым считается id, который держит либо живая сессия, либо уже заклеймленный робот.
    /// Второе условие нужно ровно для того случая, ради которого аллокатор и написан: правило
    /// режима спавнит трёх боргов подряд, и первый из них к моменту выдачи id третьему может
    /// ещё не иметь сессии — <c>StartSession</c> идёт позже по этому же методу.
    /// </para>
    /// <para>
    /// Явно заданный и уже занятый id — ОТКАЗ, а не молчаливое наложение. Это единственное место,
    /// где ошибку в прототипе ещё видно; дальше она выглядит как «робот почему-то помнит чужую
    /// смену».
    /// </para>
    /// </remarks>
    private bool TryAssignAgentId(AiBorgComponent comp, out string reason)
    {
        var taken = TakenAgentIds();

        if (!string.IsNullOrWhiteSpace(comp.AgentId))
        {
            if (taken.Contains(comp.AgentId))
            {
                reason = $"идентификатор «{comp.AgentId}» уже занят другим агентом";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        var prefix = string.IsNullOrWhiteSpace(comp.AgentIdPrefix) ? "borg" : comp.AgentIdPrefix.Trim();

        for (var n = 1; n <= 64; n++)
        {
            var id = $"{prefix}-{n}";

            if (taken.Contains(id))
                continue;

            comp.AgentId = id;
            reason = string.Empty;
            return true;
        }

        reason = $"не нашлось свободного идентификатора с префиксом «{prefix}»";
        return false;
    }

    /// <summary>
    /// Выбрать имя по номеру тела: <c>combat-3</c> получает третье имя из
    /// <see cref="AiBorgComponent.AgentNames"/>.
    ///
    /// <para>
    /// Номер берётся из уже выданного идентификатора, а не из отдельного счётчика, — иначе два
    /// источника нумерации разошлись бы на первом же освободившемся теле, и робот с каталогом
    /// <c>combat-3</c> отзывался бы на чужое имя.
    /// </para>
    /// <para>
    /// Имён меньше, чем тел, — берём с конца списка по кругу и дописываем номер: шесть «Клинов»
    /// это поломка, а «Клин-2» — всего лишь некрасиво. Список пуст — остаётся то, что стоит в
    /// прототипе, то есть прежнее поведение.
    /// </para>
    /// </summary>
    private void ApplyAgentName(AiBorgComponent comp)
    {
        if (comp.AgentNames.Count == 0)
            return;

        var dash = comp.AgentId.LastIndexOf('-');

        if (dash < 0 || !int.TryParse(comp.AgentId.AsSpan(dash + 1), out var n) || n < 1)
            return;

        var index = (n - 1) % comp.AgentNames.Count;
        var lap = (n - 1) / comp.AgentNames.Count;

        var name = comp.AgentNames[index];

        comp.AgentName = lap == 0 ? name : $"{name}-{lap + 1}";
    }

    /// <summary>Идентификаторы, которые уже кем-то заняты: живыми сессиями и заклеймленными телами.</summary>
    private HashSet<string> TakenAgentIds()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in _host.Sessions.Values)
            taken.Add(session.Body.Id);

        foreach (var claimed in _claimed.Values)
        {
            if (!string.IsNullOrWhiteSpace(claimed.AgentId))
                taken.Add(claimed.AgentId);
        }

        return taken;
    }


    /// <summary>
    /// Описание тела «шасси борга».
    /// </summary>
    /// <remarks>
    /// <c>Announce</c> оставлен <c>null</c> намеренно: общестанционное объявление у Station AI
    /// работает через встроенную <c>CommunicationsConsoleComponent</c>, которой у шасси нет. Это
    /// отсутствие органа, а не недоделка — предупреждение о компакции хост в этом случае
    /// произносит вслух.
    /// </remarks>
    private AgentBody BuildBody(EntityUid borg, AiBorgComponent comp)
    {
        // Режим инструментов фиксируется здесь, при сборке тела, и дальше не меняется. Иначе
        // промпт и провод могли бы разъехаться: провод собирается один раз на старте сессии, а
        // промпт пересобирается ещё и на компакции.
        var scripted = _cfg.GetCVar(AiCVars.ScriptMode);

        // Своя файловая система у каждого робота. Справочник в ней общий с ядром одним
        // экземпляром, а записи, заметки о людях и память — свои: раньше борг таскал в префиксе
        // двадцать килобайт библиотеки Станционного ИИ, включая досье на экипаж, которые ему
        // нечем применить.
        var vfs = _host.BuildVfs(comp.AgentId);

        return new AgentBody
        {
            Owner = borg,
            Id = comp.AgentId,
            Name = comp.AgentName,
            SoulFile = comp.SoulFile,
            Vfs = vfs,
            Eye = () => borg,
            Alive = () => Exists(borg) && !TerminatingOrDeleted(borg) && !_mobState.IsDead(borg),
            ScriptMode = scripted,
            BuildPrompt = () => BuildBorgPrompt(borg, comp, scripted, vfs),
            SelfLine = s => BorgSelfLine(s, borg),
            BeforeObservation = s => PushSightDelta(s, borg),
            RegisterTools = (s, r) => RegisterBorgTools(s, r, comp),
            Announce = null,
            Speak = _host.SpeakUntooledAsync,
            ChannelsFor = _ => comp.Channels,

            // Разбор отрезка боргу выключен (решение владельца 01.09.2026): он стоил до минуты
            // молчания на каждую свёртку, а свёрток у четырёх агентов много. Подробнее — в
            // AgentBody.Curate.
            Curate = false,
            LlmChain = string.IsNullOrWhiteSpace(comp.LlmChain) ? null : comp.LlmChain,
        };
    }
}
