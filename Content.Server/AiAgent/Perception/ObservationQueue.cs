using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Perception;

/// <summary>
/// Bounded buffer of perceived lines.
///
/// <para>
/// <b>Трогают её три потока, а не один.</b> Здесь раньше стояло «пишется с главного потока,
/// вычитывается с главного потока — контенции нет»; это перестало быть правдой и вводило в
/// заблуждение ровно там, где важно. Сегодня: <see cref="Push"/> — главный поток (обработчики
/// событий, из-под клика игрока); <see cref="Drain"/> — тоже главный, внутри маршалированного
/// вызова; а <see cref="PeekUnread"/> зовётся из <b>потока агента</b> на каждый результат
/// инструмента, и <see cref="Count"/> читается ещё и с потока отладочного HTTP.
/// </para>
/// <para>
/// Всё под одним локом, поэтому корректно. Но «контенции нет» — ложная посылка, и из неё следовал
/// вывод, что под локом можно делать что угодно: <c>PeekUnread</c> держал его через два полных
/// обхода до шестисот элементов вместе со сборкой строк, а главный поток в это время ждал на
/// <c>Push</c>. Держать лок дольше, чем нужно на выбор элементов, здесь нельзя.
/// </para>
///
/// On overflow the <em>oldest</em> entries are dropped and the count is reported to the model as
/// a "DROPPED n" line. Silently losing them would leave the agent with a blind spot it cannot
/// know about, which is far worse than a short gap it can reason around.
/// </summary>
public sealed class ObservationQueue
{
    private readonly object _lock = new();

    /// <summary>
    /// Связный список, а не <c>Queue</c>, ради одной операции: выбросить старейшую строку
    /// ОПРЕДЕЛЁННОЙ категории, не трогая остальные. У очереди такого действия нет вовсе — пришлось
    /// бы пересобирать её целиком на каждый лишний элемент, а лишние элементы приходят потоком.
    /// </summary>
    private readonly LinkedList<Observation> _items = new();

    private int _dropped;
    private int _observed;

    public int Capacity { get; set; }

    /// <summary>
    /// Сколько строк <see cref="ObsKind.Observed"/> очередь держит одновременно.
    ///
    /// Отдельный потолок нужен потому, что общий выбрасывает СТАРЕЙШЕЕ безотносительно вида. Все
    /// прочие категории — редкие сообщения, а эта приходит потоком: любое оживление в кадре
    /// вытолкнуло бы из очереди реплику по рации, то есть агент переставал бы слышать просьбы ровно
    /// в тот момент, когда их больше всего. Здесь подрезается старейшая OBSERVED, и только она.
    /// </summary>
    public int ObservedCapacity { get; set; }

