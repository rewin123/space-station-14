using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.NPC;
using Content.Shared.Pinpointer;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Маршрут через всю станцию.
///
/// <para>
/// <b>Зачем это вообще нужно.</b> Апстримовый путепоиск не рассчитан на переходы через станцию:
/// <c>PathfindingSystem.Common.cs</c> задаёт <c>NodeLimit = 512</c>, и A* просто перестаёт
/// разворачивать граф, возвращая <c>NoPath</c>. Для штатных NPC это не проблема — они дерутся и
/// прибираются в пределах комнаты, — но робот, которому сказали «иди в инженерный», упирается в
/// этот предел и сообщает «дороги нет», стоя в баре. Проверено на боевом сервере: три шага на
/// восток — «дошёл», Bar → Bridge — «дороги нет».
/// </para>
/// <para>
/// Поэтому маршрут строит <see cref="BorgPathfinder"/> — свой поиск по карте станции, — а
/// апстримовому рулевому достаются короткие ноги, каждая из которых в его лимит укладывается.
/// Глобальный маршрут наш, локальное движение чужое и проверенное.
/// </para>
/// <para>
/// Первая версия резала дорогу по навигационным маякам. Приём был плох тем, что маяки расставлены
/// по смыслу, а не по проходимости: цепочка «ближайших» упиралась в запертые отсеки, и робот
/// вставал на пересадке, хотя до цели оставались проходимые коридоры.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>Куда робот шёл на самом деле и сколько раз мы уже перекладывали маршрут.</summary>
    private readonly Dictionary<EntityUid, (EntityCoordinates Dest, string Goal, int Replans)> _goals = new();

    /// <summary>
    /// Сколько раз перекладывать маршрут, прежде чем признать, что дороги нет.
    ///
    /// <para>
    /// Раньше было три, и этого хватало, пока перепланировка была просто повтором: с тем же
    /// набором препятствий она давала тот же путь, и упираться в него больше трёх раз смысла не
    /// имело. Теперь каждая попытка ЧТО-ТО УЗНАЁТ — непроходимый тайл уходит в <see cref="_blocked"/>,
    /// и следующий путь идёт в обход, — поэтому попыток не жалко. Тамбур у входа в атмос на карте
    /// ротации окружён пятью дверьми сразу, и три попытки там кончались, не перебрав и половины.
    /// </para>
    /// </summary>
    private const int MaxReplans = 10;

    /// <summary>
    /// Тайлы, которые робот на этом маршруте признал непроходимыми.
    ///
    /// Наполняется на месте: дверь, которая не открылась, створка, которую заварили, тайл, куда
    /// корпус просто не лезет. Живёт до конца маршрута — новая задача начинает с чистого листа,
    /// потому что дверь к тому времени могли и открыть.
    /// </summary>
    private readonly Dictionary<EntityUid, HashSet<Vector2i>> _blocked = new();

    /// <summary>Пометить тайл непроходимым для текущего маршрута.</summary>
    private void BlockTile(EntityUid borg, Vector2i tile)
    {
        if (!_blocked.TryGetValue(borg, out var set))
            _blocked[borg] = set = new HashSet<Vector2i>();

        set.Add(tile);
    }

    /// <summary>
    /// Построить маршрут до точки и пойти по нему.
    /// </summary>
    public bool TryStartRoute(EntityUid borg, EntityCoordinates destination, string goal, out string why)
    {
        why = string.Empty;

        var xform = Transform(borg);
        var grid = xform.GridUid;

        if (grid == null || !TryComp<NavMapComponent>(grid.Value, out var navMap))
        {
            why = "я вне сетки станции — идти отсюда некуда";
            return false;
        }

        var from = ToTile(xform.LocalPosition);

        // Цель переводится в систему координат СЕТКИ, а не читается как есть.
        //
        // EntityCoordinates.Position — это смещение относительно РОДИТЕЛЯ, а цель по хендлу
        // привязана к самой сущности: у неё Position равен (0,0). Прочитав его как координаты
        // сетки, робот отправлялся в начало координат станции — на бою это выглядело так, что
        // на «подойди к двери в двух шагах» он уходил за полстанции в другую сторону.
        var destMap = _xform.ToMapCoordinates(destination);
        var to = ToTile(Vector2.Transform(destMap.Position, _xform.GetInvWorldMatrix(grid.Value)));

        // Цель почти никогда не проходима сама по себе: маяк — вывеска на стене, хендл двери —
        // сама дверь. Идти надо «к», а не «в».
        // Проходимость сверяем с навмешем рулевого ПО ЕГО ЖЕ ПРАВИЛУ.
        //
        // Наличия полигона мало: у апстрима тайл с непроходимой для нас коллизией остаётся на
        // навмеше, но GetTileCost возвращает по нему ноль, то есть «сюда нельзя». Так выглядят
        // машины, шкафы и столы — наша карта их не видит вовсе, а рулевой видит и обходит.
        // Повторяем его условие дословно, иначе наш путь ведёт туда, куда он не пойдёт: на бою
        // робот прошёл 27 тайлов из 47 и встал в коридоре, где ни одной двери в четырёх тайлах.
        var (ourLayer, ourMask) = TryComp<FixturesComponent>(borg, out var fixtures)
            ? _physics.GetHardCollision(borg, fixtures)
            : (0, 0);

        _blocked.TryGetValue(borg, out var blocked);

        bool Walkable(Vector2i t)
        {
            if (blocked != null && blocked.Contains(t))
                return false;

            var poly = _pathfinding.GetPoly(new EntityCoordinates(grid.Value, ToLocal(t)));

            if (poly == null)
                return false;

            var data = poly.Data;

            if ((ourLayer & data.CollisionMask) == 0 && (ourMask & data.CollisionLayer) == 0)
                return true;

            // Столкновение есть — но дверь мы открываем, а через перила перелезаем. Те же
            // послабления, что даёт рулевому наш набор PathFlags.
            return (data.Flags & PathfindingBreadcrumbFlag.Door) != 0
                   || (data.Flags & PathfindingBreadcrumbFlag.Climb) != 0;
        }

        var goalTile = BorgPathfinder.NearestPassable(navMap, to, walkable: Walkable);
        var startTile = BorgPathfinder.NearestPassable(navMap, from, walkable: Walkable);

        if (startTile == null || goalTile == null)
        {
            why = $"не вижу пола ни под собой, ни у цели «{goal}»";
            return false;
        }

        var path = BorgPathfinder.FindPath(navMap, startTile.Value, goalTile.Value, Walkable);

        if (path == null)
        {
            why = $"дороги до «{goal}» нет: всё перекрыто либо цель на другой сетке";
            return false;
        }

        // Ведём сами по всем тайлам пути. Пересадок нет вовсе: то, ради чего они заводились —
        // уложиться в чужой лимит, — перестало быть задачей вместе с чужим рулевым.
        SetTrail(borg, path);
        // Исход прошлой ходьбы забывается здесь: иначе скрипт, спросивший walk_status сразу
        // после старта нового маршрута, получил бы «пришёл» от предыдущего и пошёл дальше.
        _lastWalk.Remove(borg);
        _walking[borg] = goal;

        if (!_goals.TryGetValue(borg, out var known) || known.Goal != goal)
            _goals[borg] = (destination, goal, 0);

        _sawmill.Info(
            $"{ToPrettyString(borg)} маршрут до «{goal}»: {path.Count} тайлов; " +
            $"старт {startTile.Value} цель {goalTile.Value}");

        return true;
    }

    private static Vector2i ToTile(Vector2 local) =>
        new((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y));

    private static Vector2 ToLocal(Vector2i tile) =>
        new(tile.X + 0.5f, tile.Y + 0.5f);


    private void ClearRoute(EntityUid borg)
    {
        _goals.Remove(borg);
        _blocked.Remove(borg);
    }

    /// <summary>Робот продвинулся — счётчик перепланировок обнулить.</summary>
    private void ForgetReplans(EntityUid borg)
    {
        if (_goals.TryGetValue(borg, out var g) && g.Replans != 0)
            _goals[borg] = (g.Dest, g.Goal, 0);
    }

    /// <summary>
    /// Нога не прошла — переложить маршрут ОТ ТЕКУЩЕГО МЕСТА.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Первая версия просто пропускала неудачную ногу и бралась за следующую, и это было хуже, чем
    /// ничего: следующая нога <b>дальше</b>, а у апстримового рулевого свой предел в 512 узлов.
    /// Каждый пропуск ухудшал положение, и маршрут разваливался целиком — на бою «дойди до AME»
    /// кончалось «дороги нет», хотя наш собственный поиск находил дорогу за 47 тайлов.
    /// </para>
    /// <para>
    /// Причина, по которой нога вообще не проходит: наша карта знает пол, стены и шлюзы, но не
    /// знает мебели и машин. Точка пересадки могла попасть на тайл, занятый столом. Перепланировка
    /// с места решает и это: новый путь обойдёт занятый тайл, потому что робот уже стоит не там,
    /// где стоял.
    /// </para>
    /// </remarks>
    private bool TryReplan(EntityUid borg)
    {
        if (!_goals.TryGetValue(borg, out var goal))
            return false;

        if (goal.Replans >= MaxReplans)
            return false;

        _goals[borg] = (goal.Dest, goal.Goal, goal.Replans + 1);

        if (TryStartRoute(borg, goal.Dest, goal.Goal, out _))
        {
            _sawmill.Info($"{ToPrettyString(borg)} перекладывает маршрут до «{goal.Goal}» " +
                           $"(попытка {goal.Replans + 1} из {MaxReplans})");
            return true;
        }

        return false;
    }

}
