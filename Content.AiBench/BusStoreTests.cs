using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// Memory and skills report themselves, proved the same way the conversation is: by replay.
///
/// The interesting cases here are the ones where a write is <em>refused</em>. Both stores roll the
/// in-memory edit back when the disk says no, and the event must roll back with it — an event that
/// announced an entry the agent will not see after the next reload would be worse than no event at
/// all, because it looks authoritative.
/// </summary>
[TestFixture]
[Category("AiBus")]
public sealed class BusStoreTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("bus-store-test");

    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aibench-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Last reported entries per target, rebuilt from events alone.</summary>
    private static Dictionary<string, List<string>> ReplayMemory(AgentEventBus bus)
    {
        var state = new Dictionary<string, List<string>>();

        foreach (var e in bus.Read(bus.Instance, 0).Events.Where(e => e.Kind == AgentEventKind.MemoryUpdated))
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            var target = doc.RootElement.GetProperty("target").GetString()!;
            state[target] = doc.RootElement.GetProperty("entries")
                .EnumerateArray().Select(x => x.GetString()!).ToList();
        }

        return state;
    }

    private static Dictionary<string, Skill> ReplaySkills(AgentEventBus bus)
    {
        var state = new Dictionary<string, Skill>();

        foreach (var e in bus.Read(bus.Instance, 0).Events.Where(e => e.Kind == AgentEventKind.SkillUpdated))
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            var r = doc.RootElement;
            var skill = new Skill(
                r.GetProperty("name").GetString()!,
                r.GetProperty("when").GetString()!,
                r.GetProperty("body").GetString()!);

            state[skill.Name] = skill;
        }

        return state;
    }

    [Test]
    public void MemoryEventsReplayToTheLiveEntries()
    {
        var bus = new AgentEventBus(1024);
        var memory = new MemoryStore(_dir, Sawmill);
        memory.AttachSink(bus.ForProcess());
        memory.LoadFromDisk();

        memory.Add(MemoryTarget.Memory, "капитан доверяет мне после инцидента в атмосе");
        memory.Add(MemoryTarget.Memory, "SMES в инженерном разряжается быстрее остальных");
        memory.Add(MemoryTarget.Crew, "Иван Петров — инженер, спокойный");

        memory.Replace(MemoryTarget.Memory, "SMES в инженерном", "SMES в инженерном разряжается быстро — проверять каждые 20 минут");
        memory.Remove(MemoryTarget.Crew, "Иван Петров");

        memory.Add(MemoryTarget.Crew, "Мария Сидорова — врач");

        var replayed = ReplayMemory(bus);

        Assert.Multiple(() =>
        {
            Assert.That(replayed["memory"], Is.EqualTo(memory.Entries(MemoryTarget.Memory).ToList()),
                "воспроизведение памяти разъехалось с живыми записями");
            Assert.That(replayed["crew"], Is.EqualTo(memory.Entries(MemoryTarget.Crew).ToList()),
                "воспроизведение экипажа разъехалось с живыми записями");
        });
    }

    [Test]
    public void RefusedMemoryWriteReportsNothing()
    {
        var bus = new AgentEventBus(1024);
        var memory = new MemoryStore(_dir, Sawmill) { MemoryLimit = 80 };
        memory.AttachSink(bus.ForProcess());
        memory.LoadFromDisk();

        memory.Add(MemoryTarget.Memory, "короткая запись");
        var afterFirst = bus.Seq;

        var overflow = memory.Add(MemoryTarget.Memory, new string('щ', 200));

        Assert.Multiple(() =>
        {
            Assert.That(overflow.Ok, Is.False, "запись обязана была не влезть — иначе тест ничего не проверяет");
            Assert.That(bus.Seq, Is.EqualTo(afterFirst),
                "отказанная запись опубликовала событие: клиент увидел бы запись, которой нет на диске");
        });
    }

    [Test]
    public void SkillEventsReplayToTheLibrary()
    {
        var bus = new AgentEventBus(1024);
        var skills = new SkillStore(_dir, Sawmill);
        skills.AttachSink(bus.ForProcess());
        skills.LoadFromDisk();

        skills.Write("restore-core-power", "когда ядро обесточено", "1. station_status\n2. звать инженеров");
        skills.Write("bolt-armoury", "когда оружейную вскрывают", "опустить болты, объявить");

        // Empty match is the append path — a different branch from the fragment replace below.
        skills.Edit("restore-core-power", "", "Грабли: APC ядра не доступен для move_camera.");
        skills.Edit("bolt-armoury", "объявить", "объявить по общему каналу");

        var replayed = ReplaySkills(bus);
        var live = skills.All.ToDictionary(s => s.Name);

        Assert.That(replayed.Keys, Is.EquivalentTo(live.Keys));

        foreach (var (name, skill) in live)
        {
            Assert.Multiple(() =>
            {
                Assert.That(replayed[name].When, Is.EqualTo(skill.When), $"'когда' разъехалось у {name}");
                Assert.That(replayed[name].Body, Is.EqualTo(skill.Body), $"тело разъехалось у {name}");
            });
        }
    }

    [Test]
    public void ReloadRepublishesEverything()
    {
        // ReloadAgentFiles builds brand-new stores. A client that had been following the old pair
        // must be told the whole new contents, because nothing else would ever tell it.
        var bus = new AgentEventBus(1024);

        var first = new MemoryStore(_dir, Sawmill);
        first.LoadFromDisk();
        first.Add(MemoryTarget.Memory, "запись, пережившая перезагрузку");

        var second = new MemoryStore(_dir, Sawmill);
        second.AttachSink(bus.ForProcess());
        second.LoadFromDisk();

        var replayed = ReplayMemory(bus);

        Assert.That(replayed["memory"], Is.EqualTo(new[] { "запись, пережившая перезагрузку" }),
            "перезагруженный стор не рассказал о своём содержимом");
    }

    [Test]
    public void RefreshSnapshotIsReported()
    {
        // The frozen text is what the model actually reads, and it moves only here — at a prefix
        // rebuild, i.e. every compaction. Silent, it would leave a debugger's "frozen" column
        // stale forever while claiming to show live-versus-frozen divergence.
        var bus = new AgentEventBus(64);
        var memory = new MemoryStore(_dir, Sawmill);
        memory.AttachSink(bus.ForProcess());
        memory.LoadFromDisk();
        memory.Add(MemoryTarget.Memory, "запись, ещё не попавшая в префикс");

        var before = bus.Seq;
        memory.RefreshSnapshot();

        Assert.That(bus.Seq, Is.GreaterThan(before),
            "перестройка снимка не сообщила о себе — замороженная колонка молча устареет");
    }

    [Test]
    public void ReloadReportsTheWholeLibraryIncludingDeletions()
    {
        // A reload is the ONLY way a skill can disappear: the store clears itself and re-adds
        // whatever parsed. A per-survivor event says nothing about the ones that went, so a client
        // folding them into a map keeps ghosts — and reloads happen at every compaction.
        var bus = new AgentEventBus(256);
        var skills = new SkillStore(_dir, Sawmill);
        skills.AttachSink(bus.ForProcess());
        skills.LoadFromDisk();

        skills.Write("останется", "когда-нибудь", "тело");
        skills.Write("исчезнет", "когда-нибудь", "тело");

        File.Delete(Path.Combine(_dir, "skills", "исчезнет.md"));
        skills.LoadFromDisk();

        var reloads = bus.Read(bus.Instance, 0).Events
            .Where(e => e.Kind == AgentEventKind.SkillsReloaded).ToList();

        Assert.That(reloads, Is.Not.Empty, "перечитывание библиотеки не породило кадра");

        using var doc = JsonDocument.Parse(reloads[^1].PayloadJson);
        var names = doc.RootElement.GetProperty("skills")
            .EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("останется"));
            Assert.That(names, Does.Not.Contain("исчезнет"),
                "удалённый скилл остался бы призраком в любом клиенте, складывающем skill.updated в карту");
        });
    }

    [Test]
    public void StoresWithNoSinkPublishNothing()
    {
        var bus = new AgentEventBus(64);

        var memory = new MemoryStore(_dir, Sawmill);
        memory.LoadFromDisk();
        memory.Add(MemoryTarget.Memory, "запись");

        var skills = new SkillStore(_dir, Sawmill);
        skills.LoadFromDisk();
        skills.Write("скилл", "когда-нибудь", "тело");

        Assert.That(bus.Seq, Is.Zero, "выключенная шина обязана стоить ровно ноль");
    }
}
