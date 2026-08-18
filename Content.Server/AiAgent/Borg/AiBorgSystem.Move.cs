using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.AiAgent.Perception;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Ноги.
///
/// <para>
/// Своего кода движения здесь нет и быть не должно: робот ходит тем же рулевым
/// (<c>NPCSteeringSystem</c>), которым ходят все мобы игры, а тот синтезирует ровно тот же ввод,
/// что шлёт клиент живого игрока (<c>InputMoverComponent.CurTickSprintMovement</c>). Это и есть
/// требуемый паритет: робот не телепортируется и не скользит сквозь стены, он идёт.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;

    /// <summary>
    /// Куда шёл робот, чтобы отчитаться о прибытии.
    ///
    /// <para>
    /// Нужен, потому что инструмент ходьбы <b>не ждёт</b> прибытия. Ход, висящий тридцать секунд
    /// на переходе через станцию, — это агент, который весь переход глухой: он не услышит ни
    /// рации, ни выстрела за спиной. Поэтому <c>goto</c> отвечает «иду» немедленно, а факт
    /// прибытия приезжает наблюдением, как и всё остальное в этом модуле.
    /// </para>
    /// </summary>
    private readonly Dictionary<EntityUid, string> _walking = new();

    private void InitializeMovement()
    {
    }

    /// <summary>
    /// Опрос ходьбы каждый кадр.
    ///
    /// <para>
    /// Дёшево по построению: <see cref="_walking"/> пуст, пока никто не идёт, и первая же строка
    /// выходит. Полноценной подписки на «дошёл» рулевой не предлагает.
    /// </para>
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        PollSteering();
    }

    /// <summary>Отправить робота к координатам. Возвращает описание цели для ответа модели.</summary>
    private void StartSteering(EntityUid borg, EntityCoordinates target, string what, float range)
    {
        var comp = _steering.Register(borg, target);

        // Флаги ставятся руками, и это не перестраховка.
        //
        // Register выставляет их через PathfindingSystem.GetFlags(uid), а тот возвращает
        // PathFlags.None для всего, у чего нет HTNComponent, — то есть для нашего борга всегда.
        // Без флагов путепоиск считает любую дверь непроходимой, и робот либо идёт кругами, либо
        // сообщает NoPath в двух шагах от цели.
        comp.Flags = PathFlags.Interact | PathFlags.Prying | PathFlags.Climbing;
        comp.Range = range;

        _walking[borg] = what;
    }

    private void StopSteering(EntityUid borg)
    {
        _walking.Remove(borg);
        _progress.Remove(borg);
        ClearRoute(borg);

        if (HasComp<NPCSteeringComponent>(borg))
            _steering.Unregister(borg);
    }

    /// <summary>
    /// Опрос ходьбы: дошёл — сказать, не смог — сказать.
    ///
    /// Зовётся из <see cref="Update"/>, потому что рулевой о своих результатах никого не
    /// уведомляет: он просто меняет <c>Status</c>.
    /// </summary>
    private void PollSteering()
    {
        if (_walking.Count == 0)
            return;

        foreach (var (borg, what) in _walking.ToArray())
        {
            if (!TryComp<NPCSteeringComponent>(borg, out var steering))
            {
                _walking.Remove(borg);
                continue;
            }

            if (steering.Status == SteeringStatus.Moving)
                NudgeStuck(borg);

            switch (steering.Status)
            {
                case SteeringStatus.InRange:
                    _walking.Remove(borg);
                    _steering.Unregister(borg);

                    // Промежуточный маяк — не повод сообщать модели: она просила отсек, а не
                    // пересадку. Молча идём дальше, говорим только о конце маршрута.
                    if (AdvanceRoute(borg))
                    {
                        _sawmill.Debug($"{ToPrettyString(borg)} прошёл участок: {what}");
                        break;
                    }

                    PushToBorg(borg, Observation.Event($"ARRIVED дошёл: {what}", _host.RoundTime()));

                    // В лог тоже: «робот не идёт» и «робот идёт, но медленно» в игре выглядят
                    // одинаково, а различаются только этой строкой.
                    _sawmill.Info($"{ToPrettyString(borg)} дошёл: {what}");
                    break;

                case SteeringStatus.NoPath:
                    _walking.Remove(borg);
                    _steering.Unregister(borg);
                    ClearRoute(borg);

                    PushToBorg(borg, Observation.Event(
                        $"NOPATH дороги нет: {what}. Возможно, путь перекрыт или цель за запертой дверью.",
                        _host.RoundTime()));

                    _sawmill.Info($"{ToPrettyString(borg)} не нашёл дороги: {what}");
                    break;
            }
        }
    }

    /// <summary>Где робот был в прошлой проверке и сколько раз подряд не сдвинулся.</summary>
    private readonly Dictionary<EntityUid, (Vector2 Where, int Stalls)> _progress = new();

    /// <summary>
    /// Робот упёрся — открыть дверь, в которую упёрся.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Апстримовый рулевой умеет обходить препятствия сам, но с шлюзами это работает не всегда:
    /// путь через дверь он проложит, а открыть её должен либо бампом (нужен контакт под нужным
    /// углом), либо отжатием (долгий DoAfter). На боевом сервере это выглядело так: маршрут
    /// построен, флаги верные, путь 17 полигонов — и робот полминуты топчется в метре от
    /// закрытого стеклянного шлюза.
    /// </para>
    /// <para>
    /// Поэтому делаем то же, что сделал бы человек: упёрся — нажми на дверь. У борга есть доступ
    /// по ID, так что <c>InteractionActivate</c> её просто откроет. Это не обход путепоиска, а
    /// недостающее действие: путь-то он проложил правильно.
    /// </para>
    /// </remarks>
    private void NudgeStuck(EntityUid borg)
    {
        var now = _xform.GetMapCoordinates(borg).Position;

        if (!_progress.TryGetValue(borg, out var last))
        {
            _progress[borg] = (now, 0);
            return;
        }

        // Полтайла за проверку — движение; меньше — топтание на месте.
        if ((now - last.Where).Length() > 0.5f)
        {
            _progress[borg] = (now, 0);
            return;
        }

        var stalls = last.Stalls + 1;
        _progress[borg] = (now, stalls);

        // Не с первого раза: рулевой сам разворачивается и обходит, и мешать ему на каждой заминке
        // значило бы дёргать двери всю дорогу.
        if (stalls < 3)
            return;

        _progress[borg] = (now, 0);

        var doors = new HashSet<Entity<DoorComponent>>();
        _lookup.GetEntitiesInRange(_xform.GetMapCoordinates(borg), 1.6f, doors,
            LookupFlags.Static | LookupFlags.Approximate);

        foreach (var door in doors)
        {
            var state = door.Comp.State;
            if (state is DoorState.Open or DoorState.Opening)
                continue;

            _interaction.InteractionActivate(borg, door.Owner);
            _sawmill.Debug($"{ToPrettyString(borg)} упёрся и жмёт на {ToPrettyString(door.Owner)}");
            return;
        }
    }

    /// <summary>Идёт ли робот прямо сейчас — этим глушится дельта зрения на ходу.</summary>
    private bool IsWalking(EntityUid borg) => _walking.ContainsKey(borg);

    /// <summary>Положить наблюдение в очередь агента, который сидит в этом теле.</summary>
    private void PushToBorg(EntityUid borg, Observation obs)
    {
        if (_host.Sessions.TryGetValue(borg, out var session))
            session.Queue.Push(obs);
    }
}
