using System.Collections.Generic;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Lock;
using Content.Shared.Storage.Components;
using Content.Shared.Tools.Components;
using Robust.Shared.Containers;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// What the outcome of a hands action was.
///
/// <para>
/// Introduced after a live run where a robot made 520 calls in a row hitting a crate with a crowbar,
/// when the crate opens with a simple press. The tool responded with <c>ok</c> and "state unchanged" —
/// but that's three different cases under one label: the action is in progress and will take time; the
/// action succeeded but is invisible in the coarse summary; the action simply doesn't apply here. The
/// model picked the wrong method and got no signal about it at all.
/// </para>
/// <para>
/// This takes a detailed snapshot of the target before and after, and turns the difference into words:
/// what changed, whether it was a hit instead of tool work, and whether the tool even applies to this
/// object at all.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private DamageableSystem _damageable = default!;

    /// <summary>Everything that shows something happened to the object.</summary>
    private readonly record struct TargetSnapshot(
        string? Door,
        bool? StorageOpen,
        bool? Welded,
        bool? Locked,
        float Damage,
        int Contained,
        bool Exists);

    private TargetSnapshot Snapshot(EntityUid uid)
    {
        // A queued deletion also counts as "the object is gone."
        //
        // Half of useful tool applications DESTROY the target: a flatpack turns into a machine, a
        // part goes into a construction, a reagent is consumed. Such removals go through QueueDel,
        // i.e. DEFERRED until the end of the tick, while the "after" snapshot is taken right away, in
        // the same tick. Without this check, Exists is still true, no difference is visible, and the
        // tool reported "DIDN'T WORK, the tool doesn't apply to this object" at the exact moment it
        // actually succeeded. Caught by the shield-assembly test: nine flatpacks became shields, and
        // all nine times the robot was told the multitool had nothing to do with it.
        if (!Exists(uid) || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
            return new TargetSnapshot(null, null, null, null, 0f, 0, false);

        string? door = null;
        if (TryComp<DoorComponent>(uid, out var d))
        {
            var state = d.State;
            door = state.ToString();
        }

        bool? open = TryComp<EntityStorageComponent>(uid, out var storage) ? storage.Open : null;
        bool? welded = TryComp<WeldableComponent>(uid, out var weld) ? weld.IsWelded : null;
        bool? locked = TryComp<LockComponent>(uid, out var l) ? l.Locked : null;

        // Through the system, not through the field: TotalDamage is closed off from outside reads by
        // the [Access] attribute, and rightly so — summing damage should be done by whoever maintains it.
        var damage = HasComp<DamageableComponent>(uid)
            ? _damageable.GetTotalDamage(uid).Float()
            : 0f;

        // Total contents across all of the target's containers.
        //
        // Half of the work with machines is "insert": a canister into a controller, a battery into a
        // slot, a board into a console. None of the fields above notice that, and the tool would
        // honestly report "nothing changed" about an inserted canister. We count not a specific slot
        // but all contents: that catches removal too, and any container we didn't think of.
        var contained = 0;

        if (TryComp<ContainerManagerComponent>(uid, out var containers))
        {
            foreach (var container in _container.GetAllContainers(uid, containers))
                contained += container.ContainedEntities.Count;
        }

        return new TargetSnapshot(door, open, welded, locked, damage, contained, true);
    }

    /// <summary>
    /// In words: exactly what changed about the object.
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

        if (after.Contained > before.Contained)
            changes.Add($"внутрь вложено (стало {after.Contained})");

        if (after.Contained < before.Contained && after.Exists)
            changes.Add($"изнутри извлечено (осталось {after.Contained})");

        if (after.Damage > before.Damage + 0.01f)
            changes.Add($"получила повреждений: +{after.Damage - before.Damage:F0}");

        return changes;
    }

    /// <summary>
    /// Why nothing worked, if nothing worked.
    /// </summary>
    /// <remarks>
    /// A failure must name the next step. "State unchanged" doesn't name one, and it cost a whole run:
    /// the crate opens with a press, and the robot kept hitting it with a crowbar because nothing told
    /// it the crowbar had nothing to do with it.
    /// </remarks>
    private string Explain(EntityUid target, string? tool, TargetSnapshot before, TargetSnapshot after)
    {
        // Locked or welded is a specific reason, and it has a specific remedy.
        if (after.Locked == true)
            return "заперто на замок: нужен доступ по ID или взлом, инструментом не открыть";

        if (after.Welded == true)
            return "заварено: сначала срезать шов сваркой (use tool: welding), потом открывать";

        // An object that opens with a press, and a tool was applied to it instead.
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
