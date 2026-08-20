using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Витрина агентов: кто на ней стоит, в каком порядке и что происходит при переклейме.
///
/// <para>
/// Тесты чистые — ни мира, ни сессии, ни сокета. Это и есть довод, по которому
/// <see cref="AgentHandle"/> несёт делегаты, а не ссылку на <c>AgentSession</c>: собрать
/// настоящую сессию в тесте нельзя, а проверять витрину надо.
/// </para>
/// </summary>
[TestFixture]
public sealed class BusDirectoryTests
{
    private static AgentHandle Handle(string id, string name = "Агент") => new()
    {
        Id = id,
        Name = name,
        Brain = 1,
        Round = 7,
        StartedSeq = 0,
        Alive = true,
        Capture = () => null!,
        Roster = () => new AgentRosterEntryDto(id, name, 1, 7, 0, true, "Core", 0, 0, 0, 0, 0, 0, false, null),
        Send = _ => (true, "ок"),
    };

    [Test]
    public void AddThenFindReturnsTheSameHandle()
    {
        var directory = new AgentDirectory();
        var handle = Handle("core");

        Assert.That(directory.Add(handle), Is.True);
        Assert.That(directory.Find("core"), Is.SameAs(handle));
        Assert.That(directory.Find("нет-такого"), Is.Null);
    }

    /// <summary>
    /// Ядро первым, дальше по алфавиту.
    /// </summary>
    /// <remarks>
    /// Порядок задаёт сервер, а не клиент, чтобы стороны не разошлись. Чисто алфавитный поставил
    /// бы <c>combat-1</c> раньше <c>core</c>, и вкладка по умолчанию прыгала бы в зависимости от
    /// того, кто в этом раунде вообще есть.
    /// </remarks>
    [Test]
    public void OrderPutsTheCoreFirst()
    {
        var directory = new AgentDirectory();

        directory.Add(Handle("engineer-1"));
        directory.Add(Handle("combat-1"));
        directory.Add(Handle("core"));
        directory.Add(Handle("combat-2"));

        Assert.That(directory.All.Select(h => h.Id).ToList(),
            Is.EqualTo(new[] { "core", "combat-1", "combat-2", "engineer-1" }));
    }

    /// <summary>
    /// Занятый идентификатор — отказ, а не затирание.
    /// </summary>
    /// <remarks>
    /// Совпадение означает, что два агента пишут в один каталог памяти и один файл диалога.
    /// Витрина — единственное место, где это вообще заметно снаружи, и потому она обязана об
    /// этом сообщить, а не показать одного вместо двух.
    /// </remarks>
    [Test]
    public void AddOnATakenIdIsRefused()
    {
        var directory = new AgentDirectory();
        var first = Handle("borg-1", "Первый");

        Assert.That(directory.Add(first), Is.True);
        Assert.That(directory.Add(Handle("borg-1", "Второй")), Is.False);
        Assert.That(directory.Find("borg-1"), Is.SameAs(first), "второй затёр первого");
    }

    /// <summary>
    /// Снимается только свой хендл.
    /// </summary>
    /// <remarks>
    /// Сценарий из жизни: борга переклеймили в том же тике. Отпускание СТАРОЙ сессии не должно
    /// снимать с витрины НОВОГО агента — иначе живой робот пропадает из отладчика насовсем, а
    /// выглядит это как «он не запустился».
    /// </remarks>
    [Test]
    public void RemoveWithAForeignHandleDoesNothing()
    {
        var directory = new AgentDirectory();
        var stale = Handle("borg-1", "Старый");
        var fresh = Handle("borg-1", "Новый");

        directory.Add(stale);
        Assert.That(directory.Remove("borg-1", stale), Is.True);

        directory.Add(fresh);
        Assert.That(directory.Remove("borg-1", stale), Is.False, "старый хендл снёс нового агента");
        Assert.That(directory.Find("borg-1"), Is.SameAs(fresh));
    }

    /// <summary>Подметание убирает хендлы, за которыми не стоит живой сессии.</summary>
    [Test]
    public void RetainOnlyDropsHandlesWithoutASession()
    {
        var directory = new AgentDirectory();

        directory.Add(Handle("core"));
        directory.Add(Handle("combat-1"));
        directory.Add(Handle("combat-2"));

        directory.RetainOnly(new[] { "core", "combat-2" });

        Assert.That(directory.All.Select(h => h.Id).ToList(), Is.EqualTo(new[] { "core", "combat-2" }));
    }

    /// <summary>
    /// Чтение с чужого потока во время правок с главного.
    /// </summary>
    /// <remarks>
    /// Ровно та ситуация, ради которой витрина и написана: главный поток занимает и отпускает тела,
    /// HTTP-поток в это время собирает ростер. Прежнее решение — обычный <c>Dictionary</c> — на
    /// таком сценарии не бросало исключение, а могло уйти в бесконечный цикл внутри цепочки
    /// корзин, и симптомом был сервер, который отчитывается о живом агенте и перестаёт тикать.
    /// </remarks>
    [Test]
    public void ReadingFromAnotherThreadWhileTheMainThreadMutates()
    {
        var directory = new AgentDirectory();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var failures = new List<string>();

        var writer = Task.Run(() =>
        {
            var n = 0;

            while (!stop.IsCancellationRequested)
            {
                var id = $"borg-{n++ % 8}";
                var handle = Handle(id);

                if (directory.Add(handle))
                    directory.Remove(id, handle);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var handle in directory.All)
                {
                    if (handle == null)
                        failures.Add("в опубликованном массиве null");
                }

                directory.Roster();
                directory.Find("borg-3");
            }
        });

        Assert.That(Task.WhenAll(writer, reader).Wait(TimeSpan.FromSeconds(20)), Is.True,
            "потоки не разошлись за 20 секунд — похоже на зацикливание, а не на медленный тест");

        Assert.That(failures, Is.Empty);
    }
}
