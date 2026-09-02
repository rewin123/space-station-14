using System;
using System.Collections.Generic;
using Content.Server.AiAgent.Perception;
using Content.Shared.Damage.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Удары по телу — как событие, а не как число в SELF.
///
/// <para>
/// До этого файла робот узнавал, что его бьют, только если сам посмотрел на своё здоровье
/// следующим ходом. Ударили — молчит, ударили ещё — молчит. На живом сервере это выглядело так:
/// игрок колотит шасси, а модель продолжает идти к зарядке, потому что в очереди наблюдений
/// этого нет. Число в SELF при следующем ходе появится, но ход раз в несколько секунд, и к тому
/// моменту робот уже мог лежать.
/// </para>
/// <para>
/// Поэтому удар кладётся в очередь как EVENT — тот же канал, что ARRIVED и ЗАРЯД: редкое,
/// важное, будит петлю сразу. Не чаще раза в две секунды: серия ударов — одна строка, иначе
/// очередь забивается тем же самым и вытесняет рацию.
/// </para>
/// <para>
/// <b>Две подписки, один отчёт.</b> <see cref="AttackedEvent"/> — замах мили: есть кто и чем.
/// <see cref="DamageChangedEvent"/> — всё остальное, у чего есть виновник: пуля, хитскан,
/// взрыв. Мили поднимает оба, но первым приходит замах, и второй молчит по той же паузе.
/// Без источника (упал, обжёгся) строки нет: это не удар, и придумывать бьющего нельзя.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>Первый удар окна докладывается, остальные в этих двух секундах — нет.</summary>
    private static readonly TimeSpan HitCooldown = TimeSpan.FromSeconds(2);

    /// <summary>Когда этому телу в последний раз положили УДАР в очередь.</summary>
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
    /// Положить УДАР в очередь, если окно ещё не занято.
    /// </summary>
    /// <remarks>
    /// Пауза ставится только когда строка реально ушла: отказ «нет сессии» или безымянный
    /// виновник не должен сжигать окно, иначе следующий настоящий удар тоже промолчит.
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

        var text = $"УДАР: {handle} {name} бьёт тебя";

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
