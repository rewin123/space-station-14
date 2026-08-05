using System.Collections.Generic;
using System.Text;
using Content.Server.AiAgent.Context;

namespace Content.Server.AiAgent;

/// <summary>
/// Everything mutable about one agent, in one object.
///
/// It used to be spread across eleven fields on the session, a handful of locals inside the turn,
/// and one private field on the compactor that tests could only reach by reflection. That made two
/// things impossible that this system is otherwise built for. Snapshotting: the message list was
/// persisted and the agent was not, so a restart brought back the conversation but forgot the mode,
/// the recent speech and the compaction arming. And replaying: every other part of the design pays
/// for determinism — fixed observation order, a counter instead of a GUID for tool-call ids, tools
/// sorted by name — and there was nowhere to put the state that determines the next step.
///
/// The rule this class exists to make true: <b>if it survives a turn, it lives here.</b>
/// </summary>
public sealed class AgentState
{
    public ConversationState Conv { get; } = new();

    /// <summary>
    /// Turns the loop has run.
    ///
    /// NOT the same number as <see cref="ConversationState.TurnCount"/>, which counts appended user
    /// messages and therefore also counts nudges. Both are real and both are persisted, under
    /// different keys, because they answer different questions.
    /// </summary>
    public int Turns { get; set; }

    /// <summary>Turns that ended in prose and had to be delivered mechanically. Should stay near zero.</summary>
    public int UntooledReplies { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int Compactions { get; set; }

    /// <summary>
    /// False until usage has fallen back below <c>ai.compact_low</c> — the hysteresis.
    ///
    /// It lived as a private field on the compactor, which meant the only way for a test to set up
    /// the hysteresis case was <c>GetField(..., NonPublic | Instance)</c>. Reflection in a test is
    /// the clearest possible sign that a piece of state is in the wrong class.
    /// </summary>
    public bool CompactionArmed { get; set; } = true;

    public string? LastSummary { get; set; }

    /// <summary>
    /// One-line rendering of the laws as of the last turn, for spotting a rewrite.
    ///
    /// Polled rather than subscribed because upstream raises nothing on the path that matters: the
    /// law board reaches a virtual method, not an event, and <c>SiliconLawBoundComponent.Version</c>
    /// only increments for entities with an <c>ActorComponent</c>, which this brain has none of.
    /// </summary>
    public string? LastLawsDigest { get; set; }

    private AgentMode _mode = AgentMode.Core;

    /// <summary>
    /// Where to return after a review.
    ///
    /// Maintained by the setter rather than by whoever remembers: an AI carded while a review was
    /// running must come back carded, or the device gate hands the station's equipment to an agent
    /// sitting in somebody's pocket. Two separate code paths got this wrong by writing
    /// <c>AgentMode.Core</c> into a finally.
    /// </summary>
    public AgentMode ModeBeforeReview { get; private set; } = AgentMode.Core;

    public AgentMode Mode
    {
        get => _mode;
        set
        {
            if (value != AgentMode.Review)
                ModeBeforeReview = value;

            _mode = value;
        }
    }

    /// <summary>
    /// The last few things the agent said, normalised, so it does not broadcast them again.
    ///
    /// This model fills silence: left alone it emits "Жду указаний" every turn, and the untooled
    /// delivery path would dutifully put each copy on the radio. Suppressing an exact repeat is a
    /// mechanical fix for a mechanical habit — no prompt wording survives contact with it.
    /// </summary>
    private readonly Queue<string> _recentSpeech = new();

    public void RememberSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        _recentSpeech.Enqueue(Normalise(text));
        while (_recentSpeech.Count > RecentSpeechDepth)
            _recentSpeech.Dequeue();
    }

    /// <summary>Has this exact line gone out in the last few turns?</summary>
    public bool AlreadySaid(string text) => _recentSpeech.Contains(Normalise(text));

    /// <summary>What was said recently, for persisting across a restart.</summary>
    public IReadOnlyCollection<string> RecentSpeech => _recentSpeech;

    public void RestoreRecentSpeech(IEnumerable<string> lines)
    {
        _recentSpeech.Clear();
        foreach (var line in lines)
            _recentSpeech.Enqueue(line);
    }

    private const int RecentSpeechDepth = 4;

    /// <summary>Letters and digits only, lowercased — so punctuation and case cannot dodge the check.</summary>
    public static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
