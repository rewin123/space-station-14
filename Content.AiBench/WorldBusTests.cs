using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.Server.AiAgent.Threading;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Шина мира: бюджет, приоритеты, продвижение и то, на каком потоке всё это оказывается.
///
/// <para>
/// Утверждения здесь по возможности на СЧЁТЧИКАХ, а не на миллисекундах. Основание не моё —
/// <see cref="LookCostTests"/> уже объясняет, почему: миллисекунды на сборочной машине меряют
/// железо и шумят от холодного JIT, соседнего процесса и сборки мусора, а число срезов и номер
/// потока не шумят вовсе.
/// </para>
/// </summary>
[TestFixture]
public sealed class WorldBusTests
{
    /// <summary>
    /// Джоб на нужное число срезов, считающий, где его исполняли.
    /// </summary>
    private sealed class CountingJob : IWorldJob
    {
        private readonly int _slices;
        private readonly TaskCompletionSource<int> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountingJob(string what, WorldPriority priority, int slices)
        {
            What = what;
            Priority = priority;
            _slices = slices;
        }

        public string What { get; }
        public WorldPriority Priority { get; }
        public int Steps { get; private set; }
        public readonly List<int> StepThreadIds = new();

        public Task<int> Task => _tcs.Task;

        public bool Step(JobBudget budget)
        {
            Steps++;
            StepThreadIds.Add(Environment.CurrentManagedThreadId);

            // Выбрать бюджет кадра до конца — и это не имитация нагрузки, а способ сделать тест
            // детерминированным. Насос, отдав срез, тут же забирает недоделанную заявку обратно и
            // крутит её дальше, ПОКА бюджет не кончился, — так и задумано. Поэтому джоб, который
            // ничего не делает, отработает все свои срезы в одном кадре, и проверять на нём
            // «растянулось на кадры» бессмысленно: первая версия теста ровно так и промахнулась.
            //
            // Ожидание по самому бюджету, а не по фиксированным миллисекундам: получается ровно
            // один срез на кадр на любой машине, без порога, который надо подбирать.
            while (!budget.Exhausted)
            {
            }

            return Steps >= _slices;
        }

        public void Complete() => _tcs.TrySetResult(Steps);
        public void Fail(Exception e) => _tcs.TrySetException(e);
    }

    /// <summary>
    /// <b>Самый важный тест в этом файле.</b>
    ///
    /// <para>
    /// Продолжение после <c>await</c> обязано уехать с игрового потока. Свойство держится на
    /// <c>TaskCreationOptions.RunContinuationsAsynchronously</c> у TCS внутри
    /// <c>AtomicJob</c>, и до сих пор оно не было покрыто ни одним тестом — при том что это
    /// единственная строчка, отделяющая сервер от того, чтобы многосекундный HTTP-вызов к модели
    /// исполнился прямо в тике.
    /// </para>
    /// <para>
    /// Под шиной свойство стало КРИТИЧНЕЕ, чем было: <c>Complete()</c> теперь зовётся из насоса,
    /// который живёт внутри <c>TickUpdate</c>. Раньше встроенное продолжение попало бы хотя бы в
    /// <c>ProcessPendingTasks</c> до обновления систем; теперь оно попало бы в середину их обхода.
    /// </para>
    /// </summary>
    [Test]
    public async Task ContinuationsDoNotRunOnTheGameThread()
    {
        await using var w = await AiWorld.Create();

        var mainThreadId = await w.Read(() => Environment.CurrentManagedThreadId);

        // Настоящий инструмент через настоящий диспетчер: проверяем боевой путь, а не макет.
        var task = w.System.InvokeToolForTest(w.Brain, "noop", "{\"reason\":\"тест потока\"}");

        int continuationThreadId = 0;
        var observed = task.ContinueWith(_ =>
        {
            continuationThreadId = Environment.CurrentManagedThreadId;
        }, TaskContinuationOptions.ExecuteSynchronously);

        await PoolManager.WaitUntil(w.Pair.Server, () => observed.IsCompleted, maxTicks: 600);
        await observed;

        Assert.That(continuationThreadId, Is.Not.Zero, "продолжение не выполнилось");
        Assert.That(continuationThreadId, Is.Not.EqualTo(mainThreadId),
            "продолжение выполнилось на игровом потоке — значит RunContinuationsAsynchronously " +
            "потеряно, и следующий за await HTTP-вызов к модели встанет прямо в тик");
    }

