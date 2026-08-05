using Content.Server.AiAgent.Bus;

namespace Content.Server.AiAgent;

/// <summary>
/// The debug bus, as the system sees it.
///
/// One bus per process, not per session. Sessions come and go with rounds; memory and skills
/// outlive them, and a debugger watching a restart wants one continuous stream with the restart
/// visible in it rather than two disconnected ones. That is also why the sequence number and the
/// instance id belong here and not on <see cref="AgentSession"/>.
///
/// Null until the debug server is switched on, which is what makes the whole feature cost nothing
/// when it is off: every owner does one null check and skips building the arguments.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private AgentEventBus? _bus;

    /// <summary>The live bus, or null when <c>ai.debug_enabled</c> is off.</summary>
    public AgentEventBus? DebugBus => _bus;

    /// <summary>
    /// Bring the bus up if the operator asked for it. Main thread, from <c>Initialize</c>.
    ///
    /// Sampled once rather than watched: the owners each capture a sink, and flipping the CVar
    /// mid-round would leave half of them reporting and half silent. Off is the default, and off
    /// means every owner's null check is the entire cost of the feature.
    /// </summary>
    private void StartDebugBus()
    {
        if (!_cfg.GetCVar(AiCVars.DebugEnabled))
            return;

        _bus = new AgentEventBus(_cfg.GetCVar(AiCVars.DebugRing));
        _sawmill.Info($"шина отладки поднята, кольцо {_bus.Capacity}, instance {_bus.Instance}");
    }
}
