using System;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Threading;

/// <summary>
/// Джоб из многих срезов: тяжёлая часть режется по бюджету кадра, лёгкий хвост доезжает одним куском.
///
/// <para>
/// Первая реализация <see cref="IWorldJob"/> кроме атомарной — и та, ради которой интерфейс
/// заводился. В его док-комментарии это сказано прямо: «бюджет, проверяемый МЕЖДУ заявками, ничего
/// не даёт против одного вызова на 73 мс — такую работу нужно уметь резать изнутри». До
/// 20.08.2026 резать было нечем, и потому же не работал приоритет: речи и открыванию дверей
/// нечего было обгонять, кроме таких же атомарных заявок.
/// </para>
/// <para>
/// Разделение на <c>step</c> и <c>finish</c> — не украшение. У обзора тяжёлая часть (теневой каст,
/// три четверти времени) режется естественно, а хвост — сбор сущностей и построение строк —
/// обязан видеть согласованный мир и стоит единицы миллисекунд. Резать хвост значило бы платить
/// сложностью за проценты; не резать тяжёлую часть значило бы не решить задачу вовсе.
/// </para>
/// <para>
/// <b>Хвост не запускается в том же срезе, где кончилась тяжёлая часть.</b> Иначе кадр, в котором
/// каст доработал последний тайл, получил бы сверху ещё и весь сбор — то есть ровно тот всплеск,
/// от которого мы уходим, только реже и потому незаметнее в статистике.
/// </para>
/// </summary>
public sealed class SteppedJob<T> : IWorldJob
{
    private readonly Func<JobBudget, bool> _step;
    private readonly Func<T> _finish;

    // RunContinuationsAsynchronously по той же причине, что у AtomicJob: продолжение зовётся из
    // насоса внутри TickUpdate, и встроенное продолжение уронило бы HTTP-вызов агента в середину
    // обновления систем.
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _heavyDone;
    private T _result = default!;

    public SteppedJob(string what, WorldPriority priority, Func<JobBudget, bool> step, Func<T> finish)
    {
        What = what;
        Priority = priority;
        _step = step;
        _finish = finish;
    }

    public string What { get; }
    public WorldPriority Priority { get; }
    public Task<T> Task => _tcs.Task;

    /// <summary>Сколько срезов ушло. Пишется в журнал обзора — иначе «резка работает» непроверяема.</summary>
    public int Slices { get; private set; }

    public bool Step(JobBudget budget)
    {
        Slices++;

        if (!_heavyDone)
        {
            _heavyDone = _step(budget);

            if (!_heavyDone)
                return false;

            // Бюджет уже выбран — хвост следующим кадром. См. разбор в шапке.
            if (budget.Exhausted)
                return false;
        }

        _result = _finish();
        return true;
    }

    public void Complete() => _tcs.TrySetResult(_result);

    public void Fail(Exception e) => _tcs.TrySetException(e);
}
