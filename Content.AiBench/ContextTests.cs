using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Phase 3: the three zones, the compaction ritual and the prefix-cache watchdog.
///
/// These run without a server: <see cref="ConversationState"/>, <see cref="Compactor"/> and
/// <see cref="CacheMetrics"/> deliberately have no <c>IEntityManager</c> in scope, which is what
/// makes them testable in milliseconds rather than in the fifty seconds a pooled pair costs.
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class ContextTests
{
    private static ConversationState Fresh()
    {
        var conv = new ConversationState();
        conv.SetPrefix("СИСТЕМНЫЙ ПРОМПТ", "[]");
        return conv;
    }

    private static void AddTurn(ConversationState conv, string observation, string tool = null)
    {
        conv.AppendUser(observation);

        if (tool == null)
        {
            conv.AppendAssistant(new LlmResponse("ответ", System.Array.Empty<ToolCallDto>(), 10, 9, 1, 0.1));
            return;
        }

        var id = conv.NextCallId();
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto { Id = id, Function = new FunctionCallDto { Name = tool, Arguments = "{}" } },
        }, 10, 9, 1, 0.1));
        conv.AppendToolResult(id, """{"ok":true}""");
    }

    // ------------------------------------------------------------------ protocol

    [Test]
    public void CloseTurn_AnswersDanglingToolCall()
    {
        var conv = Fresh();
        conv.AppendUser("наблюдение");
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto { Id = "call_1", Function = new FunctionCallDto { Name = "say", Arguments = "{}" } },
        }, 10, 9, 1, 0.1));

        Assert.That(conv.HasOpenToolCalls, Is.True, "висящий вызов должен быть виден");

        conv.CloseTurn();

        Assert.That(conv.HasOpenToolCalls, Is.False, "после CloseTurn висящих вызовов быть не должно");
        Assert.That(conv.Body.Last().Role, Is.EqualTo("tool"));
        Assert.That(conv.Body.Last().Content, Does.Contain("turn_budget"));
    }

    [Test]
    public void TurnBoundaries_NeverSplitAToolCallFromItsResult()
    {
        var conv = Fresh();
        for (var i = 0; i < 5; i++)
            AddTurn(conv, $"наблюдение {i}", "look");

        foreach (var idx in conv.TurnBoundaries())
        {
            Assert.That(conv.Body[idx].Role, Is.EqualTo("user"),
                $"граница {idx} указывает не на начало хода");

            // Cutting here must leave a self-consistent conversation.
            var probe = Fresh();
            probe.ReplaceBody("сводка", conv.BodyFrom(idx));
            Assert.That(probe.HasOpenToolCalls, Is.False,
                $"разрез на границе {idx} осиротил tool-сообщение");
        }
    }

    // --------------------------------------------------------------- compaction

    private static Compactor MakeCompactor(ILlmClient llm, int high, int low, int keepTail) =>
        new(llm, new CompactionOptions
        {
            High = () => high,
            Low = () => low,
            KeepTail = () => keepTail,
        }, new Robust.Shared.Log.LogManager().GetSawmill("test"));

    [Test]
    public void ShouldCompact_HasHysteresis()
    {
        var conv = Fresh();
        var compactor = MakeCompactor(new ScriptedLlmClient(), high: 1000, low: 500, keepTail: 100);

        conv.LastPromptTokens = 1200;
        Assert.That(compactor.ShouldCompact(conv), Is.True, "выше порога — должна сработать");

        // Simulate having compacted: disarmed until usage falls back below the floor.
        conv.LastPromptTokens = 1100;
        typeof(Compactor).GetField("_armed", System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance)!
            .SetValue(compactor, false);

        Assert.That(compactor.ShouldCompact(conv), Is.False,
            "без гистерезиса компакция срабатывала бы каждый ход у самого порога");

        conv.LastPromptTokens = 400;
        compactor.ShouldCompact(conv);            // re-arms
        conv.LastPromptTokens = 1200;
        Assert.That(compactor.ShouldCompact(conv), Is.True, "после спада должна снова взводиться");
    }

    [Test]
    public void ShouldCompact_WaitsForOpenToolCalls()
    {
        var conv = Fresh();
        var compactor = MakeCompactor(new ScriptedLlmClient(), high: 10, low: 5, keepTail: 10);

        conv.AppendUser("наблюдение");
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto { Id = "call_1", Function = new FunctionCallDto { Name = "say", Arguments = "{}" } },
        }, 10, 9, 1, 0.1));
        conv.LastPromptTokens = 1000;

        Assert.That(compactor.ShouldCompact(conv), Is.False,
            "с открытым вызовом безопасной границы впереди нет");
    }

    [Test]
    public async Task Compact_FoldsBody_KeepsProtocol_AndRebuildsPrefix()
    {
        var conv = Fresh();
        for (var i = 0; i < 30; i++)
            AddTurn(conv, $"наблюдение номер {i} с некоторым количеством текста", "look");

        var before = conv.Body.Count;
        conv.LastPromptTokens = 5000;

        var llm = new ScriptedLlmClient().Then("Я следил за станцией, ничего критичного не произошло.");
        var compactor = MakeCompactor(llm, high: 1000, low: 500, keepTail: 200);

        var announced = (string)null;
        var ok = await compactor.CompactAsync(
            conv,
            text => { announced = text; return Task.CompletedTask; },
            () => ("СИСТЕМНЫЙ ПРОМПТ", "[]"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, "компакция должна была пройти");
            Assert.That(conv.Body.Count, Is.LessThan(before), "тело должно уменьшиться");
            Assert.That(conv.HasOpenToolCalls, Is.False, "компакция не должна осиротить tool-сообщения");
            Assert.That(conv.Body.First().Content, Does.Contain("СВОДКА"), "первым сообщением должна быть сводка");
            Assert.That(announced, Is.Not.Null, "ИИ обязан сказать экипажу, что чистит память");
            Assert.That(announced, Does.Contain("памяти"));
            Assert.That(compactor.Compactions, Is.EqualTo(1));
            Assert.That(conv.LastPromptTokens, Is.Zero,
                "устаревшее значение не должно немедленно вызвать вторую компакцию");
        });
    }

    [Test]
    public async Task Compact_SurvivesSummariserFailure()
    {
        var conv = Fresh();
        for (var i = 0; i < 20; i++)
            AddTurn(conv, $"наблюдение {i} подлиннее чтобы набрать символов", "look");

        conv.LastPromptTokens = 5000;

        // A summariser that returns nothing: the middle is lost, and the text must say so rather
        // than pretending the history is intact.
        var compactor = MakeCompactor(new ThrowingLlmClient(), high: 1000, low: 500, keepTail: 200);

        var ok = await compactor.CompactAsync(conv, _ => Task.CompletedTask,
            () => ("СИСТЕМНЫЙ ПРОМПТ", "[]"), CancellationToken.None);

        Assert.That(ok, Is.True, "падение суммаризатора не должно ронять компакцию");
        Assert.That(conv.Body.First().Content, Does.Contain("не удалось составить сводку"));
    }

    // ----------------------------------------------------------- prefix watchdog

    [Test]
    public void CacheMetrics_AlarmsOnPrefixChange()
    {
        var sawmill = new Robust.Shared.Log.LogManager().GetSawmill("test");
        var metrics = new CacheMetrics(sawmill);
        metrics.SetExpectedPrefix("AAAA");

        Assert.That(metrics.Record(1000, 990, "AAAA"), Is.True);
        Assert.That(metrics.Record(1000, 990, "BBBB"), Is.False, "смена хэша вне компакции — это баг");
        Assert.That(metrics.Alarms, Is.EqualTo(1));
    }

    [Test]
    public void CacheMetrics_AlarmsOnlyAfterTwoLowTurns()
    {
        var sawmill = new Robust.Shared.Log.LogManager().GetSawmill("test");
        var metrics = new CacheMetrics(sawmill);
        metrics.SetExpectedPrefix("AAAA");

        metrics.Record(1000, 990, "AAAA");                                  // turn 1, always exempt
        Assert.That(metrics.Record(1000, 100, "AAAA"), Is.True,
            "один провал бывает законно — большое наблюдение");
        Assert.That(metrics.Record(1000, 100, "AAAA"), Is.False,
            "два подряд — это уже дрейф префикса");
    }

    [Test]
    public void CacheMetrics_ForgivesTheTurnAfterCompaction()
    {
        var sawmill = new Robust.Shared.Log.LogManager().GetSawmill("test");
        var metrics = new CacheMetrics(sawmill);
        metrics.SetExpectedPrefix("AAAA");

        metrics.Record(1000, 990, "AAAA");
        metrics.ExpectMiss = true;

        Assert.That(metrics.Record(1000, 0, "AAAA"), Is.True,
            "после компакции промах ожидаем и тревогу поднимать нельзя");
    }

    // ------------------------------------------------------------- calibration

    [Test]
    public void Calibrate_UsesServerReport_NotFolkWisdom()
    {
        var conv = Fresh();
        conv.AppendUser(new string('я', 3000));

        conv.Calibrate(500);

        Assert.That(conv.CharsPerToken, Is.GreaterThan(3.0),
            "калибровка должна была подняться выше стартовых 3.0");
    }

    [Test]
    public void Calibrate_IgnoresNonsense()
    {
        var conv = Fresh();
        conv.AppendUser("коротко");
        var before = conv.CharsPerToken;

        conv.Calibrate(1_000_000);   // absurd: would give a ratio near zero

        Assert.That(conv.CharsPerToken, Is.EqualTo(before), "дикий отсчёт не должен ломать оценку");
    }
}

/// <summary>A client that always fails, for proving the compactor degrades rather than crashes.</summary>
internal sealed class ThrowingLlmClient : ILlmClient
{
    public Task<LlmResponse> ChatAsync(System.Collections.Generic.IReadOnlyList<ChatMessageDto> messages,
        System.Collections.Generic.IReadOnlyList<ToolDto> tools, CancellationToken ct) =>
        throw new LlmException("эндпоинт недоступен");

    public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(null);
}
