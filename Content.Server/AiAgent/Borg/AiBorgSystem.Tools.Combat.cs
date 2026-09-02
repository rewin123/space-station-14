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
/// Применение силы: удар и выстрел.
///
/// <para>
/// Отдельным файлом, а не строчкой среди рук, по той же причине, по которой у режима злого ИИ
/// отдельный лоусет: это единственные два инструмента, которые причиняют вред, и их стоит уметь
/// прочитать целиком, не выискивая среди подбора предметов.
/// </para>
/// <para>
/// <b>Оба идут тем же путём, что штатные NPC</b> — <c>AttemptLightAttack</c> и
/// <c>AttemptShoot</c>. Это не деталь реализации: оба метода внутри проверяют перезарядку,
/// дистанцию, боеприпас и все подписки вроде «оружие отказывается стрелять в своих», то есть
/// робот подчиняется ровно тем же правилам, что и всё остальное живое на станции.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private SharedCombatModeSystem _combat = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedGunSystem _gun = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;

    /// <summary>Слот встроенного лазера на боевом шасси. Имя совпадает с YAML <c>gun_slot</c>.</summary>
    private const string BuiltInGunSlot = "gun_slot";

    /// <summary>
    /// Дальше этого не стреляем.
    ///
    /// <para>
    /// Не про баланс оружия, а про паритет: цели приезжают в промпт из <c>look</c>, который
    /// видит дальше, чем человек различает силуэт в коридоре. Без потолка модель открывала бы
    /// огонь по хендлу, который для живого игрока — точка на другом конце палубы.
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

            // Замах, а не клик.
            //
            // Раньше здесь стоял _interaction.UserInteraction, и это была ошибка, молчавшая до
            // первого вооружённого робота: SharedInteractionSystem спрашивает боевой режим
            // только затем, чтобы решить, пускать ли взаимодействие рукой, а сам удар живёт в
            // MeleeWeaponSystem и поднимается событием от клиента. То есть робот честно
            // «взаимодействовал» с человеком и не наносил ему ничего, а инструмент отвечал
            // «ударил» — отличить это от промаха было нельзя ни по одному признаку.
            //
            // AttemptLightAttack — публичный вход, которым бьют штатные NPC
            // (NPCCombatSystem.Melee, NPCSteeringSystem.Obstacles). Он же берёт на себя
            // перезарядку, дистанцию и все подписки на попытку удара.
            var weapon = ActiveMeleeWeapon(borg);

            if (!TryComp<MeleeWeaponComponent>(weapon, out var melee))
            {
                return ToolResult.Fail(ToolError.Refused,
                    "нечем бить: ни в руке, ни у корпуса нет боевого модуля");
            }

            // Боевой режим включается на один замах и тут же гасится.
            //
            // AttemptAttack отказывает вне боевого режима молча (SharedMeleeWeaponSystem, первая
            // же проверка после перезарядки), а у робота нет клавиши, которой его переключает
            // живой игрок. Оставить режим включённым нельзя: под ним InteractionSystem перестаёт
            // пускать взаимодействие рукой (CombatModeCanHandInteract), то есть робот с
            // занесённым оружием не смог бы ни взять предмет, ни нажать кнопку — и понял бы это
            // как «инструмент use сломался».
            // ПРОМАХ — ЭТО НЕ УСПЕХ, И ЭТО ПРИХОДИТСЯ ПРОВЕРЯТЬ САМИМ.
            //
            // AttemptLightAttack возвращает true на сам факт замаха, а не на попадание: если цель
            // вне досягаемости, апстрим честно пишет в админ-лог «melee attacked (light) … and
            // missed» и на этом всё. Инструмент при этом отвечал «ударил», модель считала работу
            // сделанной и била снова — и снова. В раунде 305 это выглядело как замершие киборги:
            // Обух за минуту сделал больше тридцати замахов подряд по цели, до которой не доставал,
            // и ни один не попал. Со стороны — робот стоит и ничего не делает.
            //
            // Проверка повторяет серверную MeleeWeaponSystem.InRange для случая без сессии
            // (у агента её нет): InRangeUnobstructed на дальность оружия. Плюс Damageable —
            // апстрим считает промахом и удар по тому, кому нечего повреждать.
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
                // Причина называется поимённо, а не «не прошло».
                //
                // AttemptAttack отказывает молча и по пяти разным поводам сразу, и для модели
                // разница между «подожди секунду» и «до цели не дотянуться» — это разница между
                // повтором и другим планом. Первая версия отвечала общей фразой, и на стенде
                // отличить не отведённую руку от выключенного боевого режима было нельзя.
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
    /// Чем робот бьёт: оружие из рук, а если оружия нет ни в одной — сам корпус.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Перебираются ВСЕ руки, а не только активная, и активная при находке делается текущей.
    /// Живой игрок переключает руку клавишей и не считает это действием; у робота такой клавиши
    /// нет, а модуль с двумя руками — клинок и ствол — обычное дело. Без перебора «ударить» и
    /// «выстрелить» зависели бы от того, какая рука оказалась выбрана при установке модуля, то
    /// есть от случайности, которую модели неоткуда узнать.
    /// </para>
    /// <para>
    /// Откат на корпус не щедрость: безоружная версия <c>MeleeWeaponComponent</c> висит на самом
    /// мобе, и штатный NPC бьёт ровно так же — <c>AttemptLightAttack(uid, uid, …)</c>.
    /// </para>
    /// </remarks>
    private EntityUid ActiveMeleeWeapon(EntityUid borg) =>
        TryWieldFromHands<MeleeWeaponComponent>(borg, out var weapon) ? weapon : borg;

    /// <summary>
    /// Найти в руках предмет с нужным компонентом и сделать его руку активной.
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
    /// Ствол: сначала руки, потом сам корпус, потом запертый слот шасси.
    /// </summary>
    /// <remarks>
    /// <c>TryGetGun</c> смотрит только руку и само тело. Встроенный лазер лежит в
    /// <c>gun_slot</c> — отдельная сущность, чтобы <c>BatteryAmmoProvider</c> Dirty'ил её,
    /// а не корень шасси. Без этого шага боевой робот честно отвечал бы «нечем стрелять».
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

            // Прямая видимость обязательна, и это не придирка. Пуля летит по физике и упрётся в
            // стену сама, но инструмент, отвечающий «выстрелил» на цель за переборкой, врёт
            // модели о результате — а она поверит и будет стрелять в стену, пока не кончится
            // заряд. Проверка ровно та же, которой пользуется всё остальное взаимодействие.
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
