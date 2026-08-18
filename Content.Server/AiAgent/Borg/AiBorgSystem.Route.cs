using System.Collections.Generic;
using System.Numerics;
using Content.Shared.Pinpointer;
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
    /// <summary>Одна нога маршрута.</summary>
    private readonly record struct Leg(EntityCoordinates Coords, string What);

    /// <summary>Незаконченный маршрут робота: ноги, которые ещё предстоит пройти.</summary>
    private readonly Dictionary<EntityUid, Queue<Leg>> _routes = new();

    /// <summary>
    /// Построить маршрут до точки и начать первую ногу.
    /// </summary>
    public bool TryStartRoute(EntityUid borg, EntityCoordinates destination, string goal, out string why)
    {
        why = string.Empty;
        _routes.Remove(borg);

        var xform = Transform(borg);
        var grid = xform.GridUid;

        // Вне сетки маршрут не по чему строить — отдаём рулевому как есть, пусть решает он.
        if (grid == null || !TryComp<NavMapComponent>(grid.Value, out var navMap))
        {
            StartSteering(borg, destination, goal, range: 1.2f);
            return true;
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
        var goalTile = BorgPathfinder.NearestPassable(navMap, to);
        var startTile = BorgPathfinder.NearestPassable(navMap, from);

        if (startTile == null || goalTile == null)
        {
            why = $"не вижу пола ни под собой, ни у цели «{goal}»";
            return false;
        }

        var path = BorgPathfinder.FindPath(navMap, startTile.Value, goalTile.Value);

        if (path == null)
        {
            why = $"дороги до «{goal}» нет: всё перекрыто либо цель на другой сетке";
            return false;
        }

        var legs = new Queue<Leg>();

        foreach (var tile in BorgPathfinder.ToLegs(path))
            legs.Enqueue(new Leg(new EntityCoordinates(grid.Value, ToLocal(tile)), goal));

        // Последняя нога — сама цель, а не центр её тайла: к двери надо подойти вплотную.
        legs.Enqueue(new Leg(destination, goal));

        _routes[borg] = legs;
        _sawmill.Debug($"{ToPrettyString(borg)} маршрут до «{goal}»: {path.Count} тайлов, {legs.Count} ног");

        AdvanceRoute(borg);
        return true;
    }

    private static Vector2i ToTile(Vector2 local) =>
        new((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y));

    private static Vector2 ToLocal(Vector2i tile) =>
        new(tile.X + 0.5f, tile.Y + 0.5f);

    /// <summary>Начать следующую ногу маршрута. Возвращает false, когда маршрут кончился.</summary>
    private bool AdvanceRoute(EntityUid borg)
    {
        if (!_routes.TryGetValue(borg, out var legs) || legs.Count == 0)
        {
            _routes.Remove(borg);
            return false;
        }

        var leg = legs.Dequeue();

        // Последняя нога подходит вплотную, промежуточные — лишь бы дойти до отсека.
        StartSteering(borg, leg.Coords, leg.What, range: legs.Count == 0 ? 1.2f : 2.5f);
        return true;
    }

    private void ClearRoute(EntityUid borg) => _routes.Remove(borg);

}
