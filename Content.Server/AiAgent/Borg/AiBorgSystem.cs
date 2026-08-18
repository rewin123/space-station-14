using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Components;
using Content.Server.AiAgent.Core;
using Content.Shared.DoAfter;
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

        return new AgentBody
        {
            Owner = borg,
            Id = comp.AgentId,
            Name = comp.AgentName,
            SoulFile = comp.SoulFile,
            Eye = () => borg,
            Alive = () => Exists(borg) && !TerminatingOrDeleted(borg) && !_mobState.IsDead(borg),
            ScriptMode = scripted,
            BuildPrompt = () => BuildBorgPrompt(borg, comp, scripted),
            SelfLine = s => BorgSelfLine(s, borg),
            BeforeObservation = s => PushSightDelta(s, borg),
            RegisterTools = (s, r) => RegisterBorgTools(s, r, comp),
            Announce = null,
            Speak = _host.SpeakUntooledAsync,
            ChannelsFor = _ => comp.Channels,
            LlmChain = string.IsNullOrWhiteSpace(comp.LlmChain) ? null : comp.LlmChain,
        };
    }
}
