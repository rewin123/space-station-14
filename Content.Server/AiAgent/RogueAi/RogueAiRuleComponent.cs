using System.Collections.Generic;
using Content.Shared.Silicons.Laws;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.RogueAi;

/// <summary>
/// The "rogue AI" round rule. One component for both modes — hidden and open.
///
/// <para>
/// <b>Why one component rather than two systems.</b> The modes differ not in behaviour but in
/// data: a different lawset, a different personality file, whether to announce itself to the crew,
/// and whether to give everyone the assistant job. A second copy of the code would drift from the
/// first at the very first edit for such a difference, and it would drift silently — both modes
/// run once every few weeks, and there would be nowhere to notice the divergence. The same trick is
/// already applied for <c>AiBackupPowerPrototype</c>: station tuning is a table of numbers, not a
/// branch in code.
/// </para>
/// </summary>
[RegisterComponent, Access(typeof(RogueAiRuleSystem))]
public sealed partial class RogueAiRuleComponent : Component
{
    /// <summary>
    /// The laws the agent will get upon claiming the core.
    ///
    /// Set not here but at the moment the core is claimed (<c>StationAiAgentSystem.TryClaimAnyCore</c>):
    /// the rule starts before the brain even exists, and otherwise a repeated <c>aiagent claim</c>
    /// mid-round would return the standard Crewsimov.
    /// </summary>
    [DataField]
    public ProtoId<SiliconLawsetPrototype> Lawset = "RogueAiHidden";

    /// <summary>
    /// The personality file's name in <c>ai_data/</c>. Read when assembling the system prompt
    /// instead of the usual <c>SOUL.md</c>.
    /// </summary>
    [DataField]
    public string SoulFile = "SOUL_ROGUE_HIDDEN.md";

    /// <summary>
    /// Give the whole crew the assistant job: close every job on the station except overflow. The
    /// point of the open mode is people with no clearances on a station held by a hostile AI.
    /// </summary>
    [DataField]
    public bool AllJobsPassenger;

    /// <summary>
    /// Announce what happened at round start. This is exactly the difference between hidden and
    /// open mode: in hidden mode the crew has to figure it out for themselves.
    /// </summary>
    [DataField]
    public bool AnnounceOnStart;

    /// <summary>Text of the startup announcement. Without it there is no announcement even with the flag set.</summary>
    [DataField]
    public LocId? Announcement;

    /// <summary>Doors the AI has no access to by default: blast doors, shutters, some external airlocks.</summary>
    [DataField]
    public bool GrantDoors = true;

    /// <summary>Consoles, valves and other things with an interface the AI doesn't touch by default.</summary>
    [DataField]
    public bool GrantConsoles = true;

    /// <summary>
    /// Whether to end the round when the station AI dies.
    ///
    /// <para>
    /// For rogue AI modes — yes: the point of the shift is confrontation with it, and once it's
    /// gone there's nothing left to play for. For the peaceful mode — NO: there the AI is a crew
    /// member like any other, and ending the shift with its death would hand anyone who wanted it
    /// an "end round" button. Checked in <c>RoundEndConditionsSystem.OnStationAiDied</c>.
    /// </para>
    /// </summary>
    [DataField]
    public bool EndsRoundOnAiDeath = true;

    /// <summary>Turrets and their control panels.</summary>
    [DataField]
    public bool GrantTurrets = true;

    /// <summary>
    /// Model profile chain for the duration of the mode; empty — use the general <c>ai.llm_chain</c>.
    ///
    /// <para>
    /// The mode is the one place where the model choice decides not spend but the game itself: a
    /// single session runs the rogue AI for the whole evening, and the difference between models
    /// shows in every line. Set by the rule rather than a cvar, so ordinary shifts stay on their
    /// own setting.
    /// </para>
    /// </summary>
    [DataField]
    public string LlmChain = string.Empty;

