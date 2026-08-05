using System;
using Content.Server.AiAgent.Perception;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The perception buffer, without a server.
///
/// The interesting behaviour here is the one that stops the AI hearing the same sentence twice.
/// Upstream raises two separate events for one transmitted utterance — <c>RadioReceiveEvent</c> on
/// every listener, and <c>EntitySpokeEvent</c> on the speaker — and the AI is on the receiving end
/// of both when the speaker is standing next to its core.
///
/// The old guard tried to tell them apart by reading <c>EntitySpokeEvent.Channel</c>, which is
/// mutable and has already been nulled by the time a broadcast subscriber sees it. So it let every
/// successfully transmitted line through (the duplicate it was written to prevent) and dropped the
/// one case it should have kept — speech on a channel the speaker had no transmitter for.
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class PerceptionTests
{
    private static ObservationQueue Queue() => new(200);

    [Test]
    public void AlreadyHeardOnRadio_RecognisesTheSameLine()
    {
        var q = Queue();
        var t = TimeSpan.FromMinutes(3);

        q.Push(Observation.Radio("Common", "Иван Петров", "ИИ, открой мостик", t));

        Assert.That(q.AlreadyHeardOnRadio("Иван Петров", "ИИ, открой мостик", t), Is.True,
            "та же реплика от того же человека — это эхо той же передачи, а не второй раз сказанное");
    }

    [Test]
    public void AlreadyHeardOnRadio_LetsThroughADifferentLine()
    {
        var q = Queue();
        var t = TimeSpan.FromMinutes(3);

        q.Push(Observation.Radio("Common", "Иван Петров", "ИИ, открой мостик", t));

        Assert.Multiple(() =>
        {
            Assert.That(q.AlreadyHeardOnRadio("Иван Петров", "и закрой обратно", t), Is.False,
                "другой текст — другая реплика");
            Assert.That(q.AlreadyHeardOnRadio("Мира Восс", "ИИ, открой мостик", t), Is.False,
                "тот же текст от другого человека — тоже другая реплика");
        });
    }

    [Test]
    public void AlreadyHeardOnRadio_ForgetsAfterAMoment()
    {
        // The window is deliberately tight. Somebody who repeats themselves a minute later is
        // saying it again, and the AI should hear it again — suppressing that would make it look
        // deaf to a crewman who is escalating.
        var q = Queue();
        var t = TimeSpan.FromMinutes(3);

        q.Push(Observation.Radio("Common", "Иван Петров", "ИИ, открой мостик", t));

        Assert.That(q.AlreadyHeardOnRadio("Иван Петров", "ИИ, открой мостик", t + TimeSpan.FromSeconds(30)),
            Is.False, "через полминуты это уже новая просьба");
    }

    [Test]
    public void AlreadyHeardOnRadio_IgnoresNonRadioLines()
    {
        // Only radio can produce the duplicate; speech deduplicating against speech would hide a
        // crewman genuinely saying the same thing twice.
        var q = Queue();
        var t = TimeSpan.FromMinutes(3);

        q.Push(Observation.Speech("ядро", "Иван Петров", "ИИ, открой мостик", t));

        Assert.That(q.AlreadyHeardOnRadio("Иван Петров", "ИИ, открой мостик", t), Is.False);
    }
}
