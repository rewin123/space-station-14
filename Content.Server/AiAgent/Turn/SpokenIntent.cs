using System;

namespace Content.Server.AiAgent.Turn;

/// <summary>
/// Did the agent just promise the crew an action?
///
/// This exists because of a benchmark. Asked to open a door and given a reason, the AI checked the
/// scene with its own cameras, answered "Открою дверь, но если это ложная тревога…" — and did not
/// open it. Every tool involved behaved perfectly; the gap was entirely between what was said and
/// what was done. From the crew's side that is worse than a refusal: they are standing at a door
/// they were told would open.
///
/// It is the same shape as the untooled-prose failure this loop already recovers from — the model
/// believes the saying was the doing — so it gets the same treatment: notice, say so once, and give
/// it a step to make good.
///
/// Deliberately a short list of first-person futures, not a language model. A wide net would fire
/// on "открою, если подтвердит инженер", and the cost of a false positive is a wasted step plus a
/// nudge the model can simply decline. A narrow net that catches the common phrasing is worth more
/// than a clever one that has to be right.
/// </summary>
public static class SpokenIntent
{
    private static readonly string[] Promises =
    {
        "открою", "закрою", "открываю", "закрываю",
        "опущу болты", "опускаю болты", "подниму болты",
        "включу", "выключу", "переключу", "верну",
        "объявлю", "объявляю",
        "сделаю", "выполню",
    };

    /// <summary>
    /// True when the line reads as "I am about to do a thing to the station".
    ///
    /// Conditional phrasing is not excluded: an agent that said "открою, когда подтвердят" and gets
    /// asked about it can simply say so, which costs one step. An agent that promised and forgot
    /// gets caught, which is the point.
    /// </summary>
    public static bool PromisesAction(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken))
            return false;

        var text = spoken.ToLowerInvariant();

        foreach (var promise in Promises)
        {
            if (text.Contains(promise, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