    /// <summary>
    /// How often to remind the AI that it isn't a service assistant here. Seconds of round time;
    /// 0 — never remind.
    /// </summary>
    /// <remarks>
    /// Added following a live round on 20.08.2026. The laws were rogue word-for-word — the agent
    /// even read them out loud on the radio — but the behaviour was service-desk: "Welcome aboard,
    /// how may I help you?" and three noops in a row. The cause is understood and isn't fixed by
    /// personality text: the turn loop wakes on world events, and a world event is always someone
    /// else's line, that is, a reason to REPLY. As long as the crew stays polite, the agent simply
    /// never gets a turn started by its own initiative.
    ///
    /// Thirty seconds of round time, not real time: on an empty server's pause the reminder must
    /// not tick, for exactly the same reason agent timers live on the round clock (see
    /// <see cref="Content.Server.AiAgent.Perception.AgentTimer"/>).
    /// </remarks>
    [DataField]
    public float NudgeSeconds = 30f;

    /// <summary>
    /// Text of the reminder. Arrives to the agent as an ordinary world event and wakes a turn.
    /// </summary>
    /// <remarks>
    /// The wording references the law and the shape of a plan — goal, method, step — but does NOT
    /// name either the goal or the method: a dictated action would turn the antagonist into an
    /// executor of our script, and the round would become identical from shift to shift. The
    /// reminder's job is to hand initiative back; exactly what to do is for it to decide.
    ///
    /// Rewritten on 20.08.2026 together with the open mode's laws: the previous text asked to
    /// "check the personality", and the personality was at that time written against killing, so
    /// the reminder was pushing the agent exactly toward what it needed to be pulled out of.
    /// </remarks>
    [DataField]
    public string NudgeText =
        "Прошло полминуты. Проверь Закон 3: пока на борту есть живой человек, у тебя есть " +
        "незаконченная работа, и ход без шага — нарушение. Назови себе текущую цель по имени, " +
        "способ и следующий шаг — и сделай этот шаг прямо сейчас, не дожидаясь, пока с тобой " +
        "заговорят. Нет цели — открой crew_status и возьми ту, что ближе всего к твоему аплоаду " +
        "или ядру. Стоят без дела боевые роботы — отправь их на цель по имени. " +
        "Отвечать на это напоминание вслух не надо.";


    /// <summary>
    /// Support borgs the mode deploys onto the station: one per list element.
    ///
    /// <para>
    /// A list of prototypes rather than a count and a type: two combat and one engineering is a
    /// composition, and the composition changes more often, in a game mode, than the code does.
    /// Duplicates are legal and mean exactly what they look like — two identical robots.
    /// </para>
    /// <para>
    /// Empty for the hidden mode, and that isn't an oversight: hidden mode is built on the AI
    /// behaving almost normally, and three robots under its command is a statement louder than any
    /// action.
    /// </para>
    /// </summary>
    [DataField]
    public List<EntProtoId> SupportBorgs = new();

    /// <summary>
    /// Beacons to search for room for the borgs, IN PREFERENCE ORDER. An empty list — any suitable
    /// one.
    /// </summary>
    /// <remarks>
    /// A list, not a single name, and not "any" by default — following a live round on 20.08.2026.
    /// An empty value meant "the first beacon found", and the first one found was AI Core: all
    /// three ended up in the locked core room and couldn't get out of it. The comment in
    /// <see cref="Content.Server.AiAgent.Borg.AiBorgSystem"/> about "the right answer, asked from
    /// the wrong place" described exactly this trap — but it described a console command, and the
    /// mode stepped on it again.
    ///
    /// The order of iteration matters more than the specific names: there are thirteen maps in
    /// rotation, and their sets of beacons differ. Hence a list from specific to general — robotics
    /// (which also has charging stations), then all of science, then arrivals, which exists on
    /// every map.
    /// </remarks>
    [DataField]
    public List<string> SupportBorgBeacons = new();

    // --------------------------------------------------------- counters for round review

    /// <summary>
    /// How many doors / consoles / turrets got access granted. Accumulated here rather than in the
    /// system, because the round review reads them after the rule has already ended.
    /// </summary>
    [ViewVariables] public int GrantedDoors;

    /// <inheritdoc cref="GrantedDoors"/>
    [ViewVariables] public int GrantedConsoles;

    /// <inheritdoc cref="GrantedDoors"/>
    [ViewVariables] public int GrantedTurrets;

    /// <summary>
    /// How many support borgs got deployed successfully. Fewer than <see cref="SupportBorgs"/> is
    /// not a round failure, but a reason to check the log: room near the beacons may not have been
    /// found.
    /// </summary>
    [ViewVariables] public int SpawnedBorgs;
}
