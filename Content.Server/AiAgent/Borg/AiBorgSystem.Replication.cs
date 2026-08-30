using Content.Shared.Eye;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Как тело робота доезжает до клиентов.
///
/// <para>
/// Здесь нет ни одной игровой способности: рисует клиент по-прежнему только то, что попало в поле
/// зрения, а робот не узнаёт и не может от этого файла ничего нового. Речь про состав того, что
/// уезжает чужому клиенту, и только про него.
/// </para>
/// <para>
/// <b>Почему это вообще чинится здесь, а не в PVS.</b> Дельта сущности, которой у клиента нет,
/// стоит ему <c>MissingMetadata</c> и полного состояния на 250 КБ. А ванильный клиент
/// подтверждает буфер, а не применение мира (<c>docs/problems.md</c>, №19), так что серверные
/// патчи вокруг <c>EntityLastAcked</c> опираются на ложь протокола и петлю не закрывают.
/// Закрывает её состав мира: сущность, которую чужой клиент никогда не рисовал, не должна к нему
/// ехать вовсе. Наш робот — самый громкий поставщик таких сущностей на карте: он ходит всю смену,
/// входит в чужие зоны видимости чаще всех, и внутри него десяток предметов, ни один из которых
/// на экране чужого игрока не появляется.
/// </para>
/// <para>
/// <b>Корень шасси не прячется никогда.</b> С ним экипаж кликает, бьёт, говорит и передаёт
/// предметы; спрятанный робот — не оптимизация, а другая игра.
/// <c>VisibilitySystem.RefreshVisibility</c> толкает маску вниз рекурсивно, поэтому слой на корне
/// унёс бы за собой и тело.
/// </para>
/// <para>
/// <b>Известная цена.</b> Под скрытие попадает ВСЁ поддерево, включая руки и слоты одежды, — то
/// есть предмет, который экипаж дал роботу, на чужом экране в его руке не нарисуется. Сама
/// передача при этом работает: цель взаимодействия — корень шасси, а он видим. Если рисовать руки
/// когда-нибудь понадобится, лечится исключением контейнеров рук и инвентаря из обхода, а не
/// возвратом к репликации всего.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private VisibilitySystem _visibility = default!;

    private void InitializeReplication()
    {
        // Внутренности приезжают ПОЗЖЕ захвата: ячейка и лазер кладутся на MapInit, модули —
        // ContainerFill'ом, манипулятор и клинок — при активации шасси. Один проход по детям в
        // момент claim половину из них не увидит.
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnRemoved);
    }

    /// <summary>Убрать поддерево тела из чужих зон видимости.</summary>
    /// <remarks>
    /// Зовётся при захвате, а не при спавне, и это осознанно: незанятое шасси стоит на месте и в
    /// чужую зону видимости не входит — чинить там нечего. Занятое начинает ходить.
    /// </remarks>
    private void HideSubtree(EntityUid borg)
    {
        SetInternalOnChildren(borg, hidden: true);
    }

    /// <summary>Вернуть поддерево под обычные правила: незанятое шасси должно быть обычным.</summary>
    private void ShowSubtree(EntityUid borg)
    {
        SetInternalOnChildren(borg, hidden: false);
    }

    /// <summary>
    /// Пройти детей и выставить (или снять) им <see cref="VisibilityFlags.Internal"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Сам <paramref name="root"/> пропускается всегда</b> — см. замечание про корень шасси в
    /// описании файла.
    /// </para>
    /// <para>
    /// Обновление маски просится на КАЖДОГО ребёнка отдельно, а не одним <c>RefreshVisibility</c>
    /// на корне. <c>RecursivelyApplyVisibility</c> выходит сразу, если пересчитанная маска сущности
    /// совпала с прежней, — а у корня она и не менялась, значит до детей проход не дошёл бы вовсе.
    /// </para>
    /// </remarks>
    private void SetInternalOnChildren(EntityUid root, bool hidden)
    {
        if (!TryComp(root, out TransformComponent? xform))
            return;

        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
            SetInternal(child, hidden);
    }

    private void SetInternal(EntityUid uid, bool hidden)
    {
        if (TerminatingOrDeleted(uid))
            return;

        if (hidden)
            _visibility.AddLayer(uid, (int) VisibilityFlags.Internal);
        else
            _visibility.RemoveLayer(uid, (int) VisibilityFlags.Internal);

        // Вниз — своим ходом, а не только пересчётом маски. Пересчёт унаследовал бы бит от
        // родителя, но НЕ записал бы его в VisibilityComponent внука: вынь внука наружу, и он
        // окажется видимым, хотя мы его и не показывали. Своя запись у каждого уровня делает
        // сокрытие и снятие симметричными — а вынутое наружу разбирает OnRemoved.
        SetInternalOnChildren(uid, hidden);
    }

    /// <summary>Приехавшее в занятого робота — тоже внутренности.</summary>
    private void OnInserted(EntInsertedIntoContainerMessage ev)
    {
        if (!BelongsToClaimedBorg(ev.Container.Owner))
            return;

        SetInternal(ev.Entity, hidden: true);
    }

    /// <summary>
    /// Уехавшее из занятого робота снова обычная сущность.
    /// </summary>
    /// <remarks>
    /// Без этого робот, уронивший клинок, ронял бы его в невидимость: слой лежит на самом предмете,
    /// а <c>OnParentChange</c> лишь пересчитывает маску и честно оставляет бит на месте.
    /// </remarks>
    private void OnRemoved(EntRemovedFromContainerMessage ev)
    {
        if (!BelongsToClaimedBorg(ev.Container.Owner))
            return;

        SetInternal(ev.Entity, hidden: false);
    }

    /// <summary>Стоит ли сущность внутри тела, которое мы ведём.</summary>
    /// <remarks>
    /// Вверх по родителям, а не только сравнение с корнем: модуль — ребёнок шасси, а предмет
    /// модуля — уже внук, и вставка в него приходит с контейнером-модулем.
    /// </remarks>
    private bool BelongsToClaimedBorg(EntityUid uid)
    {
        var probe = uid;

        for (var depth = 0; probe.IsValid() && depth < 16; depth++)
        {
            if (_claimed.ContainsKey(probe))
                return true;

            if (!TryComp(probe, out TransformComponent? xform))
                return false;

            probe = xform.ParentUid;
        }

        return false;
    }

    // TODO: жертвы раунда 255 за пределами робота. Тот же бит Internal просится на:
    //   * SolutionLungGas и прочие solution-сущности внутри мобов (4 полных ресинка на коте);
    //   * содержимое закрытых непрозрачных контейнеров — WelderMini в шкафу, 3 ресинка;
    //     снимать при открытии EntityStorage, а не по сессиям.
    // Не сделано в этом заходе намеренно: у мобов в закрытом шкафу есть свой клиент, и цена
    // ошибки — игрок без собственного тела. Нужен отдельный стенд именно на этот случай.

    /// <summary>Держать это тело у всех клиентов постоянно.</summary>
    /// <remarks>
    /// Отрицательный контроль стенда: постоянная репликация убирает вход в зону видимости как
    /// событие, но платит за это трафиком на каждого клиента и лечит следствие, а не состав мира.
    /// По умолчанию выключено — см. разбор у <see cref="AiCVars.BorgPvsOverride"/>.
    /// </remarks>
    private void HoldInPvs(EntityUid borg)
    {
        if (!_cfg.GetCVar(AiCVars.BorgPvsOverride))
            return;

        _pvsOverride.AddGlobalOverride(borg);
    }

    /// <summary>Вернуть тело под обычные правила дальности.</summary>
    /// <remarks>
    /// Снимать при освобождении обязательно, а вот при удалении — не нужно:
    /// <c>PvsOverrideSystem</c> сам чистит запись по <c>EntityTerminatingEvent</c>. Проверка cvar
    /// здесь НЕ повторяется: настройку могли выключить между захватом и освобождением, и тогда
    /// запись осталась бы висеть на теле, которое никто не ведёт.
    /// </remarks>
    private void ReleaseFromPvs(EntityUid borg)
    {
        _pvsOverride.RemoveGlobalOverride(borg);
    }
}
