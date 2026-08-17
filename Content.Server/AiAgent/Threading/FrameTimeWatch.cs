using System;
using System.Collections.Generic;
using System.Globalization;

namespace Content.Server.AiAgent.Threading;

/// <summary>
/// Сколько на самом деле длится тик, в перцентилях, раз в тридцать секунд.
///
/// <para>
/// Заведено потому, что мерить отставание сервера было нечем. Единственный сигнал, движковый
/// <c>MainLoop: Cannot keep up!</c>, <b>троттлится до одного раза в 15 секунд</b>
/// (<c>Robust.Shared/Timing/GameLoop.cs:178</c>). В боевом журнале за 16.08 их 203 штуки за
/// одиннадцать часов, и 50 промежутков из 59 — ровно по пятнадцать секунд. То есть троттл был
/// насыщен, и сервер отставал почти непрерывно; но по этому же счётчику невозможно отличить
/// непрерывное отставание от двухсот отдельных всплесков. Считать эти строки инцидентами нельзя,
/// это нижняя граница, и разница между «до» и «после» на ней не видна.
/// </para>
/// <para>
/// Здесь считается то, что чувствует игрок: промежуток по настенным часам между соседними
/// тиками. Ничего не троттлится, ничего не теряется, и p99 — это ровно «худший тик из сотни»,
/// число, которое можно предъявить до и после правки.
/// </para>
/// <para>
/// <b>Не</b> <c>IGameTiming.RealFrameTime</c>: тот считает период итерации главного цикла
/// (<c>GameTiming.StartFrame</c>), а цикл крутится намного чаще, чем тикает — на пустом лобби
/// около 1400 оборотов в секунду при тридцати тиках. Первый же замер по нему дал p50=0.7мс и
/// выглядел прекрасно, не имея отношения к делу.
/// </para>
/// <para>
/// Кольцо, а не полная выборка: окно в тридцать секунд при тикрейте 30 — это 900 замеров, и
/// перцентили нужны за это окно, а не за смену. Сортировка происходит раз в тридцать секунд, на
/// девятистах <c>double</c>, и в тик не попадает.
/// </para>
/// </summary>
public sealed class FrameTimeWatch
{
    /// <summary>Окно отчёта. Тридцать секунд — как у отчёта наблюдателя, чтобы строки в журнале шли парами.</summary>
    private const float ReportSeconds = 30f;

    private readonly List<double> _samples = new(1024);
    private float _since;

    /// <summary>Длительность тика, к которой всё сравнивается. Пересчитывается при смене тикрейта.</summary>
    public double TickPeriodMs { get; set; } = 1000.0 / 30.0;

    /// <summary>
    /// Во сколько раз период должен превысить номинал, чтобы считаться опозданием.
    ///
    /// Полтора, а не единица, и это не смягчение порога, а исправление ошибки. Здоровый цикл спит
    /// ровно до следующего тика, поэтому измеренный период колеблется ВОКРУГ 33.3мс, и строгое
    /// «больше 33.3» отсекает примерно половину замеров на совершенно здоровом сервере. Первый же
    /// боевой замер это и показал: p50=33.3, p95=33.4, p99=34.6 — и «49.5% выше тика», то есть
    /// метрика, которая всегда показывает половину и ничего не значит.
    ///
    /// Полтора периода (50мс при тикрейте 30) — это уже пропущенный кадр, а не дрожание.
    /// </summary>
    private const double LateFactor = 1.5;

    private double LateAboveMs => TickPeriodMs * LateFactor;

    /// <summary>Сколько тиков за всё время наблюдения опоздали (см. <see cref="LateFactor"/>).</summary>
    public long Overruns { get; private set; }

    public long Ticks { get; private set; }

    /// <summary>Последний отчёт, для <c>aiagent status</c>.</summary>
    public string Last { get; private set; } = "—";

    /// <summary>
    /// Сколько реального времени накопило последнее закрытое окно, миллисекунды.
    ///
    /// Нужно затем, что делить затраты агента на «30000 мс» было бы неверно ровно в тех случаях,
    /// ради которых всё и меряется: окно закрывается по накопленному <b>симуляционному</b> времени
    /// (900 тиков при тикрейте 30), а когда сервер отстаёт, реального времени проходит больше.
    /// Именно тогда доля и завышалась бы — то есть измерение врало бы в пользу вывода «виноват ИИ».
    /// </summary>
    public double WindowRealMs { get; private set; }

    /// <summary>
    /// Записать тик и, если окно закрылось, вернуть строку отчёта. Иначе null.
    /// </summary>
    public string? Tick(float frameTime, double tickPeriodMs)
    {
        Ticks++;
        _samples.Add(tickPeriodMs);

        if (tickPeriodMs > LateAboveMs)
            Overruns++;

        _since += frameTime;
        if (_since < ReportSeconds)
            return null;

        _since = 0;

        if (_samples.Count == 0)
            return null;

        _samples.Sort();

        var p50 = Pick(_samples, 0.50);
        var p95 = Pick(_samples, 0.95);
        var p99 = Pick(_samples, 0.99);
        var max = _samples[^1];

        // Доля опоздавших тиков — за окно, а не за всё время. Именно она отвечает на вопрос
        // «стало лучше или нет» одним числом.
        var late = 0;
        WindowRealMs = 0;

        foreach (var ms in _samples)
        {
            WindowRealMs += ms;
            if (ms > LateAboveMs)
                late++;
        }

        var share = 100.0 * late / _samples.Count;

        Last = string.Create(CultureInfo.InvariantCulture,
            $"тик за {WindowRealMs / 1000.0:F0}с: n={_samples.Count} p50={p50:F1} p95={p95:F1} " +
            $"p99={p99:F1} max={max:F1}мс, опозданий (>{LateAboveMs:F0}мс) {share:F1}%");

        _samples.Clear();
        return Last;
    }

    private static double Pick(List<double> sorted, double q)
    {
        var i = (int)Math.Ceiling(q * sorted.Count) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }
}
