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
    /// Сторож на случай, если суита зависнет.
    ///
    /// <para>
    /// Сорок пять минут, а не двадцать (19.08.2026). Двадцати перестало хватать честному прогону:
    /// сценарных тестов стало больше (киборги поддержки, боевые инструменты, витрина отладчика), и
    /// полная суита занимает около двадцати двух минут. Сторож при этом срабатывал на ЗДОРОВОМ
    /// прогоне и валил всё, что не успело пройти, — девяносто с лишним падений с ошибками пула
    /// вместо одной строки про таймаут. Такой сторож хуже отсутствующего: он не ловит зависание,
    /// а маскирует результат.
    /// </para>
    /// <para>
    /// Число надо держать с запасом ко времени прогона, а не подгонять впритык. Если суита снова
    /// подойдёт к порогу — резать её на части по фикстурам, а не двигать порог дальше.
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
