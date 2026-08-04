using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;

namespace Content.Server.AiAgent.Threading;

/// <summary>
/// Thrown when a marshalled call arrives after the world it targeted has moved on — the AI was
/// carded, killed, or the round restarted while an LLM call was in flight. Callers treat it as a
/// clean exit, not an error.
/// </summary>
public sealed class StaleGenerationException : Exception
{
    public StaleGenerationException(int expected, int actual)
        : base($"agent generation {expected} is stale (current {actual})")
    {
    }
}

/// <summary>
/// The one and only bridge between the agent's background loop and the game's main thread.
///
/// Everything that touches <c>IEntityManager</c> goes through <see cref="RunAsync{T}"/>, which
/// posts the delegate via <see cref="ITaskManager.RunOnMainThread"/> and awaits a
/// <see cref="TaskCompletionSource{T}"/>.
///
/// Two details here are load-bearing and non-obvious:
///
/// 1. <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> is mandatory. Without it,
///    the agent loop's code after the <c>await</c> — which is typically an HTTP call lasting
///    seconds — runs inline on the game thread inside <c>ProcessPendingTasks</c>. That is the
///    single most likely way to accidentally stall the server, and it presents as a mysterious
///    tick-rate collapse rather than as an error.
///
/// 2. The generation check happens inside the posted delegate, on the main thread. Checking it on
///    the agent thread before posting would be a race: the AI can die between the check and the
///    delegate actually running.
/// </summary>
public sealed class MainThreadDispatcher
{
    private readonly ITaskManager _tasks;
    private readonly ISawmill _sawmill;
    private readonly int _mainThreadId;

    /// <summary>Per-call budget in milliseconds; exceeding it logs a warning.</summary>
    public double BudgetMs { get; set; }

    /// <summary>Default ceiling on how long a marshalled call may take to come back.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // Diagnostics, read by the `aiagent status` console command.
    public long Calls { get; private set; }
    public double MaxObservedMs { get; private set; }
    public long BudgetOverruns { get; private set; }

    /// <summary>The single worst offender so far, for `aiagent status`.</summary>
    public string Slowest { get; private set; } = "—";
    public double SlowestMs { get; private set; }

    /// <summary>Must be constructed on the main thread — that is how it learns which thread that is.</summary>
    public MainThreadDispatcher(ITaskManager tasks, ISawmill sawmill, double budgetMs)
    {
        _tasks = tasks;
        _sawmill = sawmill;
        _mainThreadId = Environment.CurrentManagedThreadId;
        BudgetMs = budgetMs;
    }

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>
    /// Guard for code that must never run off the game thread. Every <c>IGameFacade</c> body
    /// starts with this, so a mistake surfaces as a loud assert rather than as memory corruption
    /// or a heisenbug three hours into a round.
    /// </summary>
    public void AssertMainThread(string what)
    {
        if (!IsMainThread)
            throw new InvalidOperationException(
                $"{what} touched the entity manager off the main thread (tid {Environment.CurrentManagedThreadId}, main {_mainThreadId})");
    }

    /// <summary>Run <paramref name="fn"/> on the main thread and await its result.</summary>
    /// <param name="generationSource">
    /// Reads the session's current generation. Evaluated on the main thread immediately before
    /// <paramref name="fn"/>; a mismatch aborts without touching the world.
    /// </param>
    public async Task<T> RunAsync<T>(
        Func<T> fn,
        int generation,
        Func<int> generationSource,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string what = "?")
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _tasks.RunOnMainThread(() =>
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var current = generationSource();
                if (current != generation)
                {
                    tcs.TrySetException(new StaleGenerationException(generation, current));
                    return;
                }

                tcs.TrySetResult(fn());
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
            finally
            {
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Calls++;
                if (ms > MaxObservedMs)
                    MaxObservedMs = ms;
                if (ms > BudgetMs)
                {
                    BudgetOverruns++;

                    // Name the operation. An unnamed "call took 315ms" is unactionable — you
                    // cannot tell a vision sweep from a chat message from a records scan.
                    _sawmill.Warning($"main-thread call '{what}' took {ms:F1}ms (budget {BudgetMs:F1}ms)");

                    if (ms > SlowestMs)
                    {
                        SlowestMs = ms;
                        Slowest = what;
                    }
                }
            }
        });

        return await tcs.Task.WaitAsync(timeout ?? CallTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>Void-returning convenience overload.</summary>
    public Task RunAsync(Action act, int generation, Func<int> generationSource, CancellationToken ct,
        TimeSpan? timeout = null, string what = "?")
    {
        return RunAsync(() => { act(); return true; }, generation, generationSource, ct, timeout, what);
    }
}
