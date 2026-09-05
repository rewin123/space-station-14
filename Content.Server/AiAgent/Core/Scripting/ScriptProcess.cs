using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>What the process is doing. Exactly four states, three of them terminal.</summary>
public enum ScriptStatus : byte
{
    Running,
    Done,
    Failed,

    /// <summary>Stopped from the outside: <c>bp_stop</c>, end of session, body change.</summary>
    Stopped,
}

/// <summary>
/// One running agent script.
///
/// <para>
/// Lives on a dedicated thread, not in a pool. The script blocks on every tool call — waiting for
/// the world bus to reach the main thread — and does this dozens of times in a row; tying up a pool
/// thread for minutes would mean taking it away from the agent loop, which spins on that same pool.
/// There are only a handful of processes at once, so a thread of its own is cheaper here than any
/// cleverness.
/// </para>
/// <para>
/// Time is measured with a stopwatch, not the round clock. Round time can only be read on the main
/// thread (which is why <c>new_timer</c> marches over there for it), and the process only needs it
/// to report "how long it ran" — and unlike an alarm, the ceiling must still arrive even while
/// paused.
/// </para>
/// </summary>
public sealed class ScriptProcess
{
    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private readonly int _maxLines;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _watch = new();

    private int _cursor;
    private int _dropped;
    private int _calls;

    public ScriptProcess(int pid, string code, int maxLines, CancellationToken session)
    {
        Pid = pid;
        Code = code;
        _maxLines = Math.Max(8, maxLines);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(session);
    }

    public int Pid { get; }
    public string Code { get; }

    /// <summary>Read from the agent thread and from the main one; written only by its own thread.</summary>
    public volatile ScriptStatus Status = ScriptStatus.Running;

    public string? Error { get; private set; }
    public string? Detail { get; private set; }
    public object? Answer { get; private set; }
    public int Calls => Volatile.Read(ref _calls);
    public TimeSpan Elapsed => _watch.Elapsed;
    public Task Finished => _finished.Task;
    public CancellationToken Token => _cts.Token;
    public bool IsRunning => Status == ScriptStatus.Running;

    /// <summary>How many tools it has called so far; also a safeguard against a looping script.</summary>
    public int CountCall() => Interlocked.Increment(ref _calls);

    /// <summary>The script's output — what the model reads via <c>bp_get_output</c>.</summary>
    public void Print(string line)
    {
        lock (_lock)
        {
            _lines.Add(line);
            while (_lines.Count > _maxLines)
            {
                _lines.RemoveAt(0);
                _dropped++;
                if (_cursor > 0)
                    _cursor--;
            }
        }
    }

    /// <summary>
    /// Only what has appeared since the last read.
    ///
    /// The cursor here is not a convenience but a context saving: without it, every poll of a long
    /// script would re-insert its entire output into the dialogue, and the model would pay for the
    /// same lines as many times as it asked.
    /// </summary>
    public string ReadNew()
    {
        lock (_lock)
        {
            var builder = new StringBuilder();

            if (_dropped > 0)
            {
                builder.Append($"[lines dropped: {_dropped}]\n");
                _dropped = 0;
            }

            for (; _cursor < _lines.Count; _cursor++)
                builder.Append(_lines[_cursor]).Append('\n');

            return builder.ToString().TrimEnd('\n');
        }
    }

    /// <summary>The tail of the output, without moving the cursor — for the final report and the completion line.</summary>
    public string Tail(int lines)
    {
        lock (_lock)
        {
            var from = Math.Max(0, _lines.Count - lines);
            return string.Join('\n', _lines.GetRange(from, _lines.Count - from));
        }
    }

    public void Start(LuaHost host)
    {
        _watch.Start();

        var thread = new Thread(() => Body(host)) { IsBackground = true, Name = $"ии-скрипт-{Pid}" };
        thread.Start();
    }

    /// <summary>Stop the process. No going back: the script learns of it via cancellation at the nearest slice.</summary>
    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session has already been released — the process is dead anyway.
        }
    }

    private void Body(LuaHost host)
    {
        try
        {
            var outcome = host.Run(Code, _cts.Token);

            if (outcome.Ok)
            {
                Answer = LuaBridge.ToObject(outcome.Return);
                Status = ScriptStatus.Done;
            }
            else
            {
                Error = outcome.Error;
                Detail = outcome.Detail;
                Status = ScriptStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            Status = ScriptStatus.Stopped;
        }
        catch (Exception e)
        {
            // Only a failure on our own side lands here: errors from the script itself are already
            // handled above and arrive as a code. This surfaces as internal, without a stack trace —
            // the model couldn't do anything with it anyway.
            Error = ToolError.Internal;
            Detail = $"сбой исполнителя ({e.GetType().Name})";
            Status = ScriptStatus.Failed;
        }
        finally
        {
            _watch.Stop();
            _finished.TrySetResult();
        }
    }

    /// <summary>What the process is called in the tool report and in the SELF line.</summary>
    public string StatusWord(Locale.AgentLocale? loc = null)
    {
        loc ??= Locale.AgentLocale.Ru;
        return Status switch
        {
            ScriptStatus.Running => loc.ScriptRunning,
            ScriptStatus.Done => loc.ScriptDone,
            ScriptStatus.Failed => loc.ScriptFailed,
            _ => loc.ScriptStopped,
        };
    }
}
