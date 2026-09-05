using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.IdentityManagement;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage.Components;
using Content.Shared.CombatMode;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// The application of force: hitting and shooting.
///
/// <para>
/// A separate file rather than a line among the hand tools, for the same reason the evil-AI mode
/// has a separate loadset: these are the only two tools that inflict harm, and they deserve to be
/// readable in full, not hunted down among item-handling code.
/// </para>
/// <para>
/// <b>Both go through the same path as regular NPCs</b> — <c>AttemptLightAttack</c> and
/// <c>AttemptShoot</c>. This isn't an implementation detail: both methods internally check
/// cooldown, range, ammunition, and every subscription like "this weapon refuses to fire on its
/// own side," meaning the borg obeys exactly the same rules as everything else alive on the
/// station.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private SharedCombatModeSystem _combat = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedGunSystem _gun = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;

    /// <summary>The built-in laser slot on a combat chassis. Name matches the YAML <c>gun_slot</c>.</summary>
    private const string BuiltInGunSlot = "gun_slot";

    /// <summary>
    /// We don't shoot past this.
    ///
    /// <para>
    /// This isn't about weapon balance, it's about parity: targets arrive in the prompt from
    /// <c>look</c>, which sees farther than a human can make out a silhouette in a corridor.
    /// Without a cap, the model would open fire on a handle that, to a living player, is just a
    /// dot at the other end of the deck.
    /// </para>
    /// </summary>
    private const float ShootRangeTiles = 12f;

    private Task<ToolResult> HitAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "hit", () =>
        {
            if (!TryTarget(s, args, out var target, out var failure))
                return failure!;

            var name = Identity.Name(target, EntityManager);

            // A swing, not a click.
            //
            // This used to be _interaction.UserInteraction, and that was a bug that stayed silent
            // until the first armed borg: SharedInteractionSystem only checks combat mode to
            // decide whether to allow a hand interaction, while the actual hit lives in
            // MeleeWeaponSystem and is raised as an event from the client. So the borg would
            // honestly "interact" with a person and deal no damage, while the tool reported
            // "hit" — there was no signal by which to tell that apart from a miss.
            //
            // AttemptLightAttack is the public entry point regular NPCs attack through
            // (NPCCombatSystem.Melee, NPCSteeringSystem.Obstacles). It also takes care of
            // cooldown, range, and every attack-attempt subscription.
            var weapon = ActiveMeleeWeapon(borg);

            if (!TryComp<MeleeWeaponComponent>(weapon, out var melee))
            {
                return ToolResult.Fail(ToolError.Refused,
                    "нечем бить: ни в руке, ни у корпуса нет боевого модуля");
            }

            // Combat mode gets switched on for one swing and immediately switched back off.
            //
            // AttemptAttack refuses silently outside combat mode (SharedMeleeWeaponSystem, the
            // very first check after the cooldown check), and a borg has no key with which a
            // living player toggles it. Leaving the mode on isn't an option: under it,
            // InteractionSystem stops allowing hand interaction (CombatModeCanHandInteract),
            // meaning a borg with its weapon raised couldn't pick up an item or press a button —
            // and would read that as "the use tool is broken."
            // A MISS IS NOT A SUCCESS, AND WE HAVE TO CHECK THAT OURSELVES.
            //
            // AttemptLightAttack returns true for the mere fact of the swing, not for a hit: if
            // the target is out of reach, upstream honestly writes "melee attacked (light) ...
            // and missed" to the admin log and leaves it at that. The tool would still report
            // "hit," the model would consider the job done and swing again — and again. In round
            // 305 this looked like frozen cyborgs: Obukh made over thirty swings in a row in one
            // minute at a target it couldn't reach, and not one landed. From the outside, the
            // borg just stands there doing nothing.
            //
            // The check mirrors the server-side MeleeWeaponSystem.InRange for the sessionless
            // case (the agent has no session): InRangeUnobstructed at the weapon's range. Plus
            // Damageable — upstream also counts as a miss a hit on something that can't take
            // damage.
            if (!HasComp<DamageableComponent>(target))
            {
                return ToolResult.Fail(ToolError.Refused,
                    $"по «{name}» бить нечем и незачем: эта цель не получает урона", retry: "none");
            }

            if (!_interaction.InRangeUnobstructed(borg, target, melee.Range))
            {
                return ToolResult.Fail(ToolError.Refused,
                    $"до «{name}» не дотянуться: удар достаёт на {melee.Range:0.#} клетки, " +
                    "подойди вплотную (goto или step) и бей уже оттуда",
                    retry: "later");
            }

            var wasFighting = _combat.IsInCombatMode(borg);
            _combat.SetInCombatMode(borg, true);

            bool landed;

            try
            {
                landed = _melee.AttemptLightAttack(borg, weapon, melee, target);
            }
            finally
            {
                _combat.SetInCombatMode(borg, wasFighting);
            }

            if (!landed)
            {
                // The reason is named explicitly, rather than a generic "didn't go through."
                //
                // AttemptAttack refuses silently for any of five different reasons at once, and
                // for the model, the difference between "wait a second" and "can't reach the
                // target" is the difference between retrying and forming a different plan. The
                // first version answered with a generic phrase, and on the bench there was no way
                // to tell an unrecovered swing arm apart from combat mode being off.
                var why = melee.NextAttack > _timing.CurTime
                    ? "рука ещё не отведена после прошлого удара"
                    : !_blocker.CanAttack(borg, target, (weapon, melee))
                        ? "бить эту цель нельзя — она в контейнере, либо тебе мешают"
                        : "цель не достать";

                return ToolResult.Fail(ToolError.Refused, $"удар по «{name}» не прошёл: {why}", retry: "later");
            }

            return ToolResult.Effected(name, new Dictionary<string, object?>
            {
                ["ударил"] = name,
                ["чем"] = weapon == borg ? "корпусом" : Identity.Name(weapon, EntityManager),
            });
        }, ct);
    }

    /// <summary>
    /// What the borg hits with: a weapon from its hands, or the chassis itself if neither hand
    /// holds one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ALL hands are checked, not just the active one, and whichever hand the weapon is found in
    /// becomes the active hand. A living player switches hands with a key and doesn't consider
    /// that an action; a borg has no such key, and a module with two hands — a blade and a
    /// barrel — is a common setup. Without this scan, "hit" and "shoot" would depend on whichever
    /// hand happened to get chosen when the module was installed, i.e. on randomness the model has
    /// no way to learn about.
    /// </para>
    /// <para>
    /// Falling back to the chassis isn't generosity: the unarmed version of
    /// <c>MeleeWeaponComponent</c> lives on the mob itself, and a regular NPC hits exactly the
    /// same way — <c>AttemptLightAttack(uid, uid, ...)</c>.
    /// </para>
    /// </remarks>
    private EntityUid ActiveMeleeWeapon(EntityUid borg) =>
        TryWieldFromHands<MeleeWeaponComponent>(borg, out var weapon) ? weapon : borg;

    /// <summary>
    /// Find an item with the needed component among the hands and make its hand active.
    /// </summary>
    private bool TryWieldFromHands<T>(EntityUid borg, out EntityUid found) where T : IComponent
    {
        found = default;

        if (_hands.TryGetActiveItem(borg, out var active) && active is { } held && HasComp<T>(held))
        {
            found = held;
            return true;
        }

        foreach (var hand in _hands.EnumerateHands(borg))
        {
            if (!_hands.TryGetHeldItem(borg, hand, out var item) || item is not { } candidate)
                continue;

            if (!HasComp<T>(candidate))
                continue;

            _hands.SetActiveHand(borg, hand);
            found = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The gun: first the hands, then the chassis body itself, then the chassis's dedicated slot.
    /// </summary>
    /// <remarks>
    /// <c>TryGetGun</c> only looks at the hand and the body itself. The built-in laser lives in
    /// <c>gun_slot</c> — a separate entity, so that <c>BatteryAmmoProvider</c> Dirty's it rather
    /// than the chassis root. Without this step, a combat borg would honestly answer "nothing to
    /// shoot with."
    /// </remarks>
    private bool TryGetBorgGun(EntityUid borg, out Entity<GunComponent> gun)
    {
        if (_gun.TryGetGun(borg, out gun))
            return true;

        var stored = _itemSlots.GetItemOrNull(borg, BuiltInGunSlot);
        if (stored is { } uid && TryComp<GunComponent>(uid, out var gunComp))
        {
            gun = (uid, gunComp);
            return true;
        }

        gun = default;
        return false;
    }

    private bool IsBuiltInGun(EntityUid borg, EntityUid gun) =>
        _itemSlots.GetItemOrNull(borg, BuiltInGunSlot) == gun;

    private Task<ToolResult> ShootAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "shoot", () =>
        {
            if (!TryTarget(s, args, out var target, out var failure))
                return failure!;

            var name = Identity.Name(target, EntityManager);

            if (!TryGetBorgGun(borg, out var gun))
            {
                return ToolResult.Fail(ToolError.Refused,
                    "нечем стрелять: нет ни встроенного ствола, ни оружия в руках");
            }

            var here = _xform.GetMapCoordinates(borg);
            var there = _xform.GetMapCoordinates(target);

            if (here.MapId != there.MapId)
                return ToolResult.Fail(ToolError.NotVisible, $"«{name}» не на этой карте", retry: "other_target");

            var gap = (there.Position - here.Position).Length();

            if (gap > ShootRangeTiles)
            {
                return ToolResult.Fail(ToolError.NotVisible,
                    $"до «{name}» {gap:F1} тайла — слишком далеко, чтобы стрелять прицельно. Подойди ближе",
                    retry: "other_target");
            }

            // A clear line of sight is mandatory, and that's not nitpicking. The bullet flies by
            // physics and will hit the wall on its own, but a tool that reports "fired" at a
            // target behind a bulkhead lies to the model about the outcome — and it will believe
            // it and keep shooting the wall until the charge runs out. The check is exactly the
            // same one every other interaction uses.
            if (!_interaction.InRangeUnobstructed(borg, target, range: ShootRangeTiles))
            {
                return ToolResult.Fail(ToolError.NotVisible,
                    $"между тобой и «{name}» что-то стоит — отсюда не попасть",
                    retry: "other_target");
            }

            if (!_gun.AttemptShoot(borg, gun, Transform(target).Coordinates, target))
            {
                return ToolResult.Fail(ToolError.Refused,
                    $"выстрела не вышло: оружие либо разряжено, либо ещё не перезарядилось",
                    retry: "later");
            }

            return ToolResult.Effected(name, new Dictionary<string, object?>
            {
                ["выстрелил"] = name,
                ["чем"] = IsBuiltInGun(borg, gun.Owner) ? "встроенным лазером" : Identity.Name(gun.Owner, EntityManager),
                ["дистанция"] = $"{gap:F1}",
            });
        }, ct);
    }
}
