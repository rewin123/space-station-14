using System.Linq;
using Content.Server.Interaction;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Silicons.Borgs.Components;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Hands.
///
/// <para>
/// The key decision in this file is <b>one <c>use</c> tool instead of three</b>. Upstream's
/// <c>InteractionSystem.UserInteraction</c> is the full path of a player's left click: it figures
/// out on its own whether the hands are empty, what's in the active hand and what the target is,
/// and dispatches the call to <c>InteractHand</c>, <c>InteractUsing</c>, or
/// <c>InteractionActivate</c>. One call covers "use an item on a target," "open a door," and
/// "press a button" — and covers them with the <em>same</em> range, access, and
/// <c>ActionBlocker</c> checks a human gets.
/// </para>
/// <para>
/// Spelling this out as three tools would mean forcing the model to decide in advance which of the
/// three paths the engine will pick — knowledge it doesn't have and has no reason to need. The
/// module's README measures the cost of a wide toolset directly: 46 commands sink this model,
/// ~13 work.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private InteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    /// <summary>
    /// Pick up an item into a hand.
    /// </summary>
    /// <remarks>
    /// A borg's hands appear together with the selected module (<c>SelectModule</c> →
    /// <c>ProvideItems</c>), and some of them are occupied by a fixed tool. So "no free hand" is a
    /// normal refusal, not a bug: it means "switch modules."
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

    /// <summary>
    /// Which installed module holds a tool with this name.
    /// </summary>
    /// <remarks>
    /// Looks at the item prototypes in the modules' hands, not at the items themselves: while a
    /// module isn't selected, the items don't exist yet — they exist only as records saying "this
    /// hand will hold this." Otherwise there would be nothing to suggest exactly when it's needed.
    /// </remarks>
    private string? FindModuleWithTool(EntityUid borg, string toolName)
    {
        if (!TryComp<BorgChassisComponent>(borg, out var chassis))
            return null;

        foreach (var module in chassis.ModuleContainer.ContainedEntities)
        {
            if (!TryComp<ItemBorgModuleComponent>(module, out var items))
                continue;

            foreach (var hand in items.Hands)
            {
                if (hand.Item is not { } proto)
                    continue;

                if (!proto.Id.Contains(toolName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Name(module).Replace(" cyborg module", string.Empty,
                    StringComparison.OrdinalIgnoreCase).Trim();
            }
        }

        return null;
    }

    /// <summary>Select a module — i.e. switch the set of tools in the hands.</summary>
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