    /// <summary>
    /// Многосрезовый джоб действительно растягивается на несколько кадров, а не доедается за один.
    ///
    /// Счётчик, а не часы: срезов обязано быть ровно столько, сколько джоб запросил, и каждый —
    /// на игровом потоке.
    /// </summary>
    [Test]
    public async Task ChunkedJobSpansSeveralFrames()
    {
        await using var w = await AiWorld.Create();

        var mainThreadId = await w.Read(() => Environment.CurrentManagedThreadId);
        var job = new CountingJob("тест-дробление", WorldPriority.Normal, slices: 4);

        var before = await w.Read(() => w.System.WorldBusHealth().Deferrals);
        var task = await w.Read(() => w.System.SubmitWorldJobForTest(job, job.Task));

        await PoolManager.WaitUntil(w.Pair.Server, () => task.IsCompleted, maxTicks: 600);
        var steps = await task;
        var after = await w.Read(() => w.System.WorldBusHealth().Deferrals);

        Assert.Multiple(() =>
        {
            Assert.That(steps, Is.EqualTo(4), "джоб должен был отработать ровно четыре среза");

            // Переносы, а не часы: счётчик не шумит и прямо утверждает то, что проверяется —
            // работа пережила границу кадра. Три переноса на четыре среза.
            Assert.That(after - before, Is.EqualTo(3),
                "работа не переносилась между кадрами — дробление не состоялось");

            Assert.That(job.StepThreadIds, Is.All.EqualTo(mainThreadId),
                "срез исполнился вне игрового потока — это обращение к миру из чужого потока");
        });
    }

    /// <summary>
    /// Смерть сессии между срезами роняет заявку как устаревшую, а не доводит её до конца.
    ///
    /// С дроблением это перестаёт быть формальностью: ИИ вполне может быть закарден или убит
    /// между двумя срезами одного обзора, и продолжать трогать мир от его имени нельзя.
    /// </summary>
    [Test]
    public async Task StaleGenerationStopsAJobMidway()
    {
        await using var w = await AiWorld.Create();

        var job = new CountingJob("тест-устаревание", WorldPriority.Normal, slices: 50);
        var task = await w.Read(() => w.System.SubmitWorldJobForTest(job, job.Task));

        // Дать ему начаться — и убедиться, что он ещё далеко от конца, — затем отобрать ядро.
        await PoolManager.WaitUntil(w.Pair.Server, () => job.Steps >= 2, maxTicks: 120);
        Assert.That(job.Steps, Is.LessThan(50), "джоб успел доработать раньше, чем тест вмешался");

        await w.Post(() => w.System.ReleaseAll("тест"));

        await PoolManager.WaitUntil(w.Pair.Server, () => task.IsCompleted, maxTicks: 600);

        Assert.That(async () => await task, Throws.InstanceOf<StaleGenerationException>(),
            "заявка пережила смерть сессии");
        Assert.That(job.Steps, Is.LessThan(50), "джоб доработал до конца, хотя агента уже нет");
    }

    /// <summary>
    /// Счётчики здоровья шины на обычной работе обязаны быть нулями.
    ///
    /// Переполнение означает параллелизм, которого в модуле нет (инструменты вызываются строго
    /// последовательно), поэтому ненулевое значение здесь — сигнал о поломке, а не о нагрузке.
    /// </summary>
    [Test]
    public async Task HealthCountersStayCleanUnderNormalUse()
    {
        await using var w = await AiWorld.Create();
        await w.Spawn("AirlockCommand");

        for (var i = 0; i < 5; i++)
        {
            await w.Invoke("look", "{}");
            await w.Invoke("noop", "{\"reason\":\"тест\"}");
        }

        var (depth, _, _, overflows, _) = await w.Read(() => w.System.WorldBusHealth());

        Assert.Multiple(() =>
        {
            // Ноль в глубине — главное утверждение файла. Оно означает, что учёт заявок сходится:
            // на каждый инкремент при постановке нашёлся ровно один декремент на терминальном
            // пути, сколько бы их ни было (успех, отмена, устаревшее поколение, исключение,
            // отказ ждущего, переполнение). Утечка здесь означала бы, что счётчик глубины врёт, а
            // с ним и защита от переполнения.
            Assert.That(depth, Is.Zero, "в очереди осталась работа после того, как все вызовы завершились");
            Assert.That(overflows, Is.Zero, "очередь переполнялась — откуда-то взялся параллелизм");
        });

        // MaxWaitMs здесь НЕ проверяется, и это осознанно. Это рекорд за всё время жизни процесса,
        // а PoolManager переиспользует сервер между тестами — значит в него натекает ожидание из
        // соседних тестов этого же файла, которые держат джоб пятьдесят кадров НАМЕРЕННО. В
        // одиночку тест проходил, в полном наборе падал на 2453мс; проверялся бы порядок запуска,
        // а не шина. Для `aiagent cost` пожизненный максимум — то, что нужно; для утверждения в
        // тесте нужен был бы счётчик за окно, которого нет.
    }
}
