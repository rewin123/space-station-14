using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
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
            return ToolResult.Fail(ToolError.Dead, "нет сессии агента для этой сущности", retry: "none");

        // Through the real dispatcher, not around it. Calling the handler directly meant every
        // benchmark skipped the gate and the exception mapping, so a test could pass against a
        // dispatch path that was broken.
        var call = new ToolCallDto
        {
            Id = "call_test",
            Function = new FunctionCallDto { Name = tool, Arguments = argsJson },
        };

        var gate = session.Mode == AgentMode.Review ? DispatchGate.NoGameActions : DispatchGate.None;
        return (await session.Dispatcher.InvokeAsync(call, gate, ct).ConfigureAwait(false)).Result;
    }

    /// <summary>
    /// Run a tool from the server console, exactly as the model would, and log the result.
    ///
    /// Fire-and-forget on purpose: tool bodies marshal onto the main thread, and the console command
    /// runs <em>on</em> the main thread — awaiting here would deadlock against the very queue the
    /// call is waiting for. The answer arrives in the log a tick later.
    ///
    /// This exists because a live station disagreed with the benchmarks about what the AI could
    /// reach, and there was no way to ask the running server a direct question.
    /// </summary>
    public bool InvokeToolFromConsole(string tool, string argsJson, out string reason)
    {
        var brain = _sessions.Keys.FirstOrDefault();
        if (brain == default)
        {
            reason = "нет активного агента";
            return false;
        }

        _ = ReportAsync(brain, tool, argsJson);

        reason = $"{tool} запущен, результат будет в логе";
        return true;
    }

    private async Task ReportAsync(EntityUid brain, string tool, string argsJson)
    {
        try
        {
            var r = await InvokeToolForTest(brain, tool, argsJson).ConfigureAwait(false);
            _sawmill.Info($"[console] {tool}({argsJson}) -> " +
                          (r.Ok ? "ok " + r.EffectJson() : $"{r.Error}: {r.Detail}"));
        }
        catch (Exception e)
        {
            _sawmill.Error($"[console] {tool} упал: {e.GetType().Name}: {e.Message}");
        }
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

    /// <summary>Worst main-thread call observed, for the "never stalls the tick" benchmark.</summary>
    public (string What, double Ms) SlowestMainThreadCall() => (_dispatcher.Slowest, _dispatcher.SlowestMs);

    /// <summary>
    /// Build zone 0 the way a session start or a compaction would.
    ///
    /// Exists so a test can build it twice and compare: an interpolated clock, counter or GUID in
    /// the frozen prefix costs a full prefill on every turn and presents only as "the AI got slow",
    /// with no error anywhere. Two identical builds is the cheapest way to prove there isn't one.
    /// </summary>
    public string BuildSystemPromptForTest() => BuildSystemPrompt();

    /// <summary>Build the observation message synchronously on the main thread.</summary>
    public string? BuildObservationForTest(EntityUid brain, bool force = true)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return null;

        // Same order as the real path, law poll included — a test hook that skipped it would report
        // the agent as blind to a rewrite it actually notices, or the reverse.
        NoticeLawChange(session);

        var (items, dropped) = session.Queue.Drain();
        return Perception.ObservationFormatter.Format(items, dropped, RoundTime(), SelfLine(session), force);
    }
}
