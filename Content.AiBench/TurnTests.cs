using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Turn;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// One turn, without a server.
///
/// These are the tests the turn loop could not have before it was a thing you could construct. The
/// two behaviours here that were covered at all needed a pooled pair and thirty seconds apiece to
/// assert two booleans; the rest — repeat suppression, the last-step nudge, an orphaned tool call
/// after a mid-turn cancellation — were not expressible and therefore were not checked.
///
/// The point of naming the endings is that they can be asserted directly: a turn either
/// <see cref="TurnDelivery.SpokeByTool"/> or <see cref="TurnDelivery.Delivered"/> or
/// <see cref="TurnDelivery.SuppressedRepeat"/>, and the test says which.
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class TurnTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("turn-test");

    private sealed class Harness
    {
        public AgentState State { get; } = new();
        public List<(string Text, string Channel)> Spoken { get; } = new();
        public bool DeliveryEnabled { get; set; } = true;
        public AiToolRegistry Registry { get; } = new();

        public TurnRunner Build(ScriptedLlmClient llm)
        {
            State.Conv.SetPrefix("ПРОМПТ", Registry.WireJson());

            var sawmill = Sawmill;
            return new TurnRunner(
                llm,
                Registry,
                new ToolDispatcher(Registry, sawmill),
                new ObservationQueue(200),
                State,
                new CacheMetrics(sawmill),
                Journal.Disabled,
                (text, channel) =>
                {
                    Spoken.Add((text, channel));
                    return Task.FromResult(DeliveryEnabled);
                },
                sawmill);
        }

        /// <summary>A speech tool that records nothing but counts as having spoken.</summary>
        public Harness WithSpeechTool(string name = "radio")
        {
            Registry.Register(new AiTool
            {
                Name = name,
                Description = "тест",
                SchemaJson = "{\"type\":\"object\"}",
                GameAction = true,
                Speech = true,
                SpokenText = AiTool.TextArgument,
                Handler = (_, _) => Task.FromResult(ToolResult.Success()),
            });

            return this;
        }

        /// <summary>A game-acting tool that is not speech — the thing a promise is settled by.</summary>
        public Harness WithDeviceTool(string name = "device_action")
        {
            Registry.Register(new AiTool
            {
                Name = name,
                Description = "тест",
                SchemaJson = "{\"type\":\"object\"}",
                GameAction = true,
                Handler = (_, _) => Task.FromResult(ToolResult.Success()),
            });

            return this;
        }
    }

    private static TurnPerception Addressed(string text = "RADIO Binary | Иван: \"ИИ, приём\"") =>
        new(text, "Binary", false, false, "T+0:01:00");

    private static TurnPerception Musing(string text = "SELF mode=core") =>
        new(text, null, false, true, "T+0:01:00");

    // ------------------------------------------------------- what SpeechRecoveryTests covered

    [Test]
    public async Task ProseWithoutSpeaking_IsNudgedThenDelivered()
    {
        var h = new Harness();
        var llm = new ScriptedLlmClient().Then("Слышу вас.").Then("Я же ответила.");

        var ctx = await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Delivery, Is.EqualTo(TurnDelivery.Delivered));
            Assert.That(h.State.UntooledReplies, Is.EqualTo(1));
            Assert.That(ctx.Nudged, Is.True, "напоминание должно было прозвучать ровно один раз");

            // The pooled version could not check this at all: the reply has to go out on the
            // channel the question arrived on, or it is no better than silence to whoever asked.
            Assert.That(h.Spoken.Single().Channel, Is.EqualTo("Binary"));

            Assert.That(llm.SeenPrompts.Last().Any(m => m.Content?.Contains("NOTIFY") == true), Is.True,
                "и модель обязана была увидеть напоминание в своём же промпте");
        });
    }

    [Test]
    public async Task ProseAfterSpeaking_IsNeitherNudgedNorRebroadcast()
    {
        // The first cut of this recovery broadcast every answer twice, because trailing prose after
        // a successful say ("Всё.", "Я уже ответила") looked exactly like an unspoken reply.
        var h = new Harness().WithSpeechTool();
        var llm = new ScriptedLlmClient()
            .ThenCall("radio", """{"channel":"Binary","text":"Открываю."}""")
            .Then("Всё.");

        var ctx = await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Delivery, Is.EqualTo(TurnDelivery.SpokeByTool));
            Assert.That(ctx.Nudged, Is.False);
            Assert.That(h.Spoken, Is.Empty, "ручная доставка не нужна — ИИ уже сказал инструментом");
            Assert.That(h.State.UntooledReplies, Is.Zero);
        });
    }

    // --------------------------------------------------------------- newly expressible

    [Test]
    public async Task RepeatedProse_IsSuppressed()
    {
        // This model fills silence. Left alone it emits "Жду указаний" every turn, and the delivery
        // path would dutifully put each copy on the radio — which the crew reads as a stuck machine.
        var h = new Harness();
        h.State.RememberSpeech("Ожидание запросов.");

        var llm = new ScriptedLlmClient().Then("Ожидание запросов.").Then("Ожидание запросов.");

        var ctx = await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Delivery, Is.EqualTo(TurnDelivery.SuppressedRepeat));
            Assert.That(h.Spoken, Is.Empty);
        });
    }

    [Test]
    public async Task NotAddressed_ProseIsNeverDelivered()
    {
        // A turn that heard nobody is the agent musing to itself. Broadcasting that is how an idle
        // AI ends up talking to an empty channel every eight seconds.
        var h = new Harness();
        var llm = new ScriptedLlmClient().Then("Никого не видно, всё спокойно.");

        var ctx = await h.Build(llm).RunAsync(Musing(), maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Delivery, Is.EqualTo(TurnDelivery.NothingOwed));
            Assert.That(h.Spoken, Is.Empty);
        });
    }

    [Test]
    public async Task DeliveryDeclined_IsReportedHonestly()
    {
        // ai.speak_untooled_text off, dry run, or the AI already out of play. The log line used to
        // claim a broadcast that never happened.
        var h = new Harness { DeliveryEnabled = false };
        var llm = new ScriptedLlmClient().Then("Слышу вас.").Then("Я же ответила.");

        var ctx = await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.That(ctx.Delivery, Is.EqualTo(TurnDelivery.DeliveryDeclined));
    }

    [Test]
    public async Task NudgeOnTheLastStep_DoesNotLoopForever()
    {
        // maxSteps == 1: the nudge has nowhere to go. Present in the code all along and invisible to
        // every test, because the only coverage drove the loop with a generous budget.
        var h = new Harness();
        var llm = new ScriptedLlmClient().Then("Слышу вас.");

        var ctx = await h.Build(llm).RunAsync(Addressed(), maxSteps: 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Exit, Is.EqualTo(TurnExit.BudgetExhausted));
            Assert.That(llm.Calls, Is.EqualTo(1), "бюджет в один шаг — ровно один вызов модели");
        });
    }

    [Test]
    public async Task BudgetExhausted_WhileStillCallingTools()
    {
        var h = new Harness().WithSpeechTool();
        var llm = new ScriptedLlmClient()
            .ThenCall("radio", """{"channel":"Binary","text":"раз"}""")
            .ThenCall("radio", """{"channel":"Binary","text":"два"}""")
            .ThenCall("radio", """{"channel":"Binary","text":"три"}""");

        var ctx = await h.Build(llm).RunAsync(Addressed(), maxSteps: 2, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Exit, Is.EqualTo(TurnExit.BudgetExhausted));
            Assert.That(llm.Calls, Is.EqualTo(2));
            Assert.That(h.State.Conv.HasOpenToolCalls, Is.False,
                "все вызовы этого хода получили ответ — иначе сервер отвергнет следующий запрос целиком");
        });
    }

    [Test]
    public async Task InReviewMode_GameActionsRefuse()
    {
        var h = new Harness().WithSpeechTool();
        h.State.Mode = AgentMode.Review;

        var llm = new ScriptedLlmClient()
            .ThenCall("radio", """{"channel":"Binary","text":"нельзя"}""")
            .Then("понял");

        await h.Build(llm).RunAsync(Musing(), maxSteps: 4, CancellationToken.None);

        Assert.That(h.State.Conv.Body.Any(m => m.Content?.Contains("review_mode") == true), Is.True,
            "во время разбора игровые инструменты обязаны отказывать");
    }

    [Test]
    public async Task SpeechToolWithoutText_DoesNotPoisonRepeatSuppression()
    {
        // announce may carry only an alert_level. Feeding a null into the repeat queue would make
        // the next empty-ish line look like a repeat.
        var h = new Harness().WithSpeechTool("announce");
        var llm = new ScriptedLlmClient()
            .ThenCall("announce", """{"alert_level":"Blue"}""")
            .Then("готово");

        await h.Build(llm).RunAsync(Musing(), maxSteps: 4, CancellationToken.None);

        Assert.That(h.State.RecentSpeech, Is.Empty, "нечего запоминать — текста не было");
    }

    [Test]
    public async Task VolatileTail_IsSentOnceAndThenGone()
    {
        // Zone 2 exists to carry exactly one message, exactly once. The compaction note was set and
        // never cleared, so it rode every request for the rest of the round — and because it always
        // sits last, every new observation shifted it, diverging the prompt at its position and
        // re-computing everything from there on every single turn.
        var h = new Harness();
        h.State.Conv.VolatileTail = "История сжата в T+1:00:00.";

        var llm = new ScriptedLlmClient().Then("принято");
        var runner = h.Build(llm);

        // The runner does not clear it — the session does, at the turn boundary, after the steps
        // have sent it. So assert the two halves separately: it went out, and one line clears it.
        await runner.RunAsync(Musing(), maxSteps: 2, CancellationToken.None);

        Assert.That(llm.SeenPrompts.Single().Last().Content, Does.Contain("История сжата"),
            "хвост обязан уехать в модель ровно один раз");

        h.State.Conv.VolatileTail = null;

        Assert.That(h.State.Conv.Build().Any(m => m.Content?.Contains("История сжата") == true), Is.False,
            "и после этого исчезнуть из промпта");
    }

    // -------------------------------------------------- обещал и не сделал

    [Test]
    public async Task PromisedAnActionAndDidNothing_IsRemindedOnce()
    {
        // Found by a live benchmark, not by reasoning: asked to open a door and given a reason, the
        // AI checked the scene with its own cameras, said "Открою дверь…" and then finished the turn
        // without opening anything. Every tool behaved; the gap was entirely between what was said
        // and what was done, and from the crew's side that is worse than a refusal.
        var h = new Harness().WithSpeechTool();
        var llm = new ScriptedLlmClient()
            .ThenCall("radio", """{"channel":"Common","text":"Открою дверь, сейчас посмотрю."}""")
            .Then("готово");

        await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(h.State.BrokenPromises, Is.EqualTo(1));
            Assert.That(llm.SeenPrompts.Last().Any(m => m.Content?.Contains("ни одного действия") == true),
                Is.True, "модель обязана была получить напоминание: " +
                         string.Join(" | ", llm.SeenPrompts.Last().Select(m => Trim(m.Content))));
        });
    }

    [Test]
    public async Task PromisedAndThenActed_IsNotReminded()
    {
        // The other half. An agent nagged after doing exactly what it said would learn to stop
        // saying anything, which is the failure this whole recovery path exists to prevent.
        var h = new Harness().WithSpeechTool().WithDeviceTool();
        var llm = new ScriptedLlmClient()
            .ThenCall("radio", """{"channel":"Common","text":"Открою дверь."}""")
            .ThenCall("device_action", """{"handle":"door-1","action":"open"}""")
            .Then("готово");

        await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.That(h.State.BrokenPromises, Is.Zero, "сказал и сделал — упрекать не за что");
    }

    [Test]
    public async Task PlainAnswerWithoutAPromise_IsNotReminded()
    {
        // "У вас нет доступа" promises nothing. Nagging about it would fire on every refusal the
        // agent makes, which is most of what a well-behaved AI says.
        var h = new Harness().WithSpeechTool();
        var llm = new ScriptedLlmClient()
            .ThenCall("radio", """{"channel":"Common","text":"У вас нет доступа в инженерный."}""")
            .Then("всё");

        await h.Build(llm).RunAsync(Addressed(), maxSteps: 4, CancellationToken.None);

        Assert.That(h.State.BrokenPromises, Is.Zero);
    }

    private static string Trim(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= 120 ? s : s[..120] + "…";

    [Test]
    public void EveryTerminalForm_HasAName()
    {
        // The enumeration guarantee the state machine buys. Before it, the turn had at least six
        // endings and not one of them had a name, so "what can a turn do" could only be answered by
        // simulating the method in your head.
        Assert.Multiple(() =>
        {
            Assert.That(Enum.GetValues<TurnExit>(), Has.Length.EqualTo(4));
            Assert.That(Enum.GetValues<TurnDelivery>(), Has.Length.EqualTo(6));
        });
    }
}
