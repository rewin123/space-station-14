using System.Collections.Generic;
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
using Content.Server.GameTicking;
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
using Content.Shared.Doors.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Station;
using Content.Shared.StationRecords.Systems;
using Content.Shared.Radio;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

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
    [Dependency] private SharedStationSystem _station = default!;

    private ISawmill _sawmill = default!;
    private MainThreadDispatcher _dispatcher = default!;
    private GameTicker? _ticker;

    private readonly Dictionary<EntityUid, AgentSession> _sessions = new();
    private ILlmClient? _llm;

    public IReadOnlyDictionary<EntityUid, AgentSession> Sessions => _sessions;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai");

        // Constructed here, on the main thread, so it learns which thread that is.
        _dispatcher = new MainThreadDispatcher(_taskManager, _sawmill, _cfg.GetCVar(AiCVars.MainThreadBudgetMs));

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);

        // Perception. Radio arrives per receiver, so it can be scoped to our marker component.
        SubscribeLocalEvent<LlmStationAiComponent, RadioReceiveEvent>(OnRadioReceive);
        SubscribeLocalEvent<LlmStationAiComponent, MobStateChangedEvent>(OnMobStateChanged);

        // Local speech is raised on the speaker, not the listener, so it has to be filtered by
        // distance ourselves. Vanilla parity: the AI hears within VoiceRange of its physical core
        // and nowhere else — it has no camera microphones.
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);

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

        _sawmill.Info(
            $"agent system initialised enabled={_cfg.GetCVar(AiCVars.Enabled)} " +
            $"endpoint={_cfg.GetCVar(AiCVars.Endpoint)} model={_cfg.GetCVar(AiCVars.Model)}");
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ReleaseAll("server shutdown");
        (_llm as IDisposable)?.Dispose();
        _llm = null;
    }

    // ------------------------------------------------------------------ lifecycle

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound)
            return;

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
    }

    /// <summary>Find an empty AI core and put an LLM-driven brain in it.</summary>
    public bool TryClaimAnyCore(out string reason)
    {
        if (_sessions.Count >= _cfg.GetCVar(AiCVars.MaxAgents))
        {
            reason = $"already at ai.max_agents ({_cfg.GetCVar(AiCVars.MaxAgents)})";
            return false;
        }

        var query = EntityQueryEnumerator<StationAiCoreComponent>();
        while (query.MoveNext(out var coreUid, out var core))
        {
            if (_stationAi.TryGetHeld((coreUid, core), out _))
                continue;

            if (TryClaimCore(coreUid, out reason))
                return true;
        }

        reason = "no unoccupied AI core found";
        return false;
    }

    public bool TryClaimCore(EntityUid coreUid, out string reason)
    {
        if (!TryComp<StationAiCoreComponent>(coreUid, out _))
        {
            reason = $"{ToPrettyString(coreUid)} is not an AI core";
            return false;
        }

        var brain = SpawnInContainerOrDrop("StationAiBrain", coreUid, StationAiCoreComponent.Container);

        // Stop a ghost from taking over the body the model is driving. The admin takeover verb is
        // left alone on purpose — that is an intentional override — but it is logged loudly.
        RemComp<GhostRoleComponent>(brain);
        RemComp<ToggleableGhostRoleComponent>(brain);

        EnsureComp<LlmStationAiComponent>(brain);

        if (!StartSession(brain, out reason))
        {
            QueueDel(brain);
            return false;
        }

        _sawmill.Info($"claimed AI core {ToPrettyString(coreUid)} with brain {ToPrettyString(brain)}");
        reason = $"claimed {ToPrettyString(coreUid)}";
        return true;
    }

    private bool StartSession(EntityUid brain, out string reason)
    {
        var llm = EnsureClient();
        if (llm == null)
        {
            reason = "no LLM client (ai.enabled false?)";
            return false;
        }

        var queue = new ObservationQueue(_cfg.GetCVar(AiCVars.ObsBuffer));
        var registry = new AiToolRegistry();

        var session = new AgentSession(
            brain,
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
            (force, ct) => BuildObservationAsync(brain, force, ct),
            text => AnnounceInGameAsync(brain, text),
            (text, channel) => SpeakUntooledAsync(brain, text, channel),
            () => RunCuratorAsync(brain, registry),
            () =>
            {
                // Step 5 of the ritual. Picking the snapshots up HERE, and only here, is the whole
                // point of the frozen-snapshot design: writes during play went to disk immediately
                // but left the prefix untouched, and this is the one moment we are paying for a
                // prefill anyway.
                Memory.RefreshSnapshot();
                Skills.LoadFromDisk();
                return (BuildSystemPrompt(), registry.WireJson());
            },
            new CompactionOptions
            {
                High = () => _cfg.GetCVar(AiCVars.CompactHigh),
                Low = () => _cfg.GetCVar(AiCVars.CompactLow),
                KeepTail = () => _cfg.GetCVar(AiCVars.CompactKeepTail),
            },
            _sawmill);

        RegisterTools(session, registry);
        session.Conv.SetPrefix(BuildSystemPrompt(), registry.WireJson());
        session.Cache.SetExpectedPrefix(session.Conv.PrefixHash);

        _sessions[brain] = session;

        // Restore a conversation from before a restart, if the prefix still matches.
        var snapshot = SessionStoreFor().Load(SessionIdFor(brain), session.Conv.PrefixHash);
        if (snapshot != null)
        {
            session.Conv.RestoreBody(snapshot.Body, snapshot.VolatileTail, snapshot.CharsPerToken);

            // A snapshot taken mid-turn can hold an assistant tool_calls with no matching results.
            // Replaying that verbatim gets the whole request rejected, so close them first.
            session.Conv.Repair();
        }

        session.Start();

        _sawmill.Info($"session prefix hash {session.Conv.PrefixHash}");
        reason = "started";
        return true;
    }

    public void Release(EntityUid brain, string why)
    {
        if (!_sessions.Remove(brain, out var session))
            return;

        _sawmill.Info($"releasing agent on {brain}: {why}");

        // Snapshot before cancelling: a server restart mid-round should not amnesia the agent, and
        // this is the last moment the conversation is still coherent.
        try
        {
            SessionStoreFor().Save(SessionIdFor(brain), session.Conv, session.Compactor.Compactions);
        }
        catch (Exception e)
        {
            _sawmill.Warning($"снапшот при остановке не сохранён: {e.Message}");
        }

        session.Cts.Cancel();

        // Cancel then bounded wait then abandon. The main thread must never block on the agent:
        // if the loop is inside an HTTP call it would otherwise hold the tick for the full
        // request timeout.
        try
        {
            session.Loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here; nothing to do.
        }

        session.Dispose();
    }

    public void ReleaseAll(string why)
    {
        foreach (var brain in _sessions.Keys.ToList())
            Release(brain, why);
    }

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

        var sampling = new LlmSampling(
            _cfg.GetCVar(AiCVars.Temperature),
            _cfg.GetCVar(AiCVars.TopP),
            _cfg.GetCVar(AiCVars.TopK),
            _cfg.GetCVar(AiCVars.MinP),
            _cfg.GetCVar(AiCVars.MaxTokens),
            IdSlot: 0);

        _llm = new LlamaClient(
            _cfg.GetCVar(AiCVars.Endpoint),
            _cfg.GetCVar(AiCVars.Model),
            _cfg.GetCVar(AiCVars.ApiKey),
            sampling,
            TimeSpan.FromSeconds(_cfg.GetCVar(AiCVars.RequestTimeout)),
            _sawmill);

        return _llm;
    }

    // ----------------------------------------------------------------- perception

    private void OnRadioReceive(Entity<LlmStationAiComponent> ent, ref RadioReceiveEvent args)
    {
        if (!_sessions.TryGetValue(ent.Owner, out var session))
            return;

        // The displayed voice name, exactly what a human player's chat line shows. Note we do NOT
        // pass args.MessageSource on: the entity behind a voice is more than a player can know.
        var speaker = GetVoiceName(args.MessageSource);

        session.Queue.Push(Observation.Radio(args.Channel.ID, speaker, args.Message, RoundTime()));
    }

    private void OnEntitySpoke(EntitySpokeEvent args)
    {
        if (_sessions.Count == 0)
            return;

        // A radio-prefixed message is delivered through RadioReceiveEvent instead; taking it here
        // as well would show the AI every transmission twice.
        if (args.Channel != null)
            return;

        var range = _cfg.GetCVar(AiCVars.HearRange);
        var speakerXform = Transform(args.Source);

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

            session.Queue.Push(Observation.Speech(
                "core",
                GetVoiceName(args.Source),
                args.ObfuscatedMessage ?? args.Message,
                RoundTime()));
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
        session.Queue.Push(Observation.Event(
            intoCore ? "вернулся в ядро — оборудование снова доступно" : "загружен в интелликарту",
            RoundTime()));
    }

    private void OnBrainRemoved(Entity<LlmStationAiComponent> ent, ref EntGotRemovedFromContainerMessage args)
    {
        if (!_sessions.TryGetValue(ent.Owner, out var session))
            return;

        BumpGeneration(ent.Owner);

        // The loop keeps running: a carded AI still hears Binary and Common and can still speak.
        // Only the device tools refuse, via the mode gate.
        session.Mode = AgentMode.Carded;
        session.Queue.Push(Observation.Event("извлечён из ядра — доступа к устройствам нет", RoundTime()));
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

    private static string SessionIdFor(EntityUid brain) => "current";

    /// <summary>Say something in-game from the agent, used by the compaction ritual.</summary>
    private Task AnnounceInGameAsync(EntityUid brain, string text)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return Task.CompletedTask;

        return _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("compaction announce");

            if (!IsPlayable(brain))
                return false;

            _chat.TrySendInGameICMessage(brain, text, InGameICChatType.Speak, ChatTransmitRange.Normal,
                hideLog: false, shell: null, player: null, nameOverride: null,
                checkRadioPrefix: false, ignoreActionBlocker: true);

            _sawmill.Info($"[LLM] компакция: {text}");
            return true;
        }, session.Generation, () => GenerationOf(brain), CancellationToken.None, what: "compaction announce");
    }

    /// <summary>
    /// Deliver a reply the model wrote as plain text instead of calling <c>say</c>/<c>radio</c>.
    ///
    /// This is a backstop, not a feature: the loop only reaches it after the model has been told
    /// once that prose is inaudible and answered in prose anyway, and only on a turn where somebody
    /// actually addressed the AI. It routes to the channel the request came in on, because a reply
    /// whispered next to the core is no better than silence to whoever asked over the radio.
    /// </summary>
    private Task<bool> SpeakUntooledAsync(EntityUid brain, string text, string? channel)
    {
        if (!_cfg.GetCVar(AiCVars.SpeakUntooledText) || !_sessions.TryGetValue(brain, out var session))
            return Task.FromResult(false);

        return _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("untooled reply");

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
        }, session.Generation, () => GenerationOf(brain), CancellationToken.None, what: "untooled reply");
    }

    /// <summary>Persist the conversation so a restart does not amnesia the agent mid-round.</summary>
    public void SaveSessions()
    {
        foreach (var (brain, session) in _sessions)
            SessionStoreFor().Save(SessionIdFor(brain), session.Conv, session.Compactor.Compactions);
    }

    // ------------------------------------------------------------------- curator

    private Skills.Curator? _curator;

    /// <summary>
    /// Run the review as step 1 of the compaction ritual.
    ///
    /// The session is put into <see cref="AgentMode.Review"/> by the caller, so the acting tools
    /// refuse with <c>review_mode</c> while the skill and memory tools keep working — which is why
    /// the tool array can stay byte-identical to play and the warm prefix survives.
    /// </summary>
    private async Task RunCuratorAsync(EntityUid brain, Tools.AiToolRegistry registry)
    {
        if (!_cfg.GetCVar(AiCVars.CuratorEnabled))
            return;

        if (!_sessions.TryGetValue(brain, out var session))
            return;

        _curator ??= new Skills.Curator(EnsureClient()!, _sawmill);
        Memory.ResetTurnCounters();

        await _curator.ReviewAsync(
            session.Conv,
            registry.WireSchemas(),
            registry,
            Skills.RenderIndex(),
            maxSteps: _cfg.GetCVar(AiCVars.MaxToolCallsPerTurn),
            session.Cts.Token).ConfigureAwait(false);
    }

    /// <summary>Kick off a review right now, without waiting for the context to fill up.</summary>
    public bool RunCuratorNow(out string reason)
    {
        if (_sessions.Count == 0)
        {
            reason = "нет активного агента";
            return false;
        }

        var (brain, session) = _sessions.First();

        _ = Task.Run(async () =>
        {
            session.Mode = AgentMode.Review;
            try
            {
                await RunCuratorAsync(brain, session.Registry).ConfigureAwait(false);
                await _dispatcher.RunAsync(() =>
                {
                    Memory.RefreshSnapshot();
                    Skills.LoadFromDisk();
                    return true;
                }, session.Generation, () => GenerationOf(brain), CancellationToken.None,
                    what: "curator snapshot refresh").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _sawmill.Error($"куратор по команде упал: {e}");
            }
            finally
            {
                session.Mode = AgentMode.Core;
            }
        });

        reason = "ревью запущено, результат появится в логе";
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
