using System.Diagnostics;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Threading;

/// <summary>
/// Насколько срочно это надо сделать.
///
/// <para>
/// Две полосы, а не четыре, и не одна. Одной мало: объявление тревоги не должно ждать за обзором,
/// который считает две тысячи сущностей. Четырёх — театр: инструменты агента вызываются строго
/// последовательно (<c>TurnRunner</c> ждёт каждый <c>ToolDispatcher.InvokeAsync</c>, ни одного
/// <c>Task.WhenAll</c> в модуле нет), поэтому от одного агента в полёте ровно одна заявка, и
/// приоритет вообще начинает работать только при <c>ai.max_agents &gt; 1</c> либо когда появятся
/// многосрезовые джобы и короткая заявка прилетит в середину длинного обзора.
/// </para>
/// </summary>
public enum WorldPriority : byte
{
    /// <summary>Действия и речь: сказать, объявить, открыть дверь, слить наблюдения.</summary>
    Urgent,

    /// <summary>Опросы мира: обзор, карта, состояние экипажа, записи.</summary>
    Normal,
}

/// <summary>
/// Сколько ещё можно занимать кадр.
///
/// Передаётся в <see cref="IWorldJob.Step"/>, чтобы многосрезовая работа могла остановиться сама,
/// не дожидаясь, пока насос отберёт у неё управление между заявками. Для атомарных джобов
/// игнорируется.
/// </summary>
public readonly struct JobBudget
{
    private readonly long _deadline;

    public JobBudget(long deadline) => _deadline = deadline;

    public bool Exhausted => Stopwatch.GetTimestamp() >= _deadline;
}

/// <summary>
/// Единица работы, которую поток агента просит сделать в игровом мире.
///
/// <para>
/// <b>Всё, что здесь исполняется, идёт на главном потоке</b> — и только там. Это единственный
/// способ для агента дотронуться до <c>IEntityManager</c>.
/// </para>
/// <para>
/// <see cref="Step"/> может вернуть <c>false</c> и попросить ещё кадр. Ради этого интерфейс и
/// заведён: бюджет, проверяемый МЕЖДУ заявками, ничего не даёт против одного вызова на 73 мс —
/// такую работу нужно уметь резать изнутри. В этапе 2 все джобы атомарные и всегда возвращают
/// <c>true</c>; механизм стоит заранее, чтобы дробление не ломало интерфейс.
/// </para>
/// </summary>
public interface IWorldJob
{
    /// <summary>Имя операции для журнала и <c>aiagent cost</c>. Безымянное «вызов на 315мс» неразбираемо.</summary>
    string What { get; }

    WorldPriority Priority { get; }

    /// <summary>
    /// Отработать один срез на главном потоке. <c>true</c> — работа закончена.
    /// </summary>
    bool Step(JobBudget budget);

    /// <summary>Отдать результат. Зовётся ровно один раз, после <see cref="Step"/> вернувшего true.</summary>
    void Complete();

    /// <summary>
    /// Вместо <see cref="Complete"/>: устаревшее поколение, отмена, таймаут, переполнение.
    /// </summary>
    void Fail(Exception e);
}

/// <summary>
/// Джоб из одного среза — обёртка над старым добрым <c>Func&lt;T&gt;</c>.
///
/// Через него ходят все двадцать с лишним операций, унаследованных от прежнего диспетчера:
/// поведение не меняется, меняется дорога.
/// </summary>
public sealed class AtomicJob<T> : IWorldJob
{
    private readonly Func<T> _fn;

    // RunContinuationsAsynchronously — несущее, и под шиной оно стало ВАЖНЕЕ, чем было.
    //
    // Раньше продолжение исполнялось бы внутри ProcessPendingTasks (BaseServer.cs:753), то есть
    // ДО TickUpdate. Теперь Complete() зовётся из насоса, который живёт ВНУТРИ TickUpdate, — и
    // встроенное продолжение уронило бы многосекундный HTTP-вызов агента в середину обновления
    // систем. Это самый опасный способ случайно повесить сервер, и выглядит он не как ошибка, а
    // как загадочная просадка тикрейта.
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private T _result = default!;

    public AtomicJob(string what, WorldPriority priority, Func<T> fn)
    {
        What = what;
        Priority = priority;
        _fn = fn;
    }

    public string What { get; }
    public WorldPriority Priority { get; }

    public Task<T> Task => _tcs.Task;

    public bool Step(JobBudget budget)
    {
        _result = _fn();
        return true;
    }

    public void Complete() => _tcs.TrySetResult(_result);

    public void Fail(Exception e) => _tcs.TrySetException(e);
}
