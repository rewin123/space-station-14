using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Borg;
using Content.Server.AiAgent.Tools;
using Content.Shared.Damage.Systems;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// Шестеро боевых на одну станцию: имена, свои-чужие и честный промах.
///
/// <para>
/// Все три проверки родились из одного живого раунда 305 (01.09.2026), и общее у них то, что
/// каждая поломка молчала. Одно имя на шестерых выглядело как «роботы тупят», расстрел собрата —
/// как «модель сошла с ума», промах мимо цели — как «киборги стоят». Ни одна не давала ни строки
/// ошибки: инструменты честно отвечали «ок», а в игре ничего не происходило.
/// </para>
/// <para>
/// Стенд — настоящая станция (<see cref="AiStation"/>), потому что все три вопроса про мир:
/// сколько корпусов встало у маяка, что видно в <c>look</c> и достаёт ли клинок до цели. На
/// синтетическом гриде ни один из них не задать.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgSquadTests
{
    private const string CombatProto = "AiBorgCombatChassis";

    /// <summary>
    /// Каждому боевому корпусу — своё имя, а не шесть «Клинов».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Одно имя на всех ломает не косметику, а адресацию. Приказ «Клин, иди в бар» уходит в общий
    /// эфир, и каждый из шести принимает его на свой счёт: роботы либо идут толпой на одну цель,
    /// либо стоят, выясняя между собой, кого имели в виду. Номер Si тут не помогает — его выдаёт
    /// движок при спавне, в приказах экипажа он не звучит и в промпте не закреплён.
    /// </para>
    /// <para>
    /// Проверяется именно связь «номер каталога → имя», а не просто различность: имя обязано быть
    /// функцией от <c>AgentId</c>, иначе робот, восстановивший после рестарта каталог
    /// <c>combat-3</c>, отзовётся на чужое имя и заберёт себе чужие заметки.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CombatBorgs_EachGetsItsOwnName()
    {
        await using var w = await AiStation.Create();

        var claimed = new List<(string Id, string Name)>();
        var pool = new List<string>();

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            // ТРИ, а не шесть: на стенде уже занято ядро, а общий потолок ai.max_agents по
            // умолчанию равен четырём — четвёртый борг честно получает отказ в StartSession.
            // Поднимать потолок ради теста незачем: связь «номер каталога → имя из пула»
            // проверяется на трёх ровно так же, как на шести.
            for (var i = 0; i < 3; i++)
            {
                Assert.That(system.TrySpawnBorg(null, out var borg, out var placed, CombatProto), Is.True,
                    $"робот {i}: не удалось поставить — {placed}");

                Assert.That(system.TryClaim(borg, out var why), Is.True, $"робот {i}: захват не удался — {why}");

                var comp = w.Ent.GetComponent<AiBorgComponent>(borg);
                claimed.Add((comp.AgentId, comp.AgentName));

                if (pool.Count == 0)
                    pool.AddRange(comp.AgentNames);
            }
        });

        await w.Pair.Server.WaitRunTicks(5);

        Assert.Multiple(() =>
        {
            Assert.That(pool, Is.Not.Empty,
                "у боевого прототипа пуст agentNames — имя снова одно на всех");

            Assert.That(claimed.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(claimed.Count),
                $"имена совпали: {string.Join(", ", claimed.Select(c => $"{c.Id}={c.Name}"))}");

            foreach (var (id, name) in claimed)
            {
                var n = int.Parse(id[(id.LastIndexOf('-') + 1)..]);

                Assert.That(name, Is.EqualTo(pool[(n - 1) % pool.Count]),
                    $"«{id}» получил «{name}», а по номеру ему полагается «{pool[(n - 1) % pool.Count]}»");
            }
        });
    }

    /// <summary>
    /// В промпте каждого робота перечислены остальные — поимённо и без него самого.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без этого списка робот не может отличить своего от человека в принципе: в <c>look</c> и в
    /// наблюдениях чужой корпус приходит строкой того же вида, что и человек — «crew-4 Имя (Si-…)
    /// | Alive». В раунде 305 это стоило шестнадцати выстрелов Штыка по Клину и Шипу. Пока боевой
    /// корпус был один, вопрос не возникал вовсе.
    /// </para>
    /// <para>
    /// Тест проверяет и порядок: список собирается для ПЕРВОГО захваченного тела тоже. Наивная
    /// реализация брала имена из живых сессий, а правило режима спавнит корпуса пачкой и занимает
    /// их по одному — первый робот получал бы «кроме тебя никого нет», то есть ровно ту слепоту,
    /// от которой блок и заведён.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Prompt_NamesTheOtherBorgs()
    {
        await using var w = await AiStation.Create();

        var first = EntityUid.Invalid;

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            for (var i = 0; i < 3; i++)
            {
                Assert.That(system.TrySpawnBorg(null, out var borg, out var placed, CombatProto), Is.True,
                    $"робот {i}: не удалось поставить — {placed}");

                Assert.That(system.TryClaim(borg, out var why), Is.True, $"робот {i}: захват не удался — {why}");

                if (i == 0)
                    first = borg;
            }
        });

        await w.Pair.Server.WaitRunTicks(5);

        var (prompt, own, others) = await w.Read(() =>
        {
            var comp = w.Ent.GetComponent<AiBorgComponent>(first);
            var text = w.System.Sessions[first].Conv.SystemPrompt;

            var rest = comp.AgentNames
                .Where(n => !string.Equals(n, comp.AgentName, StringComparison.Ordinal))
                .ToList();

            return (text, comp.AgentName, rest);
        });

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("СВОИ"),
                "в промпте нет блока со своими — робот не отличит собрата от человека");

            foreach (var name in others)
            {
                Assert.That(prompt, Does.Contain(name),
                    $"«{name}» не назван в блоке своих: по нему и будут стрелять");
            }

            // Своё имя в списке своих не нужно и вредно: «не бей того, кем ты являешься» — это
            // строка, на которой модель начинает рассуждать вместо того, чтобы работать.
            var block = prompt[prompt.IndexOf("СВОИ", StringComparison.Ordinal)..];
            var line = block[..block.IndexOf('\n')];

            Assert.That(line, Does.Not.Contain(own),
                $"робот перечислен в списке своих сам: «{line}»");
        });
    }

    /// <summary>
    /// Удар мимо — это ОТКАЗ инструмента, а не «ударил».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Самая дорогая из трёх поломок и самая незаметная. <c>AttemptLightAttack</c> возвращает
    /// <c>true</c> на факт ЗАМАХА, а не на попадание: цель вне досягаемости — апстрим пишет в
    /// админ-лог «melee attacked (light) … and missed» и на этом всё. Инструмент отвечал
    /// «ударил», модель считала работу сделанной и била снова. В раунде 305 Обух сделал больше
    /// тридцати замахов подряд по цели, до которой не доставал; со стороны это выглядело как
    /// намертво замерший киборг.
    /// </para>
    /// <para>
    /// Отказ обязан называть причину и звать подойти: «не прошло» отправило бы модель искать
    /// другую цель вместо того, чтобы сделать два шага.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Hit_OutOfReach_RefusesInsteadOfSwingingAtAir()
    {
        await using var w = await AiStation.Create();

        var borg = EntityUid.Invalid;

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            Assert.That(system.TrySpawnBorg(null, out borg, out var placed, CombatProto), Is.True,
                $"не удалось поставить робота: {placed}");

            Assert.That(system.TryClaim(borg, out var why), Is.True, $"захват не удался: {why}");
        });

        await w.Pair.Server.WaitRunTicks(5);

        // Мишень ставится НА ТУ ЖЕ КЛЕТКУ, а отодвигается уже после того, как робот её увидел.
        //
        // Первая версия спавнила её сразу в четырёх тайлах и была красной по двум причинам сразу:
        // смещение считалось в мировых координатах и попадало не туда, куда ожидалось, а сама
        // мишень к моменту удара успевала исчезнуть — инструмент отвечал «объекта больше нет», и
        // тест проверял не то, что хотел. Клетка робота — единственное место, про которое на
        // настоящей карте известно, что там пол; два моба на одной клетке законны.
        var xform = w.Pair.Server.System<Robust.Shared.GameObjects.SharedTransformSystem>();
        var at = await w.Read(() => xform.GetWorldPosition(borg));

        var victim = await w.SpawnCrew("Далёкий", at);

        await w.Pair.Server.WaitRunTicks(5);

        var damage = w.Pair.Server.System<DamageableSystem>();

        var seen = await w.InvokeOn(borg, "look");
        var handle = HandleOf(seen.EffectJson(), "crew-");

        Assert.That(handle, Is.Not.Null, $"мишень не попала в обзор: {seen.EffectJson()}");

        // Отодвигаем РОБОТА, а не мишень.
        //
        // Двигать мишень пробовали — не работает: перенесённый моб к следующему вызову
        // инструмента переставал разрешаться по хендлу («объекта больше нет»), и тест снова
        // проверял не то. Робот же в стенде живёт гарантированно: его держит сессия агента.
        // Для проверки это равнозначно — вопрос в расстоянии между двумя точками.
        await w.Post(() => xform.SetWorldPosition(borg, at + new Vector2(8f, 0f)));
        await w.Pair.Server.WaitRunTicks(5);

        var before = await w.Read(() => damage.GetTotalDamage(victim));

        // Одного вызова достаточно, и это часть проверки. Отказ по дальности стоит ПЕРЕД замахом,
        // то есть перед перезарядкой: если бы он стоял после, первый ответ был бы «рука ещё не
        // отведена» и настоящую причину модель узнала бы только со второй попытки.
        var hit = await w.InvokeOn(borg, "hit", $"{{\"target\":\"{handle}\"}}");

        await w.Pair.Server.WaitRunTicks(5);

        var after = await w.Read(() => damage.GetTotalDamage(victim));

        Assert.Multiple(() =>
        {
            Assert.That(hit.Ok, Is.False,
                "инструмент отчитался об ударе, которого не было — модель будет бить в воздух до конца смены");

            Assert.That(hit.Detail, Does.Contain("не дотянуться"),
                $"отказ не объясняет причину: {hit.Error} {hit.Detail}");

            Assert.That(hit.Detail, Does.Contain("подойди"),
                "в отказе нет совета подойти — модель уйдёт искать другую цель вместо двух шагов");

            Assert.That(after, Is.EqualTo(before),
                "цель вне досягаемости всё-таки получила урон — проверка дальности не та");
        });
    }

    /// <summary>
    /// Первый хендл заданного вида из выдачи <c>look</c> — именно ХЕНДЛ, а не строка целиком.
    /// </summary>
    /// <remarks>
    /// Обрезка по первому пробелу обязательна, и на ней тест уже спотыкался. Строка обзора
    /// выглядит как <c>crew-1 | Далёкий | Alive | Δ(0,0) (-2,-17)</c>, инструменты принимают из
    /// неё только первое слово, а на всю строку отвечают <c>stale_handle</c> — «объекта больше
    /// нет». Отличить это от настоящего исчезновения цели по тексту ответа невозможно, поэтому
    /// сообщение и увело в сторону: искали, кто удаляет моба, а никто его не удалял.
    /// </remarks>
    private static string? HandleOf(string lookJson, string kind)
    {
        foreach (var row in lookJson.Split('"'))
        {
            if (!row.StartsWith(kind, StringComparison.Ordinal))
                continue;

            var end = row.IndexOfAny(new[] { ' ', '|' });
            return end < 0 ? row : row[..end].Trim();
        }

        return null;
    }
}
