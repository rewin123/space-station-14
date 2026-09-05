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
/// Six combat borgs on one station: names, friend-or-foe, and an honest miss.
///
/// <para>
/// All three checks came out of one live round 305 (2026-09-01), and what they have in common is
/// that each breakage was silent. One name for six looked like "the robots are being dumb," shooting
/// a squadmate looked like "the model went insane," missing the target looked like "the borgs are
/// stuck." None of them produced a single error line: the tools honestly reported "ok," and nothing
/// happened in the game.
/// </para>
/// <para>
/// The bench is a real station (<see cref="AiStation"/>), because all three questions are about the
/// world: how many chassis spawned at the beacon, what's visible in <c>look</c>, and whether the
/// blade reaches the target. None of them can be asked on a synthetic grid.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgSquadTests
{
    private const string CombatProto = "AiBorgCombatChassis";

    /// <summary>
    /// Every combat chassis gets its own name, not six copies of "Blade."
    /// </summary>
    /// <remarks>
    /// <para>
    /// One name for all of them breaks not cosmetics but addressing. The order "Blade, go to the
    /// bar" goes out over shared comms, and each of the six takes it personally: the robots either
    /// swarm one target or stand around sorting out among themselves who was meant. The Si number
    /// doesn't help here — it's assigned by the engine at spawn, doesn't appear in crew orders, and
    /// isn't fixed in the prompt.
    /// </para>
    /// <para>
    /// What's checked is specifically the "catalog number → name" mapping, not mere distinctness:
    /// the name must be a function of <c>AgentId</c>, otherwise a robot that recovers catalog entry
    /// <c>combat-3</c> after a restart will answer to someone else's name and pick up someone else's
    /// notes.
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

            // THREE, not six: the bench already has the core occupying a slot, and the shared cap
            // ai.max_agents defaults to four — a fourth borg would honestly get refused in
            // StartSession. No need to raise the cap for the test: the "catalog number → name from
            // pool" mapping is checked on three exactly the same way as on six.
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
    /// Every robot's prompt lists the others — by name and without itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this list a robot can't tell a squadmate from a human in principle: in <c>look</c>
    /// and in observations another chassis arrives as a line in the same shape as a human — "crew-4
    /// Name (Si-…) | Alive." In round 305 this cost sixteen shots fired by Shtyk at Klin and Ship.
    /// While there was only one combat chassis, the question never came up at all.
    /// </para>
    /// <para>
    /// The test also checks ordering: the list is built for the FIRST claimed body too. A naive
    /// implementation pulled names from live sessions, but the gamemode's rule spawns chassis as a
    /// batch and claims them one at a time — the first robot would get "there's no one but you," i.e.
    /// exactly the blindness this block exists to prevent.
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

            // A robot's own name in the friendly list is unnecessary and harmful: "don't hit the one
            // you are" is a line that makes the model start reasoning instead of working.
            var block = prompt[prompt.IndexOf("СВОИ", StringComparison.Ordinal)..];
            var line = block[..block.IndexOf('\n')];

            Assert.That(line, Does.Not.Contain(own),
                $"робот перечислен в списке своих сам: «{line}»");
        });
    }

    /// <summary>
    /// A missed hit is a tool REFUSAL, not "hit."
    /// </summary>
    /// <remarks>
    /// <para>
    /// The most expensive of the three breakages and the most inconspicuous. <c>AttemptLightAttack</c>
    /// returns <c>true</c> for the fact of the SWING, not for a connect: when the target is out of
    /// reach, upstream writes "melee attacked (light) … and missed" to the admin log and that's it.
    /// The tool reported "hit," the model considered the job done and struck again. In round 305,
    /// Obukh made more than thirty swings in a row at a target it couldn't reach; from the outside
    /// it looked like a borg frozen solid.
    /// </para>
    /// <para>
    /// A refusal must name the reason and tell the model to close in: "it didn't go through" would
    /// have sent the model looking for another target instead of taking two steps.
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

        // The target is placed ON THE SAME TILE and only moved away after the robot has seen it.
        //
        // The first version spawned it four tiles away right away and was red for two reasons at
        // once: the offset was computed in world coordinates and landed somewhere unexpected, and
        // the target itself would disappear by the time of the hit — the tool would answer "object
        // no longer exists," and the test was checking the wrong thing. The robot's own tile is the
        // one place a real map guarantees has floor; two mobs on one tile is legal.
        var xform = w.Pair.Server.System<Robust.Shared.GameObjects.SharedTransformSystem>();
        var at = await w.Read(() => xform.GetWorldPosition(borg));

        var victim = await w.SpawnCrew("Далёкий", at);

        await w.Pair.Server.WaitRunTicks(5);

        var damage = w.Pair.Server.System<DamageableSystem>();

        var seen = await w.InvokeOn(borg, "look");
        var handle = HandleOf(seen.EffectJson(), "crew-");

        Assert.That(handle, Is.Not.Null, $"мишень не попала в обзор: {seen.EffectJson()}");

        // We move the ROBOT away, not the target.
        //
        // Moving the target instead was tried — it doesn't work: a relocated mob would stop
        // resolving by handle by the next tool call ("object no longer exists"), and the test would
        // again be checking the wrong thing. The robot, on the other hand, is guaranteed to persist
        // on the bench: the agent session keeps it alive. For the check it's equivalent either way —
        // the question is the distance between two points.
        await w.Post(() => xform.SetWorldPosition(borg, at + new Vector2(8f, 0f)));
        await w.Pair.Server.WaitRunTicks(5);

        var before = await w.Read(() => damage.GetTotalDamage(victim));

        // One call is enough, and that's part of the check. The range refusal happens BEFORE the
        // swing, i.e. before the cooldown: if it happened after, the first response would be "arm not
        // yet drawn back," and the model would only learn the real reason on the second attempt.
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
    /// The first handle of a given kind from <c>look</c>'s output — specifically the HANDLE, not the
    /// whole line.
    /// </summary>
    /// <remarks>
    /// Trimming at the first space is mandatory, and the test has already tripped over it once. A
    /// look line looks like <c>crew-1 | Далёкий | Alive | Δ(0,0) (-2,-17)</c>; the tools accept only
    /// the first word from it, and given the whole line they respond with <c>stale_handle</c> —
    /// "object no longer exists." There's no way to tell that apart from a genuine target
    /// disappearance from the response text alone, which is what sent the investigation down the
    /// wrong path: people looked for what was deleting the mob, when nothing was deleting it.
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
