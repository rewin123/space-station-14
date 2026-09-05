using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// The channel switch and carded parity.
///
/// The second half is not cosmetic: the intellicard exists specifically to take the station away
/// from the AI, and half the point of carding is that it can no longer call Security. The
/// <c>radio</c> tool validated the channel against a static list and did not look at the mode at
/// all, and <c>RadioSystem</c> does not check whether the source has a transmitter — only the
/// recipients' channels. So a carded AI kept talking into Security from the pocket of whoever
/// carried it off.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class ChannelTests
{
    [Test]
    public async Task RadioWithoutChannelUsesTheSwitch()
    {
        await using var w = await AiWorld.Create();

        // By default the switch is on Common.
        var first = await w.Invoke("radio", """{"text":"проверка связи"}""");

        Assert.Multiple(() =>
        {
            Assert.That(first.Ok, Is.True, first.ToJson());
            Assert.That(first.ToJson(), Does.Contain("Common"),
                "без явного канала речь обязана уходить в текущий");
        });

        var switched = await w.Invoke("set_channel", """{"channel":"Engineering"}""");
        Assert.That(switched.Ok, Is.True, switched.ToJson());

        var second = await w.Invoke("radio", """{"text":"вторая проверка"}""");

        Assert.Multiple(() =>
        {
            Assert.That(second.Ok, Is.True, second.ToJson());
            Assert.That(second.ToJson(), Does.Contain("Engineering"), "переключатель не подействовал");
        });
    }

    [Test]
    public async Task ExplicitChannelDoesNotMoveTheSwitch()
    {
        // Exactly like a live player's prefix: a one-off message on another channel doesn't move
        // the selection.
        await using var w = await AiWorld.Create();

        await w.Invoke("set_channel", """{"channel":"Security"}""");
        await w.Invoke("radio", """{"channel":"Common","text":"разовое обращение"}""");

        var after = await w.Invoke("radio", """{"text":"а это снова в свой канал"}""");

        Assert.That(after.ToJson(), Does.Contain("Security"),
            "разовый канал сдвинул тумблер — тогда это не тумблер, а память о последней реплике");
    }

    [Test]
    public async Task CurrentChannelIsVisibleInSelfLine()
    {
        // The switch is acceptable only because its position is visible on EVERY turn. Otherwise
        // it would be hidden state, and the model would sooner or later send a conversation about
        // the traitor onto the general channel.
        await using var w = await AiWorld.Create();

        await w.Invoke("set_channel", """{"channel":"Medical"}""");

        var self = await w.Read(() => w.System.BuildObservationForTest(w.Brain));

        Assert.That(self, Does.Contain("канал=Medical"),
            "положение переключателя обязано печататься в SELF");
    }

    [Test]
    public async Task CardedAiCannotSpeakOutsideBinary()
    {
        await using var w = await AiWorld.Create();

        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = AgentMode.Carded);

        var security = await w.Invoke("radio", """{"channel":"Security","text":"вызываю СБ из кармана"}""");

        Assert.Multiple(() =>
        {
            Assert.That(security.Ok, Is.False,
                "закарденный ИИ вызвал СБ — карденье перестало что-либо значить");
            Assert.That(security.ToJson(), Does.Contain("carded").Or.Contain("интелликарт"),
                "отказ должен объяснять ПРИЧИНУ, иначе модель будет искать опечатку в названии канала");
        });

        var binary = await w.Invoke("radio", """{"channel":"Binary","text":"силиконам"}""");
        Assert.That(binary.Ok, Is.True, binary.ToJson());
    }

    [Test]
    public async Task CardingSnapsTheSwitchAndSaysSo()
    {
        await using var w = await AiWorld.Create();

        await w.Invoke("set_channel", """{"channel":"Command"}""");
        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = AgentMode.Carded);

        // Speech without an explicit channel must NOT fail just because the switch was left on
        // Command: the model would get a refusal about a channel it never named, and would go
        // looking for a typo in it.
        var after = await w.Invoke("radio", """{"text":"я в карте"}""");

        Assert.Multiple(() =>
        {
            Assert.That(after.Ok, Is.True,
                $"после карденья речь по умолчанию сломалась: {after.ToJson()}");
            Assert.That(after.ToJson(), Does.Contain("Binary"),
                "должна была съехать на единственный доступный канал");
        });
    }

    [Test]
    public async Task SetChannelRefusesUnknownChannelWithSuggestions()
    {
        await using var w = await AiWorld.Create();

        var bad = await w.Invoke("set_channel", """{"channel":"Инженерный"}""");

        Assert.Multiple(() =>
        {
            Assert.That(bad.Ok, Is.False);
            Assert.That(bad.ToJson(), Does.Contain("alternatives").Or.Contain("Engineering"),
                "отказ обязан предлагать похожие каналы, а не просто говорить «нет»");
        });
    }
}

/// <summary>
/// What survives the round.
///
/// This used to check that CREW.md gets wiped at the shift debrief while MEMORY.md does not. The
/// second file no longer exists: the intent was to fight metagaming, and on a live server it
/// produced the opposite — the agent stopped writing into something that would get erased anyway,
/// piled people into MEMORY.md, and ran it up against the limit.
///
/// Now everything survives: memory about the station stays memory about the station, and people
/// live in one file per person in PlayerNoteStore, with a round stamp on every entry. What's
/// pinned here is the new invariant — memory is wiped by NOTHING and makes it to disk in full.
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class MemoryLifetimeTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("memory-lifetime-test");

    [Test]
    public void StationMemorySurvivesAReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aibench-memlife-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var memory = new MemoryStore(dir, Sawmill);
            memory.LoadFromDisk();
            memory.Add("APC ядра виден в look, но недоступен для move_camera");

            var reloaded = new MemoryStore(dir, Sawmill);
            reloaded.LoadFromDisk();

            Assert.That(reloaded.Entries(), Has.Count.EqualTo(1),
                "знание о станции обязано переживать перезапуск — ради него память и заводилась");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
