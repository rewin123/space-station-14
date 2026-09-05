using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent.Core;

/// <summary>
/// The body the agent lives in — and the only thing the agent core knows about the world.
///
/// <para>
/// The seam here wasn't invented, only <b>named</b>. It already existed implicitly: <c>StartSession</c>
/// took ten arguments, six of which were station-specific and the rest common. As long as those six
/// lived as separate methods of one class, the "core" and "Station AI" were one lump of code, even
/// though <see cref="AgentSession"/>'s own doc comment honestly claims it doesn't touch the world.
/// This class turns that claim into something checkable: to bring an agent up in a new body, it's
/// enough to assemble an <see cref="AgentBody"/> — no need to touch the loop, dialogue, compaction,
/// or model routing.
/// </para>
/// <para>
/// A class, not an interface, and delegates rather than abstract methods — because the codebase is
/// already built on delegates (see the <see cref="AgentSession"/> constructor), and an interface
/// would force every body to be an <c>EntitySystem</c>. A body is assembled by a system, but is
/// <b>not</b> one.
/// </para>
/// </summary>
public sealed class AgentBody
{
    /// <summary>The entity the agent IS: the brain in the core, the borg's chassis.</summary>
    public required EntityUid Owner { get; init; }

    /// <summary>
    /// The agent's stable identifier: <c>core</c>, <c>borg-1</c>.
    ///
    /// <para>
    /// It names the session file, the memory folder, the debug bus session, and the simill. Before
    /// this field existed, the identifier was the constant <c>"current"</c>, and a second agent would
    /// silently write its dialogue to the same file — that is, would restore someone else's memory as
    /// its own after a restart.
    /// </para>
    /// </summary>
    public required string Id { get; init; }

    /// <summary>What the body is called in-game. Must match how the agent's SOUL file names it.</summary>
    public required string Name { get; init; }

    /// <summary>The name of the personality file inside the agent's directory.</summary>
    public required string SoulFile { get; init; }

    /// <summary>
    /// This agent's filesystem: the roster, its own records, its own notes, its own memory.
    ///
    /// <para>
    /// Its own per body, and that's the main thing that changed. Memory, skills, and notes about
    /// people used to live in a single instance per process, and a combat cyborg carried twenty
    /// kilobytes of the Station AI's library in its prefix — including crew dossiers it has no use
    /// for and shouldn't know. Only the roster stayed shared, and shared as one instance, not a copy
    /// per body.
    /// </para>
    /// <para>
    /// It's here, not in the system, for exactly the same reason as everything else in this class:
    /// to bring an agent up in a new body, it's enough to assemble an <see cref="AgentBody"/>.
    /// </para>
    /// </summary>
    public required Vfs.Vfs Vfs { get; init; }

    /// <summary>
    /// Where the body looks and listens from.
    ///
    /// <para>
    /// For the core, this is <c>StationAiCoreComponent.RemoteEntity</c> — a camera that can be moved
    /// away from the core itself. For a borg, it's the borg itself. The <c>OBSERVED</c> stream's gate
    /// measures distance from exactly here, which is why it's a delegate rather than a field: for the
    /// core, this point changes over the course of a round.
    /// </para>
    /// <para>
    /// <c>null</c> means "the body currently doesn't see" (core without a camera, powered-down borg),
    /// and that's a normal path, not an error.
    /// </para>
    /// </summary>
    public required Func<EntityUid?> Eye { get; init; }

    /// <summary>Whether the body is alive enough to take turns.</summary>
    public required Func<bool> Alive { get; init; }

    /// <summary>Zone 0 of the system prompt. Called only at session start and on compaction.</summary>
    public required Func<string> BuildPrompt { get; init; }

    /// <summary>The SELF line — the only part of the observation the body writes, rather than the world.</summary>
    public required Func<AgentSession, string> SelfLine { get; init; }

    /// <summary>
    /// Let the body append to the observations before the queue is flushed into a turn. Main thread.
    ///
    /// <para>
    /// Exists for perception that's computed <b>on demand</b> rather than arriving as an event. For a
    /// borg this is the field-of-view diff: comparing what's visible now to what was visible last turn
    /// can only be done by walking the radius, and there's no reason to do that thirty times a second —
    /// but doing it once per turn, exactly on time, makes sense.
    /// </para>
    /// </summary>
    public Action<AgentSession>? BeforeObservation { get; init; }

    /// <summary>
    /// This body's toolset.
    ///
    /// <para>
    /// This is exactly where bodies diverge the most: the core has <c>device_action</c> and
    /// <c>move_camera</c>, a borg has hands and legs. The shared tools (memory, skills, timers,
    /// <c>noop</c>) are registered by both and live in a common place.
    /// </para>
    /// </summary>
    public required Action<AgentSession, AiToolRegistry> RegisterTools { get; init; }

    /// <summary>
    /// A station-wide announcement, or <c>null</c> if the body has no means for it.
    ///
    /// <para>
    /// A borg has no means: <c>announce</c> in the Station AI body works through the built-in
    /// <c>CommunicationsConsoleComponent</c>, which the chassis doesn't have. The absence of the
    /// capability is expressed as <c>null</c>, not as a tool that always refuses.
    /// </para>
    /// </summary>
    public Func<AgentSession, string, Task>? Announce { get; init; }

    /// <summary>Speak aloud (text, channel) — for non-tool speech that the loop catches.</summary>
    public required Func<AgentSession, string, string?, Task<bool>> Speak { get; init; }

    /// <summary>
    /// The radio channels available to the body in a given mode.
    ///
    /// <para>
    /// This used to be a static constant lifted from the <c>AiHeld</c> prototype. A borg has a
    /// different set (see <c>base_borg_chassis.yml</c>), and a constant would have made it either
    /// deaf or talking into channels it doesn't have.
    /// </para>
    /// </summary>
    public required Func<AgentMode, string[]> ChannelsFor { get; init; }

    /// <summary>
    /// This body's own model profile chain, or <c>null</c> to use the shared <c>ai.llm_chain</c>.
    ///
    /// <para>
    /// Exists for one entirely physical reason: two agents on the same llama-server slot evict each
    /// other's prefixes and pay a full prefill every turn. Splitting them across separate profiles is
    /// cheaper than splitting the context in half.
    /// </para>
    /// </summary>
    public string? LlmChain { get; init; }

    /// <summary>
    /// This body's tool mode: <c>true</c> — scripts, <c>false</c> — the classic toolset,
    /// <c>null</c> — whatever <c>ai.script_mode</c> says.
    ///
    /// <para>
    /// The per-body override exists for exactly one thing: keeping the core on the classic toolset
    /// while the borg runs on scripts, so they can be compared on the same shift. Within a single
    /// agent, the toolsets never mix.
    /// </para>
    /// </summary>
    public bool? ScriptMode { get; init; }

    /// <summary>
    /// Prompt language, frozen at body assembly like <see cref="ScriptMode"/>.
    /// Default Russian so a body assembled in a test without setting it keeps the old prefix.
    /// </summary>
    public Locale.AgentLang Language { get; init; }

    /// <summary>
    /// Whether to run the review (curator) on this body's compaction.
    ///
    /// <para>
    /// Turned off for borgs by the owner's decision on 2026-09-01. The reason is cost: the review is a
    /// separate multi-step dialogue over a COPY of the whole history, and on a live shift it cost the
    /// robot up to a minute of silence per compaction, and compaction comes often with four agents.
    /// The core keeps it: the station AI is the only one whose records outlive the shift and
    /// accumulate over years.
    /// </para>
    /// </summary>
    public bool Curate { get; init; } = true;
}