    /// <param name="observedCapacity">
    /// По умолчанию без потолка: голая очередь ведёт себя ровно как до появления наблюдений, и
    /// тесты, которым нужна очередь как таковая, не обязаны знать про эту ручку. Живая сессия
    /// значение задаёт всегда — см. <c>ai.observe_buffer</c>.
    /// </param>
    public ObservationQueue(int capacity, int observedCapacity = int.MaxValue)
    {
        Capacity = capacity;
        ObservedCapacity = observedCapacity;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _items.Count;
        }
    }

    /// <summary>
    /// Raised the moment something lands, so the loop can start on it instead of sleeping out the
    /// rest of its tick.
    ///
    /// Polling alone made the agent's response time a coin flip between nothing and a full tick,
    /// and the crew feels that most exactly when it matters least to wait: someone shouting about
    /// a fire does not care that the poll had six seconds left on it.
    /// </summary>
    public Action? Arrived { get; set; }

    public void Push(Observation obs)
    {
        lock (_lock)
        {
            _items.AddLast(obs);

            if (obs.Kind == ObsKind.Observed)
                _observed++;

            // Сначала свой потолок, потом общий. Порядок важен: если поток OBSERVED уже упёрся в
            // свой лимит, до общего дело не дойдёт, и речь останется в очереди нетронутой.
            while (_observed > ObservedCapacity && TrimOldest(ObsKind.Observed))
            {
            }

            while (_items.Count > Capacity)
            {
                var first = _items.First!;
                if (first.Value.Kind == ObsKind.Observed)
                    _observed--;

                _items.RemoveFirst();
                _dropped++;
            }
        }

        // Outside the lock. The handler wakes the agent thread, and holding a perception lock while
        // another thread starts a turn is how a deadlock gets written.
        Arrived?.Invoke();
    }

    /// <summary>
    /// Выбросить самую старую строку заданного вида. Возвращает false, если такой в очереди нет.
    ///
    /// Считается в общий счётчик потерь, а не в свой: агенту сообщается «столько-то строк ты не
    /// увидел», и делить эту потерю по видам ему незачем — вернуть их всё равно нечем. Вызывается
    /// под уже взятым локом.
    /// </summary>
    private bool TrimOldest(ObsKind kind)
    {
        for (var node = _items.First; node != null; node = node.Next)
        {
            if (node.Value.Kind != kind)
                continue;

            _items.Remove(node);
            _dropped++;

            if (kind == ObsKind.Observed)
                _observed--;

            return true;
        }

        return false;
    }

    /// <summary>Take everything buffered and reset the drop counter.</summary>
    public (List<Observation> Items, int Dropped) Drain()
    {
        lock (_lock)
        {
            var items = _items.ToList();
            var dropped = _dropped;
            _items.Clear();
            _observed = 0;
            _dropped = 0;
            return (items, dropped);
        }
    }


    /// <summary>
    /// Has this exact line already been buffered as radio, moments ago?
    ///
    /// Speech and radio arrive from two different upstream events for the same utterance, and the
    /// radio one always lands first — <c>RadioSystem</c> and <c>HeadsetSystem</c> handle
    /// <c>EntitySpokeEvent</c> as <em>directed</em> subscriptions, which RobustToolbox dispatches
    /// before any broadcast one. So by the time a broadcast handler sees a transmitted line, the
    /// radio copy is already in here, and this is how it recognises it.
    /// </summary>
    /// <summary>
    /// Лежит ли в очереди событие ровно с этим текстом.
    ///
    /// Нужно ровно одному месту — периодическому напоминанию злому ИИ, — и именно затем, чтобы
    /// напоминания не копились стопкой, пока агент занят долгим ходом. Сравнение по тексту, а не
    /// по идентификатору, потому что текст напоминания и есть его идентичность: он задан одной
    /// строкой в прототипе правила и второго такого в очереди быть не должно.
    /// </summary>
    public bool HasEvent(string text)
    {
        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (item.Kind == ObsKind.Event && item.Text == text)
                    return true;
            }

            return false;
        }
    }

    public bool AlreadyHeardOnRadio(string speaker, string text, TimeSpan now, double withinSeconds = 1.0)
    {
        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (item.Kind != ObsKind.Radio)
                    continue;

                if ((now - item.RoundTime).Duration().TotalSeconds > withinSeconds)
                    continue;

                if (item.Speaker == speaker && item.Text == text)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The last thing the agent announced itself, kept only long enough to recognise the echo.
    ///
    /// An announcement the agent makes comes straight back at it: the brain carries the console
    /// component the tool drives, so it is both the announcer and a listener. Usually the source on
    /// the event identifies it, but a console configured to announce globally dispatches with no
    /// source at all, and then the text is the only thing left to match on.
    /// </summary>
    private string? _selfAnnounced;

    /// <summary>Called before the announcement goes out, because the echo arrives synchronously.</summary>
    public void NoteSelfAnnouncement(string text)
    {
        lock (_lock)
            _selfAnnounced = text;
    }

    public bool WasLastAnnouncedBySelf(string text)
    {
        lock (_lock)
            return _selfAnnounced != null && _selfAnnounced == text;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _selfAnnounced = null;
            _observed = 0;
            _dropped = 0;
        }
    }
}
