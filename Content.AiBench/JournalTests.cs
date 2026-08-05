using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// The acceptance journal actually reaches disk.
///
/// This exists because it did not. <c>AgentSession.Journal</c> was an <c>init</c> property, and the
/// constructor handed it to <see cref="Content.Server.AiAgent.Turn.TurnRunner"/> — which runs
/// <em>before</em> an object initializer. So the runner captured <see cref="Journal.Disabled"/>
/// forever, and the four per-turn kinds (<c>step</c>, <c>tool</c>, <c>promise</c>, <c>untooled</c>)
/// were written into a no-op. The compaction event, raised through the property at call time rather
/// than captured in the constructor, worked — so the file existed, was non-empty, and contained
/// exactly one kind. A day of live play produced <c>{"compaction": 1}</c> and nothing else, with no
/// error anywhere and a CVar that read as wired.
///
/// The assertion is therefore deliberately end-to-end: a real <see cref="AgentSession"/>, a real
/// journal pointed at a temp directory, one scripted turn, and the JSONL read back off disk. A test
/// that constructed a <c>TurnRunner</c> directly — as <c>TurnTests</c> does — would have passed
/// against the broken wiring, because the wiring is the thing under test.
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class JournalTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("journal-test");

    /// <summary>Everything the loop needs that is not the journal, stubbed to the shortest path.</summary>
    private static AgentSession BuildSession(string logDir, ScriptedLlmClient llm, AiToolRegistry registry)
    {
        return new AgentSession(
            default,
            llm,
            registry,
            new ObservationQueue(200),
            new AgentLoopOptions
            {
                // Sub-tick delays: the loop is real, but the test must not wait eight seconds for it.
                TickSeconds = () => 0.01f,
                TickSecondsIdle = () => 0.01f,
                MaxToolCallsPerTurn = () => 4,
                MaxConsecutiveFailures = () => 3,
            },
            // Nullable is disabled in this project, so no `?` annotation here — the delegate's own
            // signature (declared in Content.Server, where it is enabled) still says TurnPerception?.
            (_, _) => Task.FromResult(
                new TurnPerception("НАБЛЮДЕНИЕ", null, false, true, "T+0:00:10")),
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(true),
            null,
            () => ("ПРОМПТ", registry.WireJson()),
            new CompactionOptions
            {
                High = () => int.MaxValue,
                Low = () => 0,
                KeepTail = () => 1000,
            },
            new Journal(logDir, Sawmill),
            null,
            Sawmill);
    }

    [Test]
    public async Task StepEventReachesDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aibench-journal-" + Guid.NewGuid().ToString("N"));

        try
        {
            var registry = new AiToolRegistry();
            var llm = new ScriptedLlmClient().Then("сказал что-то и замолчал");
            var session = BuildSession(dir, llm, registry);

            session.Conv.SetPrefix("ПРОМПТ", registry.WireJson());
            session.Start();

            // The loop is asynchronous by nature; poll rather than guess a sleep long enough.
            var kinds = await WaitForKinds(dir, "step", TimeSpan.FromSeconds(10));

            session.Cts.Cancel();

            Assert.That(kinds, Does.Contain("step"),
                "TurnRunner получил Journal.Disabled — событие хода не доехало до диска");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Poll the day's JSONL until <paramref name="wanted"/> shows up, or time out.</summary>
    private static async Task<string[]> WaitForKinds(string dir, string wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var kinds = Array.Empty<string>();

        while (DateTime.UtcNow < deadline)
        {
            kinds = ReadKinds(dir);
            if (kinds.Contains(wanted))
                return kinds;

            await Task.Delay(50);
        }

        return kinds;
    }

    private static string[] ReadKinds(string dir)
    {
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        var kinds = new System.Collections.Generic.List<string>();

        foreach (var path in Directory.GetFiles(dir, "events-*.jsonl"))
        {
            string text;
            try
            {
                // The agent thread appends while we read; a torn read is a retry, not a failure.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("kind", out var kind))
                        kinds.Add(kind.GetString() ?? "");
                }
                catch (JsonException)
                {
                    // A half-flushed last line while the loop is still running.
                }
            }
        }

        return kinds.ToArray();
    }
}
