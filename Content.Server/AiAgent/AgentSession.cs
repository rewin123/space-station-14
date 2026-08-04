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

    Paused,
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

    public EntityUid Brain { get; }
    public ConversationState Conv { get; } = new();
    public ObservationQueue Queue { get; }
    public CancellationTokenSource Cts { get; } = new();
    public Task Loop { get; private set; } = Task.CompletedTask;

    /// <summary>Bumped by the owning system on every lifecycle change; marshalled calls check it.</summary>
    public int Generation;

    public AgentMode Mode { get; set; } = AgentMode.Core;

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
        ISawmill sawmill)
    {
        Brain = brain;
        _llm = llm;
        _registry = registry;
        Queue = queue;
        _options = options;
        _buildObservation = buildObservation;
        _sawmill = sawmill;
    }

    public void Start()
    {
        Loop = Task.Run(() => RunAsync(Cts.Token), Cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _sawmill.Info($"agent loop started for brain {Brain}");
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

    private async Task RunTurnAsync(string observation, CancellationToken ct)
    {
        Conv.AppendUser(observation);
        Turns++;

        var maxSteps = _options.MaxToolCallsPerTurn();

        for (var step = 0; step < maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _llm
                .ChatAsync(Conv.Build(), _registry.WireSchemas(), ct)
                .ConfigureAwait(false);

            Conv.LastPromptTokens = response.PromptTokens;
            LastCacheRatio = response.CacheRatio;
            Conv.AppendAssistant(response);

            _sawmill.Info(
                $"turn {Turns} step {step}  prompt {response.PromptTokens}t " +
                $"(cache {response.CachedTokens}, {response.CacheRatio * 100:F1}%)  " +
                $"out {response.CompletionTokens}t  {response.DurationSeconds:F1}s  " +
                $"tools={response.ToolCalls.Count}  mode={Mode}");

            if (!string.IsNullOrWhiteSpace(response.Content))
                _sawmill.Debug($"thought: {response.Content!.Trim()}");

            if (response.ToolCalls.Count == 0)
                break;

            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var result = await InvokeAsync(call, ct).ConfigureAwait(false);

                // Every result carries whatever arrived while the model was mid-turn. Reporting a
                // bare count is not enough: a bot that answers a question it never heard reads as
                // broken, and "wait, not that one" has to be actionable.
                result.Unread = Queue.PeekUnread(6);
                Conv.AppendToolResult(call.Id, result.ToJson());
            }
        }

        // Any call left dangling by the step budget gets a synthetic result, or the next request
        // is rejected wholesale for having an assistant tool_calls with no matching tool message.
        Conv.CloseTurn();
    }

    private async Task<ToolResult> InvokeAsync(ToolCallDto call, CancellationToken ct)
    {
        var name = call.Function.Name;

        if (!_registry.TryGet(name, out var tool))
        {
            return ToolResult.Fail(
                ToolError.UnknownTool,
                $"no tool named '{name}'",
                retry: "other_target",
                alternatives: _registry.Nearest(name));
        }

        if (Mode == AgentMode.Review && tool.GameAction)
            return ToolResult.Fail(ToolError.ReviewMode, "acting on the station is disabled during review");

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments)
                ? "{}"
                : call.Function.Arguments);
            args = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return ToolResult.Fail(ToolError.BadArgs, $"{name}: arguments are not valid JSON ({e.Message})");
        }

        try
        {
            return await tool.Handler(args, ct).ConfigureAwait(false);
        }
        catch (StaleGenerationException)
        {
            return ToolResult.Fail(ToolError.Dead, "the AI is no longer in play");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return ToolResult.Fail(ToolError.Timeout, $"{name} did not complete in time", retry: "later");
        }
        catch (Exception e)
        {
            _sawmill.Error($"tool {name} threw: {e}");
            return ToolResult.FromException(name, e);
        }
    }

    public void Dispose()
    {
        Cts.Cancel();
        Cts.Dispose();
    }
}
