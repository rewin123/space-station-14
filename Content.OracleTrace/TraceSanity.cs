using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Content.OracleTrace;

/// <summary>
/// Проверка здравости записанной трассы.
///
/// Она нужна не ради самой двери. Дампер, который молча пишет пустые снимки
/// или теряет тик, выглядит ровно как «расхождений нет» — и такой оракул хуже,
/// чем никакого. Поэтому у каждой дверной трассы спрашивается то же самое, что
/// у оригинального AirlockTest.OpenCloseDestroyTest: дверь была закрыта, стала
/// Opening в тот тик, когда по ней щёлкнули, и стала Open ровно через
/// OpenTimeOne — величину, взятую из самой же трассы, а не вписанную сюда рукой.
/// </summary>
public static class TraceSanity
{
    public static void AssertDoorOpens(IReadOnlyList<string> lines, ushort tickRate, string scenario)
    {
        var timeline = new List<(int Tick, string State)>();
        var times = new Dictionary<string, double>();

        foreach (var raw in lines)
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var tick = root.GetProperty("t").GetInt32();

            if (!root.TryGetProperty("e", out var ents))
                continue;

            foreach (var snapshot in ents.EnumerateArray())
            {
                if (snapshot[1].GetString() != "DoorComponent")
                    continue;

                var fields = snapshot[2];
                foreach (var key in new[] { "openTimeOne", "closeTimeTwo" })
                {
                    if (times.ContainsKey(key) || !fields.TryGetProperty(key, out var v))
                        continue;

                    times[key] = v.ValueKind == JsonValueKind.String
                        ? double.Parse(v.GetString()!, CultureInfo.InvariantCulture)
                        : v.GetDouble();
                }

                if (!fields.TryGetProperty("state", out var state))
                    continue;

                var name = state.GetString();
                if (timeline.Count == 0 || timeline[^1].State != name)
                    timeline.Add((tick, name));
            }
        }

        Assert.That(timeline, Is.Not.Empty, $"{scenario}: в трассе нет ни одного снимка DoorComponent");
        Assert.That(times.ContainsKey("openTimeOne") && times.ContainsKey("closeTimeTwo"), Is.True,
            $"{scenario}: в \"observe\" нет openTimeOne/closeTimeTwo, а без них нечем проверить, на тех ли тиках открылась дверь");

        var states = timeline.Select(x => x.State).ToArray();
        var closed = Array.IndexOf(states, "Closed");
        Assert.That(closed, Is.GreaterThanOrEqualTo(0), $"{scenario}: дверь никогда не была Closed. Хроника: {Describe(timeline)}");

        var opening = Array.IndexOf(states, "Opening", closed);
        Assert.That(opening, Is.GreaterThan(closed), $"{scenario}: за Closed не последовало Opening. Хроника: {Describe(timeline)}");

        var open = Array.IndexOf(states, "Open", opening);
        Assert.That(open, Is.GreaterThan(opening), $"{scenario}: за Opening не последовало Open. Хроника: {Describe(timeline)}");

        // Сколько тиков занимает Opening -> Open, выводится из самого движка, а
        // не подгоняется под замер:
        //
        //   SharedDoorSystem.NextState делит переход НАДВОЕ. Сначала, через
        //   OpenTimeOne, срабатывает OnPartialOpen — дверь становится проходимой,
        //   но состояние ещё Opening. Второй отрезок OnPartialOpen отмеряет
        //   полем CloseTimeTwo (да, именно Close* на открытии — так в оригинале,
        //   SharedDoorSystem.cs:392), и только после него SetState(Open).
        //
        //   Сравнение в Update строгое: NextStateChange < time. Значит на
        //   отрезке d секунд дверь стоит floor(d * тикрейт) тиков и переключается
        //   на следующем — floor(d*rate)+1.
        //
        // Ровно этого перехода ждёт AirlockTest.OpenCloseDestroyTest своим
        // WaitUntil(State == Open), не называя номера тика.
        var expected = TicksFor(times["openTimeOne"], tickRate) + TicksFor(times["closeTimeTwo"], tickRate);
        var actual = timeline[open].Tick - timeline[opening].Tick;

        Assert.That(actual, Is.EqualTo(expected),
            $"{scenario}: Opening -> Open занял {actual} тиков, а openTimeOne={times["openTimeOne"]} + " +
            $"closeTimeTwo={times["closeTimeTwo"]} при тикрейте {tickRate} дают {expected}. Хроника: {Describe(timeline)}");
    }

    /// <summary>Тиков до срабатывания отрезка длиной <paramref name="seconds"/> при строгом сравнении времени.</summary>
    private static int TicksFor(double seconds, ushort tickRate)
        => (int)Math.Floor(seconds * tickRate) + 1;

    private static string Describe(IEnumerable<(int Tick, string State)> timeline)
        => string.Join(" -> ", timeline.Select(x => $"t{x.Tick}:{x.State}"));
}
