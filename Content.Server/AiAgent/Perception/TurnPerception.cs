namespace Content.Server.AiAgent.Perception;

/// <summary>
/// What one turn heard, as one value.
///
/// This used to be two mutable fields on the session, written by a delegate defined in a different
/// file and read by the loop. It happened to be correct only because the observation builder always
/// runs immediately before the turn and unconditionally overwrites both — nothing enforced that,
/// and the type said "a field that lives as long as the agent" about data that lives for one turn.
///
/// <paramref name="Text"/> is the formatted message the model receives; the structured fields
/// beside it are what the loop needs and cannot recover by parsing that string back out.
/// </summary>
/// <param name="RadioChannel">
/// Channel of the last radio line, or null if this turn heard no radio. An answer whispered next to
/// the core is no better than silence to whoever asked over the radio, so the recovery path routes
/// by this.
/// </param>
/// <param name="RoundStamp">
/// Round time, already formatted. Carried so the compaction ritual can stamp its summary without
/// reaching into the perception layer for a clock it does not have — which it did, and which is why
/// every compaction note claimed to have happened at T+0:00:00.
/// </param>
public sealed record TurnPerception(
    string Text,
    string? RadioChannel,
    bool HeardSpeech,
    bool Forced,
    string RoundStamp)
{
    /// <summary>
    /// A turn that heard nobody is the agent musing to itself; a turn that was addressed owes an
    /// answer. That distinction is what keeps idle thoughts off the radio every eight seconds.
    /// </summary>
    public bool Addressed => RadioChannel != null || HeardSpeech;
}
