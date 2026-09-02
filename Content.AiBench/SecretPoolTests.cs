using System.Linq;
using System.Threading.Tasks;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.GameTicking.Rules;
using Content.Shared.Random;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// Секретный пул «Аксиомы»: его состав, и то, что каждый режим в нём действительно достижим.
///
/// <para>
/// Проверяются ДАННЫЕ, а не рулетка. Сама рулетка апстримовая
/// (<c>SecretRuleSystem.TryPickPreset</c>) и при равных весах честна — но она молча прощает
/// ровно те ошибки, которые здесь и ловятся: опечатку в имени пресета (в лог уйдёт
/// «Invalid preset», а режим просто никогда не выпадет) и незамеченный <c>minPlayers</c> у
/// одного из правил пресета, из-за которого режим исчезает на низком онлайне.
/// </para>
/// <para>
/// Вторая ошибка коварнее первой. Порог считается как МАКСИМУМ по всем правилам пресета
/// (<c>GameTicker.GetMinimumPlayerCount</c>), то есть его втаскивает любое добавленное правило —
/// не только антагонистическое. Добавив однажды в наш пресет чужое правило с <c>minPlayers: 10</c>,
/// мы получили бы вечер, в котором режим ИИ не выпадает ни разу, и ни строки об этом в журнале.
/// </para>
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class SecretPoolTests
{
    private const string Pool = "SecretAksioma";

    /// <summary>Режимы ИИ форка — и те, что в пуле, и те, что запускаются вручную.</summary>
    private static readonly string[] AiPresets = { "AiPeaceful", "RogueAiHidden", "RogueAiOpen" };

    /// <summary>
    /// Состав пула на 02.09.2026: предатель и мирный ИИ.
    /// </summary>
    /// <remarks>
    /// Список сверяется целиком, а не «содержит»: молча выпавший из пула режим и молча
    /// добавленный — одинаково незаметны в игре и одинаково меняют вечер.
    /// </remarks>
    [Test]
    public async Task Pool_HoldsTwoReachablePresets()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();
        var ticker = w.Pair.Server.System<GameTicker>();

        var weights = await w.Read(() => protoMan.Index<WeightedRandomPrototype>(Pool).Weights);

        Assert.That(weights.Keys, Is.EquivalentTo(new[] { "Traitor", "AiPeaceful" }),
            "состав пула изменился — если это намеренно, поправьте и тест, и комментарий в secret_weights_aksioma.yml");

        Assert.Multiple(() =>
        {
            foreach (var (id, weight) in weights)
            {
                Assert.That(weight, Is.GreaterThan(0f), $"«{id}»: нулевой вес — режим в пуле числится, но не выпадает");

                Assert.That(protoMan.HasIndex<GamePresetPrototype>(id), Is.True,
                    $"«{id}»: такого пресета нет. Рулетка молча пропустит его и напишет «Invalid preset» в лог");
            }
        });

        // Каждое правило каждого пресета обязано существовать: пресет с несуществующим правилом
        // выпадет, но соберётся не целиком — например, без нашего RogueAiRule, то есть без ИИ.
        await w.Read(() =>
        {
            foreach (var id in weights.Keys)
            {
                var preset = protoMan.Index<GamePresetPrototype>(id);

                foreach (var rule in preset.Rules)
                {
                    Assert.That(protoMan.HasIndex<EntityPrototype>(rule.Id), Is.True,
                        $"«{id}»: правило «{rule.Id}» не найдено");
                }
            }

            return 0;
        });

        var minimums = await w.Read(() =>
            weights.Keys.ToDictionary(id => id, id => ticker.GetMinimumPlayerCount(id)));

        Assert.Multiple(() =>
        {
            Assert.That(minimums["AiPeaceful"], Is.Zero,
                $"«AiPeaceful» требует {minimums["AiPeaceful"]} готовых игроков. Это единственный режим пула " +
                "без порога, и с порогом пул на пустом сервере не выберет ничего");

            // Предатель — единственный, кому порог положен: в одиночку он не играется. Проверяется
            // не «ровно 2», а «больше нуля»: точное число — балансное решение и меняется.
            Assert.That(minimums["Traitor"], Is.GreaterThan(0),
                "у предателя пропал порог по игрокам — он начнёт выпадать на пустом сервере");
        });
    }

    /// <summary>
    /// Режимы, выведенные из пула, остаются запускаемыми вручную.
    /// </summary>
    /// <remarks>
    /// 02.09.2026 злые режимы убрали из ротации, а не из форка: `forcepreset RogueAiOpen` обязан
    /// работать, и вернуть их в пул должно быть правкой одной строки, а не восстановлением
    /// удалённых прототипов. Без этого теста «убрать из пула» и «удалить» через месяц станут
    /// одним и тем же — пресет, который никогда не выпадает, никто не проверяет.
    /// </remarks>
    [Test]
    public async Task PresetsOutOfThePool_AreStillLaunchable()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        await w.Read(() =>
        {
            foreach (var id in AiPresets)
            {
                Assert.That(protoMan.HasIndex<GamePresetPrototype>(id), Is.True,
                    $"пресет «{id}» исчез — вручную его уже не запустить");

                foreach (var rule in protoMan.Index<GamePresetPrototype>(id).Rules)
                {
                    Assert.That(protoMan.HasIndex<EntityPrototype>(rule.Id), Is.True,
                        $"«{id}»: правило «{rule.Id}» не найдено");
                }
            }

            return 0;
        });
    }

    /// <summary>
    /// На пустом сервере пул не вырождается в пустоту.
    /// </summary>
    /// <remarks>
    /// Именно этот случай и был причиной завести свой пул вместо апстримового: там почти у всех
    /// режимов есть порог, и на онлайне 0-1 не выпадает ничего. У нас на низком онлайне обязаны
    /// оставаться три режима ИИ — они и есть сервер.
    /// </remarks>
    [Test]
    public async Task Pool_StaysPickableOnAnEmptyServer()
    {
        await using var w = await AiStation.Create();
        var secret = w.Pair.Server.System<SecretRuleSystem>();

        var pickable = await w.Read(() => secret.CanPickAny(Pool));

        Assert.That(pickable, Is.True,
            "на пустом сервере из пула нельзя выбрать ни одного режима — раунд не начнётся вовсе");
    }
}
