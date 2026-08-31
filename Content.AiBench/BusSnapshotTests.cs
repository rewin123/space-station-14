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
using Content.Server.AiAgent.Vfs;
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

    /// <summary>Пустое хранилище заметок: снимок обязан собираться и когда агент ещё никого не знает.</summary>
    /// <summary>
    /// Файловая система одного агента. Снимок теперь берут с неё целиком, а не с трёх сторов
    /// по отдельности: библиотеки стали своими у каждого тела.
    /// </summary>
    private Vfs NewVfs() => new VfsBuilder(Sawmill)
        .AddFolder(Path.Combine(_dir, "skills"), "skills", VfsAccess.Write, "что ты понял сам")
        .AddNotes(_dir, "players", VfsAccess.Write, "заметки о людях", () => "[раунд 1 · 01.01]")
        .AddMemory(_dir, "memory.md", VfsAccess.Write, "факты о станции")
        .Build();

    private PlayerNoteStore Notes()
    {
        var notes = new PlayerNoteStore(_dir, Sawmill);
        notes.LoadFromDisk();
        return notes;
    }

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

        var vfs = NewVfs();
        vfs.AttachSink(bus.ForProcess());
        var memory = vfs.Memory!;
        var skills = vfs.Skills!;

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
                    memory.Replace("запись", $"запись {n++}");
            }),
            Task.Run(() =>
            {
                var n = 0;
                while (!stop.IsCancellationRequested)
                    skills.Write("скилл", "скилл", "когда угодно", $"тело {n++}");
            }),
        };

        var captures = Task.Run(() =>
        {
            for (var i = 0; i < 200 && !stop.IsCancellationRequested; i++)
            {
                var snapshot = AgentDebugState.CaptureGlobal(bus, new AgentDirectory(), vfs, 7);
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
        var vfs = NewVfs();
        vfs.AttachSink(bus.ForProcess());
        var memory = vfs.Memory!;

        memory.Add("записано после того, как префикс заморозили");
        var skills = vfs.Skills!;
        skills.Reload();

        var snapshot = AgentDebugState.CaptureGlobal(bus, new AgentDirectory(), vfs, 7);

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
        var after = AgentDebugState.CaptureGlobal(bus, new AgentDirectory(), vfs, 7);

        Assert.That(after.Memory.MemoryFrozen,
            Does.Contain("записано после того, как префикс заморозили"),
            "после перестройки префикса замороженный текст обязан догнать живой");
    }

    [Test]
    public void SnapshotWithoutASessionIsNotAnError()
    {
        var bus = new AgentEventBus(64);
        var vfs = NewVfs();
        vfs.AttachSink(bus.ForProcess());
        var memory = vfs.Memory!;
        var skills = vfs.Skills!;
        skills.Reload();

        var snapshot = AgentDebugState.CaptureGlobal(bus, new AgentDirectory(), vfs, 7);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Agents, Is.Empty,
                "между раундами тела никем не заняты — это нормальный ответ, а не ошибка");
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

        var vfs = NewVfs();
        vfs.AttachSink(bus.ForProcess());

        var skills = vfs.Skills!;
        skills.Write("яблоко", "яблоко", "когда яблоко", "тело");
        skills.Write("абрикос", "абрикос", "когда абрикос", "тело");
        skills.Write("банан", "банан", "когда банан", "тело");

        var names = AgentDebugState.CaptureGlobal(bus, new AgentDirectory(), vfs, 7)
            .Skills.Select(s => s.Name).ToList();

        Assert.That(names, Is.EqualTo(names.OrderBy(n => n, StringComparer.Ordinal).ToList()));
    }

    [Test]
    public void SnapshotSerialisesWithCyrillicIntact()
    {
        var bus = new AgentEventBus(256);
        var vfs = NewVfs();
        vfs.AttachSink(bus.ForProcess());
        var memory = vfs.Memory!;
        memory.Add("Иван Петров — инженер");
        var skills = vfs.Skills!;
        skills.Reload();

        var json = JsonSerializer.Serialize(
            AgentDebugState.CaptureGlobal(bus, new AgentDirectory(), vfs, 7), LlmJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("Иван Петров"),
                "кириллица обязана уходить как UTF-8, а не как \\uXXXX");
            Assert.That(json, Does.Contain("\"memory_live\""));
            Assert.That(json, Does.Contain("\"memory_frozen\""));
        });
    }
}
