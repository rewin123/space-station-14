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
/// The Aksioma secret pool: its composition, and that every mode in it is actually reachable.
///
/// <para>
/// What's checked here is the DATA, not the roulette. The roulette itself is upstream's
/// (<c>SecretRuleSystem.TryPickPreset</c>) and is fair given equal weights — but it silently forgives
/// exactly the mistakes caught here: a typo in a preset name (the log gets "Invalid preset" and the
/// mode simply never comes up) and an overlooked <c>minPlayers</c> on one of the preset's rules, which
/// makes the mode disappear at low population.
/// </para>
/// <para>
/// The second mistake is sneakier than the first. The threshold is computed as the MAXIMUM across all
/// of the preset's rules (<c>GameTicker.GetMinimumPlayerCount</c>), meaning it gets dragged up by any
/// added rule — not just an antagonist one. Adding a borrowed rule with <c>minPlayers: 10</c> to our
/// preset one day would give us an evening where the AI mode never once comes up, and not a line about
/// it in the log.
/// </para>
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class SecretPoolTests
{
    private const string Pool = "SecretAksioma";

    /// <summary>The fork's AI modes — both the ones in the pool and the ones launched manually.</summary>
    private static readonly string[] AiPresets = { "AiPeaceful", "RogueAiHidden", "RogueAiOpen" };

    /// <summary>
    /// The pool's composition as of 02.09.2026: traitor and peaceful AI.
    /// </summary>
    /// <remarks>
    /// The list is checked for exact equality, not "contains": a mode silently dropped from the pool
    /// and one silently added are equally invisible in-game and equally change the evening.
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

        // Every rule of every preset must exist: a preset with a missing rule will still come up, but
        // will not be assembled whole — for example, without our RogueAiRule, meaning without the AI.
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

            // Traitor is the only one entitled to a threshold: it can't be played solo. What's checked
            // is not "exactly 2" but "greater than zero": the exact number is a balance decision and changes.
            Assert.That(minimums["Traitor"], Is.GreaterThan(0),
                "у предателя пропал порог по игрокам — он начнёт выпадать на пустом сервере");
        });
    }

    /// <summary>
    /// Modes taken out of the pool remain launchable manually.
    /// </summary>
    /// <remarks>
    /// On 02.09.2026 the hostile modes were removed from rotation, not from the fork: `forcepreset
    /// RogueAiOpen` must still work, and putting them back in the pool should be a one-line change,
    /// not a restoration of deleted prototypes. Without this test, "removed from the pool" and
    /// "deleted" would become the same thing within a month — a preset that never comes up is a
    /// preset nobody checks.
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
    /// On an empty server the pool does not degenerate into emptiness.
    /// </summary>
    /// <remarks>
    /// This exact case was the reason for setting up our own pool instead of upstream's: there,
    /// almost every mode has a threshold, and at a population of 0-1 nothing comes up at all. For us,
    /// at low population, three AI modes must remain — they ARE the server.
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
