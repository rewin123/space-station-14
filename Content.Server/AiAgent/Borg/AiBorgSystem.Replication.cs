using Content.Shared.Eye;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// How the borg's body makes it to clients.
///
/// <para>
/// There is not a single gameplay capability here: the client still renders only what fell into
/// its field of view, and the borg neither learns nor can gain anything new from this file. This
/// is purely about the composition of what ships to another client, and nothing else.
/// </para>
/// <para>
/// <b>Why this is fixed here at all, and not in PVS.</b> A delta for an entity the client doesn't
/// have costs it a <c>MissingMetadata</c> and a full 250 KB state. And the vanilla client
/// acknowledges the buffer, not the application of the world (<c>docs/problems.md</c>, #19), so
/// server-side patches around <c>EntityLastAcked</c> rest on a protocol lie and don't close the
/// loop. What closes it is world composition: an entity that another client has never rendered
/// must never be sent to it at all. Our borg is the loudest supplier of such entities on the
/// map: it walks around the whole shift, enters other people's visibility zones more often than
/// anything else, and carries a dozen items inside it, not one of which ever appears on another
/// player's screen.
/// </para>
/// <para>
/// <b>The chassis root is never hidden.</b> The crew clicks it, hits it, talks to it, and hands
/// it items; a hidden borg isn't an optimization, it's a different game.
/// <c>VisibilitySystem.RefreshVisibility</c> pushes the mask down recursively, so a layer on the
/// root would drag the body along with it.
/// </para>
/// <para>
/// <b>Known cost.</b> The hiding covers the ENTIRE subtree, including hands and clothing slots —
/// meaning an item the crew handed to the borg won't render in its hand on another screen. The
/// handoff itself still works: the interaction target is the chassis root, and that is visible.
/// If rendering hands ever becomes necessary, the fix is to exclude the hand and inventory
/// containers from the traversal, not to go back to replicating everything.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private VisibilitySystem _visibility = default!;

    private void InitializeReplication()
    {
        // Internals arrive AFTER the claim: the cell and laser are placed on MapInit, modules
        // via ContainerFill, and the gripper and blade on chassis activation. A single pass over
        // children at claim time would miss half of them.
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnRemoved);
    }

    /// <summary>Remove the body's subtree from other players' visibility zones.</summary>
    /// <remarks>
    /// Called on claim, not on spawn, and that's deliberate: an unclaimed chassis stays put and
    /// never enters anyone's visibility zone — there is nothing to fix there. A claimed one
    /// starts walking.
    /// </remarks>
    private void HideSubtree(EntityUid borg)
    {
        SetInternalOnChildren(borg, hidden: true);
    }

    /// <summary>Return the subtree to normal rules: an unclaimed chassis should be ordinary.</summary>
    private void ShowSubtree(EntityUid borg)
    {
        SetInternalOnChildren(borg, hidden: false);
    }

    /// <summary>
    /// Walk the children and set (or clear) <see cref="VisibilityFlags.Internal"/> on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="root"/> itself is always skipped</b> — see the note about the chassis
    /// root in the file description.
    /// </para>
    /// <para>
    /// The mask update needs to be requested on EACH child individually, not with a single
    /// <c>RefreshVisibility</c> on the root. <c>RecursivelyApplyVisibility</c> bails out
    /// immediately if the entity's recomputed mask matches the old one — and on the root it never
    /// changed at all, so the traversal would never even reach the children.
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

        // Recurse downward under its own power, not just via a mask recompute. A recompute would
        // inherit the bit from the parent but NOT write it into the grandchild's
        // VisibilityComponent: pull the grandchild out, and it turns up visible even though we
        // never showed it. Writing it explicitly at every level makes hiding and unhiding
        // symmetric — and OnRemoved handles whatever gets pulled out.
        SetInternalOnChildren(uid, hidden);
    }

    /// <summary>Anything that arrives inside a claimed borg is internal too.</summary>
    private void OnInserted(EntInsertedIntoContainerMessage ev)
    {
        if (!BelongsToClaimedBorg(ev.Container.Owner))
            return;

        SetInternal(ev.Entity, hidden: true);
    }

    /// <summary>
    /// Anything that leaves a claimed borg becomes an ordinary entity again.
    /// </summary>
    /// <remarks>
    /// Without this, a borg dropping a blade would drop it into invisibility: the layer sits on
    /// the item itself, and <c>OnParentChange</c> merely recomputes the mask, faithfully leaving
    /// the bit in place.
    /// </remarks>
    private void OnRemoved(EntRemovedFromContainerMessage ev)
    {
        if (!BelongsToClaimedBorg(ev.Container.Owner))
            return;

        SetInternal(ev.Entity, hidden: false);
    }

    /// <summary>Whether an entity sits inside a body we are driving.</summary>
    /// <remarks>
    /// Walks up through the parents rather than just comparing against the root: a module is a
    /// child of the chassis, and a module's item is already a grandchild, so an insertion into it
    /// arrives with the module container as the immediate parent.
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

    // TODO: victims of round 255 outside the borg. The same Internal bit is wanted on:
    //   * SolutionLungGas and other solution entities inside mobs (4 full resyncs on a cat);
    //   * contents of closed opaque containers — a WelderMini in a locker, 3 resyncs;
    //     clear it on EntityStorage opening, not per-session.
    // Deliberately not done in this pass: mobs in a closed locker have their own client, and the
    // cost of a mistake is a player left without a body. This needs its own dedicated bench case.

    /// <summary>Keep this body present for all clients permanently.</summary>
    /// <remarks>
    /// A negative control for the bench: permanent replication removes entering a visibility zone
    /// as an event, but pays for it with per-client traffic and treats the symptom rather than
    /// world composition. Off by default — see the discussion at
    /// <see cref="AiCVars.BorgPvsOverride"/>.
    /// </remarks>
    private void HoldInPvs(EntityUid borg)
    {
        if (!_cfg.GetCVar(AiCVars.BorgPvsOverride))
            return;

        _pvsOverride.AddGlobalOverride(borg);
    }

    /// <summary>Return the body to normal range rules.</summary>
    /// <remarks>
    /// Must be cleared on release, but not on deletion: <c>PvsOverrideSystem</c> already cleans up
    /// the entry on <c>EntityTerminatingEvent</c>. The cvar check is NOT repeated here: the setting
    /// might have been turned off between claim and release, and then the entry would stay hanging
    /// on a body nobody is driving.
    /// </remarks>
    private void ReleaseFromPvs(EntityUid borg)
    {
        _pvsOverride.RemoveGlobalOverride(borg);
    }
}
