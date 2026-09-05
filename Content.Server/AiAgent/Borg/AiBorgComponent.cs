using System.Collections.Generic;
namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Chassis driven by a language model.
///
/// <para>
/// A marker plus per-robot settings. A separate component rather than a flag on
/// <c>BorgChassisComponent</c>: that one is upstream and must not be touched, and besides, "this borg
/// is AI-controlled" is a property of our fork, not of the game.
/// </para>
/// </summary>
[RegisterComponent, Access(typeof(AiBorgSystem))]
public sealed partial class AiBorgComponent : Component
{
    /// <summary>
    /// The agent identifier: it names the memory directory, the session file, and the simill.
    ///
    /// <para>
    /// Must be unique server-wide. A collision with someone else's would mean two robots writing
    /// dialogue to the same file, and after a restart each recovering the other's memory as its own —
    /// exactly the failure that made the identifier stop being a constant.
    /// </para>
    /// <para>
    /// EMPTY is a legitimate option and the only one that scales: then the id is assigned on claim
    /// from <see cref="AgentIdPrefix"/> plus a number, and uniqueness is guaranteed by the allocator
    /// rather than by the attentiveness of whoever wrote the prototype. Filling it in by hand is
    /// worthwhile only where accumulated data is already tied to the directory — as with <c>borg-1</c>.
    /// </para>
    /// </summary>
    [DataField]
    public string AgentId = string.Empty;

    /// <summary>
    /// What the id is built from when <see cref="AgentId"/> is empty: <c>prefix-1</c>, <c>prefix-2</c>…
    ///
    /// <para>
    /// A meaningful prefix here is not decoration: the directory name is the only way to later tell a
    /// combat robot's log apart from an engineering robot's log in <c>ai_data/agents/</c>.
    /// </para>
    /// </summary>
    [DataField]
    public string AgentIdPrefix = "borg";

    /// <summary>
    /// What the robot is called over the radio. Filled from <see cref="AgentNames"/> on claim if that
    /// list isn't empty — so the value written here is a fallback, not a verdict.
    /// </summary>
    [DataField]
    public string AgentName = "Сегмент";

    /// <summary>
    /// Names by body number: the first goes to <c>prefix-1</c>, the second to <c>prefix-2</c>, and so on.
    ///
    /// <para>
    /// Introduced on 2026-09-01, when the number of combat chassis reached six. One name for all of
    /// them meant six "Klins" on the same channel, and that broke not cosmetics but function: an order
    /// "Klin, go to the bar" is addressed to all six at once, each sees it as its own, and the robots
    /// either walked in a crowd or argued about which of them was meant, instead of moving. Telling
    /// them apart by Si number didn't work either — it's assigned by the engine at spawn and never
    /// appears in crew orders.
    /// </para>
    /// <para>
    /// Shorter than a number is not decoration: the name is spoken over the radio every turn, and a
    /// long one eats both tokens and the model's attention.
    /// </para>
    /// </summary>
    [DataField]
    public List<string> AgentNames = new();

    /// <summary>The personality file inside the agent's directory.</summary>
    [DataField]
    public string SoulFile = "SOUL.md";

    /// <summary>Claim the body automatically at round start.</summary>
    [DataField]
    public bool AutoClaim = true;

    /// <summary>
    /// Its own model profile chain; empty means the shared <c>ai.llm_chain</c>.
    ///
    /// <para>
    /// Exists not for flexibility but for physics: two agents on the same llama-server slot evict
    /// each other's prefixes and pay a full prefill every turn.
    /// </para>
    /// </summary>
    [DataField]
    public string LlmChain = string.Empty;

    /// <summary>The chassis's radio channels. Must match its <c>IntrinsicRadioTransmitter</c>.</summary>
    [DataField]
    public string[] Channels = { "Binary", "Common", "Science", "Engineering" };

    /// <summary>The mind created to activate the chassis. Removed together with the session.</summary>
    [ViewVariables]
    public EntityUid? Mind;
}
