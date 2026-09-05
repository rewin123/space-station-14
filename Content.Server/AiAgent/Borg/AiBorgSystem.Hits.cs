using System;
using System.Collections.Generic;
using Content.Server.AiAgent.Perception;
using Content.Shared.Damage.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Hits on the body — as an event, not as a number in SELF.
///
/// <para>
/// Before this file, the borg only learned it was being hit if it looked at its own health on its
/// next turn. Hit — silent, hit again — silent. On a live server this looked like: a player is
/// beating on the chassis while the model keeps walking to the charger, because there's nothing
/// about it in the observation queue. The number in SELF will show up on the next turn, but a turn
/// happens once every few seconds, and by then the borg could already be lying on the floor.
/// </para>
/// <para>
/// So a hit gets pushed into the queue as an EVENT — the same channel as ARRIVED and CHARGE: rare,
/// important, wakes the loop immediately. No more often than once every two seconds: a flurry of
/// hits becomes a single line, otherwise the queue gets clogged with the same thing over and over
/// and pushes out the radio.
/// </para>
/// <para>
/// <b>Two subscriptions, one report.</b> <see cref="AttackedEvent"/> — a melee swing: who and with
/// what. <see cref="DamageChangedEvent"/> — everything else that has a culprit: a bullet, a hitscan,
/// an explosion. Melee raises both, but the swing arrives first, and the second stays silent under
/// the same cooldown. With no source (fell, got burned) there's no line: that's not a hit, and
/// making up an attacker isn't allowed.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>The first hit in a window is reported, the rest within these two seconds are not.</summary>
    private static readonly TimeSpan HitCooldown = TimeSpan.FromSeconds(2);

    /// <summary>When a HIT was last pushed into the queue for this body.</summary>
    private readonly Dictionary<EntityUid, TimeSpan> _lastHitReported = new();

    private void InitializeHits()
    {
        SubscribeLocalEvent<AiBorgComponent, AttackedEvent>(OnAttacked);
        SubscribeLocalEvent<AiBorgComponent, DamageChangedEvent>(OnDamaged);
    }

    private void ForgetHits(EntityUid borg) => _lastHitReported.Remove(borg);

    private void OnAttacked(Entity<AiBorgComponent> ent, ref AttackedEvent args)
    {
        if (args.User == ent.Owner)
            return;

        ReportHit(ent.Owner, args.User, args.Used);
    }

    private void OnDamaged(Entity<AiBorgComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        if (args.Origin is not { } origin || origin == ent.Owner)
            return;

        ReportHit(ent.Owner, origin, used: default);
    }

    /// <summary>
    /// Push a HIT into the queue if the window isn't already taken.
    /// </summary>
    /// <remarks>
    /// The cooldown is set only when a line actually went out: a "no session" refusal or an
    /// unnamed culprit must not burn the window, otherwise the next real hit would also stay silent.
    /// </remarks>
    private void ReportHit(EntityUid borg, EntityUid who, EntityUid used)
    {
        if (!_claimed.ContainsKey(borg))
            return;

        var now = _timing.CurTime;
        if (_lastHitReported.TryGetValue(borg, out var last) && now - last < HitCooldown)
            return;

        if (!_host.Sessions.TryGetValue(borg, out var session))
            return;

        var name = Identity.Name(who, EntityManager);
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!session.Handles.TryGetHandle(who, out var handle))
            handle = session.Handles.GetOrCreate(who, _host.KindOf(who));

        var loc = session.Locale;
        var text = loc.T(
            $"УДАР: {handle} {name} бьёт тебя",
            $"HIT: {handle} {name} hits you");

        if (used.IsValid() && used != who && !TerminatingOrDeleted(used))
        {
            var weapon = Identity.Name(used, EntityManager);
            if (!string.IsNullOrWhiteSpace(weapon))
                text += $" ({weapon})";
        }

        _lastHitReported[borg] = now;
        PushToBorg(borg, Observation.Event(text, _host.RoundTime()));
    }
}
