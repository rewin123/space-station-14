using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;
using Content.Server.AiAgent;
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

    private static Compactor MakeCompactor(
        ILlmClient llm, int high, int keepEvents, Journal journal = null)
    {
        var sawmill = new Robust.Shared.Log.LogManager().GetSawmill("test");
        return new Compactor(llm, new CompactionOptions
        {
            High = () => high,
            KeepEvents = () => keepEvents,
        }, new CacheMetrics(sawmill), sawmill, journal);
    }

    private static Robust.Shared.Log.ISawmill Sawmill => new Robust.Shared.Log.LogManager().GetSawmill("test");

    /// <summary>The three doors the ritual has outward, with sane no-ops for the ones under test.</summary>
    private static CompactionHooks Hooks(
        Func<string, Task> announce = null,
        Func<Task> curate = null,
        Func<(string, string)> prefix = null) =>
        new()
        {
            Announce = announce ?? (_ => Task.CompletedTask),
            RebuildPrefix = prefix ?? (() => ("СИСТЕМНЫЙ ПРОМПТ", "[]")),
            Curate = curate,
        };

    /// <summary>A state wrapping a prepared conversation, since the ritual now takes the whole agent.</summary>
    private static AgentState StateOf(ConversationState conv)
    {
        var state = new AgentState();
        state.Conv.SetPrefix(conv.SystemPrompt, conv.ToolsJson);
        state.Conv.RestoreBody(conv.Body, conv.VolatileTail, conv.CharsPerToken);
        state.Conv.LastPromptTokens = conv.LastPromptTokens;
        return state;
    }

    [Test]
    public void ShouldCompact_OnThresholdAlone()
    {
        // The low-water mark and the arming flag are gone.
        //
        // They existed to stop a conversation hovering at the limit from folding every turn, and
        // they did it by refusing to re-arm until usage fell back under the floor — which assumed a
        // fold can always get that far down. It cannot, and on a live shift one fold left 162k
        // against a floor of 45k: the agent never armed again, climbed to 236k and died there.
        //
        // Nothing replaces it because the fold now discards the body wholesale, so it lands far
        // under the threshold by construction, and the commit zeroes the reading so the next
        // decision is made on a fresh one.
        var state = new AgentState();
        var compactor = MakeCompactor(new ScriptedLlmClient(), high: 1000, keepEvents: 40);

        state.Conv.LastPromptTokens = 1200;
        Assert.That(compactor.ShouldCompact(state), Is.True, "выше порога — свернуть");

        state.Conv.LastPromptTokens = 999;
        Assert.That(compactor.ShouldCompact(state), Is.False, "ниже порога — не трогать");

        // What the commit leaves behind, and what makes an immediate second fold impossible.
        state.Conv.LastPromptTokens = 0;
        Assert.That(compactor.ShouldCompact(state), Is.False,
            "сразу после свёртки решать не на чем: показание обнулено до следующего запроса");
    }

    [Test]
    public void ShouldCompact_WaitsForOpenToolCalls()
    {
        var conv = Fresh();
        var compactor = MakeCompactor(new ScriptedLlmClient(), high: 10, keepEvents: 40);

        conv.AppendUser("наблюдение");
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto { Id = "call_1", Function = new FunctionCallDto { Name = "say", Arguments = "{}" } },
        }, 10, 9, 1, 0.1));
        conv.LastPromptTokens = 1000;

        Assert.That(compactor.ShouldCompact(StateOf(conv)), Is.False,
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
        var compactor = MakeCompactor(llm, high: 1000, keepEvents: 40);

        var announced = (string)null;
        var state = StateOf(conv);
        var ok = await compactor.CompactAsync(state, System.Array.Empty<ToolDto>(),
            Hooks(announce: text => { announced = text; return Task.CompletedTask; }),
            "T+0:05:00", CancellationToken.None);

        conv = state.Conv;

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, "компакция должна была пройти");
            Assert.That(conv.Body.Count, Is.LessThan(before), "тело должно уменьшиться");
            Assert.That(conv.HasOpenToolCalls, Is.False, "компакция не должна осиротить tool-сообщения");
            Assert.That(conv.Body.First().Content, Does.Contain("СВОДКА"), "первым сообщением должна быть сводка");
            Assert.That(announced, Is.Not.Null, "ИИ обязан сказать экипажу, что чистит память");
            Assert.That(announced, Does.Contain("памяти"));
            Assert.That(state.Compactions, Is.EqualTo(1));
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
        var compactor = MakeCompactor(new ThrowingLlmClient(), high: 1000, keepEvents: 40);

        var state = StateOf(conv);
        var ok = await compactor.CompactAsync(state, System.Array.Empty<ToolDto>(), Hooks(),
            "T+0:05:00", CancellationToken.None);

        Assert.That(ok, Is.True, "падение суммаризатора не должно ронять компакцию");
        Assert.That(state.Conv.Body.First().Content, Does.Contain("не удалось составить сводку"));
    }

    [Test]
    public async Task Compact_SummariserSendsTheSameToolArray()
    {
        // Regression guard for a real, expensive bug found on the first live run.
        //
        // Sending null tools for the summariser looked harmless — it has nothing to do in the
        // world. But tool definitions are rendered into the system block by the chat template, so
        // dropping them makes the prompt diverge at token ZERO. It cost two full prefills (one for
        // the summariser, one for the next real turn) and the watchdog logged 0% cache twice in a
        // row. The tool array must be byte-identical on every single call.
        var conv = Fresh();
        for (var i = 0; i < 20; i++)
            AddTurn(conv, $"наблюдение {i} с достаточным количеством текста для набора символов", "look");

        conv.LastPromptTokens = 5000;

        var llm = new RecordingLlmClient();
        var compactor = MakeCompactor(llm, high: 1000, keepEvents: 40);

        var tools = new[] { new ToolDto { Function = new ToolFunctionDto { Name = "look" } } };

        await compactor.CompactAsync(StateOf(conv), tools, Hooks(), "T+0:05:00", CancellationToken.None);

        Assert.That(llm.LastTools, Is.Not.Null,
            "суммаризатор ушёл без инструментов — это рвёт префикс с нулевого токена");
        Assert.That(llm.LastTools!.Count, Is.EqualTo(1));
        Assert.That(llm.LastTools[0].Function.Name, Is.EqualTo("look"));
    }

    [Test]
    public async Task Compact_AnnouncesBeforeTheSlowWork()
    {
        // Ordering guard. The curator is several model calls back to back — about a minute in
        // practice — and the summariser adds another. If the announcement came after them, the
        // crew would talk to a silent AI for that whole stretch and only then be told why. An
        // explanation that arrives after the silence is not an explanation.
        var conv = Fresh();
        for (var i = 0; i < 20; i++)
            AddTurn(conv, $"наблюдение {i} с достаточным количеством текста", "look");

        conv.LastPromptTokens = 5000;

        var order = new System.Collections.Generic.List<string>();
        var llm = new ScriptedLlmClient().Then("сводка");
        var compactor = MakeCompactor(llm, high: 1000, keepEvents: 40);

        await compactor.CompactAsync(StateOf(conv), System.Array.Empty<ToolDto>(),
            Hooks(announce: _ => { order.Add("announce"); return Task.CompletedTask; },
                  curate: () => { order.Add("curator"); return Task.CompletedTask; }),
            "T+0:05:00", CancellationToken.None);

        Assert.That(order, Is.EqualTo(new[] { "announce", "curator" }),
            "объявление обязано идти ДО куратора — оно объясняет паузу, которую куратор и создаёт");
    }

    [Test]
    public async Task Compact_FoldsOntoEventLines_NotOntoMessages()
    {
        // The change this whole rework exists for.
        //
        // The fold used to keep the last few messages, and messages carry their payloads: one look
        // of a crowded room is thousands of tokens of crates, and keeping it meant paying for that
        // room until the round ended. A measured turn took the conversation from 27k to 183k this
        // way, and the fold that followed could not get back under any threshold because the weight
        // was inside the part it was keeping.
        var conv = Fresh();
        var journal = new Journal(null, Sawmill);

        journal.Write("obs", new System.Collections.Generic.Dictionary<string, object?> { ["text"] = "RADIO Common | Autumn: где аномалия" });
        journal.Write("tool", new System.Collections.Generic.Dictionary<string, object?>
        {
            ["name"] = "look", ["args"] = """{"kind":"obj"}""", ["ok"] = true,
        });

        // The payload that must NOT survive the fold, sitting in the live conversation.
        conv.AppendUser("наблюдение");
        var id = conv.NextCallId();
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto { Id = id, Function = new FunctionCallDto { Name = "look", Arguments = "{}" } },
        }, 10, 9, 1, 0.1));
        conv.AppendToolResult(id, """{"ok":true,"seen":["ЯЩИК-СО-ХЛАМОМ"]}""");
        conv.LastPromptTokens = 5000;

        var compactor = MakeCompactor(new ScriptedLlmClient().Then("сводка"), high: 1000,
            keepEvents: 40, journal: journal);

        // StateOf copies the conversation into a state of its own, so the fold happens there.
        var state = StateOf(conv);

        var ok = await compactor.CompactAsync(state, System.Array.Empty<ToolDto>(),
            Hooks(), "T+0:05:00", CancellationToken.None);

        Assert.That(ok, Is.True);

        var folded = string.Join("\n", state.Conv.Build().Select(m => m.Content ?? ""));

        Assert.Multiple(() =>
        {
            Assert.That(folded, Does.Contain("сводка"), "сводка обязана остаться");
            Assert.That(folded, Does.Contain("где аномалия"),
                "услышанное — это то, что агент не восстановит ни памятью, ни повторным взглядом");
            Assert.That(folded, Does.Contain("look"), "какие инструменты звал — тоже");

            Assert.That(folded, Does.Not.Contain("ЯЩИК-СО-ХЛАМОМ"),
                "а вот содержимое ответа look тащить дальше нельзя — ровно из-за него контекст и рос");
        });
    }

    [Test]
    public async Task Compact_StaysQuietWhenThereIsNothingToCut()
    {
        // The other half of the ordering rule: feasibility is checked before the announcement, so
        // the AI never tells the crew it is cleaning memory and then cancels.
        //
        // What counts as infeasible changed. It used to be "one long turn, so there is no second
        // boundary to cut at" — and that case is now the most valuable fold there is, because a
        // single turn is exactly how the conversation once grew by 156k tokens. The body is
        // replaced wholesale rather than sliced, so the only thing left that cannot be folded is
        // an empty one.
        var conv = Fresh();
        conv.LastPromptTokens = 5000;

        var announced = false;
        var curated = false;
        var compactor = MakeCompactor(new ScriptedLlmClient(), high: 1000, keepEvents: 40);

        var ok = await compactor.CompactAsync(StateOf(conv), System.Array.Empty<ToolDto>(),
            Hooks(announce: _ => { announced = true; return Task.CompletedTask; },
                  curate: () => { curated = true; return Task.CompletedTask; }),
            "T+0:05:00", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(announced, Is.False, "нельзя обещать чистку памяти и потом её отменить");
            Assert.That(curated, Is.False, "и нечего гонять куратора минуту впустую");
        });
    }


    [Test]
    public void Compact_RitualOrderIsTheArray()
    {
        // The order used to be the order of statements in an eighty-eight-line method with the
        // steps marked by comment banners, and only one of its edges had a test. Nothing but that
        // layout stopped somebody moving the announcement below the curator — which is exactly the
        // bug an earlier commit had to fix.
        Assert.That(CompactionSteps.Ritual.Select(s => s.Name),
            Is.EqualTo(new[] { "feasibility", "announce", "curator", "summary", "fold", "prefix", "commit" }));
    }

    [Test]
    public void Compact_OnlyTheOutwardStepsMayFail()
    {
        // Announcing, reviewing and summarising may fail — the context still has to shrink, told or
        // untold, learned lesson or not. Folding and rebuilding may not: a failure there leaves the
        // conversation half-rewritten.
        var byName = CompactionSteps.Ritual.ToDictionary(s => s.Name, s => s.Fatal);

        Assert.Multiple(() =>
        {
            Assert.That(byName["announce"], Is.False);
            Assert.That(byName["curator"], Is.False);
            Assert.That(byName["summary"], Is.False);
            Assert.That(byName["fold"], Is.True);
            Assert.That(byName["prefix"], Is.True);
            Assert.That(byName["commit"], Is.True);
        });
    }

    [Test]
    public async Task Compact_FailedPrefixRebuild_LeavesEverythingConsistent()
    {
        // The failure mode the finally exists for. The body was already folded, the prefix stale,
        // arming still true and the token reading unchanged — so the very next turn tried to compact
        // an already-folded body, and the cache watchdog screamed about a prefix change that our own
        // half-finished ritual had caused.
        var conv = Fresh();
        for (var i = 0; i < 20; i++)
            AddTurn(conv, $"наблюдение {i} с достаточным количеством текста", "look");

        conv.LastPromptTokens = 5000;

        var state = StateOf(conv);
        var hashBefore = state.Conv.PrefixHash;

        var compactor = MakeCompactor(new ScriptedLlmClient().Then("сводка"),
            high: 1000, keepEvents: 40);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await compactor.CompactAsync(state, System.Array.Empty<ToolDto>(),
                Hooks(prefix: () => throw new InvalidOperationException("файл памяти не читается")),
                "T+0:05:00", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(state.Conv.LastPromptTokens, Is.EqualTo(5000),
                "показание должно вернуться, чтобы следующий ход оценивал честно, а не по нулю");
            Assert.That(state.Conv.PrefixHash, Is.EqualTo(hashBefore),
                "зона 0 обязана откатиться байт в байт");
            Assert.That(state.Compactions, Is.Zero);
        });

        await Task.CompletedTask;
    }

    [Test]
    public void Compact_CancelledDuringSummary_LeavesTheBodyIntact()
    {
        // Cancellation means the server is going down, and the fold step is about to replace the
        // body with whatever the summariser produced and then persist it. Catching it turned "shut
        // the server down during a compaction" into "permanently destroy the middle of the
        // conversation and write the damage to disk".
        var conv = Fresh();
        for (var i = 0; i < 20; i++)
            AddTurn(conv, $"наблюдение {i} с достаточным количеством текста", "look");

        conv.LastPromptTokens = 5000;

        var state = StateOf(conv);
        var bodyBefore = state.Conv.Body.Count;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var compactor = MakeCompactor(new ScriptedLlmClient().Then("сводка"),
            high: 1000, keepEvents: 40);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await compactor.CompactAsync(state, System.Array.Empty<ToolDto>(), Hooks(),
                "T+0:05:00", cts.Token));

        Assert.Multiple(() =>
        {
            Assert.That(state.Conv.Body.Count, Is.EqualTo(bodyBefore), "тело не должно быть свёрнуто");
            Assert.That(state.Conv.Body.First().Content, Does.Not.Contain("СВОДКА"));
            Assert.That(state.Compactions, Is.Zero);
        });
    }

    [Test]
    public async Task Compact_NoteCarriesTheRealRoundTime()
    {
        // The tail used to say "История сжата в T+0:00:00" on every compaction, because the
        // compactor reached into the perception layer for a clock it did not have and formatted a
        // constant zero. The model reads that as a fact.
        var conv = Fresh();
        for (var i = 0; i < 20; i++)
            AddTurn(conv, $"наблюдение {i} с достаточным количеством текста", "look");

        conv.LastPromptTokens = 5000;

        var state = StateOf(conv);
        await MakeCompactor(new ScriptedLlmClient().Then("сводка"), high: 1000, keepEvents: 40)
            .CompactAsync(state, System.Array.Empty<ToolDto>(), Hooks(), "T+1:23:45", CancellationToken.None);

        Assert.That(state.Conv.VolatileTail, Does.Contain("T+1:23:45"));
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
    public void CacheMetrics_DoesNotCryWolfOnAShortConversation()
    {
        // The regression this metric change fixes: on a small conversation each turn appends a
        // large fraction of the whole prompt, so a perfectly healthy cache reads as ~68% of the
        // prompt while still reusing 100% of what was reusable.
        var sawmill = new Robust.Shared.Log.LogManager().GetSawmill("test");
        var metrics = new CacheMetrics(sawmill);
        metrics.SetExpectedPrefix("AAAA");

        metrics.Record(3000, 0, "AAAA");                       // turn 1: cold, exempt
        Assert.That(metrics.Record(4300, 3000, "AAAA"), Is.True,
            "переиспользован весь предыдущий промпт — тревоге тут не место");
        Assert.That(metrics.Record(5600, 4300, "AAAA"), Is.True);
        Assert.That(metrics.Alarms, Is.Zero);
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

    [Test]
    public void EmptyAssistantResponse_IsNeverAppended()
    {
        // The bug this pins killed a live round outright.
        //
        // The model spent its entire completion budget on reasoning and returned neither text nor a
        // tool call. That empty turn went into the history, and from then on the provider rejected
        // every single request with HTTP 400 "Invalid assistant message: content or tool_calls must
        // be set" — not the offending turn, the whole conversation. The agent was dead for the rest
        // of the shift and the only trace was five identical errors in a row.
        var conv = Fresh();
        conv.AppendUser("что там в баре");

        var before = conv.Build().Count;
        var appended = conv.AppendAssistant(
            new LlmResponse(null, System.Array.Empty<ToolCallDto>(), 10, 9, 3000, 1.0, "length"));

        Assert.That(appended, Is.False, "пустой ответ модели не должен попадать в историю");
        Assert.That(conv.Build(), Has.Count.EqualTo(before),
            "история не должна вырасти на сообщении, из-за которого провайдер отвергнет её целиком");

        // And the conversation is still usable afterwards: every message carries something.
        foreach (var m in conv.Build())
        {
            if (m.Role != "assistant")
                continue;

            Assert.That(m.Content != null || m.ToolCalls is { Count: > 0 }, Is.True,
                "в истории не должно быть ассистента без content и без tool_calls");
        }
    }
}

/// <summary>Records what it was asked, so tests can assert on the prompt the agent actually built.</summary>
internal sealed class RecordingLlmClient : ILlmClient
{
    public System.Collections.Generic.IReadOnlyList<ToolDto> LastTools { get; private set; }

    public Task<LlmResponse> ChatAsync(System.Collections.Generic.IReadOnlyList<ChatMessageDto> messages,
        System.Collections.Generic.IReadOnlyList<ToolDto> tools, CancellationToken ct)
    {
        LastTools = tools;
        return Task.FromResult(new LlmResponse("сводка", System.Array.Empty<ToolCallDto>(), 100, 90, 10, 0.1));
    }

    public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(131072);
}

/// <summary>A client that always fails, for proving the compactor degrades rather than crashes.</summary>
internal sealed class ThrowingLlmClient : ILlmClient
{
    public Task<LlmResponse> ChatAsync(System.Collections.Generic.IReadOnlyList<ChatMessageDto> messages,
        System.Collections.Generic.IReadOnlyList<ToolDto> tools, CancellationToken ct) =>
        throw new LlmException("эндпоинт недоступен");

    public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(null);
}
