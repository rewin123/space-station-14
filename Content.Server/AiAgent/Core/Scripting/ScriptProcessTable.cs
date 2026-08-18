using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// Процессы одной сессии: кто сейчас работает, кто уже кончился и о ком ещё не доложено.
///
/// <para>
/// Очередь завершившихся существует потому, что процесс умирает на своём потоке, а сказать об этом
/// агенту можно только с главного: <c>ObservationQueue.Push</c> зовут из обработчиков событий, и
/// вторая точка входа с чужого потока сломала бы её единственное правило. Поэтому поток кладёт
/// процесс в очередь, а <c>Update</c> её разбирает — тем же приёмом, что и сработавшие будильники.
/// </para>
/// </summary>
public sealed class ScriptProcessTable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ScriptProcess> _all = new();
    private readonly ConcurrentQueue<ScriptProcess> _toReport = new();
    private readonly CancellationTokenSource _life = new();

    private int _nextPid;

    /// <summary>Сколько законченных процессов помнить, чтобы <c>bp_get_output</c> ещё отвечал.</summary>
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

    /// <summary>Завести процесс и пустить его. Хост строит вызывающий — таблица про Lua не знает.</summary>
    public ScriptProcess Start(string code, int maxLines, Func<ScriptProcess, LuaHost> build)
    {
        ScriptProcess process;

        lock (_lock)
        {
            process = new ScriptProcess(++_nextPid, code, maxLines, _life.Token);
            _all[process.Pid] = process;
            Prune();
        }

        // Подписка ДО старта: скрипт может кончиться раньше, чем мы вернёмся из Start.
        process.Finished.ContinueWith(_ => _toReport.Enqueue(process), TaskContinuationOptions.ExecuteSynchronously);

        process.Start(build(process));
        return process;
    }

    /// <summary>Забрать процесс, о завершении которого агенту ещё не сказали.</summary>
    public bool TryTakeReport(out ScriptProcess process) => _toReport.TryDequeue(out process!);

    /// <summary>Снять всё. Зовётся на освобождении сессии и на конце раунда.</summary>
    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var process in _all.Values)
                process.Stop();
        }

        // Отменяем и связанный источник: процесс, застрявший в вызове инструмента, узнает об этом
        // оттуда, а не с ближайшего среза.
        try
        {
            _life.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Строка для SELF: чем агент занят прямо сейчас, кроме собственного хода.</summary>
    public string SelfLine()
    {
        var running = Running();
        if (running.Count == 0)
            return "";

        return string.Join(", ", running.Select(p => $"#{p.Pid} идёт {p.Elapsed.TotalSeconds:0} с"));
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
