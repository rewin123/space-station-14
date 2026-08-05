using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The whole wiring for an untooled reply, end to end on a real server.
///
/// The behavioural matrix — nudge once, deliver on the originating channel, suppress an exact
/// repeat, decline honestly — lives in <see cref="TurnTests"/>, where it costs milliseconds instead
/// of thirty seconds a case and can assert things this shape cannot reach at all (which channel the
/// reply went out on, what the last step exited with). What is left here is the one thing those
/// cannot prove: that the pieces are actually connected to each other.
///
/// InjectRadio → RadioSystem → RadioReceiveEvent → the observation queue → perception → the turn →
/// SpeakUntooledAsync → ChatSystem. Any one of those coming loose presents as an agent that answers
/// perfectly in the transcript and says nothing on the station.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class SpeechRecoveryTests
{
    [Test]
    public async Task UntooledReply_ReachesTheStationThroughTheRealWiring()
    {
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
            Assert.That(
                llm.SeenPrompts.Any(p => p.Any(m => m.Content?.Contains("Этого никто не услышал") == true)),
                Is.True, "модели обязано было прийти напоминание, что её текст не слышен");
        });
    }
}
