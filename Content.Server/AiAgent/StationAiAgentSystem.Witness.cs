using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Content.Server.AiAgent.Perception;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Server.AiAgent;

/// <summary>
/// The agent's vision as a stream of events, not as polling.
///
/// <para>
/// Before this file, the agent saw NOTHING. It heard the radio, speech at the core, and
/// announcements, but whatever was happening in the world simply didn't exist for it: the
/// <c>look</c> tool answers "what's standing around", but not "what just happened", and it can't be
/// polled for that purpose — it costs tens of milliseconds of the main thread.
/// </para>
/// <para>
/// What makes this a hole is not a missed fight, but an impossible-to-fulfil request. "When I put
/// plasma into the anomaly generator, start it" ran into the fact that the agent had no way to learn
/// the plasma had been inserted; the only option left was to ask again over the radio, which is
/// exactly the behaviour that gets people to stop talking to it. Reactivity here isn't a nicety for
/// vision — it's the precondition for a deferred request being fulfillable at all.
/// </para>
/// <para>
/// <b>There is no semantics in this file, and there shouldn't be.</b> We don't decide what's
/// "important": a list of important things is guaranteed not to cover the crew's requests, because
/// those aren't limited to fighting. The code's job is to deliver the event with its participants
/// and coordinates; making sense of what it means is the model's work. That's why labels like
/// "предметом" (with an item) are captions, not classification, and there's no "actually it was
/// this one who shot" resolution here either: upstream puts the gun, not the person, into a
/// hitscan's <c>Origin</c> — so that's what we pass along, and the model has <c>inspect</c>.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    // ------------------------------------------------------------------ labels

    private const string LabelHand = "рукой";
    private const string LabelHandEn = "hand";
    private const string LabelUsing = "предметом";
    private const string LabelUsingEn = "item";
    private const string LabelRanged = "издали";
    private const string LabelRangedEn = "ranged";
    private const string LabelActivate = "включил";
    private const string LabelActivateEn = "activated";
    private const string LabelInserted = "вложил";
    private const string LabelInsertedEn = "inserted";
    private const string LabelRemoved = "вынул";
    private const string LabelRemovedEn = "removed";
    private const string LabelPullStart = "тащит";
    private const string LabelPullStartEn = "pulling";
    private const string LabelPullStop = "отпустил";
    private const string LabelPullStopEn = "released";
    private const string LabelEquipped = "надел";
    private const string LabelEquippedEn = "equipped";
    private const string LabelUnequipped = "снял";
    private const string LabelUnequippedEn = "unequipped";
    private const string LabelState = "состояние";
    private const string LabelStateEn = "state";
    private const string LabelDamage = "урон";
    private const string LabelDamageEn = "damage";
    private const string LabelShot = "выстрел";
    private const string LabelShotEn = "shot";
    private const string LabelDoor = "дверь";
    private const string LabelDoorEn = "door";

    // ------------------------------------------------------------------ subscriptions

    // ------------------------------------------------------- settings, taken off the hot path

    // All five used to be read via _cfg.GetCVar on EVERY station event. What made this expensive
    // wasn't the read itself, but where it sat: `EntInsertedIntoContainerMessage` and
    // `EntRemovedFromContainerMessage` are the most frequent event class in the game (every item
    // pickup, every hand swap, every mechanism part), and for each one the funnel was paying three
    // calls into IConfigurationManager BEFORE even the first distance check. `GetCVar<T>` is
    // `(T)GetCVar(name)`: acquiring a ReaderWriterLockSlim, a dictionary lookup, and boxing the value
    // into an object.
    //
    // A flat tax on the whole station for as long as the agent is alive, and it's invisible in the
    // dispatcher's stats entirely — it isn't a marshalled call. Hence the suspicion that "we're
    // stalling because of the AI" points here first, rather than at look spikes.
    //
    // The values stay live: OnValueChanged keeps them up to date, so `cvar ai.observe false` from
    // the admin console still works exactly as before.
    private bool _observe;
    private float _observeRange;
    private bool _observeOcclusion;
    private int _observeMaxChecks;

    /// <summary>
    /// Parsed <c>ai.observe_kinds</c>. An empty set means every label is allowed.
    ///
    /// Stored parsed, not as a string: the previous form called <c>string.Split</c> on every event
    /// as soon as the list stopped being empty. In other words, trying to mute one noisy category
    /// made the hot path more expensive, not cheaper — the exact opposite of the intent.
    /// </summary>
    private readonly HashSet<string> _observeKinds = new(StringComparer.OrdinalIgnoreCase);

    private void CacheWitnessCVars()
    {
        _cfg.OnValueChanged(AiCVars.Observe, v => _observe = v, true);
        _cfg.OnValueChanged(AiCVars.ObserveRange, v => _observeRange = v, true);
        _cfg.OnValueChanged(AiCVars.ObserveOcclusion, v => _observeOcclusion = v, true);
        _cfg.OnValueChanged(AiCVars.ObserveMaxChecksPerTick, v => _observeMaxChecks = v, true);
        _cfg.OnValueChanged(AiCVars.ObserveKinds, ParseObserveKinds, true);
    }

    private void ParseObserveKinds(string raw)
    {
        _observeKinds.Clear();

        if (string.IsNullOrWhiteSpace(raw))
            return;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _observeKinds.Add(part);
    }

    // ------------------------------------------------------------------ subscriptions

    /// <summary>
    /// Hook up the world listener. Called once from <c>Initialize</c>.
    /// </summary>
    /// <remarks>
    /// Half the subscriptions are broadcast, half are directed, and the difference here isn't
    /// stylistic. <c>RaiseLocalEvent(uid, ev)</c> by default raises the event WITHOUT broadcasting
    /// (<c>EntityEventBus.Directed.cs</c>), so such an event can only be subscribed to through a
    /// specific component — and a directed "component + event" pair is globally unique in
    /// RobustToolbox: a second claimant gets <c>Duplicate Subscriptions</c> at server startup. Hence
    /// <see cref="TryWitness{TComp,TEvent}"/> for the directed ones: an already-taken pair must cost
    /// us one category of observations, not the server failing to start.
    /// </remarks>
    private void SubscribeWitness()
    {
        CacheWitnessCVars();

        // Broadcast. These don't occupy a pair; nobody can take them away from us.
        //
        // Every single one of them carries its participants INSIDE the event object, and that's not
        // a coincidence — it's a selection criterion: a broadcast handler only gets the event itself
        // and doesn't know which entity raised it. That's why UseInHandEvent, DroppedEvent and
        // LockToggledEvent didn't make the cut — they name the person, but not the item or the lock,
        // and a line like "Ivan dropped something" is only half an observation. A directed
        // subscription would give them the missing entity, but each such subscription occupies a
        // globally unique pair, and spending one on an action that's already visible from a click
        // (InteractUsing on the same item) isn't worth it.
        SubscribeLocalEvent<InteractHandEvent>(OnWitnessHand);
        SubscribeLocalEvent<InteractUsingEvent>(OnWitnessUsing);
        SubscribeLocalEvent<RangedInteractEvent>(OnWitnessRanged);
        SubscribeLocalEvent<ActivateInWorldEvent>(OnWitnessActivate);
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnWitnessInserted);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnWitnessRemoved);
        SubscribeLocalEvent<PullStartedMessage>(OnWitnessPullStarted);
        SubscribeLocalEvent<PullStoppedMessage>(OnWitnessPullStopped);
        SubscribeLocalEvent<DidEquipEvent>(OnWitnessEquipped);
        SubscribeLocalEvent<DidUnequipEvent>(OnWitnessUnequipped);
        SubscribeLocalEvent<MobStateChangedEvent>(OnWitnessMobState);

        // Directed. Each one occupies a pair, so there are only three, and each was chosen as the
        // single point of entry for a whole class of things happening.
        //
        // DamageChangedEvent — all pain in the game in one place. Melee, bullets, hitscan, and fire
        // all flow through
        // TryChangeDamage → ChangeDamage → DamageDealtEvent → InjurableComponent → OnEntityDamageChanged;
        // six subscriptions on weapons get replaced by one.
        TryWitness<MobStateComponent, DamageChangedEvent>(OnWitnessDamage);
        TryWitness<GunComponent, GunShotEvent>(OnWitnessShot);
        TryWitness<DoorComponent, DoorStateChangedEvent>(OnWitnessDoor);
    }

    /// <summary>
    /// Subscribe to a directed pair without killing the server if upstream already claimed it.
    ///
    /// The failure stays loud — it's the very first line in the log — but a live server comes up and
    /// runs without one category of observations instead of not coming up at all. The pairs were
    /// verified at the time of writing; the check is there for a future rebase where someone else's
    /// subscription shows up without our knowledge.
    /// </summary>
    private void TryWitness<TComp, TEvent>(EntityEventRefHandler<TComp, TEvent> handler)
        where TComp : IComponent
        where TEvent : notnull
    {
        try
        {
            SubscribeLocalEvent(handler);
        }
        catch (InvalidOperationException e)
        {
            _sawmill.Error(
                $"наблюдение: пара ({typeof(TComp).Name}, {typeof(TEvent).Name}) уже занята — " +
                $"эта категория событий агенту не придёт. {e.Message}");
        }
    }

    private void TryWitness<TComp, TEvent>(ComponentEventHandler<TComp, TEvent> handler)
        where TComp : IComponent
        where TEvent : EntityEventArgs
    {
        try
        {
            SubscribeLocalEvent(handler);
        }
        catch (InvalidOperationException e)
        {
            _sawmill.Error(
                $"наблюдение: пара ({typeof(TComp).Name}, {typeof(TEvent).Name}) уже занята — " +
                $"эта категория событий агенту не придёт. {e.Message}");
        }
    }

    // ------------------------------------------------------------------ handlers

    // Each one is a single line. All they do is name the label, say WHERE it happened, and list the
    // participants in "who, with what, on what" order. Adding a new event is also one line; nothing
    // else in this file needs touching for it.

    private void OnWitnessHand(InteractHandEvent args) =>
        Witness(LabelHand, LabelHandEn, args.Target, args.User, args.Target);

    private void OnWitnessUsing(InteractUsingEvent args) =>
        Witness(LabelUsing, LabelUsingEn, args.Target, args.User, args.Used, args.Target);

    private void OnWitnessRanged(RangedInteractEvent args) =>
        Witness(LabelRanged, LabelRangedEn, args.TargetUid, args.UserUid, args.UsedUid, args.TargetUid);

    private void OnWitnessActivate(ActivateInWorldEvent args) =>
        Witness(LabelActivate, LabelActivateEn, args.Target, args.User, args.Target);

    // The container's name travels as a separate parameter rather than being glued into the label
    // here: gluing it as a string would mean building a string on EVERY insertion on the station,
    // including the ones the gate rejects on the very next check — and insertions outnumber every
    // other kind of event on the station. The name itself ("left hand", "storagebase",
    // "machine_parts") is passed through as-is: put into a hand, a bag, or inside a machine are
    // different things, and telling them apart is the model's job, not ours.
    private void OnWitnessInserted(EntInsertedIntoContainerMessage args) =>
        Witness(LabelInserted, LabelInsertedEn, args.Container.Owner, args.Entity, args.Container.Owner,
            detail: args.Container.ID);

    private void OnWitnessRemoved(EntRemovedFromContainerMessage args) =>
        Witness(LabelRemoved, LabelRemovedEn, args.Container.Owner, args.Entity, args.Container.Owner,
            detail: args.Container.ID);

    private void OnWitnessPullStarted(PullStartedMessage args) =>
        Witness(LabelPullStart, LabelPullStartEn, args.PulledUid, args.PullerUid, args.PulledUid);

    private void OnWitnessPullStopped(PullStoppedMessage args) =>
        Witness(LabelPullStop, LabelPullStopEn, args.PulledUid, args.PullerUid, args.PulledUid);

    private void OnWitnessEquipped(DidEquipEvent args) =>
        Witness(LabelEquipped, LabelEquippedEn, args.EquipTarget, args.EquipTarget, args.Equipment);

    private void OnWitnessUnequipped(DidUnequipEvent args) =>
        Witness(LabelUnequipped, LabelUnequippedEn, args.EquipTarget, args.EquipTarget, args.Equipment);

    private void OnWitnessMobState(MobStateChangedEvent args) =>
        Witness(
            $"{LabelState}: {StateRu(args.OldMobState)}→{StateRu(args.NewMobState)}",
            $"{LabelStateEn}: {StateEn(args.OldMobState)}→{StateEn(args.NewMobState)}",
            args.Target, args.Origin ?? args.Target, args.Target);

    private void OnWitnessDamage(Entity<MobStateComponent> ent, ref DamageChangedEvent args)
    {
        // Healing is not the event "someone got hit" — it's the opposite, and conflating the two in
        // one line means making the model infer the sign of a number that isn't in the line at all.
        if (!args.DamageIncreased)
            return;

        // Damage with no source: fell, got burned, suffocated. There is no culprit, and one must not
        // be invented — a line "X hit" with a guessed X is worse than no line at all.
        Witness(LabelDamage, LabelDamageEn, ent.Owner, args.Origin ?? ent.Owner, ent.Owner);
    }

    private void OnWitnessShot(Entity<GunComponent> ent, ref GunShotEvent args) =>
        Witness(LabelShot, LabelShotEn, ent.Owner, args.User, ent.Owner);

    // The door's label is taken pre-made rather than assembled: doors on the station click dozens of
    // times a second, and building a string on every click for an event that's almost always out of
    // frame is wasted work in the tick for no reason.
    private void OnWitnessDoor(Entity<DoorComponent> ent, ref DoorStateChangedEvent args)
    {
        var ru = DoorLabel(args.State, english: false);
        if (ru == null)
            return;

        Witness(ru, DoorLabel(args.State, english: true)!, ent.Owner, ent.Owner);
    }

    private static string StateRu(MobState state) => state switch
    {
        MobState.Alive => "жив",
        MobState.Critical => "крит",
        MobState.Dead => "мёртв",
        _ => "?",
    };

    private static string StateEn(MobState state) => state switch
    {
        MobState.Alive => "alive",
        MobState.Critical => "crit",
        MobState.Dead => "dead",
        _ => "?",
    };

    /// <summary>
    /// A ready-made label for every door state — no string building at all on the hot path.
    /// <c>null</c> means "don't report this at all".
    ///
    /// <para>
    /// <b>Intermediate states stay silent, and that's a fix, not an economy.</b> A door goes through
    /// <c>Closed → Opening → Open</c>, meaning <see cref="DoorStateChangedEvent"/> fires twice for
    /// one pass. Previously <c>Opening</c> and <c>Open</c> produced the SAME label, and the agent
    /// got two indistinguishable lines in a row. In a live session on August 16 this cost seven turns
    /// out of forty-two: the agent honestly replied "duplicate event, already noted" — seven calls to
    /// the model spent retelling itself the same thing.
    /// </para>
    /// <para>
    /// The final state is kept rather than the initial one, even though the initial one arrives half
    /// a second earlier. Reason: a door can be switched to <c>Open</c> without an animation —
    /// forced entry, depowering, a forced state set — in which case <c>Opening</c> never fires at
    /// all. Betting on the intermediate state would lose exactly the events vision was set up for in
    /// the first place.
    /// </para>
    /// </summary>
    private static string? DoorLabel(DoorState state, bool english) => state switch
    {
        DoorState.Open => english ? LabelDoorEn + ": opened" : LabelDoor + ": открылась",
        DoorState.Closed => english ? LabelDoorEn + ": closed" : LabelDoor + ": закрылась",
        DoorState.Denying => english ? LabelDoorEn + ": denied" : LabelDoor + ": отказ",
        DoorState.Emagging => english ? LabelDoorEn + ": emagged" : LabelDoor + ": взлом",
        DoorState.Welded => english ? LabelDoorEn + ": welded" : LabelDoor + ": заварена",
        _ => null,
    };

    // ------------------------------------------------------------------ funnel

    /// <summary>
    /// Everything seen converges here: the gate, identification, formatting.
    /// </summary>
    /// <param name="label">What happened. A caption for the model and a key for <c>ai.observe_kinds</c>.</param>
    /// <param name="where">
    /// Which entity to measure the distance to the eye from. Usually the action's target: the event
    /// happens where the target stands, not where the initiator stands — someone shooting from
    /// around a corner is out of frame, but the hit lands.
    /// </param>
    /// <param name="first">Who did it.</param>
    /// <param name="second">With what — or on what, if there was no tool involved.</param>
    /// <param name="third">On what, if all three are named.</param>
    /// <remarks>
    /// Three separate parameters, not <c>params EntityUid[]</c>, and that's not a style nitpick: an
    /// array would be allocated on EVERY station event, including the ones the gate rejects on the
    /// very next line. On a stream of clicks that's wasted work in the tick for no reason, and the
    /// tick in this project just had far smaller offenders cleaned out of it.
    /// </remarks>
    /// <param name="detail">
    /// A refinement of the label — glued on with a colon, and only AFTER the gate, so that an event
    /// the eye didn't actually see doesn't cost even one string concatenation.
    /// </param>
    private void Witness(string labelRu, string labelEn, EntityUid where, EntityUid first, EntityUid second = default,
        EntityUid third = default, string? detail = null)
    {
        // First thing: is there even anyone to watch for. On a station with no agent, this method
        // gets called on every click of every player, and it must cost exactly one comparison.
        if (_sessions.Count == 0 || !_observe)
            return;

        var now = RoundTime();

        foreach (var session in _sessions.Values)
        {
            var label = session.Locale.English ? labelEn : labelRu;

            if (!KindEnabled(label))
                continue;

            if (!NearTheEye(session, where, out var at, out var eyeAt))
                continue;

            var line = Describe(session, first, second, third, eyeAt, at);
            if (line == null)
                continue;

            session.Queue.Push(Observation.Observed(
                detail == null ? label : $"{label}: {detail}", line, now));

            _witnessed++;
        }
    }

    /// <summary>
    /// Is this label enabled. An empty list means all of them.
    /// </summary>
    /// <remarks>
    /// Compared by the prefix up to the colon: compound labels like <c>состояние: жив→крит</c>
    /// (state: alive→crit) are configured with a single word, <c>состояние</c>, otherwise turning off
    /// a category would only be possible by listing every one of its values.
    /// </remarks>
    private bool KindEnabled(string label)
    {
        if (_observeKinds.Count == 0)
            return true;

        var colon = label.IndexOf(':');
        var head = colon < 0 ? label : label[..colon];

        foreach (var kind in _observeKinds)
        {
            if (kind.Equals(head, StringComparison.OrdinalIgnoreCase))
                return true;

            if (Locale.AgentLocale.KindAlias(kind)
                .Equals(Locale.AgentLocale.KindAlias(head), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ gate

    /// <summary>
    /// Did this location fall within the eye's field of view.
    ///
    /// <para>
    /// Two stages instead of three, and that's a deliberate choice, not a simplification. The
    /// strict wall check is <c>StationAiVisionSystem.IsAccessible</c>, and it unrolls three hundred
    /// tiles and makes a broadphase query for each. On a rare call that's unnoticeable; here calls
    /// come in a stream, and a full check would return the tick to exactly the state that made
    /// <c>look</c> hold it for a second. The cost: within <c>ai.observe_range</c>, the agent will
    /// notice things happening behind a wall, whereas a person in its place would have seen the
    /// wall. The third stage is enabled by <c>ai.observe_occlusion</c>, see
    /// <see cref="TileIsVisible"/>.
    /// </para>
    /// <para>
    /// A square, not a circle (<c>max(|dx|,|dy|)</c>, not the length): a person's screen has a
    /// rectangular viewport, and a circle would clip the corners they can see.
    /// </para>
    /// </summary>
    private bool NearTheEye(AgentSession session, EntityUid what, out Vector2 at, out Vector2 eyeAt)
    {
        at = default;
        eyeAt = default;

        if (!_stationAi.TryGetCore(session.Brain, out var core) || core.Comp?.RemoteEntity == null)
            return false;

        var eye = core.Comp.RemoteEntity.Value;

        // Without <TransformComponent>, and that's not cosmetic. The non-generic overload goes
        // through a ready-made TransformQuery rather than the general component dictionary; on a
        // path that fires on every station event, the difference counts. The RA0030 analyzer demands
        // exactly this and treats the generic form as a build error in the Release configuration.
        if (!TryComp(what, out TransformComponent? xform) || !TryComp(eye, out TransformComponent? eyeXform))
            return false;

        // Different grids mean different places, even if the coordinates are close. A shuttle
        // flying past the station must not appear to the agent as happening in the neighbouring
        // compartment.
        if (xform.GridUid == null || xform.GridUid != eyeXform.GridUid)
            return false;

        at = _xform.GetWorldPosition(xform);
        eyeAt = _xform.GetWorldPosition(eyeXform);

        var range = _observeRange;
        var delta = at - eyeAt;

        if (MathF.Abs(delta.X) > range || MathF.Abs(delta.Y) > range)
            return false;

        return TileIsVisible(xform.GridUid.Value, xform);
    }

    /// <summary>
    /// The third stage: is it behind a wall. Off by default, see <c>ai.observe_occlusion</c>.
    /// </summary>
    /// <remarks>
    /// Two safeguards, both within the tick. The per-tile memo lives for EXACTLY one tick and is
    /// cleared in <c>Update</c> — it is not a vision cache: the set doesn't survive a single world
    /// change the agent might have missed, it only collapses a fight on one tile into one check
    /// instead of ten. The per-tick check cap is insurance against load the memo doesn't collapse;
    /// beyond it, events are skipped, and the number skipped goes into the log, because losing
    /// observations silently is worse than losing them loudly.
    /// </remarks>
    private bool TileIsVisible(EntityUid gridUid, TransformComponent xform)
    {
        if (!_observeOcclusion)
            return true;

        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid) ||
            !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
            return false;

        var tile = _mapSystem.LocalToTile(gridUid, mapGrid, xform.Coordinates);

        if (_seenTiles.TryGetValue(tile, out var known))
            return known;

        if (_visionChecks >= _observeMaxChecks)
        {
            _visionSkipped++;
            return false;
        }

        _visionChecks++;

        var visible = _vision.IsAccessible((gridUid, broadphase, mapGrid), tile, fastPath: false);
        _seenTiles[tile] = visible;
        return visible;
    }

    // ------------------------------------------------------------------ identification

    /// <summary>
    /// Assemble the participants into a line: <c>crew-7 Ivan Petrov | obj-412 sheet of plasma | Δ(2,-1) (12,-34)</c>.
    ///
    /// <para>
    /// The handle is the whole reason this exists. Seeing <c>device-3 anomaly generator</c>, the
    /// agent calls a tool on it immediately, with no intermediate <c>look</c>; without a handle,
    /// "start it" would cost three turns instead of one. It's the same registry as <c>look</c> uses
    /// (<see cref="AgentSession.Handles"/>), and that's a requirement, not a convenience: if they
    /// diverged, one thing would become two things for the agent.
    /// </para>
    /// <para>
    /// This doesn't contradict the rule that "an observation doesn't carry an EntityUid". That
    /// prohibition is about a voice over the radio: a person in that role has no way to tie a voice
    /// to an entity. Something seen is the exact opposite: a player watching someone load plasma
    /// into a generator can click on that generator.
    /// </para>
    /// </summary>
    private string? Describe(AgentSession session, EntityUid first, EntityUid second, EntityUid third,
        Vector2 eyeAt, Vector2 at)
    {
        var sb = new StringBuilder();
        var wrote = 0;

        // Duplicates are filtered by comparing against the previous ones, not with a set: there are
        // never more than three participants, and half the events name the target twice — it's also
        // the "where". Printing it twice would mean paying tokens for noise.
        AppendPart(session, sb, first, ref wrote);

        if (second != first)
            AppendPart(session, sb, second, ref wrote);

        if (third != first && third != second)
            AppendPart(session, sb, third, ref wrote);

        if (wrote == 0)
            return null;

        // Same format as look's lines: Δ answers "in which direction from me", the absolute pair
        // is fed to move_camera. Δ is measured from the eye AT THE MOMENT OF THE EVENT — the agent
        // may have moved the camera before its turn, and eye= in the SELF line will already be about
        // a different location.
        sb.Append(" | ").Append(PositionFrom(eyeAt, at));

        return sb.ToString();
    }

    /// <summary>Append one participant as "handle name", if it still exists and has some kind of name.</summary>
    private void AppendPart(AgentSession session, StringBuilder sb, EntityUid uid, ref int wrote)
    {
        if (!uid.IsValid() || Deleted(uid))
            return;

        var name = Identity.Name(uid, EntityManager);
        if (string.IsNullOrWhiteSpace(name))
            return;

        // TryGetHandle BEFORE KindOf, not GetOrCreate(uid, KindOf(uid)): the argument is always
        // evaluated, even when the handle already exists, and KindOf is a chain of thirteen HasComp
        // calls. This exact mistake cost time in look; here it would fire tens of times more often.
        if (!session.Handles.TryGetHandle(uid, out var handle))
            handle = session.Handles.GetOrCreate(uid, KindOf(uid));

        if (wrote > 0)
            sb.Append(" | ");

        sb.Append(handle).Append(' ').Append(name);
        wrote++;
    }

    // ------------------------------------------------------------------ accounting

    /// <summary>Per-tile visibility memo for a single tick. Not a vision cache: it lives until the end of the tick and dies.</summary>
    private readonly Dictionary<Vector2i, bool> _seenTiles = new();

    private int _visionChecks;
    private int _visionSkipped;

    /// <summary>How many observation lines have been issued over the process's lifetime. For tests and the log only.</summary>
    private int _witnessed;

    private float _sinceWitnessReport;

    /// <summary>Reset the tick counters and, if something was lost, say so out loud.</summary>
    private void ResetWitnessTick(float frameTime)
    {
        _seenTiles.Clear();
        _visionChecks = 0;

        if (_visionSkipped == 0)
            return;

        _sinceWitnessReport += frameTime;
        if (_sinceWitnessReport < WitnessReportSeconds)
            return;

        _sawmill.Warning(
            $"наблюдение: пропущено {_visionSkipped} событий — потолок проверок видимости " +
            $"({_observeMaxChecks} за тик) выбран");

        _visionSkipped = 0;
        _sinceWitnessReport = 0f;
    }

    private const float WitnessReportSeconds = 30f;
}
