using System.Collections.Generic;
using Content.Shared.Pinpointer;
using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Свой поиск пути по всей станции.
///
/// <para>
/// <b>Зачем понадобился свой.</b> Апстримовый <c>PathfindingSystem</c> не рассчитан на переходы
/// через станцию: <c>NodeLimit = 512</c> обрывает разворот графа, и A* возвращает <c>NoPath</c>.
/// Штатным NPC этого хватает — они дерутся и прибираются в пределах комнаты, — а робот, которому
/// сказали «иди в инженерный», упирался в лимит и сообщал «дороги нет», стоя в проходимом
/// коридоре. Замерено на бою: три шага на восток — «дошёл», Bar → Bridge — «дороги нет».
/// </para>
/// <para>
/// Обходной приём с цепочкой навигационных маяков это лечил плохо: маяки расставлены по смыслу, а
/// не по проходимости, и цепочка «ближайших» упиралась в запертые отсеки. Здесь честный поиск.
/// </para>
/// <para>
/// <b>По чему ищем.</b> По <see cref="NavMapComponent"/> — той самой карте, которую игра уже
/// строит и поддерживает для наручного навигационного планшета. Это побитовая карта всей станции:
/// пол, стены и шлюзы, чанками 8×8. Ни одного broadphase-запроса, ни одного обхода сущностей —
/// поэтому полный поиск через станцию стоит дешевле, чем один <c>look</c>.
/// </para>
/// <para>
/// <b>Что этот поиск НЕ делает.</b> Он не ведёт робота: найденный путь режется на короткие ноги и
/// отдаётся апстримовому рулевому. Тот умеет всё, чего не умеет карта, — физику, обход мебели и
/// людей, открывание дверей. Разделение намеренное: глобальный маршрут наш, локальное движение
/// чужое и проверенное.
/// </para>
/// </summary>
public static class BorgPathfinder
{
    /// <summary>
    /// Потолок развёрнутых узлов.
    ///
    /// На порядок больше апстримовых 512 и всё ещё дёшев: узел это чтение из словаря чанков и
    /// сравнение битов. Существует как страховка от поиска по бесконечной пустоте, а не как
    /// бюджет — реальный переход через станцию укладывается в тысячи.
    /// </summary>
    public const int NodeLimit = 60_000;

    /// <summary>
    /// Во сколько раз дороже пройти через шлюз.
    ///
    /// Дверь физически проходима, но её надо открыть, а это секунды и иногда отказ по доступу.
    /// Штраф заставляет предпочесть коридор в обход — ровно так же выбирает человек.
    /// </summary>
    private const float DoorCost = 4f;

    /// <summary>Тайл проходим: под ногами пол и нет стены. Шлюз проходим — у борга есть доступ.</summary>
    public static bool Passable(NavMapComponent navMap, Vector2i tile)
    {
        if (!TryGetTileData(navMap, tile, out var data))
            return false;

        if ((data & SharedNavMapSystem.FloorMask) == 0)
            return false;

        // Стена и окно — обе категории Wall. Окно прозрачно для глаз, но не для корпуса.
        return (data & SharedNavMapSystem.WallMask) == 0;
    }

    private static bool IsDoor(NavMapComponent navMap, Vector2i tile) =>
        TryGetTileData(navMap, tile, out var data) && (data & SharedNavMapSystem.AirlockMask) != 0;

    private static bool TryGetTileData(NavMapComponent navMap, Vector2i tile, out int data)
    {
        data = 0;

        var origin = SharedMapSystem.GetChunkIndices(tile, SharedNavMapSystem.ChunkSize);

        if (!navMap.Chunks.TryGetValue(origin, out var chunk))
            return false;

        var relative = SharedMapSystem.GetChunkRelative(tile, SharedNavMapSystem.ChunkSize);
        data = chunk.TileData[SharedNavMapSystem.GetTileIndex(relative)];
        return true;
    }

    /// <summary>
    /// Ближайший проходимый тайл к точке, или <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Нужен, потому что цель почти никогда не задаётся проходимым тайлом: навигационный маяк —
    /// это вывеска на стене, а хендл двери — сама дверь. Идти надо «к», а не «в».
    /// </remarks>
    public static Vector2i? NearestPassable(NavMapComponent navMap, Vector2i around, int radius = 12)
    {
        if (Passable(navMap, around))
            return around;

        for (var r = 1; r <= radius; r++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;

                    var candidate = around + new Vector2i(dx, dy);

                    if (Passable(navMap, candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Путь по тайлам от старта до цели, или <c>null</c>, если его нет.
    /// </summary>
    /// <remarks>
    /// Соседи только по четырём сторонам. Диагонали дали бы дорогу короче на проценты, а стоили бы
    /// проверки срезанных углов: по диагонали между двумя стенами корпус не пройдёт, и путь,
    /// который карта считает верным, кончился бы роботом, застрявшим в дверном косяке.
    /// </remarks>
    public static List<Vector2i>? FindPath(NavMapComponent navMap, Vector2i start, Vector2i goal)
    {
        if (start == goal)
            return new List<Vector2i> { start };

        if (!Passable(navMap, start) || !Passable(navMap, goal))
            return null;

        var frontier = new PriorityQueue<Vector2i, float>();
        var cameFrom = new Dictionary<Vector2i, Vector2i>();
        var costSoFar = new Dictionary<Vector2i, float> { [start] = 0f };

        frontier.Enqueue(start, 0f);

        var expanded = 0;
        var found = false;

        while (frontier.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                found = true;
                break;
            }

            if (++expanded > NodeLimit)
                break;

            foreach (var dir in Neighbours)
            {
                var next = current + dir;

                if (!Passable(navMap, next))
                    continue;

                var step = IsDoor(navMap, next) ? DoorCost : 1f;
                var cost = costSoFar[current] + step;

                if (costSoFar.TryGetValue(next, out var known) && cost >= known)
                    continue;

                costSoFar[next] = cost;
                cameFrom[next] = current;
                frontier.Enqueue(next, cost + Heuristic(next, goal));
            }
        }

        if (!found)
            return null;

        var path = new List<Vector2i>();
        var node = goal;

        while (node != start)
        {
            path.Add(node);

            if (!cameFrom.TryGetValue(node, out node))
                return null;
        }

        path.Add(start);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Разрезать путь на ноги, которые по силам апстримовому рулевому.
    /// </summary>
    /// <remarks>
    /// Каждая нога обязана укладываться в его <c>NodeLimit = 512</c> полигонов, поэтому берём
    /// точку раз в <paramref name="every"/> тайлов. Последний тайл добавляется всегда: без него
    /// робот останавливался бы за несколько шагов до цели.
    /// </remarks>
    public static List<Vector2i> ToLegs(List<Vector2i> path, int every = 6)
    {
        var legs = new List<Vector2i>();

        for (var i = every; i < path.Count; i += every)
            legs.Add(path[i]);

        if (legs.Count == 0 || legs[^1] != path[^1])
            legs.Add(path[^1]);

        return legs;
    }

    private static readonly Vector2i[] Neighbours =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
    };

    private static float Heuristic(Vector2i a, Vector2i b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
