using System.Collections.Generic;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Lock;
using Content.Shared.Storage.Components;
using Content.Shared.Tools.Components;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Чем кончилось действие руками.
///
/// <para>
/// Появилось после живого прогона, где робот 520 вызовов подряд бил ломом по ящику, который
/// открывается простым нажатием. Инструмент отвечал <c>ok</c> и «состояние не изменилось» — а это
/// три разных случая под одной вывеской: действие идёт и займёт время; действие прошло, но
/// незаметно в грубой сводке; действие вообще неприменимо. Модель выбрала неверный способ и не
/// получила ни одного сигнала об этом.
/// </para>
/// <para>
/// Здесь снимается подробный снимок цели до и после, и разница переводится в слова: что
/// изменилось, был ли это удар вместо работы инструментом, и подходит ли инструмент к этой вещи
/// вообще.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private DamageableSystem _damageable = default!;

    /// <summary>Всё, по чему видно, что с вещью что-то произошло.</summary>
    private readonly record struct TargetSnapshot(
        string? Door,
        bool? StorageOpen,
        bool? Welded,
        bool? Locked,
        float Damage,
        bool Exists);

    private TargetSnapshot Snapshot(EntityUid uid)
    {
        // Очередь на удаление — это тоже «вещи больше нет».
        //
        // Половина полезных применений УНИЧТОЖАЕТ цель: упаковка превращается в машину, деталь
        // уходит в конструкцию, реагент расходуется. Удаляют такое через QueueDel, то есть
        // ОТЛОЖЕННО, до конца тика; а снимок «после» снимается тут же, в том же тике. Без этой
        // проверки Exists ещё true, разницы не видно — и инструмент докладывал «НЕ ПОЛУЧИЛОСЬ,
        // инструмент к этой вещи не применяется» ровно в тот момент, когда всё получилось.
        // Поймано тестом сборки экранирования: девять упаковок стали щитами, и все девять раз
        // робот услышал, что мультитул тут не при чём.
        if (!Exists(uid) || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
            return new TargetSnapshot(null, null, null, null, 0f, false);

        string? door = null;
        if (TryComp<DoorComponent>(uid, out var d))
        {
            var state = d.State;
            door = state.ToString();
        }

        bool? open = TryComp<EntityStorageComponent>(uid, out var storage) ? storage.Open : null;
        bool? welded = TryComp<WeldableComponent>(uid, out var weld) ? weld.IsWelded : null;
        bool? locked = TryComp<LockComponent>(uid, out var l) ? l.Locked : null;

        // Через систему, а не через поле: TotalDamage закрыт атрибутом [Access] на чтение
        // извне, и правильно закрыт — считать сумму урона должен тот, кто её поддерживает.
        var damage = HasComp<DamageableComponent>(uid)
            ? _damageable.GetTotalDamage(uid).Float()
            : 0f;

        return new TargetSnapshot(door, open, welded, locked, damage, true);
    }

    /// <summary>
    /// Словами: что именно изменилось в вещи.
    /// </summary>
    private static List<string> Diff(TargetSnapshot before, TargetSnapshot after)
    {
        var changes = new List<string>();

        if (before.Exists && !after.Exists)
            changes.Add("вещь исчезла (израсходована или разобрана)");

        if (before.Door != after.Door && after.Door != null)
            changes.Add($"дверь: {before.Door} → {after.Door}");

        if (before.StorageOpen != after.StorageOpen && after.StorageOpen is { } open)
            changes.Add(open ? "открылось" : "закрылось");

        if (before.Welded != after.Welded && after.Welded is { } welded)
            changes.Add(welded ? "заварено" : "шов срезан");

        if (before.Locked != after.Locked && after.Locked is { } locked)
            changes.Add(locked ? "заперто" : "замок открыт");

        if (after.Damage > before.Damage + 0.01f)
            changes.Add($"получила повреждений: +{after.Damage - before.Damage:F0}");

        return changes;
    }

    /// <summary>
    /// Почему ничего не вышло, если ничего не вышло.
    /// </summary>
    /// <remarks>
    /// Отказ обязан называть следующий шаг. «Состояние не изменилось» его не называет и стоило
    /// прогона: ящик открывается нажатием, а робот бил по нему ломом, потому что ничто не сказало
    /// ему, что лом тут не при чём.
    /// </remarks>
    private string Explain(EntityUid target, string? tool, TargetSnapshot before, TargetSnapshot after)
    {
        // Заперто или заварено — это конкретная причина, и у неё конкретное лечение.
        if (after.Locked == true)
            return "заперто на замок: нужен доступ по ID или взлом, инструментом не открыть";

        if (after.Welded == true)
            return "заварено: сначала срезать шов сваркой (use tool: welding), потом открывать";

        // Вещь, которая открывается нажатием, а к ней применили инструмент.
        if (after.StorageOpen == false && !string.IsNullOrWhiteSpace(tool))
            return "это открывается ПРОСТЫМ НАЖАТИЕМ — вызови use без параметра tool. " +
                   "Инструмент нужен только заваренным и разбираемым вещам";

        if (after.Damage > before.Damage + 0.01f)
            return "ты не применил инструмент, а УДАРИЛ по цели. Для работы нужен подходящий " +
                   "инструмент, а этот здесь ни при чём";

        if (!string.IsNullOrWhiteSpace(tool))
            return $"инструмент «{tool}» к этой вещи не применяется — ничего не произошло. " +
                   "Посмотри examine: там сказано, что с ней вообще можно делать";

        return "ничего не изменилось: либо ты слишком далеко, либо так эта вещь не работает. " +
               "Посмотри examine";
    }
}
