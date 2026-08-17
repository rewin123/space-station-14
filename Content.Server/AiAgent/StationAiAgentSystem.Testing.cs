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

    /// <summary>
    /// Сколько строк наблюдения выпущено с начала процесса.
    ///
    /// Существует ради ОТРИЦАТЕЛЬНОГО теста, и это главное. Проверять, что далёкое событие не
    /// попало в наблюдение, по тексту сообщения — значит проверять формат: строки может не быть и
    /// потому, что она не построилась. Ноль в счётчике означает, что ворота отказали до всякой
    /// работы, а это ровно то утверждение, которое защищает паритет.
    /// </summary>
    public int WitnessedCount() => _witnessed;

    /// <summary>Worst main-thread call observed, for the "never stalls the tick" benchmark.</summary>
    public (string What, double Ms) SlowestMainThreadCall() => (_world.Slowest, _world.SlowestMs);

    /// <summary>
    /// Во что обошёлся главный поток по каждой операции, самые дорогие сверху.
    ///
    /// Одного максимума для разбора не хватает: тридцать вызовов по 26 мс и один на 73 мс дают
    /// одинаковый максимум, но отличаются по суммарной цене в двадцать раз, а чинятся по-разному —
    /// первое дроблением, второе удешевлением. Колонка <c>Total</c> и есть та, по которой стоит
    /// решать, что трогать.
    /// </summary>
    public IReadOnlyList<(string What, long Count, double P50, double P95, double Max, double Total, long Overruns)>
        MainThreadReport() => _world.Report();

    /// <summary>Сколько всего главного потока съел агент с начала процесса.</summary>
    public double MainThreadTotalMs() => _world.TotalMs;

    /// <summary>
    /// Здоровье шины мира. Три из пяти чисел обязаны быть нулями, и это утверждение, а не надежда:
    /// переполнение означает параллелизм, которого в модуле нет, а большое ожидание — голодание.
    /// </summary>
    public (int Depth, long Deferrals, long Promotions, long Overflows, double MaxWaitMs) WorldBusHealth() =>
        (_world.Depth, _world.Deferrals, _world.Promotions, _world.Overflows, _world.MaxWaitMs);

    /// <summary>Прогнать очередь мира вручную — для тестов, которые тикают сервер сами.</summary>
    public void PumpWorldBusForTest() => _world.Pump();

    /// <summary>
    /// Поставить произвольный джоб в шину мира от имени живой сессии.
    ///
    /// Существует ради тестов на дробление и на устаревшее поколение: настоящие инструменты пока
    /// все атомарные, и проверить многосрезовый путь на них нечем. Поколение берётся у сессии
    /// по-настоящему, поэтому <c>ReleaseAll</c> в середине теста роняет заявку тем же способом,
    /// каким её уронило бы закарживание в бою.
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

    /// <summary>Последняя строка о длительности тика и сколько их опоздало (>1.5 периода).</summary>
    public (string Last, long Ticks, long Overruns) FrameReport() =>
        (_frames.Last, _frames.Ticks, _frames.Overruns);

    /// <summary>
    /// Во что обошёлся последний обзор.
    ///
    /// <c>Queries</c> здесь — главное, а миллисекунды приложены. Тест на «ровно один поход в
    /// broadphase» детерминирован и не зависит ни от машины, ни от того, что за карта загружена;
    /// тест на миллисекунды меряет сборочный агент и шумит. Поэтому сторожем ставится первый.
    /// </summary>
    public (int Queries, int Tiles, int Candidates, int OnScreen, int Rows,
        double ViewMs, double GatherMs, double RowsMs) LastLookCost() =>
        (_lastLook.Queries, _lastLook.Tiles, _lastLook.Candidates, _lastLook.OnScreen, _lastLook.Rows,
            _lastLook.ViewMs, _lastLook.GatherMs, _lastLook.RowsMs);

    /// <summary>
    /// Оба пути сбора по одному и тому же кадру.
    ///
    /// Существует ради теста эквивалентности: тот требует, чтобы быстрый путь не потерял ничего из
    /// увиденного медленным. Сравнивать по ответу инструмента нельзя — его режет
    /// <c>ai.look_limit</c>, и пропажа на трёхсотой строке выглядела бы как обрезка.
    ///
    /// <b>Оба замера обязаны лежать в одном вызове, и это не удобство.</b> Первая версия теста
    /// дёргала пути по очереди через CVar, между ними проходил тик — и на радиусе в двадцать
    /// тайлов она нашла «потерю» одной сущности из двух с половиной тысяч. Потери не было: кто-то
    /// успел перейти границу видимости. Тест, который ловит шаги вместо геометрии, хуже
    /// отсутствующего: он врёт в обе стороны и приучает не верить красному.
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
