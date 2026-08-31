using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Content.Server.AiAgent.Borg;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Во что обходится разность поля зрения робота — та самая, что считается КАЖДЫЙ ход.
/// </summary>
/// <remarks>
/// В живом раунде 20.08.2026 'observation' стал первой статьёй перерасхода главного потока: 61
/// предупреждение, худшее 42 мс при кадре 33 мс. Из всей сборки наблюдения тяжёлое там ровно
/// одно — <c>BeforeObservation</c>, который у робота считает разность поля зрения; у ядра этого
/// вызова нет вовсе.
/// </remarks>
[TestFixture]
[Category("Scenario")]
public sealed class SightDeltaCostTests
{
    [Test]
    [Explicit("замер, не сторож")]
    public async Task HowExpensiveIsTheDelta()
    {
        await using var w = await AiStation.Create();
        var borg = EntityUid.Invalid;

        await w.Pair.Server.WaitPost(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(sys.TrySpawnBorg(null, out borg, out var placed), Is.True, placed);
            Assert.That(sys.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(30);

        var report = await w.Read(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();

            // Первый прогон прогревает JIT и заполняет базу сравнения.
            sys.SightDeltaCostForTest(borg);

            var worst = 0d;
            var total = 0d;
            var seen = 0;
            var candidates = 0;

            for (var i = 0; i < 10; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var (n, c) = sys.SightDeltaCostForTest(borg);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                total += ms;
                worst = Math.Max(worst, ms);
                seen = n;
                candidates = c;
            }

            return $"кандидатов {candidates}, прошло проверку видимости {seen}: " +
                   $"среднее {total / 10:F1}мс, худшее {worst:F1}мс";
        });

        TestContext.Out.WriteLine("РАЗНОСТЬ: " + report);
    }
}
