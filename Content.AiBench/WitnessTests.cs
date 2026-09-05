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
/// Whether the agent sees what happens right in front of its eye.
///
/// <para>
/// Before these subscriptions it saw nothing: it heard the radio and speech near the core, while
/// the world for it was a frozen picture that could only be polled with the <c>look</c> tool. What
/// made this a hole wasn't a missed fight, but an impossible request — "when I insert the plasma,
/// start the generator" ran into the fact that there was no way to learn the plasma had been
/// inserted.
/// </para>
/// <para>
/// Half of this file is negative checks, and that's not just caution. Vision that sees everything
/// happening across the whole station isn't vision at all — it's omniscience in a nice format;
/// what tells the two apart is exactly the fact that a distant event does NOT make it into the
/// observation. So here we check not only "the line arrived", but also "the line is absent, and the
/// work behind it never even started".
/// </para>
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class WitnessTests
{
    /// <summary>
    /// A prototype identifier, not a string at the call site: <c>Index&lt;T&gt;("Blunt")</c> is
    /// forbidden by the RA0033 analyzer, and the ban is warranted — a typo in the literal would
    /// only surface at run time.
    /// </summary>
    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";

    /// <summary>
    /// Stop the loop while keeping the session: observation handlers keep filling the queue, and
    /// nothing is racing the check for the right to drain it.
    /// </summary>
    private static async Task Freeze(AiWorld w)
    {
        await w.Post(() => w.System.GetSession(w.Brain)!.Cts.Cancel());
        await w.Pair.Server.WaitRunTicks(5);
    }

    /// <summary>
    /// Discard everything that piled up while the scene was set up.
    ///
    /// Spawning entities is noisy by itself: a human appears dressed, meaning with a dozen
    /// insertions into containers — and all of them near the core. Without this cleanup, the "no
    /// line" check would be catching the test's own setup.
    /// </summary>
    private static async Task Drain(AiWorld w) =>
        await w.Read(() => w.System.BuildObservationForTest(w.Brain));

    private static async Task<string> Observation(AiWorld w) =>
        await w.Read(() => w.System.BuildObservationForTest(w.Brain)) ?? "";

    // ------------------------------------------------------------------ wiring

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
        // The very mechanism the task was set up for: "insert the plasma - start the generator".
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
        // The flip side: healing goes through the same event, and without a sign check the agent
        // would report the medic as "hitting" the patient.
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
        // A regression from the live session on August 16. The door goes Closed -> Opening -> Open,
        // the event fires twice, and both states carried the same label — the agent got two
        // indistinguishable lines and dutifully spent a turn on "duplicate event, already noted".
        // Seven turns out of forty-two went into this self-narration, and it could only be spotted
        // in the log of a live round.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var door = await w.Spawn("Airlock", dx: 2);
        await Drain(w);

        await w.Post(() => w.Pair.Server.System<SharedDoorSystem>().StartOpening(door));

        // Enough ticks for the animation to finish playing out: we catch BOTH events, not just the first.
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

    // ------------------------------------------------------------------ parity

    [Test]
    public async Task Interaction_FarFromTheEye_IsNotReported()
    {
        // The main test in this file. Vision that sees the whole station is omniscience, and what
        // sets it apart from vision is exactly what's checked here.
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

            // A counter, not text. The absence of a line is equally well explained by the gate and
            // by the line simply failing to build; zero here means the gate rejected before any
            // work was done, and that's the only claim about parity being made.
            Assert.That(after - before, Is.Zero,
                $"наблюдение всё-таки выпустило {after - before} строк о том, чего глаз не видит");
        });
    }

    // ------------------------------------------------------------------ identification

    [Test]
    public async Task ObservedHandle_MatchesTheOneLookGives()
    {
        // Consistency between mappers. If the registries diverged, the same device would become
        // two different things for the agent: one from look, another from observation.
        await using var w = await AiWorld.Create();
        await Freeze(w);

        var device = await w.Spawn("SMESBasic", dx: 3);
        var item = await w.Spawn("Crowbar", dx: 2);

        // Look happens first - it's what mints the handle. Observation must take the existing one,
        // not mint a second one.
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
        // A line you can't act on is useless: the whole point of a handle is that the agent invokes
        // a tool by it, without hunting the thing down with look all over again.
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

    // ------------------------------------------------------------------ queue

    [Test]
    public async Task ObservedFlood_DoesNotEvictSpeech()
    {
        // The queue evicts the oldest entry regardless of kind, and observations arrive in a flood.
        // Without a separate cap, activity in view would push a radio call out of the queue - i.e.
        // the agent would stop hearing requests exactly when there are the most of them.
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

        // Twenty insertions against a cap of five: the radio line survives this only if it's each
        // category that gets trimmed, and not just the oldest entry overall.
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

    // ------------------------------------------------------------------ volume

    [Test]
    public async Task OnePersonAppearing_CostsABoundedNumberOfLines()
    {
        // A volume measurement, also a guard against a firehose.
        //
        // Observation has no throttling by owner's decision, and the model's context is 256k, so a
        // hundred lines per turn breaks nothing. What would break things is something else: a
        // subscription to anything that fires on every movement - then a single person entering
        // view would cost not tens of lines, but thousands. The number is printed so it can be used,
        // and the cap sits where it catches a catastrophe, not where one would like to see a nice
        // result.
        //
        // A dressed person appearing is the most expensive single case among the cheap ones:
        // equipment rides in as insertions into containers, and all of them are right next to the
        // eye.
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

    // ------------------------------------------------------------------ responsiveness

    [Test]
    public async Task WhatItSaw_IsActionableInTheSameTurn()
    {
        // This is exactly what the task was set up for. The request "when I insert the plasma -
        // start the generator" was impossible not because the agent is lazy, but because it had no
        // way to learn the plasma had been inserted: all it could do was ask again over the radio.
        // Here we check the whole chain end to end - the event in the world, the observation line,
        // the handle from that line, the tool call by that handle, and the answer about THAT EXACT
        // entity.
        //
        // The model is replaced by a client that reads the line and acts on it mechanically. This is
        // deliberate: what's being checked is the wiring, not intelligence - intelligence is
        // measured by live runs, and it can't be put into a pre-commit test.
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
    /// A stub model that does exactly one thing: on seeing a line about an insertion, it calls the
    /// tool using the handle from that line.
    ///
    /// It doesn't decide or choose anything - that's exactly why it's fit for a pre-commit run. All
    /// it proves is that a handle that arrived in an observation works as an address without a
    /// single intermediate <c>look</c>.
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

            // We find the tool result by SCANNING BACKWARDS FROM THE END, rather than taking the
            // last message.
            //
            // This used to be messages[^1], and that worked exactly because the tool result was the
            // last message before the next call to the model. With the arrival of the NEW_EVENTS
            // (steering) block, a user message now also gets inserted after the batch results, so
            // "last" is no longer "tool". The stub must know no more about message ordering than a
            // real model does, and a real model looks up the result by tool_call_id, not by position.
            if (ToolResult == null)
            {
                for (var i = messages.Count - 1; i >= 0; i--)
                {
                    if (messages[i].Role != "tool")
                        continue;

                    ToolResult = messages[i].Content;
                    break;
                }
            }

            if (CalledHandle == null)
            {
                for (var i = messages.Count - 1; i >= 0; i--)
                {
                    if (messages[i].Role != "user")
                        continue;

                    // The second participant is the thing INTO WHICH it was inserted: that's exactly
                    // the one the agent is meant to act on in a request like "insert the plasma -
                    // start the generator".
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
    public async Task NewEvents_KeepSpeechAheadOfWhatWasSeen()
    {
        // The NEW_EVENTS block gets mixed into the conversation mid-turn: while the model is running
        // a multi-step turn, it would otherwise be deaf, and a bot answering a question it never
        // heard reads as broken. There can be a lot of events in the queue - any activity in view
        // yields dozens of OBSERVED lines - and the line this block exists for must sit ABOVE them,
        // not get lost at the end. The order is set by ObservationFormatter.OrderedKinds, and that's
        // exactly what's checked here, not just the mere presence of the line.
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

        var events = await w.NewEvents();

        Assert.That(events, Is.Not.Null, "блок NEW_EVENTS не собрался вовсе");
        Assert.That(events, Does.Contain("ответь мне"),
            "шестьдесят наблюдений вытеснили реплику из блока событий: " + events);

        var speechAt = events!.IndexOf("ответь мне", StringComparison.Ordinal);
        var seenAt = events.IndexOf("OBSERVED", StringComparison.Ordinal);

        Assert.That(seenAt, Is.GreaterThan(-1), "в потоке нет ни одной строки OBSERVED — тест ничего не проверил");
        Assert.That(speechAt, Is.LessThan(seenAt),
            "реплика оказалась ниже строк OBSERVED: модель прочитает её последней");

        // Delivered means removed. That's the entire deduplication story: the same lines can't
        // arrive a second time, because they're no longer in the queue.
        var again = await w.NewEvents();
        Assert.That(again, Is.Null, "события остались в очереди и приедут второй раз следующим ходом");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Rename a creature so that the agent sees the new name too.
    ///
    /// <c>SetEntityName</c> alone isn't enough, and that's not a test quirk but a product property:
    /// observation refers to people through <c>Identity.Name</c> - the very name the player sees on
    /// screen - and it lives on a separate identity entity and updates with a delay. That's exactly
    /// why a masked person shows up in the line as "Unknown", and that's correct: the agent sees a
    /// figure, not an ID card.
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

    /// <summary>Deal (or heal) damage from another entity - the same path a weapon uses to hit.</summary>
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
    /// Extract the handle from an observation line with the given prefix.
    /// </summary>
    /// <param name="segment">
    /// Which participant, by position, starting from the first. Line format:
    /// <c>OBSERVED &lt;what&gt; | &lt;handle&gt; &lt;name&gt; | … | Δ(..) (..)</c> - participants go
    /// in the order "who, with what, on what", so for an insertion the first is the thing inserted,
    /// and the second is what it was inserted into.
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
