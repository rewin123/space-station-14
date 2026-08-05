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
    [Dependency] private Content.Server.Pinpointer.NavMapSystem _navMap = default!;
    [Dependency] private Content.Shared.Power.EntitySystems.SharedBatterySystem _battery = default!;
    [Dependency] private SharedContainerSystem _container = default!;

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

        // Eagerly, so no first touch from the agent thread can race one from the main thread and
        // build a second store that silently swallows whatever the loser wrote.
        ReloadAgentFiles();

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

        // The curator captured that client at construction and was never rebuilt, so from the second
        // round onwards its first call hit a disposed HttpClient. The exception was caught by the
        // compaction ritual and logged as "the curator did not run" — and the agent quietly stopped
        // learning for the rest of the process lifetime.
        _curator = null;
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

        // Closed over by the delegates below instead of looking the session up in _sessions.
        //
        // That lookup used to happen on the AGENT thread, against a plain Dictionary the main
        // thread adds to and removes from. A TryGetValue that lands on a resize is not "an
        // occasional exception" — it can spin forever inside the bucket chain, and the symptom is
        // an agent that reports a live session and silently stops taking turns. Assigned
        // immediately after construction; nothing can invoke a delegate before Start().
        AgentSession? self = null;

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
            (force, ct) => BuildObservationAsync(self!, force, ct),
            text => AnnounceInGameAsync(self!, text),
            (text, channel) => SpeakUntooledAsync(self!, text, channel),
            () => RunCuratorAsync(self!, registry),
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
                High = () => EffectiveCompactHigh(self!),
                Low = () => _cfg.GetCVar(AiCVars.CompactLow),
                KeepTail = () => _cfg.GetCVar(AiCVars.CompactKeepTail),
            },
            _sawmill)
        {
            Journal = _cfg.GetCVar(AiCVars.LogTranscript)
                ? new Journal(System.IO.Path.Combine(DataDir(), "logs"), _sawmill)
                : Journal.Disabled,
        };

        self = session;

        RegisterTools(session, registry);
        session.Conv.SetPrefix(BuildSystemPrompt(), registry.WireJson());
        session.Cache.SetExpectedPrefix(session.Conv.PrefixHash);

        _sessions[brain] = session;

        // Restore a conversation from before a restart, if the prefix still matches.
        var snapshot = SessionStoreFor().Load(SessionIdFor(brain), session.Conv.PrefixHash, CurrentRoundId());
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
        var configured = _cfg.GetCVar(AiCVars.CompactHigh);

        var limit = _cfg.GetCVar(AiCVars.CtxLimit);
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

        // Snapshot before cancelling: a server restart mid-round should not amnesia the agent, and
        // this is the last moment the conversation is still coherent.
        try
        {
            SessionStoreFor().Save(SessionIdFor(brain), session.Conv, session.Compactor.Compactions, CurrentRoundId());
        }
        catch (Exception e)
        {
            _sawmill.Warning($"снапшот при остановке не сохранён: {e.Message}");
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

        AutoSaveSessions(frameTime);
        PruneHandles(frameTime);
    }

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
    private Task<bool> SpeakUntooledAsync(AgentSession session, string text, string? channel)
    {
        if (!_cfg.GetCVar(AiCVars.SpeakUntooledText))
            return Task.FromResult(false);

        var brain = session.Brain;

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
            SessionStoreFor().Save(SessionIdFor(brain), session.Conv, session.Compactor.Compactions, CurrentRoundId());
    }

    private float _sinceAutoSave;

    /// <summary>
    /// Periodic snapshot, because the tidy place to do it does not run.
    ///
    /// <c>EntitySystem.Shutdown()</c> is never invoked on a dedicated server: <c>BaseServer.Cleanup</c>
    /// reaches <c>EntityManager.Cleanup()</c>, which calls <c>EntitySystemManager.Clear()</c> — and
    /// Clear does not call Shutdown on anything. Only the client path does. So the "save on the way
    /// out" that this class appeared to have never ran in production, and the only real save was the
    /// one at round restart, by which point the round it belonged to was already over.
    ///
    /// Serialising the body costs a few hundred kilobytes of JSON, which is why this is once a
    /// minute rather than once a turn.
    /// </summary>
    private void AutoSaveSessions(float frameTime)
    {
        if (_sessions.Count == 0)
            return;

        _sinceAutoSave += frameTime;
        if (_sinceAutoSave < AutoSaveSeconds)
            return;

        _sinceAutoSave = 0f;
        SaveSessions();
    }

    private const float AutoSaveSeconds = 60f;

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

        await _curator.ReviewAsync(
            session.Conv,
            registry.WireSchemas(),
            registry,
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
