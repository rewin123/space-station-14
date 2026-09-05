using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Locale;

namespace Content.Server.AiAgent;

/// <summary>
/// Station AI as <b>one of the bodies</b>, not as the only possible agent.
///
/// <para>
/// The file is deliberately small: it is the entire station-specific interface to the agent core.
/// Everything that used to make <c>StartSession</c> non-portable — the prompt, the tool set, speech,
/// channels, the point of view — is gathered here into one object. The second body (the borg)
/// assembles the same kind of object from its own methods and touches nothing in the shared code.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// Description of the "brain in the core" body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AgentBody.Eye"/> returns the <c>RemoteEntity</c>, not the brain itself, and that's
    /// not a small detail: for Station AI the eye travels separately from the body, and all the
    /// geometry — the view, the <c>OBSERVED</c> stream, bearings — is computed from the camera.
    /// Hearing, meanwhile, comes from the core, but hearing doesn't belong to the body: it is
    /// subscribed separately and stays station-scoped.
    /// </para>
    /// <para>
    /// <see cref="AgentBody.Announce"/> is populated: the brain in the core has a built-in
    /// <c>CommunicationsConsoleComponent</c>. For the borg this field stays <c>null</c> — not as an
    /// omission, but as the absence of that organ.
    /// </para>
    /// </remarks>
    private AgentBody BuildStationBody(EntityUid brain)
    {
        // Same as for the borg: the mode is fixed at body assembly time so the prompt and the wiring
        // don't drift apart if the cvar is flipped mid-round.
        var scripted = _cfg.GetCVar(AiCVars.ScriptMode);
        var lang = AgentLangUtil.Parse(_cfg.GetCVar(AiCVars.Language));

        // The core's own filesystem. Kept as a field because BuildSystemPrompt is a Func<string> with
        // no arguments: both the MEMORY block and the tree root must describe the same body.
        var vfs = BuildVfs(CoreAgentId, lang);
        CoreVfs = vfs;

        return new AgentBody
        {
            Owner = brain,
            Id = CoreAgentId,
            Name = AgentName,
            SoulFile = StationSoulFile(),
            Vfs = vfs,
            LlmChain = StationLlmChain(),
            Eye = () => _stationAi.TryGetCore(brain, out var core) ? core.Comp?.RemoteEntity : null,
            Alive = () => IsPlayable(brain),
            ScriptMode = scripted,
            Language = lang,
            BuildPrompt = () => BuildSystemPrompt(scripted, lang),
            SelfLine = SelfLine,
            RegisterTools = RegisterTools,
            Announce = AnnounceInGameAsync,
            Speak = SpeakUntooledAsync,
            ChannelsFor = ChannelsFor,
        };
    }
}
