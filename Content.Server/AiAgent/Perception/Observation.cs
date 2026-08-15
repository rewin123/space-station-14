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
    /// Сработал таймер, который агент завёл сам. Добавлено в конец перечисления намеренно: порядок
    /// категорий — это порядок строк в наблюдении, и вставка в середину сдвинула бы все прежние.
    /// </summary>
    Timer,

    /// <summary>
    /// Человек заступил на смену: игрок получил тело на станции. Так же в конец и по той же
    /// причине.
    /// </summary>
    Arrival,

    /// <summary>
    /// Напоминание, что о заговорившем у агента уже есть заметка. Не событие мира, а сообщение о
    /// его собственной памяти, поэтому отдельной категорией, а не через <see cref="Event"/>:
    /// <c>Event</c> обещан модели как «с тобой что-то произошло», и склеивать с ним напоминание
    /// значило бы обесценить обе строки. В конец — по правилу выше.
    /// </summary>
    Note,
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
    /// Сработавший таймер. Имя едет в <see cref="Speaker"/> — это и есть тот, кто «заговорил»:
    /// агент из прошлого. Имя обязательно в строке, иначе на две поставленные напоминалки придёт
    /// два неразличимых текста, и снять нужный через del_timer будет нечем.
    /// </summary>
    public static Observation Timer(string name, string text, TimeSpan t) =>
        new(ObsKind.Timer, string.Empty, name, text, t);

    /// <summary>
    /// Кто-то заступил на смену. Имя — в <see cref="Speaker"/>, должность — в <see cref="Text"/>,
    /// потому что это те же два поля, которыми человек представляется по рации, и агенту незачем
    /// разбирать два разных формата для одного и того же лица.
    ///
    /// Должность может быть пустой: у части ролей её в прототипе нет, а гадать нельзя — «пассажир»
    /// по умолчанию превратил бы дыру в данных в утверждение о человеке.
    /// </summary>
    public static Observation Arrival(string name, string job, TimeSpan t) =>
        new(ObsKind.Arrival, string.Empty, name, job, t);

    /// <summary>
    /// Напоминание о заметке. Имя — в <see cref="Speaker"/>, число записей — в <see cref="Text"/>:
    /// сколько накоплено, видно сразу, и по одной строке уже понятно, стоит ли тратить ход на
    /// чтение.
    /// </summary>
    public static Observation Note(string name, int entries, TimeSpan t) =>
        new(ObsKind.Note, string.Empty, name,
            entries.ToString(System.Globalization.CultureInfo.InvariantCulture), t);
}
