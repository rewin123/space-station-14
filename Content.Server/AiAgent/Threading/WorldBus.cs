using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;

namespace Content.Server.AiAgent.Threading;

/// <summary>
/// Thrown when a marshalled call arrives after the world it targeted has moved on — the AI was
/// carded, killed, or the round restarted while an LLM call was in flight. Callers treat it as a
/// clean exit, not an error.
/// </summary>
public sealed class StaleGenerationException : Exception
{
    public StaleGenerationException(int expected, int actual)
        : base($"agent generation {expected} is stale (current {actual})")
    {
    }
}

/// <summary>Очередь переполнена. Никогда не тихо: заявка отказывается вслух и попадает в счётчик.</summary>
public sealed class WorldBusOverflowException : Exception
{
    public WorldBusOverflowException(int depth)
        : base($"очередь запросов к миру переполнена ({depth})")
    {
    }
}

/// <summary>
/// Единственный мост между потоком агента и игровым миром.
///
/// <para>
/// <b>Зачем своя очередь, а не движковая.</b> Раньше каждый вызов уходил через
/// <c>ITaskManager.RunOnMainThread</c>, а движок сливает эту очередь ЦЕЛИКОМ:
/// <c>RobustSynchronizationContext.ProcessPendingTasks</c> это голый
/// <c>while (TryRead) Callback()</c> — ни потолка, ни временного среза
/// (<c>Robust.Shared/Asynchronous/RobustSynchronizationContext.cs:52</c>). Забюджетировать её на
/// месте нельзя: она общая со всеми асинхронными продолжениями движка. Поэтому очередь здесь
/// своя, и сливает её <see cref="Pump"/> из <c>Update</c>, под бюджетом на кадр.
/// </para>
/// <para>
/// <b>Два свойства прежнего диспетчера перенесены дословно, и оба несущие.</b>
/// Первое — <c>RunContinuationsAsynchronously</c> на <see cref="AtomicJob{T}"/>, см. комментарий
/// там: под шиной оно стало важнее, потому что <c>Complete()</c> теперь зовётся внутри
/// <c>TickUpdate</c>. Второе — проверка поколения происходит НА ГЛАВНОМ ПОТОКЕ непосредственно
/// перед срезом, а не на потоке агента перед постановкой в очередь: ИИ может умереть между
/// проверкой и исполнением, и это гонка, а не теоретическая.
/// </para>
/// <para>
/// <b>Честная оговорка о пользе.</b> Замеры на живом сервере 17.08 дали 0.00–0.01% времени
/// главного потока на всю работу агента, при том что <c>EntitySystems</c> занимают 63–78% кадра.
/// Шина не чинит просадку тика — её причина в другом месте. Она даёт предсказуемость: ни один
/// будущий инструмент не сможет занять кадр целиком, а сколько он занял, видно в <c>aiagent cost</c>.
/// </para>
/// </summary>
public sealed class WorldBus
{
    private readonly ITaskManager _tasks;
    private readonly ISawmill _sawmill;
    private readonly int _mainThreadId;

    /// <summary>Per-call budget in milliseconds; exceeding it logs a warning.</summary>
    public double BudgetMs { get; set; }

    /// <summary>Сколько шина может занимать один кадр. <c>ai.frame_budget_ms</c>.</summary>
    public double FrameBudgetMs { get; set; } = 3.0;

    /// <summary>Возраст, после которого обычная заявка обслуживается вне очереди. <c>ai.world_promote_ms</c>.</summary>
    public double PromoteAfterMs { get; set; } = 500;

    /// <summary>Потолок глубины очереди. <c>ai.world_queue_max</c>.</summary>
    public int QueueMax { get; set; } = 256;

    /// <summary>
    /// Рубильник. <c>false</c> — заявки уходят прямо в <c>ITaskManager</c>, как до шины.
    ///
    /// Публичный сервер, пересборка кикает всех — значит откат обязан быть командой из
    /// админ-консоли, а не выкаткой. Тот же приём, что у <c>ai.look_fast</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default ceiling on how long a marshalled call may take to come back.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // Diagnostics, read by `aiagent cost`.
    public long Calls { get; private set; }
    public double MaxObservedMs { get; private set; }
    public long BudgetOverruns { get; private set; }
    public string Slowest { get; private set; } = "—";
    public double SlowestMs { get; private set; }

    /// <summary>
    /// Сколько главного потока агент съел суммарно. Делённое на время наблюдения — это и есть
    /// «доля тика, которую стоит ИИ», единственное число, которым можно защищать или опровергать
    /// утверждение «виснем из-за ИИ». Одного максимума для этого не хватает: тридцать вызовов по
    /// 26 мс и один на 73 мс дают одинаковый максимум и разную стоимость в двадцать раз.
    /// </summary>
    public double TotalMs { get; private set; }

