using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Locale;
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
    /// <param name="agentId">
    /// Who to address. Empty means whichever comes first, which used to be fine while there was one
    /// agent; with a borg on the station, "whichever comes first" turned into a lottery over
    /// dictionary order.
    /// </param>
    public bool InvokeToolFromConsole(string tool, string argsJson, out string reason, string? agentId = null)
    {
        var brain = string.IsNullOrWhiteSpace(agentId)
            ? _sessions.Keys.FirstOrDefault()
            : _sessions.FirstOrDefault(kv =>
                string.Equals(kv.Value.Body.Id, agentId, StringComparison.OrdinalIgnoreCase)).Key;

        if (brain == default)
        {
            reason = string.IsNullOrWhiteSpace(agentId)
                ? "нет активного агента"
                : $"нет агента с идентификатором '{agentId}'. Есть: {KnownAgentIds()}";
            return false;
        }

        _ = ReportAsync(brain, tool, argsJson);

        reason = $"{tool} запущен на {_sessions[brain].Body.Id}, результат будет в логе";
        return true;
    }

    /// <summary>Identifiers of live agents — for a clear refusal in the console.</summary>
    public string KnownAgentIds() =>
        _sessions.Count == 0 ? "(ни одного)" : string.Join(", ", _sessions.Values.Select(s => s.Body.Id));

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

    /// <summary>
    /// How many observation lines have been issued since the process started.
    ///
    /// Exists for the NEGATIVE test, and that's the whole point. Checking that a distant event
    /// didn't make it into the observation by looking at the message text means testing the
    /// formatting: the line could be missing simply because it never got built. A zero counter means
    /// the gate refused before any work happened, and that's exactly the assertion that protects
    /// parity.
    /// </summary>
    public int WitnessedCount() => _witnessed;

    /// <summary>Worst main-thread call observed, for the "never stalls the tick" benchmark.</summary>
    public (string What, double Ms) SlowestMainThreadCall() => (_world.Slowest, _world.SlowestMs);

    /// <summary>
    /// What the main thread cost per operation, the most expensive on top.
    ///
    /// A single maximum isn't enough for diagnosis: thirty calls at 26 ms and one at 73 ms give the
    /// same maximum, but differ twentyfold in total cost, and they get fixed differently — the first
    /// by splitting, the second by making it cheaper. The <c>Total</c> column is the one worth
    /// deciding what to touch by.
    /// </summary>
    public IReadOnlyList<(string What, long Count, double P50, double P95, double Max, double Total, long Overruns)>
        MainThreadReport() => _world.Report();

    /// <summary>Total main-thread time the agent has consumed since the process started.</summary>
    public double MainThreadTotalMs() => _world.TotalMs;

    /// <summary>
    /// World bus health. Three of the five numbers must be zero, and that's an assertion, not a
    /// hope: an overflow means parallelism the module doesn't have, and a large wait means starvation.
    /// </summary>
    public (int Depth, long Deferrals, long Promotions, long Overflows, double MaxWaitMs) WorldBusHealth() =>
        (_world.Depth, _world.Deferrals, _world.Promotions, _world.Overflows, _world.MaxWaitMs);

    /// <summary>Drain the world queue manually — for tests that tick the server themselves.</summary>
    public void PumpWorldBusForTest() => _world.Pump();

    /// <summary>
    /// Submit an arbitrary job onto the world bus on behalf of a live session.
    ///
    /// Exists for tests on slicing and on a stale generation: the real tools are all atomic for now,
    /// so there's nothing to exercise the multi-slice path with. The generation is taken from the
    /// session for real, so a <c>ReleaseAll</c> in the middle of a test drops the request the same
    /// way a carding would drop it in combat.
    /// </summary>
    public Task<T> SubmitWorldJobForTest<T>(Threading.IWorldJob job, Task<T> result,
        TimeSpan? timeout = null)
    {
        var brain = _sessions.Keys.FirstOrDefault();
        var generation = brain != default && _sessions.TryGetValue(brain, out var session)
            ? session.Generation
            : 0;

        return _world.SubmitAsync(job, result, generation, () => GenerationOf(brain),
            CancellationToken.None, timeout ?? TimeSpan.FromSeconds(30));
    }

    /// <summary>The last line about tick duration and how many ticks ran late (>1.5 of the period).</summary>
    public (string Last, long Ticks, long Overruns) FrameReport() =>
        (_frames.Last, _frames.Ticks, _frames.Overruns);

    /// <summary>
    /// What the last look cost.
    ///
    /// <c>Queries</c> is the main thing here, with the milliseconds attached alongside. A test for
    /// "exactly one trip into broadphase" is deterministic and depends on neither the machine nor
    /// which map is loaded; a test on milliseconds measures the build agent and is noisy. So the
    /// former is the one used as the guard.
    /// </summary>
    public (int Queries, int Tiles, int Candidates, int OnScreen, int Rows,
        double ViewMs, double GatherMs, double RowsMs) LastLookCost() =>
        (_lastLook.Queries, _lastLook.Tiles, _lastLook.Candidates, _lastLook.OnScreen, _lastLook.Rows,
            _lastLook.ViewMs, _lastLook.GatherMs, _lastLook.RowsMs);

    /// <summary>
    /// Both gathering paths against the very same frame.
    ///
    /// Exists for the equivalence test: it requires that the fast path not lose anything the slow
    /// path saw. Comparing by the tool's response won't do — <c>ai.look_limit</c> truncates it, and a
    /// disappearance at the three-hundredth line would look like a cutoff.
    ///
    /// <b>Both measurements must happen within one call, and that's not a convenience.</b> The
    /// first version of the test toggled the paths one after another via a CVar, with a tick passing
    /// between them — and at a twenty-tile radius it found a "loss" of one entity out of two and a
    /// half thousand. There was no loss: someone had crossed the visibility boundary in between. A
    /// test that catches footsteps instead of geometry is worse than no test at all: it lies in both
    /// directions and teaches you not to trust red.
    /// </summary>
    public (List<EntityUid> Slow, List<EntityUid> Fast, double SlowMs, double FastMs)
        CompareLookPathsForTest(EntityUid brain, int expand)
    {
        var expansion = 8.5f + expand * 4f;

        var slowProfile = new LookProfile();
        var slow = GetVisibleEntities(brain, expansion, out _, ref slowProfile, fastOverride: false);

        var fastProfile = new LookProfile();
        var fast = GetVisibleEntities(brain, expansion, out _, ref fastProfile, fastOverride: true);

        return (slow, fast,
            slowProfile.ViewMs + slowProfile.GatherMs,
            fastProfile.ViewMs + fastProfile.GatherMs);
    }

    /// <summary>
    /// Run the upstream view and our own sliced one for a single eye in ONE frame and return both
    /// sets of tiles.
    ///
    /// <para>
    /// This method is the whole reason the sliced version has a right to exist. Our own shadow-cast
    /// is a copy of someone else's algorithm, and a copy silently drifts from the original: the AI
    /// starts seeing a tile more or less than a player would have seen from that role, and there is
    /// no way to notice it in-game. The claim "we ported it exactly" is either verifiable or it's
    /// just a promise.
    /// </para>
    /// <para>
    /// Both runs happen in one frame, for the same reason as the fast/slow gathering comparison:
    /// between two frames someone can take a step, and the test would catch that step instead of the
    /// geometry.
    /// </para>
    /// <param name="grain">
    /// The budget of a single slice, in milliseconds. Zero means "cut at every convenient point" —
    /// which is the nastiest mode there is: the finer the slice, the more chances that state carried
    /// across frames is preserved incorrectly.
    /// </param>
    /// </summary>
    public (HashSet<Vector2i> Upstream, HashSet<Vector2i> Sliced, int Slices)
        CompareViewPathsForTest(EntityUid brain, int expand, double grain = 0)
    {
        var expansion = 8.5f + expand * 4f;

        if (!TryResolveEye(brain, out var eye, out _, out var grid, out var broadphase, out var mapGrid, out var why))
            throw new InvalidOperationException(why);

        var worldPos = _xform.GetWorldPosition(Transform(eye));
        var bounds = new Box2Rotated(
            new Box2(worldPos.X - expansion, worldPos.Y - expansion, worldPos.X + expansion, worldPos.Y + expansion),
            Angle.Zero,
            worldPos);

        var upstream = new HashSet<Vector2i>();
        _vision.GetView((grid, broadphase, mapGrid), bounds, upstream, expansion);

        var view = new Vision.SlicedView(EntityManager, _lookup, _mapSystem, _xform, _power);
        view.Begin((grid, broadphase, mapGrid), bounds, expansion);

        var slices = 0;
        while (true)
        {
            // A deadline in the past means the budget is exhausted immediately, i.e. the slice breaks
            // at the very first opportunity. This is exactly how it's verified that state survives a
            // frame boundary.
            var deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                           + (long)(grain / 1000.0 * System.Diagnostics.Stopwatch.Frequency);

            slices++;

            if (view.Step(new Threading.JobBudget(deadline)))
                break;

            if (slices > 100_000)
                throw new InvalidOperationException("нарезаемый обзор не сходится — больше ста тысяч срезов");
        }

        return (upstream, view.VisibleTiles, slices);
    }

    /// <summary>
    /// Build zone 0 the way a session start or a compaction would.
    ///
    /// Exists so a test can build it twice and compare: an interpolated clock, counter or GUID in
    /// the frozen prefix costs a full prefill on every turn and presents only as "the AI got slow",
    /// with no error anywhere. Two identical builds is the cheapest way to prove there isn't one.
    /// </summary>
    public string BuildSystemPromptForTest() =>
        BuildSystemPrompt(scripted: false, lang: AgentLangUtil.Parse(_cfg.GetCVar(AiCVars.Language)));

    /// <summary>Build the observation message synchronously on the main thread.</summary>
    public string? BuildObservationForTest(EntityUid brain, bool force = true)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return null;

        // Same order as the real path, law poll included — a test hook that skipped it would report
        // the agent as blind to a rewrite it actually notices, or the reverse.
        NoticeLawChange(session);

        var (items, dropped) = session.Queue.Drain();
        return Perception.ObservationFormatter.Format(
            items, dropped, RoundTime(), SelfLine(session), force, session.Locale);
    }
}
