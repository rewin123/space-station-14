using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Configuration;
using Robust.Shared.Console;

namespace Content.Server.AiAgent.Commands;

/// <summary>
/// Server console entry point for the LLM agent. Registered by reflection like any other
/// <see cref="IConsoleCommand"/>, so adding it costs no upstream edit.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed class AiAgentCommand : IConsoleCommand
{
    public string Command => "aiagent";
    public string Description => "Управление LLM-агентом Station AI.";
    public string Help => "aiagent status | claim [uid] | release | inject <канал> <текст> | " +
                          "tool <имя> [json] | curate | skills | debug | dryrun on|off";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var cfg = IoCManager.Resolve<IConfigurationManager>();

        if (!entMan.EntitySysManager.TryGetEntitySystem<StationAiAgentSystem>(out var system))
        {
            shell.WriteError("StationAiAgentSystem недоступна.");
            return;
        }

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

        switch (sub)
        {
            case "status":
                Status(shell, system, cfg);
                break;

            case "claim":
            {
                if (args.Length > 1 && int.TryParse(args[1], out var raw))
                {
                    var uid = new EntityUid(raw);
                    shell.WriteLine(system.TryClaimCore(uid, out var why) ? why : $"не вышло: {why}");
                }
                else
                {
                    shell.WriteLine(system.TryClaimAnyCore(out var why) ? why : $"не вышло: {why}");
                }

                break;
            }

            case "release":
                system.ReleaseAll("вручную из консоли");
                shell.WriteLine("все агенты остановлены.");
                break;

            case "inject":
            {
                // Exercises the REAL perception path — RadioSystem.SendRadioMessage raises
                // RadioReceiveEvent on every ActiveRadio, which is exactly how a crewman's
                // transmission reaches the agent. Pushing straight into the observation queue
                // would test the formatter and prove nothing about the wiring.
                if (args.Length < 3)
                {
                    shell.WriteError("aiagent inject <channel> <текст…>");
                    return;
                }

                var channel = args[1];
                var text = string.Join(' ', args.Skip(2));
                shell.WriteLine(system.InjectRadio(channel, text, out var injectWhy) ? injectWhy : $"не вышло: {injectWhy}");
                break;
            }

            case "tool":
            {
                if (args.Length < 2)
                {
                    shell.WriteError("aiagent tool <имя> [json]");
                    return;
                }

                var name = args[1];
                var json = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "{}";
                shell.WriteLine(system.InvokeToolFromConsole(name, json, out var toolWhy) ? toolWhy : $"не вышло: {toolWhy}");
                break;
            }

            case "curate":
            {
                // On-demand review. Useful for ops, and the only practical way to exercise the
                // curator without waiting for the context to fill up.
                shell.WriteLine(system.RunCuratorNow(out var curateWhy) ? curateWhy : $"не вышло: {curateWhy}");
                break;
            }

            case "skills":
            {
                var index = system.Skills.RenderIndex();
                shell.WriteLine(index.Length > 0 ? index : "библиотека скиллов пуста");
                break;
            }

            case "debug":
            {
                var bus = system.DebugBus;

                if (bus == null)
                {
                    shell.WriteLine("шина отладки выключена (ai.debug_enabled)");
                    break;
                }

                shell.WriteLine($"instance {bus.Instance}, seq {bus.Seq}, кольцо {bus.Count}/{bus.Capacity}");
                shell.WriteLine(system.DebugEndpoint is { } endpoint
                    ? $"эндпоинт {endpoint}"
                    : "HTTP-сервер не поднят — смотри ошибку выше в логе (порт занят или пустой ai.debug_token)");
                break;
            }

            case "dryrun":
            {
                if (args.Length < 2)
                {
                    shell.WriteLine($"ai.dry_run = {cfg.GetCVar(AiCVars.DryRun)}");
                    return;
                }

                var on = args[1] is "on" or "true" or "1";
                cfg.SetCVar(AiCVars.DryRun, on);
                shell.WriteLine($"ai.dry_run = {on}");
                break;
            }

            default:
                shell.WriteError($"неизвестная подкоманда '{sub}'. {Help}");
                break;
        }
    }

    private static void Status(IConsoleShell shell, StationAiAgentSystem system, IConfigurationManager cfg)
    {
        shell.WriteLine($"ai.enabled     = {cfg.GetCVar(AiCVars.Enabled)}");
        shell.WriteLine($"ai.endpoint    = {cfg.GetCVar(AiCVars.Endpoint)}");
        shell.WriteLine($"ai.model       = {cfg.GetCVar(AiCVars.Model)}");
        shell.WriteLine($"ai.dry_run     = {cfg.GetCVar(AiCVars.DryRun)}");
        shell.WriteLine($"ai.tick_seconds= {cfg.GetCVar(AiCVars.TickSeconds)}");

        if (system.Sessions.Count == 0)
        {
            shell.WriteLine("агентов нет (нет занятого ядра ИИ).");
            return;
        }

        foreach (var (brain, session) in system.Sessions)
        {
            shell.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"agent {brain} mode={session.Mode} turns={session.Turns} " +
                $"prompt={session.Conv.LastPromptTokens}t cache={session.LastCacheRatio * 100:F1}% " +
                $"queue={session.Queue.Count} prefix={session.Conv.PrefixHash} " +
                $"fails={session.ConsecutiveFailures}"));

            if (session.LastError != null)
                shell.WriteLine($"  последняя ошибка: {session.LastError}");
        }
    }
}
