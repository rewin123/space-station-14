using System.Collections.Generic;
using System.Linq;
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
/// Поэтому длинная дорога режется на короткие ноги по навигационным маякам — тем самым, названия
/// которых экипаж говорит по рации. Каждая нога укладывается в лимит A*, а маяки образуют граф,
/// по которому ищется последовательность.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// Дальше этого расстояния между маяками считаем, что прямой дороги нет.
    ///
    /// Не геометрия, а бюджет: нога должна укладываться в <c>NodeLimit</c> полигонов A*.
    /// </summary>
    private const float LegLength = 22f;

    /// <summary>Ближе этого до цели маяки не нужны — идём напрямую.</summary>
    private const float DirectRange = 18f;

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

        var here = _xform.GetMapCoordinates(borg).Position;
        var thereLocal = destination.Position;
        var grid = Transform(borg).GridUid;

        // Близко — идём напрямую, без маяков: лишняя пересадка только удлиняет путь.
        if (grid == null || (thereLocal - Transform(borg).LocalPosition).Length() <= DirectRange)
        {
            StartSteering(borg, destination, goal, range: 1.2f);
            return true;
        }

        if (!TryComp<NavMapComponent>(grid.Value, out var navMap))
        {
            StartSteering(borg, destination, goal, range: 1.2f);
            return true;
        }

        var beacons = navMap.Beacons.Values
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => (b.Text!, b.Position))
            .ToList();

        if (beacons.Count == 0)
        {
            StartSteering(borg, destination, goal, range: 1.2f);
            return true;
        }

        var from = Transform(borg).LocalPosition;
        var path = FindBeaconPath(beacons, from, thereLocal);

        if (path == null)
        {
            why = $"не вижу, как добраться до «{goal}»: между мной и целью нет цепочки известных отсеков";
            return false;
        }

        var legs = new Queue<Leg>();
        foreach (var (name, pos) in path)
            legs.Enqueue(new Leg(new EntityCoordinates(grid.Value, pos), name));

        legs.Enqueue(new Leg(destination, goal));

        _routes[borg] = legs;
        AdvanceRoute(borg);
        return true;
    }

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

    /// <summary>
    /// Цепочка маяков от точки до точки: ширину в глубину по графу «маяки не дальше ноги».
    /// </summary>
    private static List<(string Name, Vector2 Pos)>? FindBeaconPath(
        List<(string Name, Vector2 Pos)> beacons, Vector2 from, Vector2 to)
    {
        int? Nearest(Vector2 p)
        {
            var best = -1;
            var bestDist = float.MaxValue;

            for (var i = 0; i < beacons.Count; i++)
            {
                var d = (beacons[i].Pos - p).Length();
                if (d >= bestDist)
                    continue;

                bestDist = d;
                best = i;
            }

            return best < 0 ? null : best;
        }

        var start = Nearest(from);
        var goal = Nearest(to);

        if (start == null || goal == null)
            return null;

        var prev = new Dictionary<int, int>();
        var seen = new HashSet<int> { start.Value };
        var queue = new Queue<int>();
        queue.Enqueue(start.Value);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();

            if (cur == goal.Value)
                break;

            for (var i = 0; i < beacons.Count; i++)
            {
                if (seen.Contains(i))
                    continue;

                if ((beacons[i].Pos - beacons[cur].Pos).Length() > LegLength)
                    continue;

                seen.Add(i);
                prev[i] = cur;
                queue.Enqueue(i);
            }
        }

        if (!seen.Contains(goal.Value))
            return null;

        var chain = new List<(string, Vector2)>();
        var node = goal.Value;

        while (true)
        {
            chain.Add(beacons[node]);

            if (node == start.Value)
                break;

            if (!prev.TryGetValue(node, out node))
                return null;
        }

        chain.Reverse();
        return chain;
    }
}
