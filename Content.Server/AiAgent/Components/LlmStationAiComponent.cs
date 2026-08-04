namespace Content.Server.AiAgent.Components;

/// <summary>
/// Marks a <c>StationAiBrain</c> entity as being driven by the LLM agent rather than a player.
///
/// Deliberately added by <see cref="StationAiAgentSystem"/> at claim time and NOT declared in the
/// <c>AiHeld</c> prototype: <c>ContainerCompSystem.OnConRemove</c> strips exactly the components
/// listed in that prototype when the brain is ejected into an intellicard, so a marker declared
/// there would vanish on carding. Added from code, it survives — which is what lets the agent
/// keep talking on Binary while carded.
/// </summary>
[RegisterComponent]
public sealed partial class LlmStationAiComponent : Component
{
    /// <summary>
    /// Bumped by the owning system on every lifecycle change (insert, eject, death, round end).
    /// The agent loop carries the generation it started with; any marshalled call whose generation
    /// no longer matches is dropped rather than applied to a world that moved on.
    /// </summary>
    [ViewVariables]
    public int Generation;
}
