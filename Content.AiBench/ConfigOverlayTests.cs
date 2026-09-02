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
/// Накладка <c>ai_data/config.d/</c>: настройки конкретного сервера поверх прототипов форка.
///
/// <para>
/// Проверяются два свойства, и оба контринтуитивны настолько, что без теста их однажды сломают.
/// Первое — <b>замещение, а не слияние</b>: запись с существующим id меняет прототип ЦЕЛИКОМ, и
/// неуказанные поля садятся на умолчания типа, а не на значения из <c>Resources/</c>. Второе —
/// <b>сломанный файл не отменяет остальные</b>: накладка правится на живом сервере, и падать
/// целиком из-за забытой запятой она не должна.
/// </para>
/// <para>
/// Заодно накладка используется здесь как инструмент: она — единственный способ завести
/// прототип профиля из теста. У <c>AiLlmProfilePrototype.ID</c> приватный сеттер (его заполняет
/// сериализатор), так что собрать профиль конструктором нельзя.
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

    /// <summary>Профиля с таким id в <c>Resources/</c> нет — он целиком из накладки.</summary>
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
    /// Повторная запись замещает прототип целиком: неупомянутые поля возвращаются к умолчаниям.
    /// </summary>
    /// <remarks>
    /// Это самая дорогая ловушка накладки, и выглядит она безобидно. Человек, желающий поменять
    /// один адрес, пишет две строки — и молча теряет диалект: <c>LlamaCpp</c> становится
    /// <c>OpenAiCompat</c>, то есть <c>top_k</c>, <c>min_p</c> и <c>cache_prompt</c> перестают
    /// уходить, и кэш промпта отключается. В игре это выглядит как «стало медленнее», без единой
    /// ошибки в журнале.
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

        // «Хотел поменять только адрес».
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

    /// <summary>Сломанный файл не уносит с собой соседний исправный.</summary>
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

        // Стенд валит тест на любом ERROR из сервера — а здесь ERROR и есть ожидаемый результат:
        // накладка обязана громко жаловаться на неразобранный файл. Порог поднят на ОДИН вызов и
        // сразу возвращён: любая ошибка вне этих двух строк по-прежнему роняет тест.
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

    /// <summary>Отсутствующий каталог — обычное состояние свежего клона, а не ошибка.</summary>
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
    /// Проверка эндпоинта видит расхождения ДО того, как сходит в сеть.
    /// </summary>
    /// <remarks>
    /// Обе поломки, которые здесь ловятся, ничего не ломают при старте и ничего не пишут в журнал.
    /// Порог свёртки не меньше окна означает, что свёртка не сработает никогда — диалог дорастёт
    /// до края и ход кончится отказом провайдера. Таймаут не меньше <c>ai.llm_total_timeout</c>
    /// означает, что фаллбек не пробуется вовсе: голова цепочки, зависнув, съедает бюджет хода
    /// целиком. Второе стоило 01.09.2026 четырёх минут молчания ИИ в живом раунде.
    ///
    /// Эндпоинт заведомо мёртвый (порт 1): проверка обязана напечатать разбор чисел и без сети.
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

            // И живой запрос всё-таки был сделан: мёртвый порт обязан быть отличим от «не проверяли».
            Assert.That(text, Does.Contain("НЕ ДОШЛИ").Or.Contain("ТАЙМАУТ").Or.Contain("ОТКАЗ"),
                $"мёртвый эндпоинт не отмечен как недоступный:\n{text}");
        });
    }
}
