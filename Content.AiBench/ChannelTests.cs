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
/// Переключатель канала и паритет закарденного.
///
/// Второе — не косметика: интелликарта существует ровно затем, чтобы отнять у ИИ станцию, и
/// половина смысла карденья в том, что он больше не может вызвать СБ. Инструмент <c>radio</c>
/// валидировал канал по статическому списку и на режим не смотрел вовсе, а <c>RadioSystem</c>
/// не проверяет наличие передатчика у источника — только каналы получателей. Значит закарденный
/// ИИ продолжал говорить в Security из кармана того, кто его унёс.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class ChannelTests
{
    [Test]
    public async Task RadioWithoutChannelUsesTheSwitch()
    {
        await using var w = await AiWorld.Create();

        // По умолчанию тумблер на Common.
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
        // Ровно как префикс у живого игрока: разовое обращение в другой канал не сбивает выбор.
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
        // Тумблер допустим только потому, что его положение видно КАЖДЫЙ ход. Иначе это скрытое
        // состояние, и модель однажды отправит разговор о предателе в общий канал.
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

        // Речь без явного канала НЕ должна упасть только потому, что тумблер остался на Command:
        // модель получила бы отказ про канал, которого не называла, и пошла бы искать в нём опечатку.
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
/// Что переживает раунд.
///
/// Раньше здесь проверялось, что CREW.md стирается на разборе смены, а MEMORY.md — нет. Второго
/// файла больше нет: замысел был против метагейминга, а на живом сервере дал обратное — агент
/// перестал писать в то, что всё равно сотрут, сложил людей в MEMORY.md и упёр его в лимит.
///
/// Теперь переживает всё: память о станции остаётся памятью о станции, а люди живут по файлу на
/// человека в PlayerNoteStore, со штампом раунда у каждой записи. Здесь пинается новый инвариант —
/// память НИЧЕМ не стирается и целиком доезжает до диска.
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
