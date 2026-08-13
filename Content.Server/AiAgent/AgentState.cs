using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;

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
    /// Будильники, которые агент завёл сам. Здесь, а не на сессии, ровно по правилу выше: они
    /// переживают ход, и перезапуск сервера посреди смены не должен стирать «проверю через десять
    /// минут», сказанное экипажу вслух.
    /// </summary>
    public Perception.TimerStore Timers { get; } = new();

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

    /// <summary>
    /// Turns where it told the crew it would act and then did not.
    ///
    /// Counted rather than merely logged, because "worse than a refusal" deserves a number: the
    /// crew is standing at a door they were told would open, and a habit like that is invisible in
    /// a transcript read one run at a time.
    /// </summary>
    public int BrokenPromises { get; set; }

    /// <summary>
    /// Ходы, которые модель закрыла явным noop.
    ///
    /// Считается, а не просто логируется, потому что это единственное, что отличает исправного
    /// молчащего агента от сломанного. И то и другое выглядит одинаково — ИИ ничего не говорит, —
    /// но «сорок ходов подряд ничего не происходило» и «сорок ходов подряд модель не смогла
    /// собрать вызов» требуют противоположных действий от того, кто смотрит на сервер.
    /// </summary>
    public int IdleTurns { get; set; }

    public int Compactions { get; set; }

    public string? LastSummary { get; set; }

    /// <summary>
    /// Канал, в который уходит речь, когда ход не назвал канал явно.
    ///
    /// Это тумблер, а не память о последней реплике: живой игрок на этой роли выбирает канал в
    /// интерфейсе и дальше просто говорит, а разовое обращение в другой канал делает префиксом,
    /// не сбивая выбор. Здесь так же — <c>radio</c> с явным каналом тумблер не двигает.
    ///
    /// Скрытое состояние опасно для модели: она может забыть, куда настроена, и отправить
    /// разговор о предателе в общий канал. Поэтому текущий канал печатается в строке SELF на
    /// КАЖДОМ ходу — помнить его не требуется, достаточно прочитать.
    /// </summary>
    public string OutputChannel { get; set; } = DefaultChannel;

    public const string DefaultChannel = "Common";

    /// <summary>Куда вернуть тумблер, когда мозг вернут в ядро. Null — возвращать некуда.</summary>
    public string? ChannelBeforeCarding { get; set; }

    /// <summary>Единственный канал, который остаётся у закарденного — см. AiHeldIntellicard.</summary>
    public const string CardedChannel = "Binary";

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

    /// <summary>Everything worth carrying across a restart.</summary>
    public SessionSnapshot ToSnapshot(string prefixHash, int roundId) => new()
    {
        PrefixHash = prefixHash,
        RoundId = roundId,
        Turns = Conv.TurnCount,
        Compactions = Compactions,
        CharsPerToken = Conv.CharsPerToken,
        VolatileTail = Conv.VolatileTail,
        // Snapshot(), not a copy of the live list: this runs on the main thread, from the periodic
        // autosave, while the agent thread is appending.
        Body = Conv.Snapshot(),

        AgentTurns = Turns,
        Mode = Mode,
        UntooledReplies = UntooledReplies,
        RecentSpeech = new List<string>(_recentSpeech),

        Timers = Timers.All().Select(t => new TimerDto
        {
            Name = t.Name,
            Message = t.Message,
            DueSeconds = t.DueAt.TotalSeconds,
            EverySeconds = t.Every?.TotalSeconds ?? 0,
        }).ToList(),
    };

    /// <summary>
    /// Reinstate a snapshot. Repairs the conversation itself, so no caller can forget to.
    /// </summary>
    public void Restore(SessionSnapshot snapshot)
    {
        Conv.RestoreBody(snapshot.Body, snapshot.VolatileTail, snapshot.CharsPerToken);

        // A snapshot taken mid-turn can hold an assistant tool_calls with no matching results.
        // Replaying that verbatim gets the whole request rejected, so close them here rather than
        // relying on every caller to remember.
        Conv.Repair();

        Turns = snapshot.AgentTurns;
        UntooledReplies = snapshot.UntooledReplies;
        Compactions = snapshot.Compactions;
        RestoreRecentSpeech(snapshot.RecentSpeech);

        // Просроченное за время простоя не отбрасывается: таймер, чей срок прошёл, пока сервер
        // лежал, сработает на первом же тике после восстановления. Это и есть правильное поведение —
        // «проверить реактор» не перестало быть нужным оттого, что мы перезагрузились.
        Timers.Restore(snapshot.Timers.Select(t => new Perception.AgentTimer(
            t.Name,
            t.Message,
            TimeSpan.FromSeconds(t.DueSeconds),
            t.EverySeconds > 0 ? TimeSpan.FromSeconds(t.EverySeconds) : null)));

        // Anything but Core or Carded collapses to Core, and this is load-bearing: a snapshot taken
        // mid-compaction holds Review, and restoring it would leave the agent refusing every game
        // action with review_mode for the rest of the round — a silent failure that looks exactly
        // like a model which has stopped trying. The world re-asserts Carded through the container
        // events anyway; the stored value only covers the gap before the first of them.
        Mode = snapshot.Mode is AgentMode.Core or AgentMode.Carded ? snapshot.Mode : AgentMode.Core;
    }

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
