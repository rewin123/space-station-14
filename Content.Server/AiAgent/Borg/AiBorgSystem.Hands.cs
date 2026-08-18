using System.Linq;
using Content.Server.Interaction;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Silicons.Borgs.Components;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Руки.
///
/// <para>
/// Ключевое решение файла — <b>один инструмент <c>use</c> вместо трёх</b>. Апстримовый
/// <c>InteractionSystem.UserInteraction</c> — это полный путь левого клика игрока: он сам
/// разбирает, пусты ли руки, что в активной руке и что за цель, и разводит вызов по
/// <c>InteractHand</c>, <c>InteractUsing</c> или <c>InteractionActivate</c>. Один вызов закрывает
/// «применить предмет к цели», «открыть дверь» и «нажать кнопку» — и закрывает их <em>той же</em>
/// проверкой дальности, доступа и <c>ActionBlocker</c>, что у человека.
/// </para>
/// <para>
/// Расписывать это тремя инструментами значило бы заставить модель заранее решать, какой из
/// трёх путей выберет движок, — знание, которого у неё нет и которое ей незачем иметь. README
/// модуля меряет цену широкого набора прямо: 46 команд топят эту модель, ~13 работают.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private InteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    /// <summary>
    /// Взять предмет в руку.
    /// </summary>
    /// <remarks>
    /// У борга руки появляются вместе с выбранным модулем (<c>SelectModule</c> → <c>ProvideItems</c>),
    /// и часть из них занята несъёмным инструментом. Поэтому «нет свободной руки» — штатный отказ,
    /// а не поломка: он значит «смени модуль».
    /// </remarks>
    private bool TryPickUp(EntityUid borg, EntityUid item, out string why)
    {
        if (!_hands.TryPickupAnyHand(borg, item))
        {
            why = "не берётся: нет свободной руки или предмет вне досягаемости. " +
                  "Свободные руки даёт смена модуля — инструмент module.";
            return false;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>Выбрать модуль — то есть сменить набор инструментов в руках.</summary>
    private bool TrySelectModule(EntityUid borg, string name, out string why)
    {
        if (!TryComp<BorgChassisComponent>(borg, out var chassis))
        {
            why = "это не шасси борга";
            return false;
        }

        if (!chassis.Active)
        {
            why = "шасси не активно: нет заряда батареи. Модули недоступны, пока не зарядишься.";
            return false;
        }

        var container = chassis.ModuleContainer;
        var match = container.ContainedEntities
            .FirstOrDefault(m => Name(m).Contains(name, StringComparison.OrdinalIgnoreCase));

        if (!match.IsValid())
        {
            var have = string.Join(", ", container.ContainedEntities.Select(m => Name(m)));
            why = string.IsNullOrEmpty(have)
                ? "в шасси нет ни одного модуля"
                : $"нет модуля «{name}». Установлены: {have}";
            return false;
        }

        _borg.SelectModule((borg, chassis), match);
        why = string.Empty;
        return true;
    }
}
