using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// Видит ли агент, что происходит перед его глазом.
///
/// <para>
/// До этих подписок он не видел ничего: слышал рацию и речь у ядра, а мир для него был неподвижной
/// картинкой, которую можно только опросить инструментом <c>look</c>. Дырой это делала не
/// пропущенная драка, а невыполнимая просьба — «когда я вставлю плазму, запусти генератор»
/// упиралась в то, что узнать о вставленной плазме нечем.
/// </para>
/// <para>
/// Половина этого файла — отрицательные проверки, и это не перестраховка. Зрение, которое видит всё
/// подряд по всей станции, — не зрение, а всезнание с красивым форматом; отличает одно от другого
/// ровно то, что дальнее событие в наблюдение НЕ попадает. Поэтому здесь проверяется не только
/// «строка пришла», но и «строки нет, и работа под неё даже не начиналась».
/// </para>
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class WitnessTests
{
    /// <summary>
    /// Идентификатор прототипа, а не строка в вызове: <c>Index&lt;T&gt;("Blunt")</c> запрещён
    /// аналитиком RA0033, и запрет по делу — опечатка в литерале всплыла бы только на прогоне.
    /// </summary>
    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";

    /// <summary>
    /// Остановить петлю, оставив сессию: обработчики наблюдения продолжают наполнять очередь, и
    /// никто не гонится с проверкой за право её осушить.
    /// </summary>
    private static async Task Freeze(AiWorld w)
    {
        await w.Post(() => w.System.GetSession(w.Brain)!.Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);
    }

    /// <summary>
    /// Выбросить всё, что накопилось за подготовку сцены.
    ///
    /// Спавн сущностей сам по себе шумит: человек появляется одетым, то есть с десятком вложений в
    /// контейнеры — и все они рядом с ядром. Без этой чистки проверка «строки нет» ловила бы
    /// собственную подготовку теста.
    /// </summary>
    private static async Task Drain(AiWorld w) =>
        await w.Read(() => w.System.BuildObservationForTest(w.Brain));

    private static async Task<string> Observation(AiWorld w) =>
        await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

    // ------------------------------------------------------------------ проводка

    [Test]
    public async Task Interaction_NearTheEye_ReachesTheAgent()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var user = await w.Spawn("MobHuman", dx: 2);
        var used = await w.Spawn("Crowbar", dx: 2, dy: 1);
        var target = await w.Spawn("SMESBasic", dx: 3);

        await Rename(w, user, "Иван Петров");
        await Drain(w);

        await w.Post(() => w.Pair.Server.System<SharedInteractionSystem>()
            .InteractUsing(user, used, target, default, checkCanInteract: false, checkCanUse: false));

        await w.Pair.Server.WaitRunTicks(3);

        var observation = await Observation(w);

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("OBSERVED предметом"),
                "приложение предмета к предмету агент обязан увидеть: " + observation);
            Assert.That(observation, Does.Contain("Иван Петров"),
                "в строке обязан быть тот, кто это сделал: " + observation);
        });
    }

    [Test]
    public async Task ContainerInsert_NearTheEye_ReachesTheAgent()
    {
        // Тот самый механизм, ради которого задача поставлена: «вставлю плазму — запусти генератор».
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var holder = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        await Drain(w);

        await w.Post(() =>
        {
            var containers = w.Pair.Server.System<SharedContainerSystem>();
            var container = containers.EnsureContainer<Container>(holder, "ai-witness-test");
            containers.Insert(item, container);
        });

        await w.Pair.Server.WaitRunTicks(3);

        var observation = await Observation(w);

        Assert.That(observation, Does.Contain("OBSERVED вложил"),
            "вложение предмета в устройство агент обязан увидеть: " + observation);
    }

    [Test]
    public async Task Damage_NearTheEye_ReachesTheAgent()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var victim = await w.Spawn("MobHuman", dx: 2);
        var attacker = await w.Spawn("MobHuman", dx: 3);

        await Rename(w, victim, "Мария Сидорова");
        await Rename(w, attacker, "Иван Петров");

        await Drain(w);
        await Hurt(w, victim, attacker, 10);

        var observation = await Observation(w);

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("OBSERVED урон"),
                "нанесённый рядом урон агент обязан увидеть: " + observation);
            Assert.That(observation, Does.Contain("Иван Петров").And.Contain("Мария Сидорова"),
                "в строке обязаны быть оба — и кто, и кому: " + observation);
        });
    }

    [Test]
    public async Task Healing_IsNotReportedAsDamage()
    {
        // Обратная сторона: лечение проходит по тому же событию, и без проверки знака агент
        // докладывал бы о медике, который «бьёт» пациента.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var patient = await w.Spawn("MobHuman", dx: 2);
        var medic = await w.Spawn("MobHuman", dx: 3);

        await Hurt(w, patient, medic, 20);
        await Drain(w);
        await Hurt(w, patient, medic, -10);

        var observation = await Observation(w);

        Assert.That(observation, Does.Not.Contain("OBSERVED урон"),
            "лечение — не урон, и путать их в одной строке нельзя: " + observation);
    }

    [Test]
    public async Task OpeningDoor_ReportsOnce_NotTwice()
    {
        // Регрессия по боевой сессии 16 августа. Дверь идёт Closed → Opening → Open, событие
        // прилетает дважды, а ярлык у обоих состояний был один — агент получал две неотличимые
        // строки и честно тратил ход на «повторное событие, уже учтено». Семь ходов из сорока двух
        // ушли на этот пересказ самому себе, и заметить это можно было только в логе живого раунда.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var door = await w.Spawn("Airlock", dx: 2);
        await Drain(w);

        await w.Post(() => w.Pair.Server.System<SharedDoorSystem>().StartOpening(door));

        // Достаточно тиков, чтобы анимация доиграла до конца: ловим ОБА события, а не только первое.
        await w.Pair.Server.WaitRunTicks(60);

        var observation = await Observation(w);
        var lines = 0;

        foreach (var line in observation.Split('\n'))
        {
            if (line.StartsWith("OBSERVED дверь", StringComparison.Ordinal))
                lines++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(lines, Is.EqualTo(1),
                "один проход двери — одна строка; промежуточное состояние докладывать нельзя: "
                + observation);
            Assert.That(observation, Does.Contain("дверь: открылась"),
                "докладывается конечное состояние, а не начальное: дверь можно перевести в Open "
                + "без анимации, и тогда Opening не придёт вовсе: " + observation);
        });
    }

    [Test]
    public async Task MobDeath_NearTheEye_ReachesTheAgent()
    {
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var victim = await w.Spawn("MobHuman", dx: 2);
        await Rename(w, victim, "Мария Сидорова");

        await Drain(w);

        await w.Post(() => w.Pair.Server.System<MobStateSystem>().ChangeMobState(victim, MobState.Dead));
        await w.Pair.Server.WaitRunTicks(3);

        var observation = await Observation(w);

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("OBSERVED состояние"),
                "смерть человека в кадре агент обязан увидеть: " + observation);
            Assert.That(observation, Does.Contain("мёртв").And.Contain("Мария Сидорова"),
                "строка обязана называть и что случилось, и с кем: " + observation);
        });
    }

    // ------------------------------------------------------------------ паритет

    [Test]
    public async Task Interaction_FarFromTheEye_IsNotReported()
    {
        // Главный тест файла. Зрение, которое видит всю станцию, — это всезнание, и отличается оно
        // от зрения ровно тем, что здесь проверяется.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var user = await w.Spawn("MobHuman", dx: 40);
        var used = await w.Spawn("Crowbar", dx: 40, dy: 1);
        var target = await w.Spawn("SMESBasic", dx: 41);

        await Drain(w);

        var before = await w.Read(() => w.System.WitnessedCount());

        await w.Post(() => w.Pair.Server.System<SharedInteractionSystem>()
            .InteractUsing(user, used, target, default, checkCanInteract: false, checkCanUse: false));

        await w.Pair.Server.WaitRunTicks(3);

        var after = await w.Read(() => w.System.WitnessedCount());
        var observation = await Observation(w);

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Not.Contain("OBSERVED"),
                "событие в сорока тайлах от глаза агент видеть не может: " + observation);

            // Счётчик, а не текст. Отсутствие строки одинаково хорошо объясняется и воротами, и
            // тем, что строка не построилась; ноль здесь означает, что ворота отказали до всякой
            // работы, и только это и есть утверждение о паритете.
            Assert.That(after - before, Is.Zero,
                $"наблюдение всё-таки выпустило {after - before} строк о том, чего глаз не видит");
        });
    }

    // ------------------------------------------------------------------ опознание

    [Test]
    public async Task ObservedHandle_MatchesTheOneLookGives()
    {
        // Согласованность мепперов. Разойдись реестры — и одно и то же устройство стало бы для
        // агента двумя разными вещами: одной из обзора, другой из наблюдения.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var device = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        // Сначала обзор — он и минтит хендл. Наблюдение обязано взять уже существующий, а не
        // завести второй.
        var expected = await w.Handle(device);
        await Drain(w);

        await w.Post(() =>
        {
            var containers = w.Pair.Server.System<SharedContainerSystem>();
            containers.Insert(item, containers.EnsureContainer<Container>(device, "ai-witness-test"));
        });

        await w.Pair.Server.WaitRunTicks(3);

        var observation = await Observation(w);

        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.Not.Empty, "хендл не выдан — тест ничего не проверяет");
            Assert.That(observation, Does.Contain(expected),
                $"наблюдение обязано звать устройство тем же хендлом «{expected}», что и обзор: {observation}");
        });
    }

    [Test]
    public async Task ObservedHandle_ResolvesBackToTheEntity()
    {
        // Строка, по которой нельзя действовать, бесполезна: весь смысл хендла в том, что агент
        // вызывает по нему инструмент, не разыскивая вещь обзором заново.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var device = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        await Drain(w);

        await w.Post(() =>
        {
            var containers = w.Pair.Server.System<SharedContainerSystem>();
            containers.Insert(item, containers.EnsureContainer<Container>(device, "ai-witness-test"));
        });

        await w.Pair.Server.WaitRunTicks(3);

        var observation = await Observation(w);
        var handle = HandleFrom(observation, "OBSERVED вложил");

        Assert.That(handle, Is.Not.Null, "в строке наблюдения не нашлось ни одного хендла: " + observation);

        var resolved = await w.Read(() =>
        {
            var session = w.System.GetSession(w.Brain)!;
            return session.Handles.TryResolve(handle!, out var uid) ? uid : EntityUid.Invalid;
        });

        Assert.That(resolved, Is.EqualTo(item).Or.EqualTo(device),
            $"хендл «{handle}» из строки не указывает ни на вложенное, ни на то, куда вложили");
    }

    // ------------------------------------------------------------------ очередь

    [Test]
    public async Task ObservedFlood_DoesNotEvictSpeech()
    {
        // Очередь выбрасывает старейшее безотносительно вида, а наблюдений приходит поток. Без
        // отдельного потолка возня в кадре выталкивала бы из очереди обращение по рации — то есть
        // агент переставал бы слышать просьбы ровно тогда, когда их больше всего.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var cfg = w.Pair.Server.ResolveDependency<IConfigurationManager>();
        await w.Post(() => cfg.SetCVar(AiCVars.ObserveBuffer, 5));

        var holder = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        await Drain(w);

        await w.Post(() =>
        {
            if (!w.System.InjectRadio("Binary", "ИИ, открой мне дверь в атмос", out var why))
                throw new InvalidOperationException("реплику не удалось передать: " + why);
        });

        // Двадцать вложений на пятёрку потолка: строка по рации переживёт это, только если
        // подрезается своя категория, а не старейшее вообще.
        await w.Post(() =>
        {
            var containers = w.Pair.Server.System<SharedContainerSystem>();
            var container = containers.EnsureContainer<Container>(holder, "ai-witness-test");

            for (var i = 0; i < 20; i++)
            {
                containers.Insert(item, container);
                containers.Remove(item, container);
            }
        });

        await w.Pair.Server.WaitRunTicks(3);

        var observation = await Observation(w);

        await w.Post(() => cfg.SetCVar(AiCVars.ObserveBuffer, 400));

        Assert.Multiple(() =>
        {
            Assert.That(observation, Does.Contain("открой мне дверь в атмос"),
                "поток наблюдений выбил из очереди обращение по рации: " + observation);
            Assert.That(observation, Does.Contain("OBSERVED"),
                "наблюдения тоже должны остаться — подрезается только лишнее: " + observation);
        });
    }

    // ------------------------------------------------------------------ объём

    [Test]
    public async Task OnePersonAppearing_CostsABoundedNumberOfLines()
    {
        // Замер объёма, он же сторож от шланга.
        //
        // Троттлинга у наблюдения нет по решению владельца, а контекст модели 256k, так что сотня
        // строк за ход ничего не ломает. Ломало бы другое: подписка на что-нибудь, что срабатывает
        // на каждое движение, — и тогда один человек, вошедший в кадр, стоил бы не десятков строк, а
        // тысяч. Число печатается, чтобы им можно было пользоваться, а потолок стоит там, где ловит
        // катастрофу, а не там, где хочется видеть результат.
        //
        // Появление одетого человека — самый дорогой одиночный случай из дешёвых: снаряжение едет
        // вложениями в контейнеры, и все они рядом с глазом.
        await using var w = await AiWorld.Create();
        await Freeze(w);
        await Drain(w);

        var before = await w.Read(() => w.System.WitnessedCount());
        await w.Spawn("MobHuman", dx: 2);
        var after = await w.Read(() => w.System.WitnessedCount());

        var lines = after - before;
        TestContext.Out.WriteLine($"одетый человек, вошедший в кадр: {lines} строк наблюдения");

        Assert.Multiple(() =>
        {
            Assert.That(lines, Is.GreaterThan(0),
                "появление человека в кадре не дало ни одной строки — подписки не работают, " +
                "и остальные тесты этого файла проверяют неизвестно что");

            Assert.That(lines, Is.LessThan(200),
                $"один человек стоил {lines} строк наблюдения — похоже, подписались на что-то, " +
                "что срабатывает на каждое движение");
        });
    }

    // ------------------------------------------------------------------ реактивность

    [Test]
    public async Task WhatItSaw_IsActionableInTheSameTurn()
    {
        // Ради этого задача и ставилась. Просьба «когда я вставлю плазму — запусти генератор» была
        // невыполнима не потому, что агент ленив, а потому, что узнать о вставленной плазме ему было
        // нечем: оставалось переспрашивать по рации. Здесь проверяется вся цепочка целиком —
        // событие в мире, строка наблюдения, хендл из этой строки, вызов инструмента по нему и
        // ответ про ТУ САМУЮ сущность.
        //
        // Модель подменена клиентом, который читает строку и действует по ней механически. Это
        // намеренно: проверяется проводка, а не сообразительность — сообразительность меряется
        // живыми прогонами, и её нельзя ставить в покоммитный тест.
        var llm = new ActsOnWhatItSees();

        await using var w = await AiWorld.CreateWith(llm);

        var device = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        await w.Post(() =>
        {
            var containers = w.Pair.Server.System<SharedContainerSystem>();
            containers.Insert(item, containers.EnsureContainer<Container>(device, "ai-witness-test"));
        });

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && llm.ToolResult == null)
            await w.Pair.Server.WaitRunTicks(15);

        Assert.Multiple(() =>
        {
            Assert.That(llm.CalledHandle, Is.Not.Null,
                "агент не увидел вложения либо не смог адресовать увиденное хендлом");
            Assert.That(llm.ToolResult, Is.Not.Null,
                $"вызов по хендлу «{llm.CalledHandle}» не вернулся ответом");
            Assert.That(llm.ToolResult, Does.Contain("SMES").IgnoreCase,
                $"хендл из строки наблюдения привёл не к тому устройству: {llm.ToolResult}");
        });
    }

    /// <summary>
    /// Подставная модель, которая делает ровно одно: увидев строку о вложении, вызывает инструмент
    /// по хендлу из этой строки.
    ///
    /// Ничего не решает и не выбирает — потому и годится в покоммитный прогон. Всё, что она
    /// доказывает: хендл, приехавший в наблюдении, работает как адрес без единого промежуточного
    /// <c>look</c>.
    /// </summary>
    private sealed class ActsOnWhatItSees : Content.Server.AiAgent.Llm.ILlmClient
    {
        public string? CalledHandle;
        public string? ToolResult;

        public Task<Content.Server.AiAgent.Llm.LlmResponse> ChatAsync(
            IReadOnlyList<Content.Server.AiAgent.Llm.ChatMessageDto> messages,
            IReadOnlyList<Content.Server.AiAgent.Llm.ToolDto> tools,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var last = messages[^1];

            if (last.Role == "tool" && ToolResult == null)
                ToolResult = last.Content;

            if (CalledHandle == null)
            {
                for (var i = messages.Count - 1; i >= 0; i--)
                {
                    if (messages[i].Role != "user")
                        continue;

                    // Второй участник — то, КУДА вложили: именно по нему агенту и предстоит
                    // действовать в просьбе вида «вставлю плазму — запусти генератор».
                    var handle = HandleFrom(messages[i].Content ?? "", "OBSERVED вложил", segment: 2);
                    if (handle == null)
                        continue;

                    CalledHandle = handle;

                    return Task.FromResult(new Content.Server.AiAgent.Llm.LlmResponse(
                        null,
                        new[]
                        {
                            new Content.Server.AiAgent.Llm.ToolCallDto
                            {
                                Id = "call_witness",
                                Type = "function",
                                Function = new Content.Server.AiAgent.Llm.FunctionCallDto
                                {
                                    Name = "inspect",
                                    Arguments = $$"""{"handle":"{{handle}}"}""",
                                },
                            },
                        },
                        100, 90, 10, 0.1));
                }
            }

            return Task.FromResult(new Content.Server.AiAgent.Llm.LlmResponse(
                string.Empty, Array.Empty<Content.Server.AiAgent.Llm.ToolCallDto>(), 100, 100, 1, 0.01));
        }

        public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(131072);
    }

    [Test]
    public async Task UnreadWindow_KeepsSpeechAheadOfWhatWasSeen()
    {
        // Окно «непрочитанного» приклеивается к КАЖДОМУ ответу инструмента: пока модель ведёт
        // многошаговый ход, она иначе глуха, и бот, отвечающий на вопрос, которого не слышал,
        // читается как сломанный. Окно маленькое — шесть строк, — и до наблюдений хвост очереди был
        // репликами просто потому, что ничего другого в ней не лежало. Теперь в ней поток, и без
        // предпочтения слов окно превратилось бы в шесть чужих движений, вытеснив ровно ту реплику,
        // ради которой заведено.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var holder = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        await Drain(w);

        await w.Post(() =>
        {
            if (!w.System.InjectRadio("Binary", "ИИ, ответь мне", out var why))
                throw new InvalidOperationException("реплику не удалось передать: " + why);
        });

        await w.Post(() =>
        {
            var containers = w.Pair.Server.System<SharedContainerSystem>();
            var container = containers.EnsureContainer<Container>(holder, "ai-witness-test");

            for (var i = 0; i < 30; i++)
            {
                containers.Insert(item, container);
                containers.Remove(item, container);
            }
        });

        await w.Pair.Server.WaitRunTicks(3);

        var unread = await w.Read(() =>
            string.Join('\n', w.System.GetSession(w.Brain)!.Queue.PeekUnread(6)));

        Assert.That(unread, Does.Contain("ответь мне"),
            "шестьдесят наблюдений вытеснили реплику из окна непрочитанного: " + unread);
    }

    // ------------------------------------------------------------------ подсобное

    /// <summary>
    /// Переименовать существо так, чтобы новое имя увидел и агент.
    ///
    /// Одного <c>SetEntityName</c> мало, и это не мелочь теста, а свойство продукта: наблюдение
    /// зовёт людей через <c>Identity.Name</c> — то самое имя, которое видит игрок на экране, — а оно
    /// живёт на отдельной сущности личности и обновляется отложенно. Именно поэтому человек в маске
    /// приходит в строке как «Unknown», и это правильно: агент видит фигуру, а не паспорт.
    /// </summary>
    private static async Task Rename(AiWorld w, EntityUid uid, string name)
    {
        await w.Post(() =>
        {
            w.Pair.Server.System<MetaDataSystem>().SetEntityName(uid, name);
            w.Pair.Server.System<Content.Shared.IdentityManagement.IdentitySystem>().QueueIdentityUpdate(uid);
        });

        await w.Pair.Server.WaitRunTicks(3);
    }

    /// <summary>Нанести (или снять) урон от чужого лица — тот же путь, которым бьёт оружие.</summary>
    private static async Task Hurt(AiWorld w, EntityUid victim, EntityUid origin, int amount)
    {
        await w.Post(() =>
        {
            var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();
            var spec = new DamageSpecifier(
                protoMan.Index(Blunt),
                FixedPoint2.New(amount));

            w.Pair.Server.System<DamageableSystem>().TryChangeDamage(victim, spec, origin: origin);
        });

        await w.Pair.Server.WaitRunTicks(3);
    }

    /// <summary>
    /// Выкусить хендл из строки наблюдения с заданным началом.
    /// </summary>
    /// <param name="segment">
    /// Какой участник по счёту, начиная с первого. Формат строки:
    /// <c>OBSERVED &lt;что&gt; | &lt;хендл&gt; &lt;имя&gt; | … | Δ(..) (..)</c> — участники идут в
    /// порядке «кто, чем, над чем», так что у вложения первый это вложенное, а второй — то, куда
    /// вложили.
    /// </param>
    private static string? HandleFrom(string observation, string prefix, int segment = 1)
    {
        foreach (var line in observation.Split('\n'))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length <= segment)
                continue;

            var space = parts[segment].IndexOf(' ');
            return space <= 0 ? parts[segment] : parts[segment][..space];
        }

        return null;
    }
}
