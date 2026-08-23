using System.Reflection;
using Content.IntegrationTests;

namespace Content.OracleTrace;

/// <summary>
/// Поднимает пул серверов-клиентов для сценариев оракула.
///
/// ПОЧЕМУ здесь свой SetUpFixture, а не переиспользуется
/// <c>Content.IntegrationTests.PoolManagerTestEventHandler</c>:
/// NUnit выполняет SetUpFixture только той сборки, тесты которой он запускает.
/// Когда гоняют Content.OracleTrace, чужой SetUpFixture не сработает и пул
/// останется неинициализированным — падение будет невнятным («Already
/// initialized» либо NRE в GetPair).
///
/// Второе, ради чего это нужно: <see cref="PoolManager.Startup"/> получает
/// нашу сборку как «общую контентную». Без этого Robust не увидит
/// <see cref="OracleTraceSystem"/> и события просто не запишутся — молча,
/// а молчаливый отказ в записи трассы выглядит как «расхождений нет».
/// </summary>
[SetUpFixture]
public sealed class OracleTraceSetUp
{
    [OneTimeSetUp]
    public void Setup()
    {
        PoolManager.Startup(Assembly.GetExecutingAssembly());
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        PoolManager.Shutdown();
    }
}