    /// <summary>Сколько раз заявка не доделалась за кадр и уехала в следующий.</summary>
    public long Deferrals { get; private set; }

    /// <summary>Сколько раз обычная заявка обслужена вне очереди по возрасту.</summary>
    public long Promotions { get; private set; }

    /// <summary>Сколько заявок отказано по переполнению. Обязано быть нулём.</summary>
    public long Overflows { get; private set; }

    /// <summary>Самое долгое ожидание в очереди, мс. Сторож голодания.</summary>
    public double MaxWaitMs { get; private set; }

    private readonly ConcurrentQueue<Request> _urgent = new();
    private readonly ConcurrentQueue<Request> _normal = new();

    // Недоделанные заявки. Плейн-поля, а не очередь: насос однопоточный (только главный поток),
    // и в каждой полосе может висеть максимум одна незавершённая работа.
    private Request? _resumeUrgent;
    private Request? _resumeNormal;

    private int _depth;

    /// <summary>Must be constructed on the main thread — that is how it learns which thread that is.</summary>
    public WorldBus(ITaskManager tasks, ISawmill sawmill, double budgetMs)
    {
        _tasks = tasks;
        _sawmill = sawmill;
        _mainThreadId = Environment.CurrentManagedThreadId;
        BudgetMs = budgetMs;
    }

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public int Depth => Volatile.Read(ref _depth);

    /// <summary>
    /// Guard for code that must never run off the game thread. Every tool body starts with this, so
    /// a mistake surfaces as a loud assert rather than as memory corruption or a heisenbug three
    /// hours into a round.
    /// </summary>
    public void AssertMainThread(string what)
    {
        if (!IsMainThread)
            throw new InvalidOperationException(
                $"{what} touched the entity manager off the main thread (tid {Environment.CurrentManagedThreadId}, main {_mainThreadId})");
    }

    private sealed class Request
    {
        public required IWorldJob Job;
        public required int Generation;
        public required Func<int> GenerationSource;
        public required CancellationToken Ct;
        public long Enqueued;

        /// <summary>Ждущий сдался по таймауту. Насос не должен тратить на такое ни одного среза.</summary>
        public volatile bool Abandoned;
    }

    /// <summary>Run <paramref name="fn"/> on the main thread and await its result.</summary>
    public Task<T> RunAsync<T>(
        Func<T> fn,
        int generation,
        Func<int> generationSource,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string what = "?",
        WorldPriority priority = WorldPriority.Normal)
    {
        var job = new AtomicJob<T>(what, priority, fn);
        return SubmitAsync(job, job.Task, generation, generationSource, ct, timeout);
    }

    /// <summary>Void-returning convenience overload.</summary>
    public Task RunAsync(Action act, int generation, Func<int> generationSource, CancellationToken ct,
        TimeSpan? timeout = null, string what = "?", WorldPriority priority = WorldPriority.Normal)
    {
        return RunAsync(() => { act(); return true; }, generation, generationSource, ct, timeout, what, priority);
    }

