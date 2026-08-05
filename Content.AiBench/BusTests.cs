using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The event bus: ring, cursor, resync, and the promise that publishing never blocks.
///
/// No pooled server and no model — the bus knows nothing about either, which is the point of it
/// being a separate object. These run in milliseconds, like <c>ContextTests</c> and <c>TurnTests</c>.
///
/// The cursor tests are the ones worth having. The design this is modelled on has no sequence
/// numbers anywhere: a client that reconnects silently loses whatever happened while it was away,
/// and the UI's answer is a banner reading "tool calls may not appear". Everything below exists so
/// that a gap is either impossible or reported.
/// </summary>
[TestFixture]
[Category("AiBus")]
public sealed class BusTests
{
    private static AgentEventBus Bus(int ring = 512) => new(ring);

    private static long PublishOne(AgentEventBus bus, string payload = "{}") =>
        bus.Publish(AgentEventKind.Stats, "current", payload);

    [Test]
    public void EveryEventKindHasADistinctName()
    {
        var kinds = Enum.GetValues<AgentEventKind>();
        var names = kinds.Select(AgentEventNames.Of).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.All.Not.Empty, "у вида события пустое имя");
            Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count),
                "два вида событий делят одно имя — ровно тот шрам, ради которого имя выводится из enum");
        });
    }

    [Test]
    public void SeqStartsAtOneAndIsGapless()
    {
        var bus = Bus();

        Assert.That(bus.Seq, Is.Zero, "до первой публикации курсор должен быть нулевым");

        for (var i = 1; i <= 5; i++)
            Assert.That(PublishOne(bus), Is.EqualTo(i));

        var read = bus.Read(bus.Instance, 0);
        Assert.Multiple(() =>
        {
            Assert.That(read.Resync, Is.False);
            Assert.That(read.Events.Select(e => e.Seq), Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
        });
    }

    [Test]
    public void ReadReturnsOnlyWhatCameAfterTheCursor()
    {
        var bus = Bus();
        for (var i = 0; i < 10; i++)
            PublishOne(bus, $"{{\"n\":{i}}}");

        var read = bus.Read(bus.Instance, 7);

        Assert.Multiple(() =>
        {
            Assert.That(read.Resync, Is.False);
            Assert.That(read.Events.Select(e => e.Seq), Is.EqualTo(new long[] { 8, 9, 10 }));
            Assert.That(read.Events[0].PayloadJson, Is.EqualTo("{\"n\":7}"),
                "порядок обязан быть по возрастанию seq, а не в порядке слотов кольца");
        });
    }

    [Test]
    public void ReadIsEmptyWhenCaughtUp()
    {
        var bus = Bus();
        PublishOne(bus);

        var read = bus.Read(bus.Instance, 1);

        Assert.Multiple(() =>
        {
            Assert.That(read.Resync, Is.False, "догнавший клиент не должен получать ресинк");
            Assert.That(read.Events, Is.Empty);
            Assert.That(read.Seq, Is.EqualTo(1));
        });
    }

    [Test]
    public void ResyncWhenCursorFellOutOfRing()
    {
        var bus = Bus(ring: 16);
        for (var i = 0; i < 40; i++)
            PublishOne(bus);

        Assert.That(bus.Read(bus.Instance, 1).Resync, Is.True,
            "кольцо давно перезаписало кадр 2 — честный ответ «перечитай снимок», а не обрывок истории");

        Assert.That(bus.Read(bus.Instance, 39).Resync, Is.False,
            "кадр в пределах кольца обязан отдаваться");
    }

    [Test]
    public void ResyncWhenSinceIsInTheFuture()
    {
        var bus = Bus();
        PublishOne(bus);

        Assert.That(bus.Read(bus.Instance, 5000).Resync, Is.True,
            "курсор из будущего — обычно клиент, переживший перезапуск процесса");
    }

    [Test]
    public void ResyncOnInstanceMismatch()
    {
        var bus = Bus();
        PublishOne(bus);

        Assert.Multiple(() =>
        {
            Assert.That(bus.Read("другой-процесс", 0).Resync, Is.True,
                "голого seq мало: после перезапуска он снова пойдёт с нуля");
            Assert.That(bus.Read(null, 0).Resync, Is.False,
                "клиент без курсора берёт что есть");
        });
    }

    [Test]
    public void SeqIsGaplessUnderConcurrentPublish()
    {
        // Publishers are the agent thread, the main thread and (for stats) the turn boundary. A
        // duplicated or skipped seq would make a client's gap detection lie in the one direction
        // that matters: silently.
        const int threads = 4;
        const int each = 1000;

        var bus = Bus(ring: threads * each + 16);
        var tasks = new Task[threads];

        for (var t = 0; t < threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < each; i++)
                    PublishOne(bus);
            });
        }

        Task.WaitAll(tasks);

        var read = bus.Read(bus.Instance, 0);
        var seqs = read.Events.Select(e => e.Seq).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(bus.Seq, Is.EqualTo(threads * each));
            Assert.That(seqs.Count, Is.EqualTo(threads * each));
            Assert.That(seqs.Distinct().Count(), Is.EqualTo(seqs.Count), "дубликат seq");
            Assert.That(seqs, Is.Ordered, "seq вышел не по возрастанию");
        });
    }

    [Test]
    public void PublishNeverBlocksWithNobodyReading()
    {
        // The whole reason there are no subscriber queues: a publisher's cost must not depend on
        // whether anyone is listening or how fast they are.
        var bus = Bus(ring: 512);
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < 100_000; i++)
            PublishOne(bus);

        clock.Stop();

        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
            $"100k публикаций заняли {clock.ElapsedMilliseconds}мс — публикация перестала быть O(1)");
        Assert.That(bus.Count, Is.EqualTo(bus.Capacity), "кольцо обязано перезаписываться, а не расти");
    }

    [Test]
    public async Task LongPollReturnsAsSoonAsSomethingIsPublished()
    {
        var bus = Bus();
        var clock = Stopwatch.StartNew();

        var waiting = bus.ReadAsync(bus.Instance, 0, TimeSpan.FromSeconds(20), CancellationToken.None);

        // Give the reader a moment to actually park on the signal, then publish.
        await Task.Delay(50);
        PublishOne(bus, "{\"woke\":true}");

        var read = await waiting;
        clock.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(read.Events, Has.Count.EqualTo(1));
            Assert.That(read.Events[0].PayloadJson, Is.EqualTo("{\"woke\":true}"));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
                "long-poll досидел до таймаута вместо того, чтобы проснуться на публикации");
        });
    }

    [Test]
    public async Task LongPollTimesOutEmpty()
    {
        var bus = Bus();
        PublishOne(bus);

        var read = await bus.ReadAsync(bus.Instance, 1, TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(read.Events, Is.Empty, "пустой ответ по таймауту — нормальный ответ, а не ошибка");
            Assert.That(read.Resync, Is.False);
        });
    }

    [Test]
    public async Task LongPollDoesNotMissAPublishRacingTheParkedWait()
    {
        // The window between "nothing new" and "park on the signal" is where a naive implementation
        // loses an event and makes the reader wait out a full timeout for news it already had.
        var bus = Bus();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var since = bus.Seq;
            var reader = bus.ReadAsync(bus.Instance, since, TimeSpan.FromSeconds(5), CancellationToken.None);
            var publisher = Task.Run(() => PublishOne(bus));

            var read = await reader;
            await publisher;

            Assert.That(read.Events, Is.Not.Empty, $"попытка {attempt}: публикация проскочила мимо ожидающего");
        }
    }

    [Test]
    public async Task LongPollReturnsImmediatelyOnResync()
    {
        var bus = Bus(ring: 16);
        for (var i = 0; i < 40; i++)
            PublishOne(bus);

        var clock = Stopwatch.StartNew();
        var read = await bus.ReadAsync(bus.Instance, 1, TimeSpan.FromSeconds(20), CancellationToken.None);
        clock.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(read.Resync, Is.True);
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
                "ресинк — не повод ждать: клиенту нужно перечитать снимок прямо сейчас");
        });
    }

    [Test]
    public void SessionSinkAndProcessSinkTagDifferently()
    {
        var bus = Bus();

        bus.ForSession("current").PrefixReplaced("hash", "промпт", "[]");
        bus.ForProcess().MemoryUpdated(Content.Server.AiAgent.Skills.MemoryTarget.Crew, new[] { "запись" });

        var events = bus.Read(bus.Instance, 0).Events;

        Assert.Multiple(() =>
        {
            Assert.That(events[0].SessionId, Is.EqualTo("current"));
            Assert.That(events[0].Kind, Is.EqualTo(AgentEventKind.PrefixReplaced));

            Assert.That(events[1].SessionId, Is.Empty,
                "память и скиллы процессные — они переживают сессию и не принадлежат ей");
            Assert.That(events[1].Kind, Is.EqualTo(AgentEventKind.MemoryUpdated));
            Assert.That(events[1].PayloadJson, Does.Contain("\"target\":\"crew\""));
            Assert.That(events[1].PayloadJson, Does.Contain("запись"),
                "кириллица обязана уходить как UTF-8, а не как \\uXXXX");
        });
    }

    [Test]
    public void PayloadIsSerialisedAtPublishTimeNotAtReadTime()
    {
        // The ring must not hold a reference to a live mutable object: the wire DTOs are classes
        // the agent thread keeps editing. Mutating the source after publishing must not change
        // what a reader sees.
        var bus = Bus();
        var message = Content.Server.AiAgent.Llm.ChatMessageDto.User("исходный текст");

        bus.ForSession("current").MessageAppended(0, 0, message);
        message.Content = "переписанный текст";

        var payload = bus.Read(bus.Instance, 0).Events[0].PayloadJson;

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain("исходный текст"));
            Assert.That(payload, Does.Not.Contain("переписанный текст"),
                "кадр держит ссылку на живой объект — HTTP-поток гоняется с потоком агента");
        });
    }
}
