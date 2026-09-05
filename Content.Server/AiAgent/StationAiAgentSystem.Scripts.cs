using System;
using Content.Server.AiAgent.Core.Scripting;
using Content.Server.AiAgent.Perception;

namespace Content.Server.AiAgent;

/// <summary>
/// Script mode as seen from the system side: where it's turned on and how the agent learns that a
/// script has finished.
///
/// <para>
/// A script dies on its own thread, and the only place to tell the agent about it is the main one:
/// <see cref="ObservationQueue.Push"/> is called from world event handlers, and a second entry point
/// from a foreign thread would break its one rule. So the thread puts the process into a queue, and
/// the tick drains it — exactly the same trick used for fired timers.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private ScriptTools? _scripts;

    /// <summary>Mode knobs are read live, as everywhere else: the cvar changes on a live server without a restart.</summary>
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
    /// Running scripts in the SELF line — following the pattern of timers.
    ///
    /// Without this, an agent that started a background task and closed its turn would forget about
    /// it until the completion observation arrived, and could launch a second identical one.
    /// </summary>
    public string ScriptsForSelf(AgentSession session)
    {
        var line = session.Scripts?.SelfLine() ?? "";
        return string.IsNullOrEmpty(line) ? "" : $"{session.Locale.SelfScripts}=[{line}]";
    }

    /// <summary>
    /// Delivering the outcome of a background script as an observation.
    ///
    /// This is exactly what closes the loop: the model started a long-running task, closed its turn
    /// and is asleep — and wakes up on its own once the task is done. Without this, it would have to
    /// poll <c>bp_get_output</c>, and polling costs exactly the model call this whole mode was
    /// written to save.
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
                var loc = session.Locale;
                var head = loc.T(
                    $"СКРИПТ #{process.Pid} {process.StatusWord(loc)} за {seconds:0} с, вызовов {process.Calls}",
                    $"SCRIPT #{process.Pid} {process.StatusWord(loc)} in {seconds:0}s, {process.Calls} calls");

                var text = process.Status switch
                {
                    ScriptStatus.Done => $"{head}. {Tail(process, loc)}",
                    ScriptStatus.Stopped => loc.T(
                        $"{head} — снят. Сделанное не отменено. {Tail(process, loc)}",
                        $"{head} — stopped. What was done is not undone. {Tail(process, loc)}"),
                    _ => $"{head}: {process.Detail}. {Tail(process, loc)}",
                };

                session.Queue.Push(Observation.Event(text, RoundTime()));
            }
        }
    }

    /// <summary>
    /// The output tail included in the observation itself — four lines, no more.
    ///
    /// The cursor doesn't move for this: the full output stays available through
    /// <c>bp_get_output</c>. Here we only need enough for the model to decide whether it's worth
    /// looking there at all.
    /// </summary>
    private static string Tail(ScriptProcess process, Locale.AgentLocale loc)
    {
        var tail = process.Tail(4);
        return string.IsNullOrWhiteSpace(tail)
            ? loc.T("Скрипт ничего не напечатал.", "The script printed nothing.")
            : tail;
    }
}