    /// <summary>
    /// Поставить джоб в очередь и дождаться его результата.
    /// </summary>
    public async Task<T> SubmitAsync<T>(
        IWorldJob job,
        Task<T> result,
        int generation,
        Func<int> generationSource,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        if (!Enabled)
            return await LegacyAsync(job, result, generation, generationSource, ct, timeout).ConfigureAwait(false);

        var request = new Request
        {
            Job = job,
            Generation = generation,
            GenerationSource = generationSource,
            Ct = ct,
            Enqueued = Stopwatch.GetTimestamp(),
        };

        // Переполнение отказывает НОВОЙ заявке, а не выбрасывает старую: старая уже кого-то ждёт.
        // При глубине в единицу это не должно срабатывать никогда, и в этом главная польза —
        // сработало, значит где-то завёлся параллелизм, которого в модуле нет.
        if (Interlocked.Increment(ref _depth) > QueueMax)
        {
            Interlocked.Decrement(ref _depth);
            Overflows++;
            job.Fail(new WorldBusOverflowException(Depth));
            return await result.ConfigureAwait(false);
        }

        (job.Priority == WorldPriority.Urgent ? _urgent : _normal).Enqueue(request);

        try
        {
            return await result.WaitAsync(timeout ?? CallTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception) when (MarkAbandoned(request))
        {
            // Фильтр всегда возвращает false — он существует ради побочного эффекта.
            throw;
        }
    }

    /// <summary>
    /// Ждущий ушёл. Пометить, чтобы насос не тратил кадры на работу, которая никому не нужна.
    ///
    /// Без этого дроблёный обзор, от которого отказались по таймауту, продолжал бы жечь бюджет
    /// ещё два десятка тиков.
    /// </summary>
    private static bool MarkAbandoned(Request request)
    {
        request.Abandoned = true;
        return false;
    }

    /// <summary>Дошинный путь: постановка прямо в очередь движка. Живёт ради <c>ai.world_bus false</c>.</summary>
    private async Task<T> LegacyAsync<T>(IWorldJob job, Task<T> result, int generation,
        Func<int> generationSource, CancellationToken ct, TimeSpan? timeout)
    {
        // Глубину надо поднять и здесь: RunSlice снимает её на каждом терминальном пути, и без
        // парного инкремента счётчик уехал бы в минус — а он показывается в `aiagent cost`.
        Interlocked.Increment(ref _depth);

        _tasks.RunOnMainThread(() =>
        {
            var request = new Request
            {
                Job = job,
                Generation = generation,
                GenerationSource = generationSource,
                Ct = ct,
                Enqueued = Stopwatch.GetTimestamp(),
            };

            // До конца, в одном кадре: бюджета на этом пути нет по определению — он существует
            // ровно затем, чтобы вернуть поведение, которое было ДО шины. Без цикла многосрезовый
            // джоб повис бы навсегда: складывать его некуда, насос в режиме отката его не увидит.
            var budget = new JobBudget(long.MaxValue);
            while (RunSlice(request, budget) != null)
            {
            }
        });

        return await result.WaitAsync(timeout ?? CallTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Слить очередь в пределах бюджета. Зовётся из <c>StationAiAgentSystem.Update</c>.
    ///
    /// <para>
    /// Место выбрано: <c>Update</c> идёт внутри <c>_entityManager.TickUpdate</c>, то есть ПОСЛЕ
    /// того, как движок слил свою очередь (<c>BaseServer.cs:753</c>, затем <c>:757</c>). Правки
    /// мира ложатся вместе с остальными системами, а не тиком раньше. Из <c>Input</c>
    /// (<c>BaseServer.cs:723</c>) насос звать нельзя по той же причине.
    /// </para>
    /// <para>
    /// Один срез исполняется ДО первой проверки дедлайна — это гарантия продвижения. Иначе
    /// перегруженный сервер, у которого бюджет выбран всегда, заморозил бы агента навсегда, и тот
    /// тихо умер бы посреди раунда: ровно тот класс отказов, от которого модуль защищается везде.
    /// </para>
    /// </summary>
    public void Pump()
    {
        AssertMainThread("world bus pump");

        // Проверки `Enabled` здесь НЕТ намеренно. Рубильник меняет только то, куда попадают новые
        // заявки; уже стоящие в очереди обязаны доехать. Иначе `cvar ai.world_bus false`,
        // отданный в момент, когда в полосе что-то лежит, подвесил бы эту заявку до таймаута —
        // то есть аварийный рубильник сам устраивал бы аварию.
        var deadline = Stopwatch.GetTimestamp() + (long)(FrameBudgetMs / 1000.0 * Stopwatch.Frequency);
        var budget = new JobBudget(deadline);

        while (TryTake(out var request))
        {
            if (RunSlice(request!, budget) is { } unfinished)
            {
                if (unfinished.Job.Priority == WorldPriority.Urgent)
                    _resumeUrgent = unfinished;
                else
                    _resumeNormal = unfinished;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
                break;
        }
    }

    /// <summary>
    /// Взять следующую заявку: недоделанные вперёд, затем срочные, затем обычные.
    ///
    /// Состарившаяся обычная заявка обгоняет срочные — иначе поток срочных мог бы держать обзор в
    /// очереди неограниченно долго.
    /// </summary>
    private bool TryTake(out Request? request)
    {
        if (_resumeUrgent != null)
        {
            request = _resumeUrgent;
            _resumeUrgent = null;
            return true;
        }

        if (_resumeNormal != null && (_urgent.IsEmpty || Aged(_resumeNormal)))
        {
            request = _resumeNormal;
            _resumeNormal = null;
            return true;
        }

        if (_normal.TryPeek(out var head) && Aged(head) && _normal.TryDequeue(out request))
        {
            Promotions++;
            return true;
        }

        if (_urgent.TryDequeue(out request))
            return true;

        if (_resumeNormal != null)
        {
            request = _resumeNormal;
            _resumeNormal = null;
            return true;
        }

        return _normal.TryDequeue(out request);
    }

    private bool Aged(Request request) =>
        Stopwatch.GetElapsedTime(request.Enqueued).TotalMilliseconds >= PromoteAfterMs;

    /// <summary>
    /// Отработать один срез заявки. Только главный поток.
    /// </summary>
    /// <returns>
    /// Ту же заявку, если она попросила ещё кадр; <c>null</c>, если всё кончено — успехом или
    /// отказом. Решать, КУДА положить недоделанную, — дело вызывающего: насос кладёт её в свою
    /// полосу, путь отката просто крутит цикл.
    /// </returns>
    private Request? RunSlice(Request request, JobBudget budget)
    {
        var job = request.Job;

        // Ждущий ушёл по таймауту — работа больше никому не нужна.
        if (request.Abandoned)
        {
            Interlocked.Decrement(ref _depth);
            return null;
        }

        if (request.Ct.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _depth);
            job.Fail(new OperationCanceledException(request.Ct));
            return null;
        }

        var waited = Stopwatch.GetElapsedTime(request.Enqueued).TotalMilliseconds;
        if (waited > MaxWaitMs)
            MaxWaitMs = waited;

        var start = Stopwatch.GetTimestamp();
        var done = true;

        try
        {
            // Поколение проверяется здесь, на главном потоке, перед КАЖДЫМ срезом — не только
            // перед первым. С дроблением это перестаёт быть формальностью: ИИ вполне может умереть
            // между двумя срезами одного обзора.
            var current = request.GenerationSource();
            if (current != request.Generation)
            {
                Interlocked.Decrement(ref _depth);
                job.Fail(new StaleGenerationException(request.Generation, current));
                return null;
            }

            done = job.Step(budget);
        }
        catch (Exception e)
        {
            Interlocked.Decrement(ref _depth);
            job.Fail(e);
            return null;
        }
        finally
        {
            Observe(job.What, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        if (done)
        {
            Interlocked.Decrement(ref _depth);
            job.Complete();
            return null;
        }

        Deferrals++;
        return request;
    }

    private void Observe(string what, double ms)
    {
        Calls++;
        TotalMs += ms;

        if (!_byOp.TryGetValue(what, out var op))
            _byOp[what] = op = new OpStats();

        op.Add(ms);

        if (ms > MaxObservedMs)
            MaxObservedMs = ms;

        if (ms > SlowestMs)
        {
            SlowestMs = ms;
            Slowest = what;
        }

        if (ms <= BudgetMs)
            return;

        BudgetOverruns++;
        op.Overruns++;

        // Name the operation. An unnamed "call took 315ms" is unactionable — you cannot tell a
        // vision sweep from a chat message from a records scan.
        _sawmill.Warning($"main-thread call '{what}' took {ms:F1}ms (budget {BudgetMs:F1}ms)");
    }

    /// <summary>
    /// Замеры по операциям. Без синхронизации — и это требование, а не упущение: пишется только из
    /// <see cref="RunSlice"/>, то есть с главного потока, и читается только оттуда же
    /// (<c>aiagent cost</c> — консольная команда, тесты идут через <c>AiWorld.Read</c>).
    /// Появится читатель с потока отладочного HTTP — сюда нужен будет лок.
    /// </summary>
    private readonly Dictionary<string, OpStats> _byOp = new();

    /// <summary>
    /// Кольцо на 256 последних замеров, а не полная выборка: перцентили нужны свежие, память —
    /// ограниченная, а сортировка при чтении происходит только по команде.
    /// </summary>
    private sealed class OpStats
    {
        private const int Capacity = 256;
        private readonly double[] _ring = new double[Capacity];
        private int _next;
        private int _filled;

        public long Count;
        public double Max;
        public double Total;
        public long Overruns;

        public void Add(double ms)
        {
            Count++;
            Total += ms;
            if (ms > Max)
                Max = ms;

            _ring[_next] = ms;
            _next = (_next + 1) % Capacity;
            if (_filled < Capacity)
                _filled++;
        }

        public (double P50, double P95) Percentiles()
        {
            if (_filled == 0)
                return (0, 0);

            var sample = new double[_filled];
            Array.Copy(_ring, sample, _filled);
            Array.Sort(sample);

            return (Pick(sample, 0.50), Pick(sample, 0.95));
        }

        private static double Pick(double[] sorted, double q)
        {
            var i = (int)Math.Ceiling(q * sorted.Length) - 1;
            return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
        }
    }

    /// <summary>Построчный отчёт для <c>aiagent cost</c>, самые дорогие сверху.</summary>
    public IReadOnlyList<(string What, long Count, double P50, double P95, double Max, double Total, long Overruns)> Report()
    {
        var rows = new List<(string, long, double, double, double, double, long)>(_byOp.Count);

        foreach (var (what, s) in _byOp)
        {
            var (p50, p95) = s.Percentiles();
            rows.Add((what, s.Count, p50, p95, s.Max, s.Total, s.Overruns));
        }

        rows.Sort((a, b) => b.Item6.CompareTo(a.Item6));
        return rows;
    }
}
