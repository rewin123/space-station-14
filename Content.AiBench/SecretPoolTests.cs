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
/// Секретный пул «Аксиомы»: четыре режима, и каждый действительно достижим.
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

    /// <summary>Режимы, ради которых сервер существует: они обязаны выпадать при любом онлайне.</summary>
    private static readonly string[] AiPresets = { "AiPeaceful", "RogueAiHidden", "RogueAiOpen" };

    [Test]
    public async Task Pool_HoldsFourReachablePresets()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();
        var ticker = w.Pair.Server.System<GameTicker>();

        var weights = await w.Read(() => protoMan.Index<WeightedRandomPrototype>(Pool).Weights);

        Assert.That(weights.Keys, Is.EquivalentTo(new[] { "Traitor", "AiPeaceful", "RogueAiHidden", "RogueAiOpen" }),
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
            foreach (var id in AiPresets)
            {
                Assert.That(minimums[id], Is.Zero,
                    $"«{id}» требует {minimums[id]} готовых игроков — на пустом и почти пустом сервере " +
                    "режим ИИ выпадать перестанет, а в журнале об этом не будет ни строки");
            }

            // Предатель — единственный, кому порог положен: в одиночку он не играется. Проверяется
            // не «ровно 2», а «больше нуля»: точное число — балансное решение и меняется.
            Assert.That(minimums["Traitor"], Is.GreaterThan(0),
                "у предателя пропал порог по игрокам — он начнёт выпадать на пустом сервере");
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
