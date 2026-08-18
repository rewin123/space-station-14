using System.Collections.Generic;
using Content.Server.AiAgent.Perception;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Заряд: робот обязан знать, сколько его осталось, ДО того как встанет.
///
/// <para>
/// Появилось после живого прогона, где робот собрал семь клеток экранирования вокруг реактора,
/// сел без заряда и сообщил об этом уже постфактум: «батарея села». В строке SELF до этого был
/// только флаг «шасси активно / НЕ АКТИВНО», то есть заряд становился видимым ровно в тот момент,
/// когда чинить положение уже нечем — модули отваливаются вместе с руками.
/// </para>
/// <para>
/// Поэтому две вещи: процент в каждой строке SELF и отдельная строка на каждый потерянный
/// процент. Второе — по прямому решению владельца: расход у борга неровный (ходьба, инструменты,
/// простой), и «сколько осталось времени» надёжнее считать по скорости падения, чем по одному
/// числу.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private PowerCellSystem _powerCell = default!;
    [Dependency] private SharedBatterySystem _battery = default!;

    /// <summary>
    /// Через сколько процентов падения докладывать.
    /// </summary>
    /// <remarks>
    /// ИЗВЕСТНЫЙ ДЕФЕКТ: ступень держится не всегда. Сравнение идёт с последним ДОЛОЖЕННЫМ
    /// уровнем, а он обновляется и в ветке «докладывать не надо» — при зарядке и при быстром
    /// разряде база сползает, и шаг схлопывается обратно в один процент. На боевом прогоне это
    /// видно так: 99, 98, 97 … 80, потом честные 75, 70, 65, 60, а дальше снова по одному.
    /// Чинится переходом на сетку (percent / ChargeStep), а не на разницу с предыдущим.
    /// </remarks>
    private const int ChargeStep = 5;

    /// <summary>Последний доложенный процент заряда, чтобы не повторяться.</summary>
    private readonly Dictionary<EntityUid, int> _lastCharge = new();

    /// <summary>Заряд в процентах, или <c>null</c>, если батареи нет вовсе.</summary>
    public int? ChargePercent(EntityUid borg)
    {
        if (!_powerCell.TryGetBatteryFromSlot(borg, out var battery))
            return null;

        var max = battery.Value.Comp.MaxCharge;

        if (max <= 0f)
            return null;

        var now = _battery.GetCharge(battery.Value.Owner);
        return (int) MathF.Floor(now / max * 100f);
    }

    /// <summary>
    /// Доложить, если заряд просел ещё на процент.
    /// </summary>
    /// <remarks>
    /// Только на СНИЖЕНИЕ: зарядка идёт быстро, и обратный отсчёт вверх залил бы очередь
    /// наблюдений десятками строк, вытеснив из неё рацию.
    ///
    /// <para>
    /// Шаг — пять процентов. Начинали с одного, и на живом прогоне это оказалось шумом: за ход
    /// набегало по десять строк «ЗАРЯД», которые в очереди конкурируют с рацией и чужой речью.
    /// Пять процентов дают тот же ответ на вопрос «успею ли», занимая впятеро меньше места.
    /// </para>
    /// </remarks>
    private void WatchCharge(EntityUid borg)
    {
        if (ChargePercent(borg) is not { } percent)
            return;

        if (!_lastCharge.TryGetValue(borg, out var last))
        {
            _lastCharge[borg] = percent;
            return;
        }

        // Шаг доклада: молчим, пока не потеряли целую ступень.
        if (percent > last - ChargeStep)
        {
            // Зарядился — просто запоминаем новый уровень, молча.
            if (percent > last)
                _lastCharge[borg] = percent;

            return;
        }

        _lastCharge[borg] = percent;

        // Ниже двадцати процентов формулировка меняется: там уже не сводка, а срок.
        var text = percent switch
        {
            <= 5 => $"ЗАРЯД {percent}% — вот-вот встанешь. Бросай дело и иди на зарядную станцию.",
            <= 20 => $"ЗАРЯД {percent}% — мало. Прикинь, хватит ли на текущее дело, и иди заряжаться.",
            _ => $"ЗАРЯД {percent}%",
        };

        PushToBorg(borg, Observation.Event(text, _host.RoundTime()));
    }

    private void ForgetCharge(EntityUid borg) => _lastCharge.Remove(borg);
}
