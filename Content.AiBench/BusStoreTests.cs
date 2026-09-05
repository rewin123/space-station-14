using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Vfs;
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

    /// <summary>Last reported entries, rebuilt from events alone.</summary>
    private static List<string> ReplayMemory(AgentEventBus bus)
    {
        var state = new List<string>();

        foreach (var e in bus.Read(bus.Instance, 0).Events.Where(e => e.Kind == AgentEventKind.MemoryUpdated))
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            state = doc.RootElement.GetProperty("entries")
                .EnumerateArray().Select(x => x.GetString()!).ToList();
        }

        return state;
    }

    /// <summary>The round stamp is fixed: note formatting is not what's tested here.</summary>
    private const string Stamp = "[раунд 7 · 15.08]";

    /// <summary>
    /// Notes rebuilt from events alone, exactly as a client would do it: folding them into a map and
    /// removing the key once its entry list is empty.
    /// </summary>
    private static Dictionary<string, List<string>> ReplayNotes(AgentEventBus bus)
    {
        var state = new Dictionary<string, List<string>>();

        foreach (var e in bus.Read(bus.Instance, 0).Events)
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            var r = doc.RootElement;

            switch (e.Kind)
            {
                case AgentEventKind.PlayerNotesReloaded:
                    state.Clear();
                    foreach (var n in r.GetProperty("notes").EnumerateArray())
                        state[n.GetProperty("slug").GetString()!] = EntriesOf(n);
                    break;

                case AgentEventKind.PlayerNoteUpdated:
                    var slug = r.GetProperty("slug").GetString()!;
                    var entries = EntriesOf(r);

                    if (entries.Count == 0)
                        state.Remove(slug);
                    else
                        state[slug] = entries;
                    break;
            }
        }

        return state;

        static List<string> EntriesOf(JsonElement el) =>
            el.GetProperty("entries").EnumerateArray().Select(x => x.GetString()!).ToList();
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

        memory.Add("капитан доверяет мне после инцидента в атмосе");
        memory.Add("SMES в инженерном разряжается быстрее остальных");
        memory.Add("Иван Петров — инженер, спокойный");

        memory.Replace("SMES в инженерном", "SMES в инженерном разряжается быстро — проверять каждые 20 минут");
        memory.Remove("Иван Петров");

        memory.Add("Мария Сидорова — врач");

        var replayed = ReplayMemory(bus);

        Assert.Multiple(() =>
        {
            Assert.That(replayed, Is.EqualTo(memory.Entries().ToList()),
                "воспроизведение памяти разъехалось с живыми записями");
        });
    }

    [Test]
    public void RefusedMemoryWriteReportsNothing()
    {
        var bus = new AgentEventBus(1024);
        var memory = new MemoryStore(_dir, Sawmill) { MemoryLimit = 80 };
        memory.AttachSink(bus.ForProcess());
        memory.LoadFromDisk();

        memory.Add("короткая запись");
        var afterFirst = bus.Seq;

        var overflow = memory.Add(new string('щ', 200));

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
        var skills = new DocTree(Path.Combine(_dir, "skills"), Sawmill);
        skills.AttachSink(bus.ForProcess());
        skills.Reload();

        skills.Write("restore-core-power", "restore-core-power", "когда ядро обесточено", "1. station_status\n2. звать инженеров");
        skills.Write("bolt-armoury", "bolt-armoury", "когда оружейную вскрывают", "опустить болты, объявить");

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
        first.Add("запись, пережившая перезагрузку");

        var second = new MemoryStore(_dir, Sawmill);
        second.AttachSink(bus.ForProcess());
        second.LoadFromDisk();

        var replayed = ReplayMemory(bus);

        Assert.That(replayed, Is.EqualTo(new[] { "запись, пережившая перезагрузку" }),
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
        memory.Add("запись, ещё не попавшая в префикс");

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
        var skills = new DocTree(Path.Combine(_dir, "skills"), Sawmill);
        skills.AttachSink(bus.ForProcess());
        skills.Reload();

        skills.Write("останется", "останется", "когда-нибудь", "тело");
        skills.Write("исчезнет", "исчезнет", "когда-нибудь", "тело");

        File.Delete(Path.Combine(_dir, "skills", "исчезнет.md"));
        skills.Reload();

        var reloads = bus.Read(bus.Instance, 0).Events
            .Where(e => e.Kind == AgentEventKind.SkillsReloaded).ToList();

        Assert.That(reloads, Is.Not.Empty, "перечитывание библиотеки не породило кадра");

        using var doc = JsonDocument.Parse(reloads[^1].PayloadJson);
        var names = doc.RootElement.GetProperty("skills")
            .EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("останется.md"));
            Assert.That(names, Does.Not.Contain("исчезнет.md"),
                "удалённый скилл остался бы призраком в любом клиенте, складывающем skill.updated в карту");
        });
    }

    [Test]
    public void NoteEventsReplayToTheLiveStore()
    {
        var bus = new AgentEventBus(1024);
        var notes = new PlayerNoteStore(_dir, Sawmill);
        notes.AttachSink(bus.ForProcess());
        notes.LoadFromDisk();

        notes.Add("Autumn Treeby", "просила запомнить: ей нельзя кофе — шутка", Stamp);
        notes.Add("Autumn Treeby", "работает в ботанике, спокойная", Stamp);
        notes.Add("Hareeya-Seek", "клоун, выпрашивал доступ ложью", Stamp);

        notes.Replace("Autumn Treeby", "спокойная", "инженер, не ботаник — я перепутала отдел");
        notes.Remove("Hareeya-Seek", "выпрашивал доступ");

        notes.Add("Autumn Treeby", "во вторую смену помогала в карго", Stamp);

        var replayed = ReplayNotes(bus);
        var live = notes.All.ToDictionary(n => n.Slug, n => n.Entries.ToList());

        Assert.That(replayed.Keys, Is.EquivalentTo(live.Keys),
            "воспроизведение разъехалось по составу заметок");

        foreach (var (slug, entries) in live)
            Assert.That(replayed[slug], Is.EqualTo(entries), $"записи разъехались у «{slug}»");
    }

    [Test]
    public void ClosingANoteIsReportedAsATombstone()
    {
        // Removing the LAST entry also deletes the file. Without an event about that, a client
        // folding note.updated into a map would keep holding a person about whom nothing is known
        // any more, and would only notice after the store reloads, i.e. at the next compaction.
        var bus = new AgentEventBus(256);
        var notes = new PlayerNoteStore(_dir, Sawmill);
        notes.AttachSink(bus.ForProcess());
        notes.LoadFromDisk();

        notes.Add("Ezbozo", "единственная запись", Stamp);
        Assert.That(ReplayNotes(bus), Does.ContainKey("ezbozo"), "заметки не было — тест ничего не проверяет");

        var closed = notes.Remove("Ezbozo", "единственная");

        Assert.Multiple(() =>
        {
            Assert.That(closed.Ok, Is.True, closed.Message);
            Assert.That(notes.All, Is.Empty, "заметка обязана была закрыться вместе с файлом");
            Assert.That(ReplayNotes(bus), Does.Not.ContainKey("ezbozo"),
                "закрытая заметка осталась призраком в воспроизведении");
        });
    }

    [Test]
    public void RefusedNoteWriteReportsNothing()
    {
        var bus = new AgentEventBus(256);
        var notes = new PlayerNoteStore(_dir, Sawmill) { NoteLimit = 120 };
        notes.AttachSink(bus.ForProcess());
        notes.LoadFromDisk();

        notes.Add("Autumn Treeby", "короткая запись", Stamp);
        var afterFirst = bus.Seq;

        var overflow = notes.Add("Autumn Treeby", new string('щ', 200), Stamp);

        Assert.Multiple(() =>
        {
            Assert.That(overflow.Ok, Is.False, "запись обязана была не влезть — иначе тест пустой");
            Assert.That(bus.Seq, Is.EqualTo(afterFirst),
                "отказанная запись породила событие: клиент увидел бы запись, которой нет на диске");
        });
    }

    [Test]
    public void ReloadRepublishesEveryNote()
    {
        // The same argument as for skills: a reload is the only way a note disappears without an
        // event of its own. Here the file is removed from disk behind the store's back, the way a
        // human hand would do it.
        var bus = new AgentEventBus(256);

        var first = new PlayerNoteStore(_dir, Sawmill);
        first.LoadFromDisk();
        first.Add("Останется", "запись", Stamp);
        first.Add("Исчезнет", "запись", Stamp);

        File.Delete(Path.Combine(_dir, "people", "исчезнет.md"));

        var second = new PlayerNoteStore(_dir, Sawmill);
        second.AttachSink(bus.ForProcess());
        second.LoadFromDisk();

        var replayed = ReplayNotes(bus);

        Assert.Multiple(() =>
        {
            Assert.That(replayed, Does.ContainKey("останется"));
            Assert.That(replayed, Does.Not.ContainKey("исчезнет"),
                "удалённая руками заметка осталась бы призраком в любом клиенте");
        });
    }

    [Test]
    public void StoresWithNoSinkPublishNothing()
    {
        var bus = new AgentEventBus(64);

        var memory = new MemoryStore(_dir, Sawmill);
        memory.LoadFromDisk();
        memory.Add("запись");

        var skills = new DocTree(Path.Combine(_dir, "skills"), Sawmill);
        skills.Reload();
        skills.Write("скилл", "скилл", "когда-нибудь", "тело");

        var notes = new PlayerNoteStore(_dir, Sawmill);
        notes.LoadFromDisk();
        notes.Add("Кто-то", "запись", Stamp);

        Assert.That(bus.Seq, Is.Zero, "выключенная шина обязана стоить ровно ноль");
    }
}
