using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Threading;
using Content.Server.AiAgent.Tools;

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
    private readonly Func<bool, CancellationToken, Task<string?>> _buildObservation;
    private readonly Func<string, Task> _announce;
    private readonly Func<string, string?, Task<bool>> _speak;
    private readonly Func<Task>? _curate;
    private readonly Func<(string SystemPrompt, string ToolsJson)> _rebuildPrefix;

    /// <summary>Context compaction, wired in phase 3.</summary>
    public Compactor Compactor { get; }

    /// <summary>Prefix-cache watchdog. A broken cache is silent; this is what makes it loud.</summary>
    public CacheMetrics Cache { get; }

    /// <summary>Machine-readable event log for the acceptance run. <see cref="Journal.Disabled"/> when off.</summary>
    public Journal Journal { get; init; } = Journal.Disabled;

    /// <summary>
    /// The model server's real context window, asked for once when the loop starts.
    ///
    /// Zero until it answers, or if it cannot. Compaction thresholds are clamped against it, so a
    /// server reconfigured to a smaller window does not let the agent sail past it into bare HTTP
    /// errors with nothing to say why.
    /// </summary>
    public int ContextLimit { get; private set; }

    public EntityUid Brain { get; }
    public ConversationState Conv { get; } = new();
    /// <summary>The live tool registry — benchmarks invoke through it, never around it.</summary>
    public AiToolRegistry Registry => _registry;

    /// <summary>
    /// The one door every tool call goes through: the loop's, the curator's and the test harness's.
    /// </summary>
    public ToolDispatcher Dispatcher { get; }

    public ObservationQueue Queue { get; }

    /// <summary>Handle registry — per session, so names never leak between rounds.</summary>
    public Handles.EntityHandleRegistry Handles { get; } = new();

    /// <summary>
    /// Channel of the last radio line in the observation that opened this turn, or null if the
    /// turn heard no radio. Set by the observation builder while it drains the queue, because that
    /// is the only place the raw <see cref="Perception.Observation"/> list still exists.
    /// </summary>
    public string? HeardOnChannel { get; set; }

    /// <summary>Whether the observation that opened this turn contained speech near the core.</summary>
    public bool HeardSpeech { get; set; }

    /// <summary>
    /// One-line rendering of the laws as of the last turn, for spotting a rewrite.
    ///
    /// Polled rather than subscribed because upstream raises nothing on the path that matters: the
    /// law board and the upload console reach <c>NotifyLawsChanged</c>, which is a virtual method,
    /// not an event. <c>SiliconLawBoundComponent.Version</c> is no use either — it only increments
    /// for entities with an <c>ActorComponent</c>, and this brain has none. Comparing the rendered
    /// lawset costs one string per turn and catches every path, including the ion storm.
    /// </summary>
    public string? LastLawsDigest { get; set; }

    /// <summary>Turns that ended in prose and had to be delivered mechanically. Should stay near zero.</summary>
    public int UntooledReplies { get; private set; }

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

    private AgentMode _mode = AgentMode.Core;

    public AgentMode Mode
    {
        get => _mode;
        set
        {
            if (value != AgentMode.Review)
                _modeBeforeReview = value;
            _mode = value;
        }
    }

    // Diagnostics surfaced by `aiagent status`.
    public int Turns { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public double LastCacheRatio { get; private set; }
    public string? LastError { get; private set; }

    public AgentSession(
        EntityUid brain,
        ILlmClient llm,
        AiToolRegistry registry,
        ObservationQueue queue,
        AgentLoopOptions options,
        Func<bool, CancellationToken, Task<string?>> buildObservation,
        Func<string, Task> announce,
        Func<string, string?, Task<bool>> speak,
        Func<Task>? curate,
        Func<(string SystemPrompt, string ToolsJson)> rebuildPrefix,
        CompactionOptions compaction,
        ISawmill sawmill)
    {
        Brain = brain;
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
        Compactor = new Compactor(llm, compaction, sawmill);
        Dispatcher = new ToolDispatcher(registry, sawmill);
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
                var observation = await _buildObservation(force, ct).ConfigureAwait(false);

                if (observation == null && !force)
                {
                    idleStreak++;
                    continue;
                }

                idleStreak = 0;
                await RunTurnAsync(observation ?? string.Empty, ct).ConfigureAwait(false);
                ConsecutiveFailures = 0;
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
                ConsecutiveFailures++;
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

    private async Task RunTurnAsync(string observation, CancellationToken ct)
    {
        Conv.AppendUser(observation);
        Turns++;

        // The turn's input, verbatim. It carries the SELF line — where the eye is, whether the core
        // has power — which is the first thing anyone asks when the agent behaves oddly, and until
        // now the only copy of it lived inside the request nobody could see.
        _sawmill.Debug($"turn {Turns} <- {Trim(observation, 400)}");

        // The turn closes on the way out, whatever the way out is.
        //
        // CloseTurn used to sit on the happy path only, so a cancellation inside the tool-result
        // loop — shutdown, carding, death, any of which arrive mid-turn — left the body ending in
        // `assistant{tool_calls:[1,2,3]}, tool(1)`. That is a protocol error the server rejects
        // wholesale, not per message. It survived only because Release → Save → Repair-on-load
        // happened to paper over it on the one path that was taken; nothing made it true.
        try
        {
            await RunStepsAsync(_options.MaxToolCallsPerTurn(), ct).ConfigureAwait(false);
        }
        finally
        {
            // Any call left dangling — by the step budget or by an exception — gets a synthetic
            // result, or the next request is rejected for having an assistant tool_calls with no
            // matching tool message.
            Conv.CloseTurn();
        }

        // Compaction sits here, at a turn boundary, precisely because that is the only place the
        // body may be cut without orphaning a tool result from its parent call.
        var compacted = false;

        if (Compactor.ShouldCompact(Conv))
        {
            Mode = AgentMode.Review;
            try
            {
                compacted = await Compactor
                    .CompactAsync(Conv, _registry.WireSchemas(), _curate, _announce, _rebuildPrefix, ct)
                    .ConfigureAwait(false);

                if (compacted)
                {
                    Cache.SetExpectedPrefix(Conv.PrefixHash);
                    Cache.ExpectMiss = true;

                    Journal.Write("compaction", new Dictionary<string, object?>
                    {
                        ["turn"] = Turns,
                        ["n"] = Compactor.Compactions,
                        ["messages_after"] = Conv.Body.Count,
                        ["prefix_hash"] = Conv.PrefixHash,
                        ["summary_chars"] = Compactor.LastSummary?.Length ?? 0,
                    });
                }
            }
            finally
            {
                Mode = _modeBeforeReview;
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
    /// The model-facing part of one turn: call, dispatch, repeat until it stops or the budget runs
    /// out, then deliver anything the crew was owed and never heard.
    /// </summary>
    private async Task RunStepsAsync(int maxSteps, CancellationToken ct)
    {
        // A turn that heard nobody is the agent musing to itself; a turn that was addressed owes
        // an answer. The distinction is what keeps the recovery below from broadcasting idle
        // thoughts over the radio every eight seconds.
        var addressed = HeardOnChannel != null || HeardSpeech;
        var nudged = false;
        string? undelivered = null;

        // Set the moment a say/radio/announce actually lands. Everything below hangs off it: once
        // the crew has heard something, trailing prose is the model tidying up its own turn
        // ("Всё.", "Я уже ответила"), not an unspoken reply. Treating that as unspoken is how the
        // first cut of this recovery broadcast every answer twice.
        var spoke = false;

        for (var step = 0; step < maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _llm
                .ChatAsync(Conv.Build(), _registry.WireSchemas(), ct)
                .ConfigureAwait(false);

            Conv.LastPromptTokens = response.PromptTokens;
            Conv.Calibrate(response.PromptTokens);
            LastCacheRatio = response.CacheRatio;

            Cache.Record(response.PromptTokens, response.CachedTokens, Conv.PrefixHash, Conv.SystemPrompt);
            Conv.AppendAssistant(response);

            _sawmill.Info($"turn {Turns} step {step}  " +
                          Cache.Format(response.PromptTokens, response.CachedTokens,
                              response.CompletionTokens, response.DurationSeconds,
                              response.ToolCalls.Count, Mode.ToString()));

            Journal.Write("step", new Dictionary<string, object?>
            {
                ["turn"] = Turns,
                ["step"] = step,
                ["prompt_tokens"] = response.PromptTokens,
                ["cached_tokens"] = response.CachedTokens,
                ["completion_tokens"] = response.CompletionTokens,
                ["seconds"] = Math.Round(response.DurationSeconds, 2),
                ["tools"] = response.ToolCalls.Count,
                ["mode"] = Mode.ToString(),
            });

            if (!string.IsNullOrWhiteSpace(response.Content))
                _sawmill.Debug($"thought: {response.Content!.Trim()}");

            if (response.ToolCalls.Count == 0)
            {
                var prose = response.Content?.Trim();

                // The failure this guards against: the model composes a perfectly good reply as
                // plain text and stops, believing it has answered. Nothing reaches the station and
                // the crew sees a dead AI. Prompting alone does not fix it reliably, so the loop
                // says so out loud and gives it one more step to say it properly.
                if (!nudged && addressed && !spoke && !string.IsNullOrEmpty(prose))
                {
                    nudged = true;
                    undelivered = prose;
                    Conv.AppendUser(
                        "NOTIFY Этого никто не услышал: обычный текст не доходит до экипажа. " +
                        "Если хочешь ответить — вызови инструмент say или radio.");
                    continue;
                }

                // Still prose after being told. Rather than let the crew face a silent AI, deliver
                // it on the channel the request arrived on. Loud in the log on purpose: this is a
                // model failure being papered over, and it must be countable.
                undelivered = addressed && !spoke && !string.IsNullOrEmpty(prose) ? prose : null;
                break;
            }

            undelivered = null;

            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                var gate = Mode == AgentMode.Review ? DispatchGate.NoGameActions : DispatchGate.None;
                var invocation = await Dispatcher.InvokeAsync(call, gate, ct).ConfigureAwait(false);
                var result = invocation.Result;

                // Every result carries whatever arrived while the model was mid-turn. Reporting a
                // bare count is not enough: a bot that answers a question it never heard reads as
                // broken, and "wait, not that one" has to be actionable.
                result.Unread = Queue.PeekUnread(6);
                Conv.AppendToolResult(call.Id, result.ToJson());

                // Without this the log shows "tools=1" and nothing else: which tool ran, with what
                // arguments, and which gate refused are all invisible. That turns any behavioural
                // question — why did it not move the eye, why did it give up — into guesswork.
                _sawmill.Debug(
                    $"  {call.Function.Name}({Trim(call.Function.Arguments)}) -> " +
                    (result.Ok ? "ok " + Trim(result.EffectJson(), 1200) : $"{result.Error}: {result.Detail}"));

                Journal.Write("tool", new Dictionary<string, object?>
                {
                    ["turn"] = Turns,
                    ["name"] = call.Function.Name,
                    ["args"] = Trim(call.Function.Arguments, 400),
                    ["ok"] = result.Ok,
                    ["error"] = result.Error,
                    ["detail"] = result.Ok ? null : Trim(result.Detail, 200),

                    // The one consumer of via_skill. It is declared on every game-facing tool and
                    // was read by nothing at all — fifteen parameters sitting in the frozen prefix
                    // referring to a concept the prompt never mentioned. Recorded here it becomes
                    // what it was meant to be: mechanical attribution, so which skills actually
                    // route is a question with an answer instead of a guess.
                    ["via_skill"] = ArgumentValue(call.Function.Arguments, "via_skill"),
                });

                if (result.Ok && invocation.Tool is { Speech: true } speech)
                {
                    spoke = true;
                    RememberSpeech(speech.SpokenText?.Invoke(invocation.Args));
                }
            }
        }

        // Repeating itself on the radio is worse than saying nothing: the crew reads it as a stuck
        // machine, and it is the failure this model reaches for whenever it has nothing to add.
        if (undelivered != null && AlreadySaid(undelivered))
        {
            _sawmill.Debug($"проза повторяет уже сказанное, не доставляю: {undelivered}");
            undelivered = null;
        }

        if (undelivered != null)
        {
            UntooledReplies++;
            RememberSpeech(undelivered);

            // Log after the attempt, not before: the delivery can decline (ai.speak_untooled_text
            // off, dry run, AI no longer in play), and a line claiming a broadcast that never
            // happened is worse than no line at all.
            var delivered = await _speak(undelivered, HeardOnChannel).ConfigureAwait(false);

            _sawmill.Warning(delivered
                ? $"модель ответила текстом без say/radio даже после напоминания — доставлено " +
                  $"вручную ({(HeardOnChannel is { } ch ? "radio " + ch : "say")}): {undelivered}"
                : $"модель ответила текстом без say/radio даже после напоминания; доставка выключена, " +
                  $"экипаж этого не услышал: {undelivered}");

            Journal.Write("untooled", new Dictionary<string, object?>
            {
                ["turn"] = Turns,
                ["channel"] = HeardOnChannel,
                ["delivered"] = delivered,
                ["text"] = Trim(undelivered, 400),
            });
        }
    }

    /// <summary>
    /// Run the curator over the live conversation, on the loop's own thread.
    ///
    /// The mode is restored to <see cref="_modeBeforeReview"/> rather than to
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
            Mode = _modeBeforeReview;
        }
    }

    /// <summary>Mode to return to after a review; carding during a review must not be forgotten.</summary>
    private AgentMode _modeBeforeReview = AgentMode.Core;

    /// <summary>
    /// The last few things the agent said, normalised, so it does not broadcast them again.
    ///
    /// This model fills silence: left alone it emits "Жду указаний" every turn, and the recovery
    /// path below would dutifully put each copy on the radio. Suppressing an exact repeat is a
    /// mechanical fix for a mechanical habit — no prompt wording survives contact with it.
    /// </summary>
    private readonly Queue<string> _recentSpeech = new();

    /// <summary>Keep a log line to one line — device_ui payloads are long and the point is the shape.</summary>
    private static string Trim(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string Normalise(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private void RememberSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        _recentSpeech.Enqueue(Normalise(text));
        while (_recentSpeech.Count > 4)
            _recentSpeech.Dequeue();
    }

    /// <summary>Has this exact line gone out in the last few turns? Public so the speech tools can refuse it.</summary>
    public bool AlreadySaid(string text) => _recentSpeech.Contains(Normalise(text));

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
