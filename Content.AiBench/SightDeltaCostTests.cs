using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Content.Server.AiAgent.Borg;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// What a borg's field-of-view delta costs — the very thing computed on EVERY turn.
/// </summary>
/// <remarks>
/// On a live round on 20.08.2026, 'observation' became the top offender for main-thread overrun: 61
/// warnings, worst case 42ms against a 33ms frame. Of the whole observation build, exactly one part
/// is heavy — <c>BeforeObservation</c>, which computes the borg's field-of-view delta; the core
/// never makes this call at all.
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

            // The first run warms up the JIT and fills in the comparison baseline.
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
