namespace Content.Server.AiAgent.Perception;

/// <summary>
/// Categories of thing the AI can perceive. The order of this enum is the order categories appear
/// in the observation message — fixed on purpose, so that identical world states always produce
/// identical bytes and a benchmark replay does not drift.
/// </summary>
public enum ObsKind : byte
{
    Radio,
    Speech,
    Notify,
    Announce,
    Alert,
    Laws,
    Event,
}

/// <summary>
/// One perceived line, fully resolved on the main thread at the moment it happened.
///
/// Deliberately carries no <c>EntityUid</c>. Two reasons: the agent loop reads these off-thread
/// where an EntityUid may already be dangling, and — more importantly — the raw uid behind a
/// radio message is information a human Station AI player does not have. Handing it to the model
/// would be handing it a metagame key linking a voice to an entity.
/// </summary>
public sealed record Observation(
    ObsKind Kind,
    string Channel,
    string Speaker,
    string Text,
    TimeSpan RoundTime)
{
    public static Observation Radio(string channel, string speaker, string text, TimeSpan t) =>
        new(ObsKind.Radio, channel, speaker, text, t);

    public static Observation Speech(string where, string speaker, string text, TimeSpan t) =>
        new(ObsKind.Speech, where, speaker, text, t);

    public static Observation Notify(string text, TimeSpan t) =>
        new(ObsKind.Notify, string.Empty, string.Empty, text, t);

    public static Observation Announce(string sender, string text, TimeSpan t) =>
        new(ObsKind.Announce, string.Empty, sender, text, t);

    public static Observation Alert(string text, TimeSpan t) =>
        new(ObsKind.Alert, string.Empty, string.Empty, text, t);

    public static Observation Laws(string text, TimeSpan t) =>
        new(ObsKind.Laws, string.Empty, string.Empty, text, t);

    public static Observation Event(string text, TimeSpan t) =>
        new(ObsKind.Event, string.Empty, string.Empty, text, t);
}
