using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
    public static string FormatLine(Observation o) => o.Kind switch
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
        ObsKind.Note => $"NOTE о «{o.Speaker}» есть заметки ({o.Text}) — " +
                        "read_player_related_memory, если пригодится",
        ObsKind.Observed => $"OBSERVED {o.Channel} | {o.Text}",
        _ => $"EVENT {o.Text}",
    };

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
        bool force)
    {
        if (items.Count == 0 && dropped == 0 && !force)
            return null;

        var sb = new StringBuilder();
        sb.Append('[').Append(FormatRoundTime(roundTime)).Append("]\n");

        foreach (var kind in OrderedKinds)
        {
            foreach (var o in items.Where(i => i.Kind == kind))
                sb.Append(FormatLine(o)).Append('\n');
        }

        sb.Append("SELF ").Append(self).Append('\n');

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

        // Последними, и это не безразличие к порядку. Строк этой категории бывает много, а
        // остальные — по одной; поставь их выше, и обращение по рации уедет под сотню строк про
        // то, кто что куда положил, в самый конец сообщения. Реплика, на которую надо ответить,
        // должна лежать сверху.
        ObsKind.Observed,
    };

    /// <summary>T+H:MM:SS since round start.</summary>
    public static string FormatRoundTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"T+{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");
}
