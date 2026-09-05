using System.Collections.Generic;
using Content.Server.AiAgent.Perception;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Charge: the robot must know how much is left BEFORE it powers down.
///
/// <para>
/// This came out of a live run where the robot collected seven shielding cells around the
/// reactor, powered down without charge, and reported it only after the fact: "battery died".
/// Before that, the SELF line only had a "chassis active / NOT ACTIVE" flag, meaning the charge
/// only became visible at the exact moment nothing could be done about it anymore — modules fall
/// off along with the hands.
/// </para>
/// <para>
/// Hence two things: a percentage in every SELF line, and a separate line for every percent
/// lost. The second one is a deliberate decision by the owner: a borg's drain is uneven (walking,
/// tools, idling), so "how much time is left" is more reliably estimated from the rate of drop
/// than from a single number.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private PowerCellSystem _powerCell = default!;
    [Dependency] private SharedBatterySystem _battery = default!;

    /// <summary>
    /// After how many percent of drop to report.
    /// </summary>
    /// <remarks>
    /// KNOWN DEFECT: the step doesn't always hold. The comparison is against the last REPORTED
    /// level, but that level also gets updated on the "no need to report" branch — during charging
    /// and during a fast discharge the baseline drifts, and the step collapses back to one percent.
    /// On a live run this looks like: 99, 98, 97 … 80, then honest 75, 70, 65, 60, and then back to
    /// one-by-one. Fixed by switching to a grid (percent / ChargeStep) instead of a diff against
    /// the previous value.
    /// </remarks>
    private const int ChargeStep = 5;

    /// <summary>Last reported charge percentage, so we don't repeat.</summary>
    private readonly Dictionary<EntityUid, int> _lastCharge = new();

    /// <summary>Charge in percent, or <c>null</c> if there's no battery at all.</summary>
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
    /// Report if the charge has dropped by another percent.
    /// </summary>
    /// <remarks>
    /// Only on a DECREASE: charging is fast, and counting back up would flood the observation
    /// queue with dozens of lines, pushing the radio out of it.
    ///
    /// <para>
    /// The step is five percent. Started with one, and on a live run that turned out to be noise:
    /// per turn there'd be a pile-up of ten "CHARGE" lines competing in the queue with radio and
    /// other people's speech. Five percent gives the same answer to "will I make it" while taking
    /// up five times less space.
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

        // Report step: stay quiet until we've lost a whole step.
        if (percent > last - ChargeStep)
        {
            // Charged up — just remember the new level, silently.
            if (percent > last)
                _lastCharge[borg] = percent;

            return;
        }

        _lastCharge[borg] = percent;

        // Below twenty percent the wording changes: it's no longer a summary, it's a deadline.
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
