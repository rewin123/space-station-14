using System.Linq;
using Content.Server.Administration;
using Content.Server.GameTicking;
using Content.Server.Station.Systems;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server.AiAgent.Commands;

/// <summary>
/// Стендовые команды: то, что нужно замеру и не нужно игре.
/// </summary>
/// <remarks>
/// <para>
/// Существует ради одной беды. Замер сетевых болезней требует НАСТОЯЩЕГО клиента — headless,
/// собранного в Release, подключённого через <c>Tools/laglink.py</c>. Такой клиент не может
/// нажать «готов» в лобби: у него нет ни экрана, ни ввода. <c>readyall</c> его не берёт
/// (готовность слетает при входе в лобби), <c>respawn</c> не находит по имени вида
/// <c>localhost@Proba</c> и не заводится по идентификатору, а <c>controlmob</c> и <c>observe</c>
/// требуют шелла игрока, которого у серверной консоли нет.
/// </para>
/// <para>
/// Без тела клиент сидит в лобби, зона видимости у него пустая, и замерять нечего: стенд
/// показывает ноль ресинков просто потому, что сервер ничего не шлёт. Именно на это я потратил
/// два прогона впустую, прежде чем понять.
/// </para>
/// <para>
/// Отдельным файлом и отдельной командой, а не веткой в <c>aiagent</c>: та адресует агента, а эта
/// не про агента вовсе. В игровую логику ничего не добавляет — зовёт тот же
/// <c>GameTicker.MakeJoinGame</c>, что и обычный вход в смену.
/// </para>
/// </remarks>
[AdminCommand(AdminFlags.Debug)]
public sealed class AiBenchCommand : IConsoleCommand
{
    public string Command => "aibench";
    public string Description => "Стенд: посадить подключённого клиента в тело из серверной консоли.";
    public string Help => "aibench join <часть имени игрока> [должность]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || args[0].ToLowerInvariant() != "join")
        {
            shell.WriteError(Help);
            return;
        }

        if (args.Length < 2)
        {
            shell.WriteError("нужна часть имени игрока: " + Help);
            return;
        }

        var players = IoCManager.Resolve<IPlayerManager>();
        var entMan = IoCManager.Resolve<IEntityManager>();

        // Частичное совпадение намеренно: настоящее имя сессии выглядит как «localhost@Proba»,
        // и заставлять стенд знать префикс — лишний повод для опечатки.
        var session = players.Sessions.FirstOrDefault(s =>
            s.Name.Contains(args[1], System.StringComparison.OrdinalIgnoreCase));

        if (session == null)
        {
            shell.WriteError($"нет подключённого игрока с «{args[1]}». Есть: "
                             + string.Join(", ", players.Sessions.Select(s => s.Name)));
            return;
        }

        if (session.AttachedEntity != null)
        {
            shell.WriteLine($"{session.Name} уже в теле {entMan.ToPrettyString(session.AttachedEntity.Value)}");
            return;
        }

        if (!entMan.EntitySysManager.TryGetEntitySystem<GameTicker>(out var ticker)
            || !entMan.EntitySysManager.TryGetEntitySystem<StationSystem>(out var stations))
        {
            shell.WriteError("GameTicker или StationSystem недоступны");
            return;
        }

        var station = stations.GetStations().FirstOrDefault();
        if (station == default)
        {
            shell.WriteError("станций на карте нет — раунд идёт?");
            return;
        }

        var job = args.Length > 2 ? args[2] : null;
        ticker.MakeJoinGame(session, station, job);
        shell.WriteLine($"{session.Name} отправлен в смену на {entMan.ToPrettyString(station)}"
                        + (job == null ? "" : $" должностью {job}"));
    }
}
