using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Locale;

namespace Content.Server.AiAgent.Perception;

/// <summary>
/// Turns a drained batch of observations into the single user message the model receives every N
/// seconds.
///
/// Every rule below exists to make the output a deterministic function of the world state:
/// categories in fixed enum order, arrival order within a category, round-relative timestamps
/// rather than wall clock (wall clock would make benchmark replays diverge and adds entropy for
/// nothing), "\n" only, and invariant-culture numbers. Absent categories are omitted entirely
/// rather than printed as empty headers.
/// </summary>
public static class ObservationFormatter
{
    public static string FormatLine(Observation o, AgentLocale? loc = null)
    {
        loc ??= AgentLocale.Ru;
        return o.Kind switch
        {
            ObsKind.Radio => $"RADIO {o.Channel} | {o.Speaker}: \"{o.Text}\"",
            ObsKind.Speech => $"SPEECH {o.Channel} | {o.Speaker}: \"{o.Text}\"",
            ObsKind.Announce => string.IsNullOrEmpty(o.Speaker)
                ? $"ANNOUNCE {o.Text}"
                : $"ANNOUNCE {o.Speaker}: \"{o.Text}\"",
            ObsKind.Alert => $"ALERT {o.Text}",
            ObsKind.Laws => $"LAWS {o.Text}",
            ObsKind.Timer => $"TIMER {o.Speaker}: \"{o.Text}\"",
            ObsKind.Arrival => string.IsNullOrEmpty(o.Text)
                ? $"ARRIVAL {o.Speaker}"
                : $"ARRIVAL {o.Speaker} ({o.Text})",
            ObsKind.Note => loc.FormatNote(o.Speaker, o.Text, o.Channel),
            ObsKind.Observed => $"OBSERVED {o.Channel} | {o.Text}",
            _ => $"EVENT {o.Text}",
        };
    }

    /// <summary>
    /// Build the observation message. Returns null when there is nothing at all to say, so the
    /// caller can skip the turn instead of paying a model call to report silence.
    /// </summary>
    /// <param name="self">
    /// The SELF line. Always emitted with the same fields in the same order even when unchanged:
    /// deciding what changed is the model's job, and omitting it would force it to guess.
    /// </param>
    public static string? Format(
        IReadOnlyList<Observation> items,
        int dropped,
        TimeSpan roundTime,
        string self,
        bool force,
        AgentLocale? loc = null)
    {
        loc ??= AgentLocale.Ru;

        if (items.Count == 0 && dropped == 0 && !force)
            return null;

        var sb = new StringBuilder();
        sb.Append('[').Append(FormatRoundTime(roundTime)).Append("]\n");

        foreach (var kind in OrderedKinds)
        {
            foreach (var o in items.Where(i => i.Kind == kind))
                sb.Append(FormatLine(o, loc)).Append('\n');
        }

        sb.Append("SELF ").Append(self).Append('\n');

        if (dropped > 0)
            sb.Append("DROPPED ").Append(dropped.ToString(CultureInfo.InvariantCulture)).Append(" older lines\n");

        return sb.ToString();
    }

    /// <summary>
    /// The block of events mixed into the conversation MID-TURN — right after tool results.
    ///
    /// <para>
    /// <b>Why separate from <see cref="Format"/>.</b> There is no SELF line here and no timestamp
    /// at the start: both belong to the START of a turn, where the agent looks around anew. Mid-turn,
    /// it just read the state from each tool's <c>effect</c>, and repeating it would mean paying for
    /// the same thing twice on every step of a long turn — and turns can run to twenty-five steps.
    /// </para>
    /// <para>
    /// The category order is the same <see cref="OrderedKinds"/>, i.e. speech on top and sightings
    /// last. Same reasoning as there, and mid-turn it's even stronger: if the crew changed their mind
    /// halfway through an action, that line has to reach the model first.
    /// </para>
    /// </summary>
    public static string? FormatSteering(
        IReadOnlyList<Observation> items,
        int dropped,
        TimeSpan roundTime,
        AgentLocale? loc = null)
    {
        loc ??= AgentLocale.Ru;

        if (items.Count == 0 && dropped == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("NEW_EVENTS [").Append(FormatRoundTime(roundTime)).Append("] ").Append(loc.NewEventsHeader).Append('\n');

        foreach (var kind in OrderedKinds)
        {
            foreach (var o in items.Where(i => i.Kind == kind))
                sb.Append(FormatLine(o, loc)).Append('\n');
        }

        if (dropped > 0)
            sb.Append("DROPPED ").Append(dropped.ToString(CultureInfo.InvariantCulture)).Append(" older lines\n");

        return sb.ToString();
    }

    private static readonly ObsKind[] OrderedKinds =
    {
        ObsKind.Radio,
        ObsKind.Speech,
        ObsKind.Announce,
        ObsKind.Alert,
        ObsKind.Laws,
        ObsKind.Event,
        ObsKind.Timer,
        ObsKind.Arrival,
        ObsKind.Note,

        // Last, and that is not indifference to ordering. This category can have many lines while
        // the rest have one each; put it higher and a radio call would end up buried under a hundred
        // lines about who put what where, way down at the end of the message. The line that needs an
        // answer has to sit on top.
        ObsKind.Observed,
    };

    /// <summary>T+H:MM:SS since round start.</summary>
    public static string FormatRoundTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"T+{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");
}
