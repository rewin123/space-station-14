using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Content.Server.AiAgent.Locale;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// The processes of one session: who's running now, who has already finished, and who hasn't been
/// reported yet.
///
/// <para>
/// The finished-process queue exists because a process dies on its own thread, and the agent can
/// only be told about it from the main one: <c>ObservationQueue.Push</c> is called from event
/// handlers, and a second entry point from a foreign thread would break its one rule. So the thread
/// puts the process in the queue, and <c>Update</c> drains it — the same trick used for fired timers.
/// </para>
/// </summary>
public sealed class ScriptProcessTable
{
    public AgentLocale Locale { get; set; } = AgentLocale.Ru;

    private readonly object _lock = new();
    private readonly Dictionary<int, ScriptProcess> _all = new();
    private readonly ConcurrentQueue<ScriptProcess> _toReport = new();
    private readonly CancellationTokenSource _life = new();

    private int _nextPid;

    /// <summary>How many finished processes to remember so <c>bp_get_output</c> can still answer.</summary>
    private const int KeepFinished = 8;

    public IReadOnlyList<ScriptProcess> Running()
    {
        lock (_lock)
            return _all.Values.Where(p => p.IsRunning).OrderBy(p => p.Pid).ToList();
    }

    public ScriptProcess? Get(int pid)
    {
        lock (_lock)
            return _all.TryGetValue(pid, out var process) ? process : null;
    }

    /// <summary>Create a process and start it. The host builds the caller — the table knows nothing about Lua.</summary>
    public ScriptProcess Start(string code, int maxLines, Func<ScriptProcess, LuaHost> build)
    {
        ScriptProcess process;

        lock (_lock)
        {
            process = new ScriptProcess(++_nextPid, code, maxLines, _life.Token);
            _all[process.Pid] = process;
            Prune();
        }

        // Subscribe BEFORE starting: the script can finish before we return from Start.
        process.Finished.ContinueWith(_ => _toReport.Enqueue(process), TaskContinuationOptions.ExecuteSynchronously);

        process.Start(build(process));
        return process;
    }

    /// <summary>Take a process whose completion hasn't been reported to the agent yet.</summary>
    public bool TryTakeReport(out ScriptProcess process) => _toReport.TryDequeue(out process!);

    /// <summary>Stop everything. Called when the session is released and at the end of the round.</summary>
    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var process in _all.Values)
                process.Stop();
        }

        // Also cancel the linked source: a process stuck in a tool call learns about it from there,
        // not from the nearest slice.
        try
        {
            _life.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The SELF line: what the agent is doing right now besides its own turn.</summary>
    public string SelfLine()
    {
        var running = Running();
        if (running.Count == 0)
            return "";

        return string.Join(", ", running.Select(p =>
        {
            var loc = Locale;
            return loc.English
                ? $"#{p.Pid} {loc.ScriptRunning} {p.Elapsed.TotalSeconds:0}s"
                : $"#{p.Pid} идёт {p.Elapsed.TotalSeconds:0} с";
        }));
    }

    private void Prune()
    {
        var finished = _all.Values
            .Where(p => !p.IsRunning)
            .OrderBy(p => p.Pid)
            .ToList();

        for (var i = 0; i < finished.Count - KeepFinished; i++)
            _all.Remove(finished[i].Pid);
    }
}
