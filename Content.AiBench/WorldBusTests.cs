using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.Server.AiAgent.Threading;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The world bus: budget, priorities, progression, and which thread all of this ends up on.
///
/// <para>
/// Assertions here rely on COUNTERS wherever possible, rather than on milliseconds. The reasoning
/// isn't mine — <see cref="LookCostTests"/> already explains why: milliseconds on the build machine
/// measure hardware and are noisy from a cold JIT, a neighboring process, and garbage collection,
/// while the number of slices and the thread id aren't noisy at all.
/// </para>
/// </summary>
[TestFixture]
public sealed class WorldBusTests
{
    /// <summary>
    /// A job for a given number of slices, which counts where it was executed.
    /// </summary>
    private sealed class CountingJob : IWorldJob
    {
        private readonly int _slices;
        private readonly TaskCompletionSource<int> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountingJob(string what, WorldPriority priority, int slices)
        {
            What = what;
            Priority = priority;
            _slices = slices;
        }

        public string What { get; }
        public WorldPriority Priority { get; }
        public int Steps { get; private set; }
        public readonly List<int> StepThreadIds = new();

        public Task<int> Task => _tcs.Task;

        public bool Step(JobBudget budget)
        {
            Steps++;
            StepThreadIds.Add(Environment.CurrentManagedThreadId);

            // Exhaust the frame budget fully — this isn't a simulated load, it's a way to make the
            // test deterministic. The pump, having handed out a slice, immediately takes the unfinished
            // request back and keeps spinning it WHILE the budget isn't exhausted — that's intentional.
            // So a job that does nothing would burn through all its slices in a single frame, and
            // checking for "spread across frames" on it would be pointless: the first version of this
            // test missed exactly that way.
            //
            // Waiting on the budget itself, rather than on fixed milliseconds, gives exactly one slice
            // per frame on any machine, with no threshold that needs tuning.
            while (!budget.Exhausted)
            {
            }

            return Steps >= _slices;
        }

        public void Complete() => _tcs.TrySetResult(Steps);
        public void Fail(Exception e) => _tcs.TrySetException(e);
    }

    /// <summary>
    /// <b>The most important test in this file.</b>
    ///
    /// <para>
    /// The continuation after <c>await</c> must move off the game thread. This property rests on
    /// <c>TaskCreationOptions.RunContinuationsAsynchronously</c> on the TCS inside
    /// <c>AtomicJob</c>, and until now it wasn't covered by a single test — even though it's the
    /// one line standing between the server and a multi-second HTTP call to the model executing
    /// right in the tick.
    /// </para>
    /// <para>
    /// Under the bus this property became MORE critical than it was: <c>Complete()</c> is now called
    /// from the pump, which lives inside <c>TickUpdate</c>. Previously an inline continuation would at
    /// least have landed in <c>ProcessPendingTasks</c> before the systems update; now it would land in
    /// the middle of their traversal.
    /// </para>
    /// </summary>
    [Test]
    public async Task ContinuationsDoNotRunOnTheGameThread()
    {
        await using var w = await AiWorld.Create();

        var mainThreadId = await w.Read(() => Environment.CurrentManagedThreadId);

        // A real tool through a real dispatcher: we're checking the production path, not a mock.
        var task = w.System.InvokeToolForTest(w.Brain, "noop", "{\"reason\":\"тест потока\"}");

        int continuationThreadId = 0;
        var observed = task.ContinueWith(_ =>
        {
            continuationThreadId = Environment.CurrentManagedThreadId;
        }, TaskContinuationOptions.ExecuteSynchronously);

        await PoolManager.WaitUntil(w.Pair.Server, () => observed.IsCompleted, maxTicks: 600);
        await observed;

        Assert.That(continuationThreadId, Is.Not.Zero, "продолжение не выполнилось");
        Assert.That(continuationThreadId, Is.Not.EqualTo(mainThreadId),
            "продолжение выполнилось на игровом потоке — значит RunContinuationsAsynchronously " +
            "потеряно, и следующий за await HTTP-вызов к модели встанет прямо в тик");
    }

