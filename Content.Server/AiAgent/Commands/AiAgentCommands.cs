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
    public string Help => "aiagent status | cost | llm [use <профиль>|revive <профиль>] | claim [uid] | " +
                          "release | inject <канал> <текст> | tool <имя> [json] | curate | skills | " +
                          "timers | debug | dryrun on|off";

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

            case "cost":
                Cost(shell, system);
                break;

            case "llm":
                Llm(shell, system, cfg, args);
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

            case "timers":
            {
                // Отдельная подкоманда, а не строчка в status: таймер, о котором агент забыл, —
                // это будущие ходы и будущие деньги, и увидеть их до срабатывания можно только так.
                var now = system.RoundTime();
                var any = false;

                foreach (var session in system.Sessions.Values)
                {
                    foreach (var timer in session.State.Timers.All())
                    {
                        any = true;
                        var left = (int)(timer.DueAt - now).TotalSeconds;
                        shell.WriteLine($"{timer.Name} — через {left}с" +
                                        (timer.Every.HasValue ? $", повтор каждые {(int)timer.Every.Value.TotalSeconds}с" : "") +
                                        $": {timer.Message}");
                    }
                }

                if (!any)
                    shell.WriteLine("ни одного таймера не заведено");

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

                // Ростер печатается здесь потому, что это единственный способ проверить витрину,
                // не открывая браузер. Расхождение «в игре три робота, на витрине один» иначе
                // видно только по пустому переключателю в интерфейсе.
                var roster = system.DebugAgents.Roster();

                if (roster.Count == 0)
                {
                    shell.WriteLine("на витрине никого — тело не занято, либо шина поднялась позже захвата");
                    break;
                }

                foreach (var agent in roster)
                {
                    shell.WriteLine($"  {agent.Id} | {agent.Name} | {(agent.Alive ? "жив" : "МЁРТВ")} | " +
                                    $"ходов {agent.Turns} | сообщений {agent.Messages} | режим {agent.Mode}" +
                                    (agent.PendingInput ? " | ждёт сообщение оператора" : "") +
                                    (string.IsNullOrWhiteSpace(agent.LastError) ? "" : $" | ОШИБКА: {agent.LastError}"));
                }

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

    /// <summary>
    /// Цепочка провайдеров: кто сейчас отвечает, кто спит и до каких пор, сколько израсходовано.
    ///
    /// Расход показывается по двум причинам сразу. Для платных профилей это деньги. Для подписок —
    /// единственный способ узнать потолок: ни OpenAI, ни xAI своих настоящих лимитов не публикуют,
    /// известно лишь, что у Codex окно пятичасовое, а у Grok Build пул недельный. Так что «сколько
    /// обращений за окно» здесь не украшение, а измерительный прибор.
    /// </summary>
    private static void Llm(
        IConsoleShell shell,
        StationAiAgentSystem system,
        IConfigurationManager cfg,
        string[] args)
    {
        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "show";

        if (verb is "use" or "revive")
        {
            if (args.Length < 3)
            {
                shell.WriteError($"aiagent llm {verb} <профиль>");
                return;
            }

            if (system.Router is not { } target)
            {
                shell.WriteError("цепочка не собрана — задай ai.llm_chain и дождись первого хода агента.");
                return;
            }

            var ok = verb == "use"
                ? target.TryUse(args[2], out var why)
                : target.Revive(args[2], out why);

            if (ok)
                shell.WriteLine(why);
            else
                shell.WriteError(why);

            return;
        }

        var chain = cfg.GetCVar(AiCVars.LlmChain);

        if (system.Router is not { } router)
        {
            // Клиент собирается лениво, при первом обращении к модели. До этого момента показать
            // состояние нечего, и сказать об этом прямо честнее, чем напечатать пустую таблицу.
            shell.WriteLine(string.IsNullOrWhiteSpace(chain)
                ? $"цепочка не задана: работает одиночный эндпоинт {cfg.GetCVar(AiCVars.Endpoint)} " +
                  $"({cfg.GetCVar(AiCVars.Model)})"
                : $"ai.llm_chain = «{chain}», но клиент ещё не собран — соберётся на первом ходу агента.");
            return;
        }

        shell.WriteLine(router.Describe());
    }

    /// <summary>
    /// Чем агент занял главный поток и во что обошёлся кадр.
    ///
    /// Отдельной командой, а не строкой в <c>status</c>: это таблица на два десятка строк, а
    /// <c>status</c> должен оставаться тем, что читают с одного экрана.
    /// </summary>
    private static void Cost(IConsoleShell shell, StationAiAgentSystem system)
    {
        var (last, ticks, overruns) = system.FrameReport();

        shell.WriteLine(last);
        shell.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"тиков всего: {ticks}, опозданий: {overruns} " +
            $"({(ticks == 0 ? 0 : 100.0 * overruns / ticks):F1}%)"));

        var (depth, deferrals, promotions, overflows, maxWait) = system.WorldBusHealth();
        shell.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"шина мира: в очереди {depth}, переносов {deferrals}, обгонов {promotions}, " +
            $"отказов по переполнению {overflows}, худшее ожидание {maxWait:F1}мс"));

        var report = system.MainThreadReport();
        if (report.Count == 0)
        {
            shell.WriteLine("главный поток агентом ещё не занимался.");
            return;
        }

        shell.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"главного потока на агента всего: {system.MainThreadTotalMs():F0}мс"));
        shell.WriteLine($"{"операция",-20} {"n",6} {"p50",7} {"p95",7} {"max",7} {"итого",9} {"сверх",6}");

        foreach (var (what, count, p50, p95, max, total, over) in report)
        {
            shell.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{what,-20} {count,6} {p50,7:F1} {p95,7:F1} {max,7:F1} {total,9:F0} {over,6}"));
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
