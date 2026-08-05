using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// What happens when the agent is torn down while it is busy.
///
/// The suite had nothing of this shape, which is why the heaviest defects in the system could not
/// be caught by it: every lifecycle test either stopped the loop first or drove tools synchronously,
/// so the one interesting moment — a live loop, mid-call, being released from the game thread — was
/// never exercised. Killing the AI and restarting the round are the two commonest events on a
/// server, and both go down this path.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class TeardownTests
{
    /// <summary>
    /// A model client that hangs until the test lets it go, so the loop can be caught mid-turn.
    ///
    /// It deliberately does NOT observe the cancellation token, and that is the point rather than a
    /// shortcut. What Release has to survive is a loop that cannot be freed by cancelling it — most
    /// concretely a marshalled call already queued for the main thread, which by definition cannot
    /// run while the main thread is the thing waiting for it. A client that unblocks the instant the
    /// token is cancelled would let the old blocking Release pass this test, because the loop would
    /// finish before the wait even started.
    /// </summary>
    private sealed class BlockingLlmClient : ILlmClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the loop is actually inside a model call.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessageDto> messages,
            IReadOnlyList<ToolDto> tools,
            CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new LlmResponse("готово", Array.Empty<ToolCallDto>(), 100, 90, 5, 0.1);
        }

        public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(131072);
    }

    /// <summary>
    /// Give the loop something to react to and keep ticking until it is inside a model call.
    ///
    /// Wall-clock rather than a tick budget: the loop waits <c>ai.tick_seconds</c> of REAL time
    /// between turns, and a test server runs its ticks as fast as it can, so counting ticks would
    /// time out long before a single second of that delay had passed.
    /// </summary>
    private static async Task<bool> EnterModelCall(AiWorld w, BlockingLlmClient llm)
    {
        await w.Post(() => w.System.InjectRadio("Binary", "ИИ, приём", out _));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!llm.Entered.IsCompleted && DateTime.UtcNow < deadline)
            await w.Pair.Server.WaitRunTicks(5);

        return llm.Entered.IsCompleted;
    }

    [Test]
    public async Task Release_DoesNotStallTheGameThread()
    {
        // Release used to wait up to two seconds for the loop, from inside TickUpdate. That wait
        // could never succeed: the pending-task queue the loop is blocked on is drained *before*
        // TickUpdate on the same thread, so nothing could complete while the wait held it. The
        // timeout elapsed in full, every time, on every AI death and every round restart — about a
        // hundred and twenty lost ticks, visible to every player as a freeze.
        var llm = new BlockingLlmClient();
        await using var w = await AiWorld.CreateWith(llm);

        Assert.That(await EnterModelCall(w, llm), Is.True, "петля так и не дошла до вызова модели");

        var elapsed = Stopwatch.StartNew();
        await w.Post(() => w.System.Release(w.Brain, "teardown test"));
        elapsed.Stop();

        Assert.That(elapsed.Elapsed.TotalMilliseconds, Is.LessThan(500),
            $"Release держал главный поток {elapsed.Elapsed.TotalMilliseconds:F0}мс — тик стоит всё это время");

        llm.Release();
    }

    [Test]
    public async Task Release_LeavesNoSession_AndTheLoopGivesUp()
    {
        // The loop is not waited for, so what has to be true is the other half: the session is gone
        // immediately, and every marshalled call the loop still has in flight fails as stale rather
        // than being applied to a world that has moved on.
        var llm = new BlockingLlmClient();
        await using var w = await AiWorld.CreateWith(llm);
        var brain = w.Brain;

        Assert.That(await EnterModelCall(w, llm), Is.True, "петля так и не дошла до вызова модели");

        await w.Post(() => w.System.Release(brain, "teardown test"));
        llm.Release();

        var session = await w.Read(() => w.System.GetSession(brain));
        Assert.That(session, Is.Null, "сессия должна исчезнуть сразу, а не когда петля соизволит");

        var result = await w.Invoke("look");
        Assert.That(result.Ok, Is.False, "без сессии инструменты обязаны отказывать: " + result.ToJson());
    }
}