    /// <summary>
    /// A multi-slice job actually spans several frames, rather than getting finished off in one.
    ///
    /// A counter, not a clock: there must be exactly as many slices as the job requested, and each
    /// one on the game thread.
    /// </summary>
    [Test]
    public async Task ChunkedJobSpansSeveralFrames()
    {
        await using var w = await AiWorld.Create();

        var mainThreadId = await w.Read(() => Environment.CurrentManagedThreadId);
        var job = new CountingJob("тест-дробление", WorldPriority.Normal, slices: 4);

        var before = await w.Read(() => w.System.WorldBusHealth().Deferrals);
        var task = await w.Read(() => w.System.SubmitWorldJobForTest(job, job.Task));

        await PoolManager.WaitUntil(w.Pair.Server, () => task.IsCompleted, maxTicks: 600);
        var steps = await task;
        var after = await w.Read(() => w.System.WorldBusHealth().Deferrals);

        Assert.Multiple(() =>
        {
            Assert.That(steps, Is.EqualTo(4), "джоб должен был отработать ровно четыре среза");

            // Deferrals, not a clock: the counter isn't noisy and directly asserts what's being
            // checked — the work survived a frame boundary. Three deferrals for four slices.
            Assert.That(after - before, Is.EqualTo(3),
                "работа не переносилась между кадрами — дробление не состоялось");

            Assert.That(job.StepThreadIds, Is.All.EqualTo(mainThreadId),
                "срез исполнился вне игрового потока — это обращение к миру из чужого потока");
        });
    }

    /// <summary>
    /// The session dying between slices drops the request as stale, rather than letting it run
    /// to completion.
    ///
    /// With chunking this stops being a formality: the AI can very well get carded or killed
    /// between two slices of the same observation, and touching the world on its behalf must not
    /// continue.
    /// </summary>
    [Test]
    public async Task StaleGenerationStopsAJobMidway()
    {
        await using var w = await AiWorld.Create();

        var job = new CountingJob("тест-устаревание", WorldPriority.Normal, slices: 50);
        var task = await w.Read(() => w.System.SubmitWorldJobForTest(job, job.Task));

        // Let it get started — and confirm it's still far from done — then take the core away.
        await PoolManager.WaitUntil(w.Pair.Server, () => job.Steps >= 2, maxTicks: 120);
        Assert.That(job.Steps, Is.LessThan(50), "джоб успел доработать раньше, чем тест вмешался");

        await w.Post(() => w.System.ReleaseAll("тест"));

        await PoolManager.WaitUntil(w.Pair.Server, () => task.IsCompleted, maxTicks: 600);

        Assert.That(async () => await task, Throws.InstanceOf<StaleGenerationException>(),
            "заявка пережила смерть сессии");
        Assert.That(job.Steps, Is.LessThan(50), "джоб доработал до конца, хотя агента уже нет");
    }

    /// <summary>
    /// The bus's health counters must be zero under normal operation.
    ///
    /// An overflow means concurrency that doesn't exist in this module (tools are invoked strictly
    /// sequentially), so a nonzero value here is a sign of breakage, not of load.
    /// </summary>
    [Test]
    public async Task HealthCountersStayCleanUnderNormalUse()
    {
        await using var w = await AiWorld.Create();
        await w.Spawn("AirlockCommand");

        for (var i = 0; i < 5; i++)
        {
            await w.Invoke("look", "{}");
            await w.Invoke("noop", "{\"reason\":\"тест\"}");
        }

        var (depth, _, _, overflows, _) = await w.Read(() => w.System.WorldBusHealth());

        Assert.Multiple(() =>
        {
            // Zero depth is the file's main assertion. It means request accounting balances out:
            // for every increment on enqueue there was exactly one decrement on a terminal path,
            // however many of those there are (success, cancellation, stale generation, exception,
            // a waiter giving up, overflow). A leak here would mean the depth counter is lying, and
            // with it the overflow protection too.
            Assert.That(depth, Is.Zero, "в очереди осталась работа после того, как все вызовы завершились");
            Assert.That(overflows, Is.Zero, "очередь переполнялась — откуда-то взялся параллелизм");
        });

        // MaxWaitMs is deliberately NOT checked here. It's a record for the entire lifetime of the
        // process, and PoolManager reuses the server across tests — meaning wait time leaks into it
        // from neighboring tests in this same file, which hold a job for fifty frames ON PURPOSE. In
        // isolation the test passed, but in the full suite it failed at 2453ms; what would be checked
        // is run order, not the bus. For `aiagent cost` a lifetime max is exactly what's needed; an
        // assertion in a test would need a windowed counter, which doesn't exist.
    }
}
