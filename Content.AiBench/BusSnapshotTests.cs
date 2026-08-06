using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// The state getter, and the property that makes it composable with the stream.
///
/// A snapshot alone is not enough for a debugger and a stream alone is not either: the client needs
/// "here is everything, as of N" and then "here is what happened after N", with each change landing
/// exactly once. Getting that wrong is invisible — a doubled message looks like the agent repeating
/// itself, and a dropped one looks like the agent skipping a step.
/// </summary>
[TestFixture]
[Category("AiBus")]
public sealed class BusSnapshotTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("bus-snapshot-test");

    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aibench-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public void SnapshotAndStreamAgreeAtSeq()
    {
        var bus = new AgentEventBus(1024);
        var conv = new ConversationState();
        conv.AttachSink(bus.ForSession("current"));
        conv.SetPrefix("ПРОМПТ", "[]");

        for (var i = 0; i < 5; i++)
            conv.AppendUser($"до снимка {i}");

        // The snapshot: the body as it stands, paired with the sequence number it stands at.
        var atSnapshot = conv.Snapshot();
        var seq = bus.Seq;

        for (var i = 0; i < 5; i++)
            conv.AppendUser($"после снимка {i}");

        var read = bus.Read(bus.Instance, seq);

        Assert.That(read.Events, Has.Count.EqualTo(5),
            "после снимка было ровно пять изменений — ни одного лишнего, ни одного потерянного");

        // Apply what came after onto what the snapshot held, and the result must be the present.
        var rebuilt = atSnapshot.Select(m => m.Content).ToList();

        foreach (var e in read.Events)
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            rebuilt.Add(doc.RootElement.GetProperty("message").GetProperty("content").GetString());
        }

        Assert.That(rebuilt, Is.EqualTo(conv.Body.Select(m => m.Content).ToList()),
            "снимок плюс поток не сошлись с настоящим состоянием");
    }

    [Test]
    public void CaptureUnderConcurrentPublishDoesNotDeadlock()
    {
        // Capture takes each owner's lock in turn, never two at once, while three threads publish
        // and therefore hold (own domain, Bus) pairs. Deadlock should be unconstructible — but this
        // is the test that would notice if someone later nested the acquisitions to make the
        // capture atomic and got the order wrong. It hangs rather than failing an assertion, so the
        // assertion is on the clock.
        var bus = new AgentEventBus(4096);

        var conv = new ConversationState();
        conv.AttachSink(bus.ForSession("current"));
        conv.SetPrefix("ПРОМПТ", "[]");

        var memory = new MemoryStore(_dir, Sawmill);
        memory.AttachSink(bus.ForProcess());
        memory.LoadFromDisk();

        var skills = new SkillStore(_dir, Sawmill);
        skills.AttachSink(bus.ForProcess());
        skills.LoadFromDisk();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writers = new[]
        {
            Task.Run(() =>
            {
                var n = 0;
                while (!stop.IsCancellationRequested)
                    conv.AppendUser($"сообщение {n++}");
            }),
            Task.Run(() =>
            {
                var n = 0;
                while (!stop.IsCancellationRequested)
                    memory.Replace(MemoryTarget.Memory, "запись", $"запись {n++}");
            }),
            Task.Run(() =>
            {
                var n = 0;
                while (!stop.IsCancellationRequested)
                    skills.Write("скилл", "когда угодно", $"тело {n++}");
            }),
        };

        var captures = Task.Run(() =>
        {
            for (var i = 0; i < 200 && !stop.IsCancellationRequested; i++)
            {
                var snapshot = AgentDebugState.Capture(bus, null, memory, skills, "current", 7);
                Assert.That(snapshot.Instance, Is.EqualTo(bus.Instance));
            }
        });

        Assert.That(captures.Wait(TimeSpan.FromSeconds(20)), Is.True,
            "снимок не вернулся за 20 секунд — похоже на взаимную блокировку, а не на медленный тест");

        stop.Cancel();
        Task.WaitAll(writers, TimeSpan.FromSeconds(20));
    }

    [Test]
    public void SnapshotShowsLiveAndFrozenMemorySideBySide()
    {
        // The divergence is by design and is the most confusing thing about this system: a write
        // lands on disk at once but reaches zone 0 only at the next prefix rebuild. Showing one
        // without the other is how an operator concludes the endpoint is broken.
        var bus = new AgentEventBus(256);
        var memory = new MemoryStore(_dir, Sawmill);
        memory.LoadFromDisk();

        memory.Add(MemoryTarget.Memory, "записано после того, как префикс заморозили");

        var skills = new SkillStore(_dir, Sawmill);
        skills.LoadFromDisk();

        var snapshot = AgentDebugState.Capture(bus, null, memory, skills, "current", 7);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Memory.MemoryLive,
                Does.Contain("записано после того, как префикс заморозили"),
                "живые записи обязаны показывать свежую правку");
            Assert.That(snapshot.Memory.MemoryFrozen,
                Does.Not.Contain("записано после того, как префикс заморозили"),
                "замороженный снимок зоны 0 не должен был догнать — иначе тест ничего не показывает");
        });

        memory.RefreshSnapshot();
        var after = AgentDebugState.Capture(bus, null, memory, skills, "current", 7);

        Assert.That(after.Memory.MemoryFrozen,
            Does.Contain("записано после того, как префикс заморозили"),
            "после перестройки префикса замороженный текст обязан догнать живой");
    }

    [Test]
    public void SnapshotWithoutASessionIsNotAnError()
    {
        var bus = new AgentEventBus(64);
        var memory = new MemoryStore(_dir, Sawmill);
        memory.LoadFromDisk();
        var skills = new SkillStore(_dir, Sawmill);
        skills.LoadFromDisk();

        var snapshot = AgentDebugState.Capture(bus, null, memory, skills, "current", 7);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Session, Is.Null,
                "между раундами ядро никем не занято — это нормальный ответ, а не ошибка");
            Assert.That(snapshot.Memory, Is.Not.Null, "память процессная и переживает сессию");
            Assert.That(snapshot.Skills, Is.Not.Null);
        });
    }

    [Test]
    public void SkillsComeOutInAStableOrder()
    {
        // The zone-0 index is ordinal-sorted for determinism; the debug view matches it so a
        // refresh does not reshuffle the list under the reader.
        var bus = new AgentEventBus(256);
        var skills = new SkillStore(_dir, Sawmill);
        skills.LoadFromDisk();

        skills.Write("яблоко", "когда яблоко", "тело");
        skills.Write("абрикос", "когда абрикос", "тело");
        skills.Write("банан", "когда банан", "тело");

        var memory = new MemoryStore(_dir, Sawmill);
        memory.LoadFromDisk();

        var names = AgentDebugState.Capture(bus, null, memory, skills, "current", 7)
            .Skills.Select(s => s.Name).ToList();

        Assert.That(names, Is.EqualTo(names.OrderBy(n => n, StringComparer.Ordinal).ToList()));
    }

    [Test]
    public void SnapshotSerialisesWithCyrillicIntact()
    {
        var bus = new AgentEventBus(256);
        var memory = new MemoryStore(_dir, Sawmill);
        memory.LoadFromDisk();
        memory.Add(MemoryTarget.Crew, "Иван Петров — инженер");

        var skills = new SkillStore(_dir, Sawmill);
        skills.LoadFromDisk();

        var json = JsonSerializer.Serialize(
            AgentDebugState.Capture(bus, null, memory, skills, "current", 7), LlmJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("Иван Петров"),
                "кириллица обязана уходить как UTF-8, а не как \\uXXXX");
            Assert.That(json, Does.Contain("\"memory_live\""));
            Assert.That(json, Does.Contain("\"crew_frozen\""));
        });
    }
}
