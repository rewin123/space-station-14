using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Config;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// The <c>ai_data/config.d/</c> overlay: server-specific settings layered on top of the fork's prototypes.
///
/// <para>
/// Two properties are checked here, and both are counterintuitive enough that without a test they
/// would eventually break. The first is <b>replacement, not merging</b>: a record with an existing
/// id replaces the prototype WHOLE, and unspecified fields fall back to the type's defaults rather
/// than to the values from <c>Resources/</c>. The second is <b>a broken file does not cancel the
/// rest</b>: the overlay is edited on a live server, and it must not fail entirely over one forgotten
/// comma.
/// </para>
/// <para>
/// The overlay also doubles here as a tool: it is the only way to bring up a profile prototype from
/// a test. <c>AiLlmProfilePrototype.ID</c> has a private setter (filled in by the serializer), so a
/// profile cannot be assembled through the constructor.
/// </para>
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class ConfigOverlayTests
{
    private const string Profile = "bench-overlay";

    private static void Write(string dataDir, string name, string yaml)
    {
        var dir = Path.Combine(dataDir, AiConfigOverlay.DirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), yaml);
    }

    /// <summary>No profile with this id exists in <c>Resources/</c> — it comes entirely from the overlay.</summary>
    [Test]
    public async Task Overlay_AddsAProfileMissingFromResources()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        var before = await w.Read(() => protoMan.HasIndex<AiLlmProfilePrototype>(Profile));
        Assert.That(before, Is.False, $"«{Profile}» уже есть в Resources/ — тест проверяет не то, что думает");

        Write(w.DataDir, "10-endpoints.yml", $"""
- type: aiLlmProfile
  id: {Profile}
  endpoint: http://127.0.0.1:8080/v1
  model: local-model
  dialect: LlamaCpp
  quota: Free
  ctxLimit: 65536
  timeoutSeconds: 42
""");

        var report = await w.Read(() => w.System.LoadOverlay(live: true));

        Assert.That(report.Failed, Is.Zero, "файл не разобрался");
        Assert.That(report.Files.Single().Prototypes, Does.Contain($"aiLlmProfile: {Profile}"),
            "отчёт не назвал прототип, который файл завёл");

        var loaded = await w.Read(() => protoMan.Index<AiLlmProfilePrototype>(Profile));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Endpoint, Is.EqualTo("http://127.0.0.1:8080/v1"));
            Assert.That(loaded.Model, Is.EqualTo("local-model"));
            Assert.That(loaded.Dialect, Is.EqualTo(LlmDialect.LlamaCpp));
            Assert.That(loaded.CtxLimit, Is.EqualTo(65536));
            Assert.That(loaded.TimeoutSeconds, Is.EqualTo(42f));
        });
    }

    /// <summary>
    /// Re-writing replaces the prototype whole: unmentioned fields revert to their defaults.
    /// </summary>
    /// <remarks>
    /// This is the overlay's most expensive pitfall, and it looks harmless. Someone wanting to
    /// change a single address writes two lines — and silently loses the dialect: <c>LlamaCpp</c>
    /// becomes <c>OpenAiCompat</c>, meaning <c>top_k</c>, <c>min_p</c> and <c>cache_prompt</c> stop
    /// being sent, and the prompt cache gets disabled. In-game this shows up as "things got slower",
    /// with not a single error in the log.
    /// </remarks>
    [Test]
    public async Task Overlay_ReplacesThePrototypeWhole()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        Write(w.DataDir, "10-endpoints.yml", $"""
- type: aiLlmProfile
  id: {Profile}
  endpoint: http://127.0.0.1:8080/v1
  model: local-model
  dialect: LlamaCpp
  timeoutSeconds: 42
""");

        await w.Read(() => w.System.LoadOverlay(live: true));

        var first = await w.Read(() => protoMan.Index<AiLlmProfilePrototype>(Profile));
        Assert.That(first.Dialect, Is.EqualTo(LlmDialect.LlamaCpp), "первая загрузка не применилась");

        // "I only wanted to change the address."
        Write(w.DataDir, "10-endpoints.yml", $"""
- type: aiLlmProfile
  id: {Profile}
  endpoint: http://192.168.1.50:8080/v1
  model: local-model
""");

        await w.Read(() => w.System.LoadOverlay(live: true));

        var second = await w.Read(() => protoMan.Index<AiLlmProfilePrototype>(Profile));

        Assert.Multiple(() =>
        {
            Assert.That(second.Endpoint, Is.EqualTo("http://192.168.1.50:8080/v1"));

            Assert.That(second.Dialect, Is.EqualTo(LlmDialect.OpenAiCompat),
                "диалект обязан вернуться к умолчанию типа: накладка замещает прототип, а не сливает поля. " +
                "Если это изменилось — перепишите docs/reconfig.md, там на замещении построен целый раздел");

            Assert.That(second.TimeoutSeconds, Is.Zero,
                "таймаут обязан вернуться к нулю (то есть к ai.request_timeout), а не сохраниться с прошлой загрузки");
        });
    }

    /// <summary>A broken file does not take its valid neighbor down with it.</summary>
    [Test]
    public async Task Overlay_KeepsGoingPastABrokenFile()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        Write(w.DataDir, "10-broken.yml", "- type: aiLlmProfile\n  id: bench-broken\n   endpoint: нет\n\t\tмусор\n");

        Write(w.DataDir, "20-good.yml", $"""
- type: aiLlmProfile
  id: {Profile}
  endpoint: http://127.0.0.1:8080/v1
  model: local-model
""");

        // The bench fails a test on any ERROR from the server — but here an ERROR is exactly the
        // expected result: the overlay must complain loudly about a file it could not parse. The
        // threshold is raised for exactly ONE call and immediately restored: any error outside
        // these two lines still fails the test.
        var previous = w.Pair.ServerLogHandler.FailureLevel;
        w.Pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;

        AiConfigOverlay.OverlayReport report;

        try
        {
            report = await w.Read(() => w.System.LoadOverlay(live: true));
        }
        finally
        {
            w.Pair.ServerLogHandler.FailureLevel = previous;
        }

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.EqualTo(1), "сломанный файл не отмечен ошибкой");
            Assert.That(report.Ok, Is.EqualTo(1), "исправный файл не прочитан");

            var broken = report.Files.Single(f => f.Name == "10-broken.yml");
            Assert.That(broken.Error, Is.Not.Null.And.Not.Empty, "ошибка не попала в отчёт — чинить будет нечего");
        });

        var loaded = await w.Read(() => protoMan.HasIndex<AiLlmProfilePrototype>(Profile));
        Assert.That(loaded, Is.True, "исправный файл не применился из-за соседа");
    }

    /// <summary>A missing directory is the normal state of a fresh clone, not an error.</summary>
    [Test]
    public async Task Overlay_IsQuietWithoutTheDirectory()
    {
        await using var w = await AiStation.Create();

        var dir = Path.Combine(w.DataDir, AiConfigOverlay.DirName);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);

        var report = await w.Read(() => w.System.LoadOverlay(live: true));

        Assert.Multiple(() =>
        {
            Assert.That(report.DirExists, Is.False);
            Assert.That(report.Files, Is.Empty);
            Assert.That(report.Failed, Is.Zero);
        });
    }

    /// <summary>
    /// The endpoint probe catches discrepancies BEFORE it ever goes out onto the network.
    /// </summary>
    /// <remarks>
    /// Both breakages caught here break nothing at startup and write nothing to the log. A
    /// compaction threshold no smaller than the context window means compaction will never fire —
    /// the dialogue will grow to the edge and the turn will end in a provider refusal. A timeout no
    /// smaller than <c>ai.llm_total_timeout</c> means the fallback is never tried at all: the head
    /// of the chain, hung, eats the entire turn budget. The second one cost four minutes of AI
    /// silence in a live round on 01.09.2026.
    ///
    /// The endpoint is deliberately dead (port 1): the probe must print the number analysis even
    /// with no network available.
    /// </remarks>
    [Test]
    public async Task Probe_NamesTheTwoSilentMisconfigurations()
    {
        await using var w = await AiStation.Create();

        Write(w.DataDir, "10-endpoints.yml", $"""
- type: aiLlmProfile
  id: {Profile}
  endpoint: http://127.0.0.1:1/v1
  model: local-model
  dialect: LlamaCpp
  quota: Free
  proxy: None
  ctxLimit: 1000
  compactHigh: 2000
  timeoutSeconds: 300
""");

        await w.Read(() => w.System.LoadOverlay(live: true));

        var found = await w.Read(() =>
        {
            var ok = w.System.TryProfile(Profile, out var profile, out var endpoint, out var sampling);
            return (ok, profile, endpoint, sampling);
        });

        Assert.That(found.ok, Is.True, "профиль из накладки не собрался в эндпоинт");

        var lines = await LlmProbe.RunAsync(
            found.profile, found.endpoint, found.sampling,
            keyNote: "не нужен",
            compactHighDefault: 96000,
            totalTimeout: 240f,
            new LogManager().GetSawmill("probe-test"),
            CancellationToken.None);

        var text = string.Join("\n", lines);

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("ПОРОГ СВЁРТКИ НЕ СРАБОТАЕТ"),
                $"compactHigh 2000 при ctxLimit 1000 не назван поломкой:\n{text}");

            Assert.That(text, Does.Contain("ФАЛЛБЕК НЕ ПРОБУЕТСЯ"),
                $"timeoutSeconds 300 при бюджете хода 240 не назван поломкой:\n{text}");

            // And a live request was actually made: a dead port must be distinguishable from "was not checked".
            Assert.That(text, Does.Contain("НЕ ДОШЛИ").Or.Contain("ТАЙМАУТ").Or.Contain("ОТКАЗ"),
                $"мёртвый эндпоинт не отмечен как недоступный:\n{text}");
        });
    }
}
