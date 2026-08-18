using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.AiAgent.Perception;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Ноги.
///
/// <para>
/// Маршрут строит <see cref="BorgPathfinder"/>, ведёт по нему <see cref="StepAlongTrail"/>, а вся
/// физика — движение, столкновения, скорость, открывание дверей корпусом — остаётся апстримовой:
/// мы кладём направление в то же поле, куда клиент живого игрока кладёт нажатые стрелки.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _xform = default!;

    /// <summary>
    /// Куда робот идёт, чтобы отчитаться о прибытии.
    ///
    /// <para>
    /// Инструмент ходьбы <b>не ждёт</b> прибытия: ход, висящий полминуты на переходе через
    /// станцию, — это агент, глухой весь переход. <c>goto</c> отвечает «иду» немедленно, а факт
    /// прибытия приезжает наблюдением, как и всё остальное в этом модуле.
    /// </para>
    /// </summary>
    private readonly Dictionary<EntityUid, string> _walking = new();

    /// <summary>
    /// Чем кончилась последняя ходьба: «пришёл» или «нет пути».
    ///
    /// Нужно скрипту, который ждёт прибытия. Наблюдение ARRIVED адресовано модели между ходами, а
    /// скрипт исполняется, пока ход идёт, и очередь наблюдений в этот момент трогать не может —
    /// её вычитывает петля. Поэтому исход дублируется сюда: одна строка на робота, живёт до
    /// следующего маршрута.
    /// </summary>
    private readonly Dictionary<EntityUid, string> _lastWalk = new();

    /// <summary>Где робот был в прошлой проверке и сколько раз подряд не сдвинулся.</summary>
    private readonly Dictionary<EntityUid, (Vector2 Where, int Stalls)> _progress = new();

    /// <summary>Столько проверок без движения — и пробуем нажать на дверь.</summary>
    private const int StallsBeforeDoor = 4;

    /// <summary>Столько — и признаём, что здесь не пройти, и перекладываем маршрут.</summary>
    private const int StallsBeforeReplan = 30;

    private void InitializeMovement()
    {
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        PollWalking();

        foreach (var borg in _claimed.Keys)
            WatchCharge(borg);
    }

    private void StopSteering(EntityUid borg)
    {
        _walking.Remove(borg);
        _progress.Remove(borg);
        ClearRoute(borg);
        ClearTrail(borg);
    }

    /// <summary>Идёт ли робот прямо сейчас — этим глушится дельта зрения на ходу.</summary>
    private bool IsWalking(EntityUid borg) => _walking.ContainsKey(borg);

    /// <summary>Состояние ходьбы одной строкой — это читает скрипт через walk_status.</summary>
    private string WalkStatus(EntityUid borg)
    {
        if (_walking.TryGetValue(borg, out var what))
            return $"идёт: {what}";

        return _lastWalk.TryGetValue(borg, out var last) ? last : "стоит";
    }

    /// <summary>Вести всех идущих: шаг по пути, разбор заторов, доклад о прибытии.</summary>
    private void PollWalking()
    {
        if (_walking.Count == 0)
            return;

        foreach (var (borg, what) in _walking.ToArray())
        {
            if (!Exists(borg) || TerminatingOrDeleted(borg))
            {
                StopSteering(borg);
                continue;
            }

            if (StepAlongTrail(borg))
            {
                WatchForStall(borg, what);
                continue;
            }

            // Тайлы кончились — дошли.
            _walking.Remove(borg);
            _progress.Remove(borg);
            ClearRoute(borg);

            _lastWalk[borg] = "пришёл";

            PushToBorg(borg, Observation.Event($"ARRIVED дошёл: {what}", _host.RoundTime()));

            // В лог тоже: «робот не идёт» и «робот идёт медленно» в игре выглядят одинаково, а
            // различаются только этой строкой.
            _sawmill.Info($"{ToPrettyString(borg)} дошёл: {what}");
        }
    }

    /// <summary>
    /// Робот упёрся — сначала открыть дверь, потом переложить маршрут, потом сдаться.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок важен. Самая частая причина затора — закрытый шлюз: корпус открывает его тараном
    /// (<c>DoorBumpOpener</c>), но не всегда с нужного угла, а прав у робота хватает, чтобы просто
    /// нажать. Если и это не помогло — дело не в двери, и надо искать другую дорогу от текущего
    /// места.
    /// </para>
    /// <para>
    /// Порог перепланировки намеренно велик: перекладывать маршрут на каждой заминке значит
    /// дёргать станцию впустую, пока робота просто обходит человек.
    /// </para>
    /// </remarks>
    private void WatchForStall(EntityUid borg, string what)
    {
        var now = _xform.GetMapCoordinates(borg).Position;

        if (!_progress.TryGetValue(borg, out var last))
        {
            _progress[borg] = (now, 0);
            return;
        }

        if ((now - last.Where).Length() > 0.15f)
        {
            _progress[borg] = (now, 0);

            // Робот сдвинулся — значит затор был проходим, и потраченные попытки не в счёт.
            // Без этого длинная дорога с тремя дверями исчерпывала бюджет перепланировок на
            // полпути, хотя каждая дверь в итоге открывалась.
            ForgetReplans(borg);
            return;
        }

        var stalls = last.Stalls + 1;
        _progress[borg] = (now, stalls);

        // Жмём периодически, а не однажды: шлюз закрывается сам, и одного нажатия на всю заминку
        // хватает не всегда — особенно когда робот подошёл к нему под углом.
        if (stalls % StallsBeforeDoor == 0 && TryPressClosedDoor(borg, 1.6f))
        {
            _sawmill.Debug($"{ToPrettyString(borg)} упёрся и нажал на дверь");
            return;
        }

        if (stalls < StallsBeforeReplan)
            return;

        _progress[borg] = (now, 0);

        // Дверь не поддалась — считаем её стеной и ищем обход.
        //
        // Так честнее любого числа попыток: причина может быть какой угодно — нет доступа, дверь
        // заварена, обесточена, — и робот всё равно должен либо найти другую дорогу, либо честно
        // сказать, что её нет. Заодно это единственное, что спасает от вечного тыканья в одну и
        // ту же створку.
        if (NextTile(borg) is { } blocked)
        {
            BlockTile(borg, blocked);
            _sawmill.Debug($"{ToPrettyString(borg)} считает тайл {blocked} непроходимым и ищет обход");
        }

        if (TryReplan(borg))
            return;

        _walking.Remove(borg);
        _progress.Remove(borg);
        ClearRoute(borg);
        ClearTrail(borg);

        _lastWalk[borg] = $"нет пути: {what}";

        PushToBorg(borg, Observation.Event(
            $"NOPATH дороги нет: {what}. Путь перекрыт, и обойти не вышло.", _host.RoundTime()));

        _sawmill.Info($"{ToPrettyString(borg)} не смог пройти к «{what}»");
    }

    /// <summary>
    /// Нажать на ближайшую закрытую дверь. Возвращает true, если нашлась и нажали.
    /// </summary>
    /// <remarks>
    /// Апстримовый путепоиск знает про двери два способа: «нажать» — для дверей без замка — и
    /// «отжать ломом» — для дверей с замком, через долгий DoAfter, который на запитанном шлюзе ещё
    /// и не факт что пройдёт. Варианта «у меня есть доступ по ID, просто открой» у него нет вовсе,
    /// а у борга доступ есть: его включает появление разума.
    /// </remarks>
    private bool TryPressClosedDoor(EntityUid borg, float radius)
    {
        var doors = new HashSet<Entity<DoorComponent>>();
        _lookup.GetEntitiesInRange(_xform.GetMapCoordinates(borg), radius, doors,
            LookupFlags.Static | LookupFlags.Approximate);

        if (doors.Count == 0)
            return false;

        // Жмём дверь ПО ХОДУ ДВИЖЕНИЯ, а не первую попавшуюся.
        //
        // В тамбуре их бывает пять сразу — на бою робот вставал в развязке у входа в атмос,
        // окружённый maintenance access, Engineering Lobby, Atmospherics и двумя шлюзами. «Первая
        // попавшаяся» с равной вероятностью оказывалась той, из которой он только что вышел.
        var aim = NextTile(borg) is { } tile && Transform(borg).GridUid is { } grid
            ? _xform.ToMapCoordinates(new EntityCoordinates(grid, Center(tile))).Position
            : _xform.GetMapCoordinates(borg).Position;

        var best = EntityUid.Invalid;
        var bestDist = float.MaxValue;

        foreach (var door in doors)
        {
            var state = door.Comp.State;
            if (state is DoorState.Open or DoorState.Opening)
                continue;

            var d = (_xform.GetMapCoordinates(door.Owner).Position - aim).Length();
            if (d >= bestDist)
                continue;

            bestDist = d;
            best = door.Owner;
        }

        if (!best.IsValid())
            return false;

        _interaction.InteractionActivate(borg, best);
        return true;
    }

    /// <summary>Положить наблюдение в очередь агента, который сидит в этом теле.</summary>
    private void PushToBorg(EntityUid borg, Observation obs)
    {
        if (_host.Sessions.TryGetValue(borg, out var session))
            session.Queue.Push(obs);
    }
}
