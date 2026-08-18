using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>Чем занят процесс. Ровно четыре состояния, и три из них конечные.</summary>
public enum ScriptStatus : byte
{
    Running,
    Done,
    Failed,

    /// <summary>Снят снаружи: <c>bp_stop</c>, конец сессии, смена тела.</summary>
    Stopped,
}

/// <summary>
/// Один запущенный скрипт агента.
///
/// <para>
/// Живёт на выделенном потоке, а не в пуле. Скрипт блокируется на каждом вызове инструмента —
/// ждёт, пока шина мира доедет до главного потока, — и делает это десятки раз подряд; занять
/// этим поток пула на минуты значило бы отобрать его у петли агента, которая на том же пуле и
/// крутится. Процессов одновременно единицы, так что свой поток здесь дешевле любой хитрости.
/// </para>
/// <para>
/// Время меряется секундомером, а не раундовыми часами. Раундовое время читается только на
/// главном потоке (ради него <c>new_timer</c> и марширует туда), а процессу оно нужно лишь для
/// отчёта «сколько работал» — и, в отличие от будильника, потолок обязан наступать даже на паузе.
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

    /// <summary>Читается с потока агента и с главного; пишется только своим потоком.</summary>
    public volatile ScriptStatus Status = ScriptStatus.Running;

    public string? Error { get; private set; }
    public string? Detail { get; private set; }
    public object? Answer { get; private set; }
    public int Calls => Volatile.Read(ref _calls);
    public TimeSpan Elapsed => _watch.Elapsed;
    public Task Finished => _finished.Task;
    public CancellationToken Token => _cts.Token;
    public bool IsRunning => Status == ScriptStatus.Running;

    /// <summary>Сколько инструментов уже позвал; заодно предохранитель от зациклившегося скрипта.</summary>
    public int CountCall() => Interlocked.Increment(ref _calls);

    /// <summary>Вывод скрипта — то, что модель прочитает через <c>bp_get_output</c>.</summary>
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
    /// Только то, что появилось с прошлого чтения.
    ///
    /// Курсор здесь не удобство, а экономия контекста: без него каждый опрос длинного скрипта
    /// заново вкладывал бы в диалог весь его вывод, и модель платила бы за одни и те же строки
    /// столько раз, сколько раз спросила.
    /// </summary>
    public string ReadNew()
    {
        lock (_lock)
        {
            var builder = new StringBuilder();

            if (_dropped > 0)
            {
                builder.Append($"[потеряно строк: {_dropped}]\n");
                _dropped = 0;
            }

            for (; _cursor < _lines.Count; _cursor++)
                builder.Append(_lines[_cursor]).Append('\n');

            return builder.ToString().TrimEnd('\n');
        }
    }

    /// <summary>Хвост вывода, не двигая курсор — для итогового отчёта и строки о завершении.</summary>
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

    /// <summary>Снять процесс. Возврата нет: скрипт узнает об этом отменой на ближайшем срезе.</summary>
    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Сессию уже освободили — процесс и так мёртв.
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
            // Сюда попадает только сбой на нашей стороне: ошибки самого скрипта уже разобраны
            // выше и пришли кодом. Наружу это уходит как internal, без стека — модель с ним всё
            // равно ничего не сделает.
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

    /// <summary>Как процесс называется в отчёте инструменту и в строке SELF.</summary>
    public string StatusWord() => Status switch
    {
        ScriptStatus.Running => "идёт",
        ScriptStatus.Done => "готово",
        ScriptStatus.Failed => "ошибка",
        _ => "снят",
    };
}
