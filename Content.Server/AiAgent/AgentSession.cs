using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Threading;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Locale;
using Content.Server.AiAgent.Turn;

namespace Content.Server.AiAgent;

public enum AgentMode : byte
{
    /// <summary>In the core: full tool surface.</summary>
    Core,

    /// <summary>Ejected into an intellicard: still hears and speaks on Binary, but cannot touch devices.</summary>
    Carded,

    /// <summary>The curator is reviewing; game-acting tools refuse with review_mode.</summary>
    Review,
}

/// <summary>Knobs the loop reads each turn, so a live <c>cvar</c> change takes effect without a restart.</summary>
public sealed class AgentLoopOptions
{
    public required Func<float> TickSeconds { get; init; }
    public required Func<float> TickSecondsIdle { get; init; }
    public required Func<int> MaxToolCallsPerTurn { get; init; }
    public required Func<int> MaxConsecutiveFailures { get; init; }
}

/// <summary>
/// One LLM-driven Station AI: its conversation, its perception queue, and the background loop
/// that ties them together.
///
/// The loop runs on the thread pool via <see cref="Task.Run(Func{Task})"/> rather than
/// <c>Task.Factory.StartNew</c>. That matters: <c>TaskManager.Initialize</c> installs a
/// <c>RobustSynchronizationContext</c> on the game thread, and starting the loop there would make
/// every <c>await</c> resume on the game thread. On the pool, <c>SynchronizationContext.Current</c>
/// is null, so continuations stay off the tick — and every await carries
/// <c>ConfigureAwait(false)</c> as belt and braces.
///
/// Nothing in this class dereferences the entity world. Its only door to the game is the set of
/// delegates handed in by <see cref="StationAiAgentSystem"/>, each of which marshals itself onto
/// the main thread. If you cannot name <c>EntityManager</c>, you cannot touch it off-thread.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly ILlmClient _llm;
    private readonly AiToolRegistry _registry;

    private readonly ISawmill _sawmill;
    private readonly AgentLoopOptions _options;
    private readonly Func<bool, CancellationToken, Task<TurnPerception?>> _buildObservation;
    private readonly Func<string, Task> _announce;
    private readonly Func<string, string?, Task<bool>> _speak;
    /// <summary>
    /// Review of a stretch of history. Returns a short report if it wrote anything, and
    /// <c>null</c> if not.
    ///
    /// The return value appeared together with the report in the conversation: it used to be that
    /// the verdict only went to the log and the agent had no way of knowing it had written anything
    /// at all — the review ran on a copy and vanished with it.
    /// </summary>
    private readonly Func<Task<string?>>? _curate;
    private readonly Func<(string SystemPrompt, string ToolsJson)> _rebuildPrefix;

    private readonly TurnRunner _turn;

    /// <summary>The debug bus, or null when it is off. The loop uses it only for the stats sample.</summary>
    private readonly IAgentEventSink? _sink;

    /// <summary>How often to retry once failures have crossed the threshold.</summary>
    private const int DegradedRetryMs = 60_000;

    /// <summary>Announce degraded mode once, not on every failure.</summary>
    private bool _notedDegraded;

    /// <summary>How the last turn ended, for diagnostics and for tests that assert on the shape.</summary>
    public TurnContext? LastTurn { get; private set; }

    /// <summary>Context compaction, wired in phase 3.</summary>
    public Compactor Compactor { get; }

    /// <summary>Prefix-cache watchdog. A broken cache is silent; this is what makes it loud.</summary>
    public CacheMetrics Cache { get; }

    /// <summary>
    /// Machine-readable event log for the acceptance run. <see cref="Journal.Disabled"/> when off.
    ///
    /// A constructor parameter, deliberately, and not an <c>init</c> property: the constructor hands
    /// this to <see cref="TurnRunner"/>, and an object initializer runs <em>after</em> the constructor.
    /// As an <c>init</c> property it read <see cref="Journal.Disabled"/> every time, so the four
    /// per-turn event kinds never reached disk while the compaction event — the only one written
    /// through this property at call time — did. A day of acceptance log said "1 compaction" and
    /// nothing else, and nothing anywhere reported an error.
    /// </summary>
    public Journal Journal { get; }

    /// <summary>
    /// The model server's real context window, asked for once when the loop starts.
    ///
    /// Zero until it answers, or if it cannot. Compaction thresholds are clamped against it, so a
    /// server reconfigured to a smaller window does not let the agent sail past it into bare HTTP
    /// errors with nothing to say why.
    /// </summary>
    public int ContextLimit { get; private set; }

    /// <summary>The body the agent lives in — the core's only door to the game world.</summary>
    public AgentBody Body { get; }

    /// <summary>Prompt language frozen on the body. Tools, SELF and observations all read this.</summary>
    public AgentLocale Locale => AgentLocale.Of(Body.Language);

    /// <summary>The body's entity. Kept as a property so as not to rewrite fifty call sites.</summary>
    public EntityUid Brain => Body.Owner;

    /// <summary>Everything mutable about this agent. See <see cref="AgentState"/> for why.</summary>
    public AgentState State { get; } = new();

    // Forwarders. The console command, the SELF line, the speech tools and the benchmarks all read
    // these, and keeping them here is what lets the state move without touching any of that.
    public ConversationState Conv => State.Conv;
    /// <summary>The live tool registry — benchmarks invoke through it, never around it.</summary>
    public AiToolRegistry Registry => _registry;

    /// <summary>
    /// The one door every tool call goes through: the loop's, the curator's and the test harness's.
    /// </summary>
    public ToolDispatcher Dispatcher { get; }

    public ObservationQueue Queue { get; }

    /// <summary>
    /// This agent's background scripts — <c>null</c> until script mode is enabled.
    ///
    /// Lives on the session rather than in the system, for exactly the same reason as everything
    /// else here: the processes belong to the agent and must die together with it. This table does
    /// not touch the world itself — its processes reach the world through the same dispatcher as an
    /// ordinary turn.
    /// </summary>
    public Core.Scripting.ScriptProcessTable? Scripts { get; set; }

    /// <summary>Handle registry — per session, so names never leak between rounds.</summary>
    public Handles.EntityHandleRegistry Handles { get; } = new();

    /// <summary>
    /// Who has already been reminded this shift that there is a note about them.
    ///
    /// Lives on the session rather than in <see cref="AgentState"/> deliberately: <c>AgentState</c>
    /// goes into the snapshot and is restored, and a field here would change the snapshot schema
    /// just so that, after a restart mid-round, the reminder is not given a second time. One extra
    /// line is cheaper than a schema.
    ///
    /// No reset is needed: the session dies together with the round (<c>OnRoundCleanup</c> calls
    /// <c>ReleaseAll</c>), and this set with it. This comment exists precisely so that nobody adds a
    /// "forgotten" cleanup for it.
    ///
    /// Read and written from the main thread, from speech handlers; the lock is here in case that
    /// ever stops being true.
    /// </summary>
    private readonly HashSet<string> _notedPeople = new(StringComparer.Ordinal);

    /// <summary>
    /// Is this the first utterance from this person this shift. The caller then decides whether
    /// there is anything to remind them about.
    ///
    /// The name is remembered even when there is no note: otherwise a nameless chatterbox would
    /// cost a lock acquisition on the store for every line they say, whereas this way it is one per
    /// shift.
    /// </summary>
    public bool FirstUtteranceOf(string speaker)
    {
        lock (_notedPeople)
            return _notedPeople.Add(speaker);
    }

    public string? LastLawsDigest
    {
        get => State.LastLawsDigest;
        set => State.LastLawsDigest = value;
    }

    /// <summary>Turns that ended in prose and had to be delivered mechanically. Should stay near zero.</summary>
    public int UntooledReplies => State.UntooledReplies;

    /// <summary>
    /// Somebody asked for a review out of band (the <c>aiagent curate</c> console command).
    ///
    /// A flag the loop picks up at a turn boundary, never a second thread. The curator walks the
    /// same message list the loop appends to, so there can only ever be one owner of it, and that
    /// owner is the loop.
    /// </summary>
    public volatile bool CurateRequested;

    /// <summary>
    /// Text an operator injected through the debug API, for the next turn. Same rule as
    /// <see cref="CurateRequested"/>: asked for from outside, applied by the loop.
    /// </summary>
    public AgentInbox Inbox { get; }

    /// <summary>
    /// Released whenever something the agent should look at arrives — a radio line, speech, an
    /// announcement, an operator's message.
    ///
    /// Capacity one on purpose. A burst of chatter should start exactly one turn, and that turn's
    /// observation carries every line of it; releasing per line would queue up turns describing a
    /// conversation that has already moved on. The count also survives a turn: something that lands
    /// while the model is working is waited on for zero milliseconds afterwards, not slept past.
    /// </summary>
    public SemaphoreSlim Woken { get; } = new(0, 1);

    /// <summary>
    /// Wake the loop. Safe to call from any thread and as often as anything likes — a signal that
    /// is already pending is simply left pending.
    /// </summary>
    public void Wake()
    {
        try
        {
            if (Woken.CurrentCount == 0)
                Woken.Release();
        }
        catch (SemaphoreFullException)
        {
            // Two perception handlers released at once. The loop is awake either way, which is the
            // entire point of the call.
        }
        catch (ObjectDisposedException)
        {
            // The session is going away mid-round. Nothing left to wake.
        }
    }

    public CancellationTokenSource Cts { get; } = new();
    public Task Loop { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Write a snapshot of the conversation to disk. Wired up after the constructor — like
    /// <c>Arrived</c> on the observation queue, and for the same reason: the delegate closes over
    /// the system, which at the moment the session is built has nothing to say about it yet.
    ///
    /// Called ONLY from the loop, that is, from the agent's thread. All it needs is storage (its
    /// own files), an identifier (a constant) and the round number (a <c>volatile int</c> captured
    /// on the main thread). Not a single access to the world, or it would have to be marshalled and
    /// the whole point would be lost.
    /// </summary>
    public Action? Persist { get; set; }

    /// <summary>When the snapshot last hit disk. <c>Release</c> reads this to decide whether an emergency save is needed.</summary>
    public DateTime LastPersistedUtc { get; private set; } = DateTime.MinValue;

    private void SaveSnapshot()
    {
        if (Persist == null)
            return;

        try
        {
            Persist();
            LastPersistedUtc = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            // Don't fail the turn over disk. Silence isn't acceptable either: "the agent forgot the
            // shift" is the kind of thing noticed a day later and blamed on anything except an
            // unsaved file.
            _sawmill.Warning($"снапшот не сохранён: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Bumped by the owning system on every lifecycle change; marshalled calls check it.</summary>
    public int Generation;

    public AgentMode Mode
    {
        get => State.Mode;
        set => State.Mode = value;
    }

    // Diagnostics surfaced by `aiagent status`.
    public int Turns => State.Turns;
    public int ConsecutiveFailures => State.ConsecutiveFailures;
    public double LastCacheRatio { get; private set; }
    public string? LastError { get; private set; }

    public AgentSession(
        AgentBody body,
        ILlmClient llm,
        AiToolRegistry registry,
        ObservationQueue queue,
        AgentLoopOptions options,
        Func<bool, CancellationToken, Task<TurnPerception?>> buildObservation,
        Func<string, Task> announce,
        Func<string, string?, Task<bool>> speak,
        Func<Task<string?>>? curate,
        Func<(string SystemPrompt, string ToolsJson)> rebuildPrefix,
        CompactionOptions compaction,
        Journal journal,
        IAgentEventSink? sink,
        ISawmill sawmill)
    {
        Body = body;
        Journal = journal;
        _llm = llm;
        _registry = registry;
        Queue = queue;
        _options = options;
        _buildObservation = buildObservation;
        _announce = announce;
        _speak = speak;
        _curate = curate;
        _rebuildPrefix = rebuildPrefix;
        _sawmill = sawmill;

        Cache = new CacheMetrics(sawmill);
        Compactor = new Compactor(llm, compaction, Cache, sawmill, Journal);
        Dispatcher = new ToolDispatcher(registry, sawmill);
        Inbox = new AgentInbox(Locale.OperatorPrefix);
        _turn = new TurnRunner(llm, registry, Dispatcher, queue, State, Cache, Journal, speak, sawmill, Locale);

        // Here rather than in the field initializer: the conversation is built before this
        // constructor body runs (AgentState's field initializer builds it), so it cannot take the
        // sink as a constructor parameter the way every other collaborator here does. Attaching
        // before Start() means the loop's very first append is already reported.
        _sink = sink;
        if (sink != null)
            Conv.AttachSink(sink);
    }

    public void Start()
    {
        Loop = Task.Run(() => RunAsync(Cts.Token), Cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _sawmill.Info($"agent loop started for brain {Brain}");

        await DiscoverContextLimitAsync(ct).ConfigureAwait(false);

        var idleStreak = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var idle = idleStreak > 2;
                var wait = idle ? _options.TickSecondsIdle() : _options.TickSeconds();

                // A ceiling on the sleep, not a period.
                //
                // Anything pushed into the observation queue releases this, so being spoken to
                // starts a turn now rather than whenever the timer happened to land. Polling alone
                // made response time a coin flip across the whole interval, and the crew feels that
                // precisely when waiting is least acceptable: on a shout about a fire.
                //
                // A signal that arrived while the previous turn was still running is still sitting
                // in the semaphore, so this returns immediately and nothing is missed. Several lines
                // in the same instant collapse into one wake, and the observation carries them all —
                // which is the batching the old delay provided, without the latency it charged for
                // it.
                await Woken.WaitAsync(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

                // Claimed HERE, at the top of the body, and not at the end of the turn where
                // CurateRequested is picked up.
                //
                // On an idle station _buildObservation returns null and the loop `continue`s
                // without running a turn at all; force only kicks in after six such ticks, which
                // at tick_seconds_idle = 25 is up to 150 seconds. An operator's message sitting
                // there for two and a half minutes is not a debugger. Claiming here — and forcing
                // on it — means the very next tick carries it.
                //
                // It also has to be here for correctness: the previous turn's
                // finally { Conv.CloseTurn(); } has already run by this point, so nothing can land
                // between an assistant's tool_calls and their results.
                // Peeked, not claimed.
                //
                // Claiming here and building the observation afterwards lost the message outright
                // whenever the build returned null, and two of the three ways it can do that —
                // ai.enabled switched off, and a world paused because the last player left — do not
                // look at `force` at all. The text was already out of the inbox by then, so an
                // operator's message sent in either of those windows went nowhere and nothing
                // anywhere reported it. Observed live: a message typed into the debugger never
                // reached the agent.
                //
                // Only this loop ever claims, so nothing can take it in between; and a message that
                // arrives during the build is simply picked up by the Claim below.
                // A turn cut off by the step budget is unfinished business, not a decision.
                //
                // The model was mid-plan — the observed case was move_camera as the last allowed
                // call, so the eye was aimed and nothing was ever looked at or said — and the loop
                // then went back to waiting for a new observation. On a quiet station there is no
                // new observation, so the agent simply stopped, with the crew watching it do
                // nothing after being asked something. Forcing the next tick lets it carry on from
                // where it was cut, which is what a player would do.
                var unfinished = LastTurn?.Exit == TurnExit.BudgetExhausted;

                var force = idleStreak >= 6 || Inbox.HasPending || unfinished;
                var perception = await _buildObservation(force, ct).ConfigureAwait(false);

                if (perception == null)
                {
                    idleStreak++;
                    continue;
                }

                var pending = Inbox.Claim();

                // Merged into the one observation rather than appended as a second user message:
                // two adjacent user messages fabricate a turn boundary that TurnBoundaries() will
                // happily cut at, and strict providers reject the alternation outright.
                if (pending != null)
                {
                    perception = perception with
                    {
                        Text = pending + "\n\n" + perception.Text,
                        Forced = true,
                    };
                }

                idleStreak = 0;
                await RunTurnAsync(perception, ct).ConfigureAwait(false);

                // A turn closed by noop is also idle.
                //
                // The model explicitly said intervention is not needed; continuing to poll it at
                // full pace means paying for the same answer every few seconds. Count such a turn
                // the same as a tick with nothing at all to observe — after three in a row the loop
                // switches itself to tick_seconds_idle.
                //
                // This does not harm force: while the crew is talking, the observation is non-empty
                // and gets built regardless of idleStreak.
                if (LastTurn?.Exit == TurnExit.Idled)
                    idleStreak++;

                if (_notedDegraded)
                {
                    _notedDegraded = false;
                    _sawmill.Info($"агент вернулся в строй после {ConsecutiveFailures} отказов");
                }

                State.ConsecutiveFailures = 0;
                LastError = null;

                // The snapshot is written HERE, on the agent's thread, not once a minute from Update.
                //
                // The main thread used to do this: `Conv.Snapshot()` under a lock the agent held at
                // the same moment, then serializing the body (at 83k tokens that's hundreds of
                // kilobytes of JSON) and a blocking file write — all inside the tick. None of that
                // is in the frame here: the body belongs to the loop, serialization on this same
                // thread already happens on every model request, and the lock is contended by
                // nobody.
                //
                // After every turn, not once a minute: the cost dropped low enough that there is
                // nothing left to save by batching, and the loss on a crash shrank from a minute to
                // one turn.
                SaveSnapshot();
            }
            // ONLY our own cancellation, and this `when` is not decoration.
            //
            // `HttpClient.Timeout` throws `TaskCanceledException`, which INHERITS
            // `OperationCanceledException`. Without the filter, one request that hit
            // `ai.request_timeout` (180s) would come out here and kill the loop for the rest of the
            // round — bypassing the whole degraded mode below, written for exactly this case. And
            // the log would show no error at all: `LastError` = "cancelled", the same as for
            // ordinary carding. The crew would read that as "the AI got carded" and go looking for
            // it at the core.
            //
            // How close to the edge: the measured maximum turn duration in the 16.08 combat run was
            // 163.0s against a ceiling of 180. The margin was seventeen seconds, and in round 72 it
            // was not enough.
            //
            // Now a model timeout is an ordinary failure: it goes to the general handler, increments
            // ConsecutiveFailures and goes to backoff, like any other provider unavailability.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LastError = "cancelled";
                break;
            }
            catch (StaleGenerationException e)
            {
                // The AI was carded, killed, or the round restarted mid-call. Not an error — but
                // it must still be visible, because a generation mismatch caused by a bug looks
                // exactly like a legitimate one: the loop just stops, quietly, doing nothing.
                LastError = e.Message;
                _sawmill.Warning($"agent loop exiting: {e.Message}");
                break;
            }
            catch (Exception e)
            {
                State.ConsecutiveFailures++;
                LastError = $"{e.GetType().Name}: {e.Message}";
                _sawmill.Error($"agent turn failed ({ConsecutiveFailures}): {LastError}");

                // The failure threshold no longer kills the loop.
                //
                // A `break` used to sit here, and that meant three to five minutes of model
                // unavailability turned the AI off for the rest of the round. There was no way to
                // bring it back: no watchdog, and the core stayed claimed. Nothing in the game
                // showed any sign of it — the crew read the silence as "the AI got carded" and went
                // looking for it at the core.
                //
                // Now the threshold only puts the agent into degraded mode: it keeps trying once a
                // minute and comes back on its own once the provider revives. There is no core
                // spinning at that interval, which is what the `break` was there to prevent.
                var degraded = ConsecutiveFailures >= _options.MaxConsecutiveFailures();

                if (degraded && !_notedDegraded)
                {
                    _notedDegraded = true;
                    _sawmill.Error(
                        $"агент в разреженном режиме после {ConsecutiveFailures} отказов подряд, " +
                        $"продолжит пробовать раз в {DegradedRetryMs / 1000}с; последняя ошибка: {LastError}");
                }

                // Exponential back-off, capped. A dead endpoint must not spin a core all round.
                var backoff = degraded
                    ? DegradedRetryMs
                    : Math.Min(30_000, 1000 * (int)Math.Pow(2, Math.Min(ConsecutiveFailures, 5)));
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _sawmill.Info($"agent loop ended for brain {Brain} after {Turns} turns (reason: {LastError ?? "cancelled"})");
    }

    /// <summary>
    /// Ask the model server how big its context actually is.
    ///
    /// Until this ran, <c>ai.compact_high</c> was a guessed constant checked against nothing:
    /// reconfigure llama-server to a smaller window and the agent would grow happily past it and
    /// start collecting bare HTTP errors with no hint of the cause anywhere.
    /// </summary>
    private async Task DiscoverContextLimitAsync(CancellationToken ct)
    {
        try
        {
            ContextLimit = await _llm.GetContextSizeAsync(ct).ConfigureAwait(false) ?? 0;

            if (ContextLimit > 0)
                _sawmill.Info($"окно контекста модели: {ContextLimit}т");
            else
                _sawmill.Warning("сервер модели не сообщил n_ctx — пороги компакции сверять не с чем");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не удалось прочитать n_ctx: {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task RunTurnAsync(TurnPerception perception, CancellationToken ct)
    {
        Conv.AppendUser(perception.Text);
        State.Turns++;

        // The turn's input, verbatim. It carries the SELF line — where the eye is, whether the core
        // has power — which is the first thing anyone asks when the agent behaves oddly, and until
        // now the only copy of it lived inside the request nobody could see.
        _sawmill.Debug($"turn {Turns} <- {Trim(perception.Text, 400)}");

        // The turn closes on the way out, whatever the way out is.
        //
        // CloseTurn used to sit on the happy path only, so a cancellation inside the tool-result
        // loop — shutdown, carding, death, any of which arrive mid-turn — left the body ending in
        // `assistant{tool_calls:[1,2,3]}, tool(1)`. That is a protocol error the server rejects
        // wholesale, not per message. It survived only because Release → Save → Repair-on-load
        // happened to paper over it on the one path that was taken; nothing made it true.
        try
        {
            var outcome = await _turn.RunAsync(perception, _options.MaxToolCallsPerTurn(), ct)
                .ConfigureAwait(false);

            LastCacheRatio = outcome.LastCacheRatio;
            LastTurn = outcome;
        }
        finally
        {
            // Any call left dangling — by the step budget or by an exception — gets a synthetic
            // result, or the next request is rejected for having an assistant tool_calls with no
            // matching tool message.
            Conv.CloseTurn();

            // One statistics sample per turn, from the one place a turn always passes through
            // however it ended. Counters are not diffed individually: they are `++` on
            // auto-properties across four files, and six publishing setters would be six new
            // chances to forget, feeding a stream nobody reads as a delta.
            _sink?.Stats(AgentDebugState.Stats(this));
        }

        // Zone 2 is consumed by the turn that sent it.
        //
        // The compaction note was set and never cleared, so it rode every subsequent request for the
        // rest of the round. Because it always sits LAST, after the body, each new observation
        // pushes it along — which means the prompt diverges from the previous one at the note's
        // position and the server re-computes it, plus everything after, every single turn. A
        // permanent cache tax of the note's own length, silently paid by the one field designed to
        // be temporary.
        Conv.VolatileTail = null;

        // Compaction sits here, at a turn boundary, precisely because that is the only place the
        // body may be cut without orphaning a tool result from its parent call.
        var compacted = false;

        if (Compactor.ShouldCompact(State))
        {
            Mode = AgentMode.Review;
            try
            {
                var hooks = new CompactionHooks
                {
                    Announce = _announce,
                    RebuildPrefix = _rebuildPrefix,
                    Curate = _curate,
                };

                compacted = await Compactor
                    .CompactAsync(State, _registry.WireSchemas(), hooks, perception.RoundStamp, ct)
                    .ConfigureAwait(false);

                if (compacted)
                {
                    Journal.Write("compaction", new Dictionary<string, object?>
                    {
                        ["turn"] = Turns,
                        ["n"] = State.Compactions,
                        ["messages_after"] = Conv.Body.Count,
                        ["prefix_hash"] = Conv.PrefixHash,
                        ["summary_chars"] = State.LastSummary?.Length ?? 0,
                    });
                }
            }
            finally
            {
                Mode = State.ModeBeforeReview;
            }
        }

        // A review asked for from the console, honoured here rather than on its own thread. Skipped
        // when the ritual just ran one anyway — step 1 of a compaction IS the review.
        if (CurateRequested)
        {
            CurateRequested = false;

            if (!compacted)
                await RunReviewAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run the curator over the live conversation, on the loop's own thread.
    ///
    /// The mode is restored to <see cref="AgentState.ModeBeforeReview"/> rather than to
    /// <see cref="AgentMode.Core"/>: an AI carded while the review was running must come back
    /// carded, or the device gate silently hands the station's equipment to an agent sitting in
    /// somebody's pocket.
    /// </summary>
    private async Task RunReviewAsync(CancellationToken ct)
    {
        if (_curate == null)
            return;

        Mode = AgentMode.Review;
        try
        {
            var report = await _curate().ConfigureAwait(false);

            // Unlike the ritual, there was no compaction here: the body ends with a tool result or
            // a model reply, and a separate user message is legitimate. Zone 0 stays as it was
            // until the next prefix rebuild — exactly as after any other write.
            if (!string.IsNullOrWhiteSpace(report))
                Conv.AppendUserOrMerge(report!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _sawmill.Error($"ревью по запросу не отработало: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Mode = State.ModeBeforeReview;
        }
    }

    /// <summary>Keep a log line to one line — device_ui payloads are long and the point is the shape.</summary>
    private static string Trim(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private void RememberSpeech(string? text) => State.RememberSpeech(text);

    /// <summary>Has this exact line gone out in the last few turns? Public so the speech tools can refuse it.</summary>
    public bool AlreadySaid(string text) => State.AlreadySaid(text);

    /// <summary>One string argument out of a raw tool-call payload, or null if it is not there.</summary>
    private static string? ArgumentValue(string argsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Cts.Cancel();
        Cts.Dispose();
    }
}
