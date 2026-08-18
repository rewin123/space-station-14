using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Silicons.Borgs.Components;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Консоль управления ИИ-боргом.
///
/// <para>
/// Отдельная команда, а не подкоманда <c>aiagent</c>: та адресует агента в ядре, и смешивать в ней
/// два тела значило бы, что каждая её подкоманда обязана сначала выяснить, о ком речь.
/// </para>
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed class AiBorgCommand : IConsoleCommand
{
    public string Command => "aiborg";
    public string Description => "Управление ИИ-боргом: спавн, захват, освобождение.";
    public string Help => "aiborg list | spawn [маяк|x y] | claim [uid] | release [uid] | where | " +
                          "tool <имя> [json] | path <маяк>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();

        if (!entMan.EntitySysManager.TryGetEntitySystem<AiBorgSystem>(out var system))
        {
            shell.WriteError("AiBorgSystem недоступна.");
            return;
        }

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
            {
                var query = entMan.EntityQueryEnumerator<AiBorgComponent>();
                var found = 0;

                while (query.MoveNext(out var uid, out var comp))
                {
                    found++;
                    var active = entMan.TryGetComponent<BorgChassisComponent>(uid, out var chassis) && chassis.Active;
                    var xform = entMan.GetComponent<TransformComponent>(uid);

                    shell.WriteLine(
                        $"{entMan.ToPrettyString(uid)} agent={comp.AgentId} имя={comp.AgentName} " +
                        $"активно={active} позиция={xform.Coordinates}");
                }

                if (found == 0)
                    shell.WriteLine("ИИ-боргов на карте нет. Создать: aiborg spawn");

                break;
            }

            case "spawn":
            {
                EntityUid borg;
                string reason;

                if (args.Length >= 3
                    && float.TryParse(args[1], out var x)
                    && float.TryParse(args[2], out var y))
                {
                    if (!system.TryFindGrid(out var grid))
                    {
                        shell.WriteError("не нашёл сетку станции");
                        return;
                    }

                    borg = entMan.SpawnEntity("AiBorgChassis",
                        new EntityCoordinates(grid, new System.Numerics.Vector2(x, y)));
                    reason = $"поставлен в ({x}, {y})";
                }
                else if (!system.TrySpawnBorg(args.Length > 1 ? args[1] : null, out borg, out reason))
                {
                    shell.WriteError(reason);
                    return;
                }

                shell.WriteLine($"создан {entMan.ToPrettyString(borg)}: {reason}");

                shell.WriteLine(system.TryClaim(borg, out var claim)
                    ? $"агент занял тело: {claim}"
                    : $"захват не удался: {claim}");

                break;
            }

            case "claim":
            {
                if (!TryTargetBorg(shell, entMan, args, out var uid))
                    return;

                shell.WriteLine(system.TryClaim(uid, out var reason) ? reason : $"не вышло: {reason}");
                break;
            }

            case "release":
            {
                if (!TryTargetBorg(shell, entMan, args, out var uid))
                    return;

                system.ReleaseBody(uid, "команда администратора");
                shell.WriteLine("освобождено");
                break;
            }

            case "tool":
            {
                if (args.Length < 2)
                {
                    shell.WriteError("aiborg tool <имя> [json]");
                    return;
                }

                if (!entMan.EntitySysManager.TryGetEntitySystem<StationAiAgentSystem>(out var host))
                {
                    shell.WriteError("StationAiAgentSystem недоступна");
                    return;
                }

                // Адресуем ИМЕННО борга. Общая aiagent tool берёт первого попавшегося из словаря
                // сессий, и с двумя агентами это лотерея: команда «иди на мостик» могла уехать
                // мозгу в ядре, у которого такого инструмента нет.
                var agentId = FirstBorgAgentId(entMan);
                if (agentId == null)
                {
                    shell.WriteError("ИИ-боргов на карте нет");
                    return;
                }

                var name = args[1];
                var json = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "{}";

                shell.WriteLine(host.InvokeToolFromConsole(name, json, out var why, agentId)
                    ? why
                    : $"не вышло: {why}");
                break;
            }

            case "path":
            {
                // Почему поиск не нашёл дороги. Без этого «дороги нет» неотличимо от «цель в
                // стене», «старт в стене» и «отсеки не связаны» — а лечатся они по-разному.
                if (args.Length < 2)
                {
                    shell.WriteError("aiborg path <маяк>");
                    return;
                }

                if (!TryTargetBorg(shell, entMan, args.Length > 2 ? new[] { args[0], args[2] } : new[] { args[0] }, out var who))
                    return;

                var xf = entMan.GetComponent<TransformComponent>(who);
                if (xf.GridUid is not { } g || !entMan.TryGetComponent<Content.Shared.Pinpointer.NavMapComponent>(g, out var nav))
                {
                    shell.WriteError("робот вне сетки с навигационной картой");
                    return;
                }

                var beacon = nav.Beacons.Values.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.Text) &&
                    x.Text!.Contains(args[1], StringComparison.OrdinalIgnoreCase));

                if (beacon.Text == null)
                {
                    shell.WriteError($"нет маяка «{args[1]}»");
                    return;
                }

                var from = new Vector2i((int) MathF.Floor(xf.LocalPosition.X), (int) MathF.Floor(xf.LocalPosition.Y));
                var to = new Vector2i((int) MathF.Floor(beacon.Position.X), (int) MathF.Floor(beacon.Position.Y));

                var a = BorgPathfinder.NearestPassable(nav, from);
                var bb = BorgPathfinder.NearestPassable(nav, to);

                shell.WriteLine($"маяк «{beacon.Text}» тайл {to}; проходимый рядом: {(bb == null ? "НЕ НАЙДЕН" : bb.ToString())}");
                shell.WriteLine($"робот тайл {from}; проходимый рядом: {(a == null ? "НЕ НАЙДЕН" : a.ToString())}");

                if (a == null || bb == null)
                    return;

                var path = BorgPathfinder.FindPath(nav, a.Value, bb.Value);

                shell.WriteLine(path == null
                    ? $"путь НЕ найден (развёрнуто до {BorgPathfinder.NodeLimit} узлов)"
                    : $"путь найден: {path.Count} тайлов, ног {BorgPathfinder.ToLegs(path).Count}");

                break;
            }

            case "where":
            {
                var query = entMan.EntityQueryEnumerator<AiBorgComponent>();
                while (query.MoveNext(out var uid, out _))
                {
                    var xform = entMan.GetComponent<TransformComponent>(uid);
                    var line = $"{entMan.ToPrettyString(uid)} → {xform.Coordinates}";

                    // Состояние рулевого — единственный способ отличить «идёт медленно» от
                    // «упёрся и стоит»: в игре это выглядит одинаково.
                    if (entMan.TryGetComponent<Content.Shared.Movement.Components.InputMoverComponent>(uid, out var mover))
                        line += $" | CanMove={mover.CanMove}";

                    // Стоит ли робот на навмеше. Если нет — путепоиск не построит маршрут даже
                    // на соседний тайл, и «не нашёл дороги» означает не «дороги нет», а
                    // «пути не существует даже из-под ног».
                    var pathfinding = entMan.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
                    var poly = pathfinding.GetPoly(xform.Coordinates);
                    line += $" | навмеш={(poly == null ? "НЕТ" : "есть")}";

                    if (entMan.TryGetComponent<Content.Server.NPC.Components.NPCSteeringComponent>(uid, out var st))
                        line += $" | рулевой={st.Status} флаги={st.Flags} путь={st.CurrentPath.Count} цель={st.Coordinates}";
                    else
                        line += " | рулевой не зарегистрирован";

                    shell.WriteLine(line);
                }

                break;
            }

            default:
                shell.WriteLine(Help);
                break;
        }
    }

    private static string? FirstBorgAgentId(IEntityManager entMan)
    {
        var query = entMan.EntityQueryEnumerator<AiBorgComponent>();
        return query.MoveNext(out _, out var comp) ? comp.AgentId : null;
    }

    private static bool TryTargetBorg(IConsoleShell shell, IEntityManager entMan, string[] args, out EntityUid uid)
    {
        uid = default;

        if (args.Length > 1 && int.TryParse(args[1], out var raw))
        {
            uid = new EntityUid(raw);
            return true;
        }

        var query = entMan.EntityQueryEnumerator<AiBorgComponent>();
        if (query.MoveNext(out uid, out _))
            return true;

        shell.WriteError("ИИ-боргов на карте нет");
        return false;
    }



}
