using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The agent roster: who's on it, in what order, and what happens on a re-claim.
///
/// <para>
/// The tests are pure — no world, no session, no socket. That's precisely the argument for why
/// <see cref="AgentHandle"/> carries delegates rather than a reference to <c>AgentSession</c>:
/// a real session can't be assembled in a test, yet the roster still needs testing.
/// </para>
/// </summary>
[TestFixture]
public sealed class BusDirectoryTests
{
    private static AgentHandle Handle(string id, string name = "Агент") => new()
    {
        Id = id,
        Name = name,
        Brain = 1,
        Round = 7,
        StartedSeq = 0,
        Alive = true,
        Capture = () => null!,
        Roster = () => new AgentRosterEntryDto(id, name, 1, 7, 0, true, "Core", 0, 0, 0, 0, 0, 0, false, null),
        Send = _ => (true, "ок"),
    };

    [Test]
    public void AddThenFindReturnsTheSameHandle()
    {
        var directory = new AgentDirectory();
        var handle = Handle("core");

        Assert.That(directory.Add(handle), Is.True);
        Assert.That(directory.Find("core"), Is.SameAs(handle));
        Assert.That(directory.Find("нет-такого"), Is.Null);
    }

    /// <summary>
    /// The core comes first, then alphabetical order.
    /// </summary>
    /// <remarks>
    /// The order is dictated by the server, not the client, so the two sides don't diverge. A
    /// purely alphabetical order would put <c>combat-1</c> ahead of <c>core</c>, and the default
    /// tab would jump around depending on who happens to exist in a given round.
    /// </remarks>
    [Test]
    public void OrderPutsTheCoreFirst()
    {
        var directory = new AgentDirectory();

        directory.Add(Handle("engineer-1"));
        directory.Add(Handle("combat-1"));
        directory.Add(Handle("core"));
        directory.Add(Handle("combat-2"));

        Assert.That(directory.All.Select(h => h.Id).ToList(),
            Is.EqualTo(new[] { "core", "combat-1", "combat-2", "engineer-1" }));
    }

    /// <summary>
    /// A taken identifier is a refusal, not an overwrite.
    /// </summary>
    /// <remarks>
    /// A collision means two agents are writing into the same memory directory and the same
    /// dialogue file. The roster is the only place this is even visible from the outside, so it
    /// must report the problem instead of showing one agent in place of two.
    /// </remarks>
    [Test]
    public void AddOnATakenIdIsRefused()
    {
        var directory = new AgentDirectory();
        var first = Handle("borg-1", "Первый");

        Assert.That(directory.Add(first), Is.True);
        Assert.That(directory.Add(Handle("borg-1", "Второй")), Is.False);
        Assert.That(directory.Find("borg-1"), Is.SameAs(first), "второй затёр первого");
    }

    /// <summary>
    /// Only a handle's own entry can be removed by it.
    /// </summary>
    /// <remarks>
    /// A scenario from real life: a borg gets re-claimed within the same tick. Releasing the OLD
    /// session must not remove the NEW agent from the roster — otherwise a live robot disappears
    /// from the debugger for good, and it looks like "it never started".
    /// </remarks>
    [Test]
    public void RemoveWithAForeignHandleDoesNothing()
    {
        var directory = new AgentDirectory();
        var stale = Handle("borg-1", "Старый");
        var fresh = Handle("borg-1", "Новый");

        directory.Add(stale);
        Assert.That(directory.Remove("borg-1", stale), Is.True);

        directory.Add(fresh);
        Assert.That(directory.Remove("borg-1", stale), Is.False, "старый хендл снёс нового агента");
        Assert.That(directory.Find("borg-1"), Is.SameAs(fresh));
    }

    /// <summary>Sweeping removes handles that no longer have a live session behind them.</summary>
    [Test]
    public void RetainOnlyDropsHandlesWithoutASession()
    {
        var directory = new AgentDirectory();

        directory.Add(Handle("core"));
        directory.Add(Handle("combat-1"));
        directory.Add(Handle("combat-2"));

        directory.RetainOnly(new[] { "core", "combat-2" });

        Assert.That(directory.All.Select(h => h.Id).ToList(), Is.EqualTo(new[] { "core", "combat-2" }));
    }

    /// <summary>
    /// Reading from a foreign thread while the main thread is mutating.
    /// </summary>
    /// <remarks>
    /// Exactly the situation the roster was written for: the main thread claims and releases
    /// bodies while an HTTP thread is assembling the roster at the same time. The previous
    /// solution — a plain <c>Dictionary</c> — didn't throw an exception under this scenario, but
    /// could enter an infinite loop inside a bucket chain, and the symptom was a server that
    /// reports a live agent and then stops ticking.
    /// </remarks>
    [Test]
    public void ReadingFromAnotherThreadWhileTheMainThreadMutates()
    {
        var directory = new AgentDirectory();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var failures = new List<string>();

        var writer = Task.Run(() =>
        {
            var n = 0;

            while (!stop.IsCancellationRequested)
            {
                var id = $"borg-{n++ % 8}";
                var handle = Handle(id);

                if (directory.Add(handle))
                    directory.Remove(id, handle);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var handle in directory.All)
                {
                    if (handle == null)
                        failures.Add("в опубликованном массиве null");
                }

                directory.Roster();
                directory.Find("borg-3");
            }
        });

        Assert.That(Task.WhenAll(writer, reader).Wait(TimeSpan.FromSeconds(20)), Is.True,
            "потоки не разошлись за 20 секунд — похоже на зацикливание, а не на медленный тест");

        Assert.That(failures, Is.Empty);
    }
}
