using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Threading;
using Content.Server.AiAgent.Tools;
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
    private readonly Func<Task>? _curate;
    private readonly Func<(string SystemPrompt, string ToolsJson)> _rebuildPrefix;

    private readonly TurnRunner _turn;

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

    public EntityUid Brain { get; }

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

    /// <summary>Handle registry — per session, so names never leak between rounds.</summary>
    public Handles.EntityHandleRegistry Handles { get; } = new();

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
    public CancellationTokenSource Cts { get; } = new();
    public Task Loop { get; private set; } = Task.CompletedTask;

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
        EntityUid brain,
        ILlmClient llm,
        AiToolRegistry registry,
        ObservationQueue queue,
        AgentLoopOptions options,
        Func<bool, CancellationToken, Task<TurnPerception?>> buildObservation,
        Func<string, Task> announce,
        Func<string, string?, Task<bool>> speak,
        Func<Task>? curate,
        Func<(string SystemPrompt, string ToolsJson)> rebuildPrefix,
        CompactionOptions compaction,
        Journal journal,
        IAgentEventSink? sink,
        ISawmill sawmill)
    {
        Brain = brain;
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
        Compactor = new Compactor(llm, compaction, Cache, sawmill);
        Dispatcher = new ToolDispatcher(registry, sawmill);
        _turn = new TurnRunner(llm, registry, Dispatcher, queue, State, Cache, Journal, speak, sawmill);

        // Here rather than in the field initializer: the conversation is built before this
        // constructor body runs (AgentState's field initializer builds it), so it cannot take the
        // sink as a constructor parameter the way every other collaborator here does. Attaching
        // before Start() means the loop's very first append is already reported.
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
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

                // Force a turn occasionally even when nothing was heard, so the agent can act on
                // its own initiative rather than only ever reacting to being spoken to.
                var force = idleStreak >= 6;
                var perception = await _buildObservation(force, ct).ConfigureAwait(false);

                if (perception == null)
                {
                    idleStreak++;
                    continue;
                }

                idleStreak = 0;
                await RunTurnAsync(perception, ct).ConfigureAwait(false);
                State.ConsecutiveFailures = 0;
                LastError = null;
            }
            catch (OperationCanceledException)
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

                if (ConsecutiveFailures >= _options.MaxConsecutiveFailures())
                {
                    _sawmill.Error(
                        $"agent disabled after {ConsecutiveFailures} consecutive failures; last error: {LastError}");
                    break;
                }

                // Exponential back-off, capped. A dead endpoint must not spin a core all round.
                var backoff = Math.Min(30_000, 1000 * (int)Math.Pow(2, Math.Min(ConsecutiveFailures, 5)));
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
            await _curate().ConfigureAwait(false);
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
