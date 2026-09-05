using System;
using System.Threading.Tasks;
using Content.IntegrationTests;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Starts and stops the server/client pool for this assembly.
///
/// Upstream's <c>PoolManagerTestEventHandler</c> does the same job for Content.IntegrationTests,
/// but a <c>[SetUpFixture]</c> only applies within the assembly that declares it — so a suite in
/// its own project gets "Pool manager has not been initialized" on the very first test until it
/// brings its own.
/// </summary>
[SetUpFixture]
public sealed class BenchSetup
{
    /// <summary>
    /// A watchdog in case the suite hangs.
    ///
    /// <para>
    /// Forty-five minutes, not twenty (19.08.2026). Twenty stopped being enough for an honest run:
    /// the number of scenario tests grew (support borgs, combat tools, the debugger roster), and the
    /// full suite now takes around twenty-two minutes. The watchdog was firing on a HEALTHY run and
    /// failing everything that had not finished yet — ninety-plus failures with pool errors instead
    /// of a single timeout line. A watchdog like that is worse than none at all: it does not catch a
    /// hang, it masks the result.
    /// </para>
    /// <para>
    /// The number needs headroom over the run time, not a tight fit. If the suite approaches the
    /// threshold again, split it into fixtures rather than pushing the threshold further out.
    /// </para>
    /// </summary>
    private static TimeSpan TotalTimeLimit => TimeSpan.FromMinutes(45);

    [OneTimeSetUp]
    public void Setup()
    {
        PoolManager.Startup();

        // Same watchdog as upstream: if the suite wedges, shut the pool down rather than letting
        // the run hang until the CI job is killed with no output at all.
        _ = Task.Delay(TotalTimeLimit).ContinueWith(_ =>
        {
            TestContext.Error.WriteLine($"\n\n{nameof(BenchSetup)}: тесты идут слишком долго, останавливаю пул.\n\n");
            PoolManager.Shutdown();
        });
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        PoolManager.Shutdown();
    }
}
