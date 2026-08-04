using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent;

/// <summary>
/// Entry points used by the benchmark suite.
///
/// These call the <em>same</em> handlers a model's tool call reaches, through the same registry,
/// the same main-thread marshalling and the same gate chain. A test harness that reimplemented the
/// dispatch would pass while the real path was broken, which is the failure mode worth avoiding
/// above all others here.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// Claim a specific core without waiting for a round to start. Returns the brain entity.
    /// </summary>
    public EntityUid? ClaimForTest(EntityUid coreUid)
    {
        return TryClaimCore(coreUid, out _)
            ? _sessions.Keys.FirstOrDefault(b => _stationAi.TryGetCore(b, out var c) && c.Owner == coreUid)
            : null;
    }

    public AgentSession? GetSession(EntityUid brain) =>
        _sessions.GetValueOrDefault(brain);

    /// <summary>
    /// Invoke a tool by name with raw JSON arguments, exactly as the agent loop would.
    ///
    /// The returned task completes only once the main thread has pumped the marshalled delegate,
    /// so callers must keep ticking the server while awaiting it — see <c>AiWorld.Invoke</c>.
    /// </summary>
    public async Task<ToolResult> InvokeToolForTest(EntityUid brain, string tool, string argsJson,
        CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return ToolResult.Fail(ToolError.Dead, "нет сессии агента для этой сущности");

        if (!session.Registry.TryGet(tool, out var entry))
            return ToolResult.Fail(ToolError.UnknownTool, $"нет инструмента '{tool}'",
                alternatives: session.Registry.Nearest(tool));

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        return await entry.Handler(doc.RootElement.Clone(), ct).ConfigureAwait(false);
    }

    /// <summary>Mint a handle for an entity so a test can address it without calling look first.</summary>
    public string HandleFor(EntityUid brain, EntityUid target)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return string.Empty;

        return session.Handles.GetOrCreate(target, KindOf(target));
    }

    /// <summary>Push a synthetic observation, for tests that exercise the formatter rather than the wiring.</summary>
    public void PushObservationForTest(EntityUid brain, Perception.Observation obs)
    {
        if (_sessions.TryGetValue(brain, out var session))
            session.Queue.Push(obs);
    }

    /// <summary>Build the observation message synchronously on the main thread.</summary>
    public string? BuildObservationForTest(EntityUid brain, bool force = true)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return null;

        var (items, dropped) = session.Queue.Drain();
        return Perception.ObservationFormatter.Format(items, dropped, RoundTime(), SelfLine(session), force);
    }
}
