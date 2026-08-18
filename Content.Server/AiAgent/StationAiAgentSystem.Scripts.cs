using System;
using Content.Server.AiAgent.Core.Scripting;
using Content.Server.AiAgent.Perception;

namespace Content.Server.AiAgent;

/// <summary>
/// Режим скрипта со стороны системы: где он включается и как о законченном скрипте узнаёт агент.
///
/// <para>
/// Скрипт умирает на своём потоке, а сказать об этом агенту можно только с главного:
/// <see cref="ObservationQueue.Push"/> зовут из обработчиков событий мира, и вторая точка входа с
/// чужого потока сломала бы её единственное правило. Поэтому поток кладёт процесс в очередь, а
/// тик её разбирает — ровно тем же приёмом, что и сработавшие будильники.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private ScriptTools? _scripts;

    /// <summary>Ручки режима читаются живыми, как и везде: cvar меняется на боевом сервере без рестарта.</summary>
    private ScriptTools Scripts => _scripts ??= new ScriptTools(new ScriptOptions
    {
        ForegroundMs = () => _cfg.GetCVar(AiCVars.ScriptForegroundMs),
        MaxProcesses = () => _cfg.GetCVar(AiCVars.ScriptMaxProcesses),
        MaxSeconds = () => _cfg.GetCVar(AiCVars.ScriptMaxSeconds),
        MaxCalls = () => _cfg.GetCVar(AiCVars.ScriptMaxCalls),
        MaxSteps = () => _cfg.GetCVar(AiCVars.ScriptMaxSteps),
        OutputLines = () => _cfg.GetCVar(AiCVars.ScriptOutputLines),
    });

    /// <summary>
    /// Идущие скрипты в строке SELF — по образцу будильников.
    ///
    /// Без этого агент, запустивший фоновое дело и закрывший ход, забывал бы о нём до самого
    /// наблюдения о конце и мог запустить второе такое же.
    /// </summary>
    public string ScriptsForSelf(AgentSession session)
    {
        var line = session.Scripts?.SelfLine() ?? "";
        return string.IsNullOrEmpty(line) ? "" : $"скрипты=[{line}]";
    }

    /// <summary>
    /// Досылка итога фонового скрипта наблюдением.
    ///
    /// Именно она замыкает петлю: модель запустила длинное дело, закрыла ход и спит — и просыпается
    /// сама, когда дело кончилось. Без этого пришлось бы опрашивать <c>bp_get_output</c>, а опрос
    /// стоит ровно того обращения к модели, ради экономии которого весь режим и написан.
    /// </summary>
    private void ReportFinishedScripts()
    {
        foreach (var session in _sessions.Values)
        {
            var table = session.Scripts;
            if (table == null)
                continue;

            while (table.TryTakeReport(out var process))
            {
                var seconds = process.Elapsed.TotalSeconds;
                var head = $"СКРИПТ #{process.Pid} {process.StatusWord()} за {seconds:0} с, вызовов {process.Calls}";

                var text = process.Status switch
                {
                    ScriptStatus.Done => $"{head}. {Tail(process)}",
                    ScriptStatus.Stopped => $"{head} — снят. Сделанное не отменено. {Tail(process)}",
                    _ => $"{head}: {process.Detail}. {Tail(process)}",
                };

                session.Queue.Push(Observation.Event(text, RoundTime()));
            }
        }
    }

    /// <summary>
    /// Хвост вывода в самом наблюдении — четыре строки, не больше.
    ///
    /// Курсор при этом не двигается: полный вывод остаётся доступным через <c>bp_get_output</c>.
    /// Здесь нужно ровно столько, чтобы модель поняла, надо ли туда смотреть вообще.
    /// </summary>
    private static string Tail(ScriptProcess process)
    {
        var tail = process.Tail(4);
        return string.IsNullOrWhiteSpace(tail) ? "Скрипт ничего не напечатал." : tail;
    }
}
