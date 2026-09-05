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
    Announce,
    Alert,
    Laws,
    Event,

    /// <summary>
    /// A timer the agent set itself has fired. Added at the end of the enum deliberately: category
    /// order is the order of lines in the observation, and inserting in the middle would shift all
    /// the earlier ones.
    /// </summary>
    Timer,

    /// <summary>
    /// A human has come on shift: the player got a body on the station. Also at the end, for the
    /// same reason.
    /// </summary>
    Arrival,

    /// <summary>
    /// A reminder that the agent already has a note about whoever just spoke. Not a world event but
    /// a message about the agent's own memory, so it gets its own category rather than going through
    /// <see cref="Event"/>: <c>Event</c> is promised to the model as "something happened to you", and
    /// merging the reminder into it would cheapen both lines. At the end, per the rule above.
    /// </summary>
    Note,

    /// <summary>
    /// The agent saw something happen near its eye.
    ///
    /// The only category that arrives as a stream: the rest are rare messages, this one is every
    /// action of every person in frame. That is why it has its own cap in the queue, see
    /// <see cref="ObservationQueue"/>: without it, a long bustle in frame would push radio traffic
    /// out of the queue, i.e. the agent would go deaf exactly when it is being addressed the most.
    ///
    /// At the end of the enum, by the same rule as the three previous ones.
    /// </summary>
    Observed,
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

    public static Observation Announce(string sender, string text, TimeSpan t) =>
        new(ObsKind.Announce, string.Empty, sender, text, t);

    public static Observation Alert(string text, TimeSpan t) =>
        new(ObsKind.Alert, string.Empty, string.Empty, text, t);

    public static Observation Laws(string text, TimeSpan t) =>
        new(ObsKind.Laws, string.Empty, string.Empty, text, t);

    public static Observation Event(string text, TimeSpan t) =>
        new(ObsKind.Event, string.Empty, string.Empty, text, t);

    /// <summary>
    /// A timer that fired. The name rides in <see cref="Speaker"/> — that is who "spoke": the agent's
    /// past self. The name has to be in the line, otherwise two reminders set at once would come
    /// through as two indistinguishable texts, with nothing to tell them apart when cancelling one via
    /// del_timer.
    /// </summary>
    public static Observation Timer(string name, string text, TimeSpan t) =>
        new(ObsKind.Timer, string.Empty, name, text, t);

    /// <summary>
    /// Someone has come on shift. Name goes in <see cref="Speaker"/>, job in <see cref="Text"/>,
    /// because those are the same two fields a human introduces themselves with over the radio, and
    /// the agent has no reason to parse two different formats for the same fact.
    ///
    /// Job can be empty: some roles have none set in the prototype, and guessing is not an option —
    /// defaulting to "passenger" would turn a hole in the data into a claim about the person.
    /// </summary>
    public static Observation Arrival(string name, string job, TimeSpan t) =>
        new(ObsKind.Arrival, string.Empty, name, job, t);

    /// <summary>
    /// A reminder about a note. Name goes in <see cref="Speaker"/>, entry count in <see cref="Text"/>:
    /// how much has accumulated is visible at a glance, and a single line is enough to tell whether
    /// spending a turn to read it is worth it.
    /// </summary>
    /// <param name="slug">
    /// The file name under <c>/players</c>. Rides in <see cref="Channel"/> so the reminder names the
    /// path directly: the line used to just point at the tool, leaving the agent to guess how the
    /// person was filed.
    /// </param>
    public static Observation Note(string name, string slug, int entries, TimeSpan t) =>
        new(ObsKind.Note, slug, name,
            entries.ToString(System.Globalization.CultureInfo.InvariantCulture), t);

    /// <summary>
    /// The agent saw this. <paramref name="label"/> is what happened, <paramref name="what"/> is who
    /// was involved and where.
    ///
    /// The label lives in <see cref="Channel"/> rather than inside the text, and that's not
    /// decoration: it lets the line be counted in the journal and filtered via
    /// <c>ai.observe_kinds</c> without parsing it back apart with a regex.
    ///
    /// Participants arrive here already as a string — handles and names captured on the main thread
    /// at the moment of the event. Same deal as the other categories: an <c>EntityUid</c> has no
    /// place in an observation, because observations are read from another thread, seconds later,
    /// when the uid may no longer point at anything.
    /// </summary>
    public static Observation Observed(string label, string what, TimeSpan t) =>
        new(ObsKind.Observed, label, string.Empty, what, t);
}
