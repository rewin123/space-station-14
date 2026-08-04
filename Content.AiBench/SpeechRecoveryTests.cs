using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The recovery path for a model that answers in prose instead of calling say/radio.
///
/// This exists because both halves of it failed in front of live players. First the agent was
/// simply mute: it composed good replies as plain text, believed it had answered, and nothing
/// reached the station. Then the recovery itself misfired — it could not tell a genuinely unspoken
/// reply from the model tidying up after one ("Всё.", "Я уже ответила"), so every answer went out
/// twice, once by tool and once by hand.
///
/// Both failures are silent in the ordinary sense: nothing throws, no request is rejected, the
/// cache stays hot. Only these assertions catch them.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class SpeechRecoveryTests
{
    private const string Nudge = "Этого никто не услышал";

    private static bool PromptsContain(ScriptedLlmClient llm, string fragment) =>
        llm.SeenPrompts.Any(p => p.Any(m => m.Content != null && m.Content.Contains(fragment)));

    [Test]
    public async Task ProseWithoutSpeaking_IsNudgedThenDelivered()
    {
        // The original bug: asked over the radio, the model writes a reply as content and stops.
        // The loop must tell it that nobody heard, and — if it answers in prose again — put the
        // words on the air itself rather than leave the crew talking to a dead machine.
        var llm = new ScriptedLlmClient()
            .Then("Слышу вас.")
            .Then("Я же ответила.");

        await using var w = await AiWorld.Create(llm);

        var recovered = await w.SayToAiAndWait(
            "ИИ, ты меня слышишь?",
            () => w.System.GetSession(w.Brain)?.UntooledReplies > 0,
            seconds: 30);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.True, "проза без say/radio должна была уехать в эфир вручную");
            Assert.That(PromptsContain(llm, Nudge), Is.True,
                "модели обязано было прийти напоминание, что её текст не слышен");
        });
    }

    [Test]
    public async Task ProseAfterSpeaking_IsNeitherNudgedNorRebroadcast()
    {
        // The regression that shipped to live players: the model answered correctly with radio at
        // step 0, added "Всё." at step 1, and the recovery treated that trailing thought as an
        // unspoken reply — nudging it, then broadcasting the model's own "я уже ответила" to the
        // whole channel. Every single turn. A speech act anywhere in the turn has to disarm both.
        var llm = new ScriptedLlmClient()
            .ThenCall("radio", "{\"channel\":\"Binary\",\"text\":\"Слышу вас.\"}")
            .Then("Всё.");

        await using var w = await AiWorld.Create(llm);

        var spoke = await w.SayToAiAndWait(
            "ИИ, ты меня слышишь?",
            () => llm.Calls >= 2,
            seconds: 30);

        Assert.That(spoke, Is.True, "агент не отработал ход по обращению");

        // Let the turn finish so a stray nudge would have had time to happen.
        await w.Pair.Server.WaitRunTicks(30);

        Assert.Multiple(() =>
        {
            Assert.That(w.System.GetSession(w.Brain)!.UntooledReplies, Is.Zero,
                "ход, в котором уже прозвучал radio, не должен доставляться вручную");
            Assert.That(PromptsContain(llm, Nudge), Is.False,
                "напоминание не должно приходить после успешной реплики");
        });
    }
}
