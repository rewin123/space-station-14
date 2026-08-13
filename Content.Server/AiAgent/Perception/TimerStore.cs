using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Perception;

/// <summary>
/// Один заведённый агентом таймер.
/// </summary>
/// <param name="DueAt">
/// Раундовое время срабатывания, а не реальное. Это единственный правильный выбор здесь:
/// <c>game.auto_pause_empty</c> замораживает симуляцию на пустом сервере, вместе с ней стоит
/// <c>CurTime</c> и стоит раундовый час, который агент видит в каждом наблюдении. Таймер на
/// реальном времени в этот момент продолжал бы тикать и разбудил бы агента посреди паузы — то есть
/// заставил бы его действовать в мире, который не сделал ни одного тика, и заплатить за это модели.
/// </param>
/// <param name="Every">
/// Интервал повтора, или null для одноразового. После срабатывания повторный таймер встаёт заново
/// от МОМЕНТА СРАБАТЫВАНИЯ, а не от прошлого срока: иначе таймер, проспавший паузу, отстрелялся бы
/// столько раз, сколько интервалов уместилось в простой, и агент получил бы пачку одинаковых
/// напоминаний об одном и том же.
/// </param>
public sealed record AgentTimer(string Name, string Message, TimeSpan DueAt, TimeSpan? Every);

/// <summary>Что вышло из попытки завести таймер. Отказ всегда называет причину словами для модели.</summary>
public sealed record TimerSetResult(bool Ok, string Message, AgentTimer? Timer = null, bool Replaced = false);

/// <summary>
/// Будильники агента: единственный способ для него самому вернуться к делу, о котором сейчас
/// договорились, а сделать надо потом.
///
/// Без них у петли ровно два повода начать ход — кто-то заговорил или истёк тик простоя, — и
/// «проверю через десять минут» превращалось в обещание, которое нечем сдержать: следующий ход
/// приходил по чужой реплике, с другим контекстом, и о проверке никто не вспоминал. Сработавший
/// таймер входит в ту же <see cref="ObservationQueue"/>, что и речь экипажа, и будит петлю тем же
/// сигналом — для агента это такое же событие мира, как оклик по рации.
///
/// Хранилище живёт в <see cref="AgentState"/> (правило: переживает ход — живёт там) и не знает ни
/// про сущности, ни про часы: время ему передают снаружи. Замок нужен потому, что писать сюда
/// может поток агента (инструменты), а вычитывать — главный поток (тик) и поток отладочной шины.
/// </summary>
public sealed class TimerStore
{
    private readonly object _lock = new();
    private readonly List<AgentTimer> _timers = new();

    /// <summary>Дольше раунда таймеру жить незачем: сессия всё равно кончится вместе со сменой.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(2);

    public int Count
    {
        get
        {
            lock (_lock)
                return _timers.Count;
        }
    }

    /// <summary>Все таймеры по возрастанию срока. Порядок фиксирован, чтобы строка SELF не дрожала.</summary>
    public IReadOnlyList<AgentTimer> All()
    {
        lock (_lock)
            return _timers.OrderBy(t => t.DueAt).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Names()
    {
        lock (_lock)
            return _timers.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Завести таймер или переставить существующий с тем же именем.
    ///
    /// Совпадение имени именно переставляет, а не отказывает: «напомни ещё через десять минут» —
    /// самая частая правка собственного плана, и отказ по занятому имени заставлял бы модель
    /// сначала удалять, тратя второй вызов из шести, отпущенных на ход. Замена названа в ответе
    /// словом, чтобы перезапись не выглядела как заведение второго.
    /// </summary>
    /// <param name="now">Текущее раундовое время.</param>
    /// <param name="max">Потолок числа таймеров, из <c>ai.max_timers</c>.</param>
    public TimerSetResult Set(string name, string message, TimeSpan after, TimeSpan? every, TimeSpan now, int max)
    {
        var timer = new AgentTimer(name, message, now + after, every);

        lock (_lock)
        {
            var index = _timers.FindIndex(t => Same(t.Name, name));

            if (index < 0 && _timers.Count >= max)
            {
                return new TimerSetResult(false,
                    $"уже заведено {_timers.Count} таймеров, это потолок — удали ненужный через del_timer");
            }

            if (index < 0)
            {
                _timers.Add(timer);
                return new TimerSetResult(true, "заведён", timer);
            }

            _timers[index] = timer;
            return new TimerSetResult(true, "переставлен", timer, Replaced: true);
        }
    }

    public bool Remove(string name, out AgentTimer? removed)
    {
        lock (_lock)
        {
            var index = _timers.FindIndex(t => Same(t.Name, name));
            if (index < 0)
            {
                removed = null;
                return false;
            }

            removed = _timers[index];
            _timers.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    /// Забрать сработавшие и перевести повторные на следующий круг. Вызывается из тика.
    ///
    /// Возвращает список, а не по одному: за один тик может подойти срок сразу у нескольких, и
    /// отдавать их порознь значило бы разбудить агента столько же раз подряд.
    /// </summary>
    public IReadOnlyList<AgentTimer> TakeDue(TimeSpan now)
    {
        lock (_lock)
        {
            if (_timers.Count == 0)
                return Array.Empty<AgentTimer>();

            List<AgentTimer>? fired = null;

            for (var i = _timers.Count - 1; i >= 0; i--)
            {
                var timer = _timers[i];
                if (timer.DueAt > now)
                    continue;

                (fired ??= new List<AgentTimer>()).Add(timer);

                if (timer.Every is { } every)
                    _timers[i] = timer with { DueAt = now + every };
                else
                    _timers.RemoveAt(i);
            }

            if (fired == null)
                return Array.Empty<AgentTimer>();

            // Обратный порядок обхода дал бы порядок срабатывания задом наперёд; сортируем по сроку,
            // как и всё остальное здесь, чтобы одно и то же состояние давало одни и те же байты.
            fired.Sort((a, b) => a.DueAt != b.DueAt
                ? a.DueAt.CompareTo(b.DueAt)
                : string.CompareOrdinal(a.Name, b.Name));

            return fired;
        }
    }

    /// <summary>Ближайшие по написанию имена — для внятного отказа на промах в del_timer.</summary>
    public IReadOnlyList<string> Nearest(string name, int count = 3)
    {
        lock (_lock)
        {
            return _timers
                .Select(t => t.Name)
                .OrderBy(n => Tools.AiToolRegistry.Distance(n.ToLowerInvariant(), name.ToLowerInvariant()))
                .ThenBy(n => n, StringComparer.Ordinal)
                .Take(count)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
            _timers.Clear();
    }

    /// <summary>Восстановить снимок. Полностью замещает содержимое — снимок и есть состояние.</summary>
    public void Restore(IEnumerable<AgentTimer> timers)
    {
        lock (_lock)
        {
            _timers.Clear();
            _timers.AddRange(timers);
        }
    }

    /// <summary>Имя сравнивается без регистра: модель пишет «Обход» и «обход» как одно и то же.</summary>
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
