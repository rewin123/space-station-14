using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Components;
using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Locale;
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
/// The language model inside a borg body.
///
/// <para>
/// The agent's second body, and its first mobile one. Everything that makes an agent an agent —
/// the turn loop, dialogue, compaction, memory, model routing — comes ready-made: the system
/// assembles an <see cref="AgentBody"/> and hands it to the host. What lives here is only what
/// distinguishes a borg from a stationary eye: how it occupies a body, how it walks, how it sees,
/// and what it does with its hands.
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

    /// <summary>Bodies we already occupy. Key is the chassis entity.</summary>
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
        // The host releases sessions itself; our job is not to leave orphaned minds behind.
        foreach (var uid in _claimed.Keys.ToList())
            ReleaseBody(uid, "перезапуск раунда");

        ForgetTakenTiles();
    }

    /// <summary>
    /// Seat an agent into a chassis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order here isn't arbitrary. <b>The mind is assigned first, and it's mandatory.</b>
    /// <c>SharedBorgSystem.CanActivate</c> requires <c>TryGetMind</c>, and without a mind the
    /// chassis won't activate: modules won't come up, ID-based access won't turn on
    /// (<c>SharedBorgSystem.OnMindAdded</c>), and speed will stay at walking pace. The mind itself
    /// is headless — <c>CreateMind(null)</c>, with no player: <c>TransferTo</c> only trips over an
    /// <c>ActorComponent</c>, which the chassis doesn't have.
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

        // The identifier comes before the mind and before the session, because it's what picks
        // the directory where the log and dialogue file will land. A mistake here is the most
        // costly one: two robots with the same id don't crash, they quietly write over each other.
        if (!TryAssignAgentId(comp, out reason))
            return false;

        ApplyAgentName(comp);

        // The mind is a condition for activation, not decoration. See remarks.
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

        // The "an LLM agent lives here" marker. Named after the first body, but that's exactly
        // what it means, and the borg needs it for more than tidiness: radio reception is hung off
        // the pair (marker, RadioReceiveEvent), and without it the robot is completely DEAF to the
        // airwaves. In combat this looked like: the order went out on Common, Station AI answered,
        // the borg took zero turns and stayed standing in the bar.
        EnsureComp<LlmStationAiComponent>(borg);

        // The name comes from the agent's settings, not the vanilla NameIdentifier.
        //
        // The prototype hands out "Le Borgue (Si-6785)", and that's exactly what would go out on
        // the airwaves, while SOUL calls the agent something else the whole time. Addressed by its
        // real name, the model then fails to respond: that name appears nowhere in its prompt.
        // Exactly the same reason the core's brain gets renamed on claim.
        _metaData.SetEntityName(borg, comp.AgentName);

        if (!_host.StartSession(BuildBody(borg, comp), out reason))
        {
            ReleaseMind(comp);
            return false;
        }

        _claimed[borg] = comp;

        // The body is on the move now — meaning it will start entering other people's sight ranges.
        // See AiBorgSystem.Replication.cs.
        HideSubtree(borg);
        HoldInPvs(borg);

        var active = TryComp<BorgChassisComponent>(borg, out var chassis) && chassis.Active;
        _sawmill.Info(
            $"агент {comp.AgentId} занял {ToPrettyString(borg)}; шасси активно: {active}");

        if (!active)
        {
            // Not a failure: a borg without a battery still moves and talks, just without modules.
            // But this needs to be known right away, or "hands don't work" will get investigated
            // as a tool bug.
            _sawmill.Warning(
                $"{ToPrettyString(borg)} не активировался — нет заряда или он в крите. " +
                "Модули и доступ по ID будут недоступны, пока это не исправится.");
        }

        reason = "занято";
        return true;
    }

    /// <summary>Release the body and clean up its mind.</summary>
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

        // The mind was created solely to activate the chassis and belongs to no one else: there's
        // no player behind it. Leaving it around would mean accumulating entities every round.
        if (!TerminatingOrDeleted(mind))
            QueueDel(mind);
    }

    /// <summary>
    /// Assign the robot an agent identifier if the prototype didn't name it explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An id counts as taken if it's held either by a live session or by an already-claimed
    /// robot. The second condition exists for exactly the case the allocator was written for: a
    /// game-mode rule spawns three borgs in a row, and by the time the third one is assigned an
    /// id, the first may not yet have a session — <c>StartSession</c> runs later in this same
    /// method.
    /// </para>
    /// <para>
    /// An explicitly set id that's already taken is a REJECTION, not a silent override. This is
    /// the one place where a mistake in the prototype is still visible; past this point it looks
    /// like "the robot somehow remembers someone else's shift."
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
    /// Pick a name by body number: <c>combat-3</c> gets the third name from
    /// <see cref="AiBorgComponent.AgentNames"/>.
    ///
    /// <para>
    /// The number is taken from the already-assigned identifier, not a separate counter — otherwise
    /// the two numbering sources would drift apart the first time a body is freed up, and a robot
    /// with the directory <c>combat-3</c> would answer to someone else's name.
    /// </para>
    /// <para>
    /// If there are fewer names than bodies, we wrap around from the end of the list and append a
    /// number: six "Blade"s is a breakage, while "Blade-2" is merely ugly. If the list is empty, we
    /// keep whatever's in the prototype, i.e. the previous behavior.
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

    /// <summary>Identifiers already taken by someone: live sessions and claimed bodies.</summary>
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
    /// Description of the "borg chassis" body.
    /// </summary>
    /// <remarks>
    /// <c>Announce</c> is deliberately left <c>null</c>: Station AI's station-wide announcement
    /// works through a built-in <c>CommunicationsConsoleComponent</c>, which the chassis doesn't
    /// have. This is a missing organ, not an oversight — the host speaks the compaction warning
    /// aloud in this case instead.
    /// </remarks>
    private AgentBody BuildBody(EntityUid borg, AiBorgComponent comp)
    {
        // Tool mode is fixed right here, when the body is assembled, and never changes after that.
        // Otherwise the prompt and the wire could drift apart: the wire is assembled once at
        // session start, while the prompt is also reassembled on compaction.
        var scripted = _cfg.GetCVar(AiCVars.ScriptMode);
        var lang = AgentLangUtil.Parse(_cfg.GetCVar(AiCVars.Language));

        // Each robot has its own filesystem. The reference section in it is shared with the core
        // as a single instance, while records, notes about people, and memory are its own: the
        // borg used to carry twenty kilobytes of the Station AI's library in its prefix, including
        // crew dossiers it had no use for.
        var vfs = _host.BuildVfs(comp.AgentId, lang);

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
            Language = lang,
            BuildPrompt = () => BuildBorgPrompt(borg, comp, scripted, vfs, lang),
            SelfLine = s => BorgSelfLine(s, borg),
            BeforeObservation = s => PushSightDelta(s, borg),
            RegisterTools = (s, r) => RegisterBorgTools(s, r, comp),
            Announce = null,
            Speak = _host.SpeakUntooledAsync,
            ChannelsFor = _ => comp.Channels,

            // Segment curation is disabled for the borg (owner's decision, 2026-09-01): it cost up
            // to a minute of silence on every compaction, and with four agents there are a lot of
            // compactions. More details in AgentBody.Curate.
            Curate = false,
            LlmChain = string.IsNullOrWhiteSpace(comp.LlmChain) ? null : comp.LlmChain,
        };
    }
}
