using System.Collections.Generic;
using System.Linq;
using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Borg;
using Content.Shared.Access.Components;
using Content.Shared.Access;
using Content.Shared.Access.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Power.Components;
using Content.Shared.Whitelist;
using Content.Shared.Doors.Systems;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.NPC;
using Content.Shared.Silicons.Borgs.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Maths;
using Robust.Shared.Map.Components;

namespace Content.AiBench;

/// <summary>
/// The agent inside a borg body: claiming, legs, eyes, hands.
///
/// <para>
/// The bench is a real station (<see cref="AiStation"/>, the Box map), because everything
/// interesting here is about the world: floor underfoot, walls between the robot and the target,
/// navigation beacons and real airlocks. None of these questions can be asked on the thirteen
/// tiles of a test grid.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgAgentTests
{
    private const string BorgProto = "AiBorgChassis";

    /// <summary>Spawn an AI borg near the core and claim it.</summary>
    private static async Task<EntityUid> SpawnAndClaim(AiStation w)
    {
        var ent = w.Ent;
        var borg = EntityUid.Invalid;

        await w.Pair.Server.WaitPost(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            // Via real placement, not "next to the core": the AI core room is locked, and a robot
            // placed inside it genuinely finds no way out anywhere. The first version of the test
            // did exactly that and thereby tested a broken scene.
            Assert.That(system.TrySpawnBorg(null, out borg, out var placed), Is.True,
                $"не удалось поставить робота: {placed}");

            Assert.That(system.TryClaim(borg, out var reason), Is.True, $"захват не удался: {reason}");
        });

        await w.Pair.Server.WaitRunTicks(5);
        return borg;
    }

    /// <summary>
    /// Three robots spawned in a row get three DIFFERENT identifiers.
    /// </summary>
    /// <remarks>
    /// An id collision is not a crash but silent corruption: the agents share the
    /// <c>ai_data/agents/id</c> directory, share a dialogue file, and share a "session" on the
    /// bus. It surfaces a round later, after a restart, when a robot restores someone else's
    /// memory as its own — that is, at a point where cause can no longer be linked to effect.
    /// While id was a prototype constant, the only defense was the attentiveness of whoever wrote
    /// the YAML.
    /// </remarks>
    [Test]
    public async Task AgentIds_AreHandedOutOnePerRobot()
    {
        await using var w = await AiStation.Create();

        var ids = new List<string>();

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            for (var i = 0; i < 3; i++)
            {
                Assert.That(system.TrySpawnBorg(null, out var borg, out var placed, "AiBorgCombatChassis"), Is.True,
                    $"робот {i}: не удалось поставить — {placed}");

                Assert.That(system.TryClaim(borg, out var why), Is.True, $"робот {i}: захват не удался — {why}");

                ids.Add(w.Ent.GetComponent<AiBorgComponent>(borg).AgentId);
            }
        });

        await w.Pair.Server.WaitRunTicks(5);

        Assert.Multiple(() =>
        {
            Assert.That(ids, Has.Count.EqualTo(3));
            Assert.That(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(3),
                $"идентификаторы совпали: {string.Join(", ", ids)}");

            foreach (var id in ids)
                Assert.That(id, Does.StartWith("combat-"), $"«{id}» собран не из префикса прототипа");
        });
    }

    /// <summary>
    /// An identifier taken explicitly in a prototype is a refusal, not an overwrite.
    /// </summary>
    /// <remarks>
    /// This is the only place where a mistake in a prototype is still visible. Beyond this point
    /// it looks like "the robot somehow remembers someone else's shift," and people will go
    /// looking for it in compaction rather than in the YAML.
    /// </remarks>
    [Test]
    public async Task AgentId_TakenExplicitly_IsRefused()
    {
        await using var w = await AiStation.Create();
        await SpawnAndClaim(w);

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            Assert.That(system.TrySpawnBorg(null, out var second, out var placed), Is.True, placed);
            Assert.That(system.TryClaim(second, out var why), Is.False,
                "второй робот с тем же явным id захватился — каталоги памяти теперь общие");
            Assert.That(why, Does.Contain("borg-1"), $"причина отказа не называет виновный id: {why}");
        });
    }

    /// <summary>
    /// The <c>hit</c> tool actually deals damage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test was written after a discovery: <c>hit</c> called <c>UserInteraction</c>, i.e. a
    /// CLICK, and dealt no damage at all. There was no way to tell this apart from a miss — the
    /// tool reported "hit" in both cases, and the model believed it. The actual hit lives in
    /// <c>MeleeWeaponSystem</c> and is raised by a client-side event that the robot doesn't have.
    /// </para>
    /// <para>
    /// It's the damage that's checked, not the call's success: success would have come back even
    /// on the broken version.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Hit_ActuallyHurts()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        // The target is placed in the SAME tile as the robot, and that's not carelessness.
        //
        // On a real map, the adjacent tile can just as easily turn out to be floor plus a
        // bulkhead, and the hit genuinely doesn't go through a bulkhead: DoLightAttack checks
        // InRangeUnobstructed. The first version of the test placed the target one tile east and
        // stayed green right up until a run on the shared bench handed the robot a different spot
        // by the beacon. Two mobs on one tile is a legitimate state, and the point of the test is
        // whether damage is dealt, not the geometry.
        var at = await w.Read(() => w.Ent.System<SharedTransformSystem>().GetWorldPosition(borg));
        var victim = await w.SpawnCrew("Мишень", at);

        await w.Pair.Server.WaitRunTicks(5);

        var damage = w.Pair.Server.System<Content.Shared.Damage.Systems.DamageableSystem>();
        var before = await w.Read(() => damage.GetTotalDamage(victim));

        // The target's handle is taken from look: the tool only accepts those, and slipping in a
        // uid would mean testing a path the model doesn't actually walk.
        var seen = await w.InvokeOn(borg, "look");
        // We search by KIND, not by name, and that's not laziness.
        //
        // The look string carries Identity.Name, and the identity system hides a person's name
        // until they're wearing an ID tag: the name given to the spawned entity (literally
        // "target") will never appear there, no matter how many times SetEntityName is called.
        // The robot sees a stranger exactly the way a human would.
        var handle = HandleOfKind(seen.EffectJson(), "crew-");
        Assert.That(handle, Is.Not.Null, $"мишень не попала в обзор: {seen.EffectJson()}");

        // Retrying is part of the check, not a workaround. When an item is picked up into a hand,
        // upstream pushes MeleeWeaponComponent.NextAttack one interval forward
        // (ResetOnHandSelected), so you can't attack by switching weapons. The robot just
        // installed the module, i.e. landed exactly in this window, and the first hit genuinely
        // gets refused. The tool responds to such a refusal with retry: later, and this also
        // checks that this advice actually works.
        Content.Server.AiAgent.Tools.ToolResult hit = null!;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            hit = await w.InvokeOn(borg, "hit", $"{{\"target\":\"{handle}\"}}");

            if (hit.Ok)
                break;

            await w.Pair.Server.WaitRunTicks(10);
        }

        Assert.That(hit.Ok, Is.True, $"удар не прошёл: {hit.Error} {hit.Detail}");

        await w.Pair.Server.WaitRunTicks(5);

        var after = await w.Read(() => damage.GetTotalDamage(victim));

        Assert.That(after, Is.GreaterThan(before),
            "урона нет: инструмент отчитался об ударе, которого не было");
    }

    /// <summary>The first handle of a given kind from a <c>look</c> result.</summary>
    private static string? HandleOfKind(string lookJson, string kind)
    {
        foreach (var row in lookJson.Split('"'))
        {
            if (!row.StartsWith(kind, StringComparison.Ordinal) || !row.Contains(" | ", StringComparison.Ordinal))
                continue;

            return row.Split('|')[0].Trim();
        }

        return null;
    }


    /// <summary>
    /// After claiming, a combat robot has a blade in hand and a gun in a locked slot, not on the
    /// chassis root.
    /// </summary>
    /// <remarks>
    /// The gun is <c>AiBorgBuiltInLaser</c> in <c>gun_slot</c>. A Gun on the chassis root dirtied
    /// the root on every power-cell discharge; a pistol in hand spawned into the world on claim.
    /// Both broke PVS. There is deliberately no target here: with a target, the test would also
    /// be checking whether the hit lands.
    /// </remarks>
    [Test]
    public async Task CombatBorg_HasBothWeapons_AndPicksTheRightHand()
    {
        await using var w = await AiStation.Create();
        var borg = EntityUid.Invalid;

        await w.Post(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(system.TrySpawnBorg(null, out borg, out var placed, "AiBorgCombatChassis"), Is.True, placed);
            Assert.That(system.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(10);

        var held = await w.Read(() => w.Pair.Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>()
            .EnumerateHeld(borg)
            .Select(x => w.Ent.GetComponent<MetaDataComponent>(x).EntityPrototype?.ID ?? "?")
            .ToList());

        var (gunOnChassis, gunInSlot, slotProto) = await w.Read(() =>
        {
            var onChassis = w.Ent.HasComponent<Content.Shared.Weapons.Ranged.Components.GunComponent>(borg);
            var slots = w.Pair.Server.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();
            var stored = slots.GetItemOrNull(borg, "gun_slot");
            var proto = stored is { } uid
                ? w.Ent.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID
                : null;
            return (onChassis, stored != null, proto);
        });

        Assert.Multiple(() =>
        {
            Assert.That(held, Does.Contain("KukriKnife"), $"клинка нет в руках: {string.Join(", ", held)}");
            Assert.That(held, Does.Not.Contain("WeaponAdvancedLaser"),
                $"пистолет снова в руках: {string.Join(", ", held)}");
            Assert.That(held, Does.Not.Contain("AiBorgBuiltInLaser"),
                $"встроенный лазер оказался в руке: {string.Join(", ", held)}");
            Assert.That(gunOnChassis, Is.False, "Gun снова на корне шасси — вернётся петля PVS");
            Assert.That(gunInSlot, Is.True, "в gun_slot нет ствола — shoot не найдёт его");
            Assert.That(slotProto, Is.EqualTo("AiBorgBuiltInLaser"), $"в слоте не тот ствол: {slotProto}");
        });

        // Both tools are handed a deliberately nonexistent handle: what matters is exactly what
        // they trip over. A "nothing to hit with" / "nothing to shoot with" refusal would mean the
        // hand with the weapon wasn't found — a refusal about the handle means the weapon was
        // found and things got as far as the target.
        var hit = await w.InvokeOn(borg, "hit", "{\"target\":\"obj-999\"}");
        var shot = await w.InvokeOn(borg, "shoot", "{\"target\":\"obj-999\"}");

        Assert.Multiple(() =>
        {
            Assert.That(hit.Detail ?? "", Does.Not.Contain("нечем бить"), "клинок не найден ни в одной руке");
            Assert.That(shot.Detail ?? "", Does.Not.Contain("нечем стрелять"), "ствол не найден в слоте шасси");
        });
    }

    /// <summary>
    /// An engineering robot refuses to shoot instead of faking it.
    /// </summary>
    /// <remarks>
    /// The engineer has no gun in a slot and no gun in hand. The refusal text is what's checked:
    /// it's the only thing the model can use to understand there's nothing to shoot with.
    /// </remarks>
    [Test]
    public async Task Shoot_WithoutAGun_IsRefused()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        var seen = await w.InvokeOn(borg, "look");
        var anything = HandleOfKind(seen.EffectJson(), "obj-");
        Assert.That(anything, Is.Not.Null, "обзор пуст");

        var shot = await w.InvokeOn(borg, "shoot", $"{{\"target\":\"{anything}\"}}");

        Assert.Multiple(() =>
        {
            Assert.That(shot.Ok, Is.False, "инженер выстрелил, не имея оружия");
            Assert.That(shot.Detail ?? "", Does.Contain("нечем стрелять"),
                $"отказ не объясняет причину: {shot.Detail}");
        });
    }

    [Test]
    public async Task LookDelta_IsInTheSameFrameAsSelfAndGoto()
    {
        // Three numbers must live in one coordinate system: "me" from SELF, Δ from look, and the
        // point that goto understands. While Δ was computed in MAP coordinates and everything
        // else in GRID coordinates, the model's arithmetic silently produced the wrong tile — the
        // discrepancy only shows up on a rotated grid, which is why the grid is rotated on
        // purpose here. On a live run this looked like: the robot computed the coordinate of the
        // next tile, walked to it, and ended up in the neighboring compartment, over and over.
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        await w.Post(() =>
        {
            var xforms = w.Pair.Server.System<SharedTransformSystem>();
            xforms.SetWorldRotation(w.Grid, Angle.FromDegrees(90));
        });
        await w.Pair.Server.WaitRunTicks(10);

        var seen = await w.InvokeOn(borg, "look");
        var rows = seen.EffectJson().Split('"').Where(x => x.Contains(" | Δ(", StringComparison.Ordinal)).ToList();
        Assert.That(rows, Is.Not.Empty, $"обзор пуст: {seen.EffectJson()}");

        // Take the first object with a nonzero Δ: rotation can't be checked on a zero delta.
        var checkedAny = false;

        // The line carries TWO pairs: "Δ(dx,dy) (x,y)". Both must be in grid coordinates — the
        // first as an offset, the second as a ready-made point for goto. The model plugs the
        // absolute pair in without any arithmetic, and if it drifts, the error won't be "off by a
        // step" but "in a different compartment."
        var pairs = new Regex(@"Δ\((-?\d+),(-?\d+)\) \((-?\d+),(-?\d+)\)");

        foreach (var row in rows)
        {
            var handle = row[..row.IndexOf(' ')];
            var m = pairs.Match(row);

            Assert.That(m.Success, Is.True, $"{handle}: в строке нет двух пар чисел — «{row}»");

            var dx = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var dy = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var ax = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var ay = float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
                continue;

            var expected = await w.Read(() =>
            {
                var session = w.System.GetSession(borg);
                if (session == null || !session.Handles.TryResolve(handle, out var uid))
                    return ((Vector2 Delta, Vector2 Absolute)?) null;

                var xforms = w.Pair.Server.System<SharedTransformSystem>();
                var toGrid = xforms.GetInvWorldMatrix(w.Grid);
                var here = Vector2.Transform(xforms.GetMapCoordinates(borg).Position, toGrid);
                var there = Vector2.Transform(xforms.GetMapCoordinates(uid).Position, toGrid);
                return (there - here, there);
            });

            if (expected == null)
                continue;

            checkedAny = true;

            Assert.Multiple(() =>
            {
                Assert.That(dx, Is.EqualTo(expected.Value.Delta.X).Within(0.6f),
                    $"{handle}: Δx разъехалась — look={dx}, сетка={expected.Value.Delta.X}");
                Assert.That(dy, Is.EqualTo(expected.Value.Delta.Y).Within(0.6f),
                    $"{handle}: Δy разъехалась — look={dy}, сетка={expected.Value.Delta.Y}");
                Assert.That(ax, Is.EqualTo(expected.Value.Absolute.X).Within(0.6f),
                    $"{handle}: X абсолютной пары разъехался — look={ax}, сетка={expected.Value.Absolute.X}");
                Assert.That(ay, Is.EqualTo(expected.Value.Absolute.Y).Within(0.6f),
                    $"{handle}: Y абсолютной пары разъехался — look={ay}, сетка={expected.Value.Absolute.Y}");
            });

            break;
        }

        Assert.That(checkedAny, Is.True, "не нашлось ни одного объекта с ненулевой Δ — проверять нечего");
    }

    [Test]
    public async Task CarriedItem_SurvivesAModuleSwitch_AndCanStillBeDropped()
    {
        // A live bug that cost the robot all of its item handling. Upstream attaches
        // UnremoveableComponent to anything that ends up in a module hand without a whitelist
        // (SharedBorgSystem.Module.cs, IsItemInHandUnremovable). For stock modules this is
        // correct — a crowbar is welded to the hand. For an empty manipulator it meant: pick up a
        // flatpack, switch modules — and the cargo is welded on forever, after which every
        // pickup attempt gets "no free hand." In a live run the robot spent a dozen-plus turns
        // cycling modules trying to put it down.
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        var crowbar = EntityUid.Invalid;
        await w.Post(() => crowbar = w.Ent.SpawnEntity("Crowbar",
            w.Ent.GetComponent<TransformComponent>(borg).Coordinates));
        await w.Pair.Server.WaitRunTicks(5);

        var handle = await w.Read(() => w.System.HandleFor(borg, crowbar));

        await w.InvokeOn(borg, "module", """{"name":"manipulator"}""");
        var took = await w.InvokeOn(borg, "pickup", $$"""{"target":"{{handle}}"}""");
        Assert.That(took.Ok, Is.True, took.ToJson());

        // The loop through modules — this is exactly what welded the cargo on.
        await w.InvokeOn(borg, "module", """{"name":"tool"}""");
        await w.InvokeOn(borg, "module", """{"name":"manipulator"}""");

        var stuck = await w.Read(() =>
            w.Ent.HasComponent<Content.Shared.Interaction.Components.UnremoveableComponent>(crowbar));
        var put = await w.InvokeOn(borg, "drop");

        Assert.Multiple(() =>
        {
            Assert.That(stuck, Is.False, "груз приварился к руке при смене модуля");
            Assert.That(put.Ok, Is.True, $"взятое обязано выкладываться обратно: {put.ToJson()}");
        });

        // The extra module cycle after dropping isn't ritual, it's cleaning up someone else's
        // bookkeeping.
        //
        // Upstream remembers the hand contents of a deselected module in StoredItems and doesn't
        // clear that record when the item is dropped. When the bench tears down, it tries to pull
        // an already-deleted entity out of the container and logs an ERRO about a missing
        // TransformComponent — and the pool treats ANY ERRO in the log as a failure, so this made
        // the NEXT test in the fixture fail, not this one.
        await w.InvokeOn(borg, "module", """{"name":"tool"}""");
        await w.InvokeOn(borg, "module", """{"name":"manipulator"}""");
        await w.Post(() => w.Ent.DeleteEntity(crowbar));
        await w.Pair.Server.WaitRunTicks(5);
    }

    /// <summary>
    /// Claiming activates the chassis — which means it grants hands and ID-based access.
    /// </summary>
    /// <remarks>
    /// The central assertion of this file. <c>SharedBorgSystem.CanActivate</c> requires a mind,
    /// and without one the chassis stays deactivated: no modules, no access, walking speed only.
    /// And nothing crashes anywhere — the robot just stands there, unable to do anything. That's
    /// exactly why <c>Active</c> is checked, rather than "the session came up."
    /// </remarks>
    [Test]
    public async Task Claim_ActivatesTheChassis()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        var active = await w.Read(() => ent.GetComponent<BorgChassisComponent>(borg).Active);
        var hasMind = await w.Read(() =>
            ent.TryGetComponent<MindContainerComponent>(borg, out var mc) && mc.HasMind);
        var hasSession = await w.Read(() => w.System.Sessions.ContainsKey(borg));

        Assert.Multiple(() =>
        {
            Assert.That(hasMind, Is.True, "разум не посажен — шасси не активируется");
            Assert.That(active, Is.True, "шасси не активно: не будет ни модулей, ни доступа по ID");
            Assert.That(hasSession, Is.True, "сессия агента не завелась");
        });
    }

    /// <summary>
    /// Two agents write to DIFFERENT session files.
    /// </summary>
    /// <remarks>
    /// A direct check of the fix without which a second agent can't be brought up at all: the
    /// session identifier used to be the constant <c>"current"</c>, and the borg and the core
    /// would restore each other's dialogues.
    /// </remarks>
    [Test]
    public async Task TwoAgents_DoNotShareASessionId()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        var ids = await w.Read(() => w.System.Sessions.Values.Select(s => s.Body.Id).ToList());

        Assert.That(ids, Has.Count.GreaterThanOrEqualTo(2), "ожидались агент в ядре и агент в борге");
        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count),
            $"идентификаторы сессий совпали: [{string.Join(", ", ids)}] — агенты затрут память друг друга");
    }

    /// <summary>
    /// The borg has its own toolset: it has hands and legs, no station consoles.
    /// </summary>
    [Test]
    public async Task BorgToolset_HasHandsAndNoStationConsoles()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        var names = await w.Read(() =>
            w.System.Sessions[borg].Registry.Tools.Select(t => t.Name).ToHashSet());

        Assert.Multiple(() =>
        {
            foreach (var want in new[] { "goto", "step", "look", "examine", "use", "pickup", "drop", "module", "say", "radio", "noop", "laws" })
                Assert.That(names, Does.Contain(want), $"у борга нет инструмента {want}");

            // All of these rely on the built-in consoles of the Station AI body or on a device
            // whitelist. The borg has neither: it doesn't operate a door remotely, it walks up to it.
            foreach (var forbidden in new[] { "announce", "device_action", "device_ui", "move_camera", "jump_to_core", "crew_status" })
                Assert.That(names, Does.Not.Contain(forbidden), $"борг не должен иметь {forbidden}");
        });
    }

    /// <summary>
    /// The robot sees with its own eyes, not through the camera network.
    /// </summary>
    /// <remarks>
    /// A separate test precisely because reusing <c>StationAiVisionSystem</c> was tempting and
    /// wrong: it merges the view of ALL cameras in range, and a robot in a dark corridor would
    /// "see" half the station. This checks that the borg's view is limited and noticeably smaller
    /// than what the core sees with its cameras.
    /// </remarks>
    [Test]
    public async Task Look_SeesLessThanTheCameraNetwork()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        var borgSees = await w.InvokeOn(borg, "look", "{}");
        var coreSees = await w.InvokeOn(w.Brain, "look", "{}");

        Assert.That(borgSees.Ok, Is.True, $"look борга не отработал: {borgSees.Error} {borgSees.Detail}");
        Assert.That(coreSees.Ok, Is.True, $"look ядра не отработал: {coreSees.Error} {coreSees.Detail}");

        var borgCount = System.Convert.ToInt32(borgSees.Effect!["видно"]);

        Assert.That(borgCount, Is.GreaterThan(0), "робот не увидел вообще ничего — обзор сломан");
    }












    /// <summary>
    /// The robot actually walks to a target on its own legs.
    /// </summary>
    /// <remarks>
    /// Several targets are tried: one computed point can end up behind a locked door, in which
    /// case <c>NoPath</c> is the right answer to the wrong question. The test asserts not "got to
    /// that exact spot" but "is able to walk."
    /// </remarks>
    [Test]
    public async Task Goto_ActuallyMovesTheRobot()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        // The navmesh is built asynchronously after the round starts, and "wait N ticks" is not a
        // condition but a gamble: under the load of a full run those same ticks weren't enough,
        // and the test failed through no fault of the robot's. Instead we wait for the actual
        // readiness of the graph underfoot.
        for (var i = 0; i < 60; i++)
        {
            var ready = await w.Read(() =>
            {
                var pf = w.Pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
                return pf.GetPoly(ent.GetComponent<TransformComponent>(borg).Coordinates) != null;
            });

            if (ready)
                break;

            await w.Pair.Server.WaitRunTicks(10);
        }

        var start = await w.Read(() => ent.GetComponent<TransformComponent>(borg).LocalPosition);
        var grid = await w.Read(() => ent.GetComponent<TransformComponent>(borg).GridUid!.Value);
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var offsets = new[]
        {
            new Vector2(5, 0), new Vector2(-5, 0), new Vector2(0, 5), new Vector2(0, -5),
            new Vector2(9, 0), new Vector2(-9, 0), new Vector2(0, 9), new Vector2(0, -9),
        };

        var log = new System.Collections.Generic.List<string>();

        foreach (var off in offsets)
        {
            var target = await w.Read(() =>
            {
                var sys = w.Pair.Server.System<AiBorgSystem>();
                return sys.TryFreeTileNear(grid, start + off, out var found) ? (Vector2?) found.Position : null;
            });

            if (target == null)
                continue;

            var json = "{\"to\":\"" + target.Value.X.ToString("F0", inv) + "," + target.Value.Y.ToString("F0", inv) + "\"}";
            await w.InvokeOn(borg, "goto", json);

            for (var i = 0; i < 30; i++)
            {
                await w.Pair.Server.WaitRunTicks(10);

                var moved = await w.Read(() =>
                    (ent.GetComponent<TransformComponent>(borg).LocalPosition - start).Length());

                if (moved > 1.5f)
                {
                    TestContext.Out.WriteLine($"дошёл/идёт: цель {json}, сдвиг {moved:F1}");
                    Assert.Pass();
                }

                var gone = await w.Read(() =>
                    !ent.HasComponent<Content.Server.NPC.Components.NPCSteeringComponent>(borg));

                if (gone)
                    break;
            }

            log.Add($"{json} — не сдвинулся");
            await w.InvokeOn(borg, "goto", "{\"stop\":true}");
        }

        Assert.Fail("робот не сдвинулся ни к одной из целей:\n" + string.Join("\n", log));
    }

    /// <summary>
    /// Our own pathfinder finds a route across the whole station — where the upstream one gives up.
    /// </summary>
    /// <remarks>
    /// The point of this test is the comparison. Upstream's <c>PathfindingSystem</c> cuts off
    /// graph expansion at <c>NodeLimit = 512</c>, and for it, crossing the station is "no path" —
    /// that's not a bug but its working range: stock NPCs live within a single room. Our
    /// pathfinder walks the bitwise <c>NavMapComponent</c> map and must find a path between
    /// distant compartments.
    /// </remarks>
    [Test]
    public async Task Pathfinder_CrossesTheWholeStation()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        var report = await w.Read(() =>
        {
            var grid = ent.GetComponent<TransformComponent>(borg).GridUid!.Value;
            var navMap = ent.GetComponent<Content.Shared.Pinpointer.NavMapComponent>(grid);

            // The two mutually farthest-apart passable points given by the beacons: this is
            // "across the station," expressed in terms of the map itself, not our own numbers.
            var beacons = navMap.Beacons.Values
                .Select(b => new Vector2i((int) MathF.Floor(b.Position.X), (int) MathF.Floor(b.Position.Y)))
                .Select(t => Content.Server.AiAgent.Borg.BorgPathfinder.NearestPassable(navMap, t))
                .Where(t => t != null)
                .Select(t => t!.Value)
                .ToList();

            if (beacons.Count < 2)
                return "маяков меньше двух — сцена сломана";

            var a = beacons[0];
            var b = beacons[0];
            var best = 0;

            foreach (var x in beacons)
            {
                foreach (var y in beacons)
                {
                    var d = Math.Abs(x.X - y.X) + Math.Abs(x.Y - y.Y);
                    if (d <= best)
                        continue;

                    best = d;
                    a = x;
                    b = y;
                }
            }

            var path = Content.Server.AiAgent.Borg.BorgPathfinder.FindPath(navMap, a, b);

            return path == null
                ? $"путь {a} → {b} (по прямой {best} тайлов) НЕ найден"
                : $"ok: {a} → {b}, по прямой {best}, путь {path.Count} тайлов, ног {Content.Server.AiAgent.Borg.BorgPathfinder.ToLegs(path).Count}";
        });

        TestContext.Out.WriteLine("ПОИСК: " + report);

        Assert.That(report, Does.StartWith("ok:"), report);
        Assert.That(report, Does.Not.Contain("путь 0"), "путь пустой");
    }

    /// <summary>
    /// <c>goto</c> by handle heads TOWARD THE TARGET, not to the station's coordinate origin.
    /// </summary>
    /// <remarks>
    /// A regression caught on the live server. A target given by handle is set as
    /// <c>EntityCoordinates(target, Vector2.Zero)</c>, so it can follow a moving target, and its
    /// <c>Position</c> is an offset relative to THE TARGET ITSELF, i.e. (0,0). Read as grid
    /// coordinates, this sent the robot to point (0,0) of the station: for "approach the door two
    /// steps away," it would silently walk off across half the station. The bug is silent — a
    /// route is built, the robot walks, everything looks like it's working.
    /// </remarks>
    [Test]
    public async Task Goto_ByHandle_HeadsTowardsTheTarget()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        for (var i = 0; i < 60; i++)
        {
            var ready = await w.Read(() =>
            {
                var pf = w.Pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
                return pf.GetPoly(ent.GetComponent<TransformComponent>(borg).Coordinates) != null;
            });

            if (ready)
                break;

            await w.Pair.Server.WaitRunTicks(10);
        }

        // The target is a few tiles away: far enough that "to the coordinate origin" and "toward
        // the target" diverge, and close enough that the route is short.
        var target = EntityUid.Invalid;
        await w.Pair.Server.WaitPost(() =>
        {
            var here = ent.GetComponent<TransformComponent>(borg).Coordinates;
            target = ent.SpawnEntity("Crowbar", here.Offset(new Vector2(4, 0)));
        });

        await w.Pair.Server.WaitRunTicks(5);

        var handle = await w.Read(() => w.System.HandleFor(borg, target));
        var before = await w.Read(() => Distance(ent, borg, target));

        var r = await w.InvokeOn(borg, "goto", "{\"to\":\"" + handle + "\"}");
        Assert.That(r.Ok, Is.True, $"goto отказал: {r.Error} {r.Detail}");

        var closest = before;
        for (var i = 0; i < 30; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);
            var d = await w.Read(() => Distance(ent, borg, target));
            closest = MathF.Min(closest, d);

            if (closest < 1.5f)
                break;
        }

        Assert.That(closest, Is.LessThan(before - 1.0f),
            $"робот не приблизился к цели: было {before:F1}, ближе всего {closest:F1} — " +
            "похоже, цель по хендлу снова читается в чужой системе координат");
    }

    private static float Distance(IEntityManager ent, EntityUid a, EntityUid b) =>
        (ent.GetComponent<TransformComponent>(a).LocalPosition
         - ent.GetComponent<TransformComponent>(b).LocalPosition).Length();

    /// <summary>
    /// The SELF line: no doubled tag, and in GRID coordinates.
    /// </summary>
    /// <remarks>
    /// Both facets were caught live. The <c>SELF</c> tag is added by
    /// <c>ObservationFormatter</c>, and the body's own prefix produced "SELF SELF mode=…". The
    /// coordinates, meanwhile, must match what <c>goto {"to":"x,y"}</c> understands, i.e. be grid
    /// coordinates: by printing map coordinates, the robot reported its own position (the "me="
    /// prefix) as (-521,435), and a goto using those same numbers would send it off into empty
    /// space. The model reads its own position from here and has no way to notice a
    /// coordinate-system mismatch.
    /// </remarks>
    [Test]
    public async Task SelfLine_IsUntaggedAndInGridCoordinates()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        var (line, local) = await w.Read(() =>
        {
            var session = w.System.Sessions[borg];
            return (session.Body.SelfLine(session), ent.GetComponent<TransformComponent>(borg).LocalPosition);
        });

        TestContext.Out.WriteLine("SELF: " + line);

        Assert.Multiple(() =>
        {
            Assert.That(line, Does.Not.StartWith("SELF"),
                "тег добавляет форматтер — своя добавка даёт «SELF SELF»");

            Assert.That(line, Does.Contain($"я=({local.X:F0},{local.Y:F0})"),
                $"позиция в строке не совпадает с координатами сетки {local} — goto поймёт её иначе");
        });
    }

    /// <summary>
    /// The robot hears the radio and nearby speech.
    /// </summary>
    /// <remarks>
    /// Caught live, and it was a completely silent failure. Receiving comms hangs on the pair
    /// <c>(LlmStationAiComponent, RadioReceiveEvent)</c> — a marker named after the first body —
    /// while nearby hearing in <c>OnEntitySpoke</c> started with "no core → skip." The borg had
    /// neither: the order went out on Common, Station AI answered, and the robot took ZERO turns
    /// and stayed standing in the bar. No error, no log line — just a deaf agent.
    /// </remarks>
    [Test]
    public async Task Borg_HearsRadio()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);

        var marker = await w.Read(() =>
            w.Ent.HasComponent<Content.Server.AiAgent.Components.LlmStationAiComponent>(borg));

        Assert.That(marker, Is.True,
            "без маркера LLM-агента приём рации на борге не подписан — он глух к эфиру");

        // Measurement in the SAME tick as the transmission.
        //
        // The observation queue is not an accumulator: the agent loop wakes up when it's
        // replenished and immediately drains it. The first version of this test measured ten
        // ticks later and saw 0 → 0 for BOTH agents, i.e. it was blaming reception when it was
        // actually measuring its own race against the loop.
        var sent = false;
        var why = string.Empty;
        var before = 0;
        var after = 0;

        await w.Pair.Server.WaitPost(() =>
        {
            before = w.System.Sessions[borg].Queue.Count;
            sent = w.System.InjectRadio("Binary", "Сегмент, доложи обстановку", out why);
            after = w.System.Sessions[borg].Queue.Count;
        });

        TestContext.Out.WriteLine($"ЭФИР: отправлено={sent} ({why}) очередь борга {before}→{after}");

        Assert.That(sent, Is.True, $"передача не ушла: {why}");
        Assert.That(after, Is.GreaterThan(before),
            "радиопередача не попала в очередь наблюдений робота — он глух к эфиру");
    }

    /// <summary>
    /// Diagnostic: connectivity of live-map compartments as seen by our own pathfinder.
    /// </summary>
    [Test]
    [Explicit("диагностика связности конкретной карты, не для общего прогона")]
    public async Task Diag_PackedConnectivity()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;

        var report = await w.Read(() =>
        {
            var nav = ent.GetComponent<Content.Shared.Pinpointer.NavMapComponent>(w.Grid);

            Vector2i? TileOf(string name)
            {
                foreach (var b in nav.Beacons.Values)
                {
                    if (string.IsNullOrWhiteSpace(b.Text) || !b.Text!.Contains(name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var t = new Vector2i((int) MathF.Floor(b.Position.X), (int) MathF.Floor(b.Position.Y));
                    return BorgPathfinder.NearestPassable(nav, t);
                }

                return null;
            }

            var lines = new System.Collections.Generic.List<string>();
            var bar = TileOf("Bar");
            lines.Add($"маяков всего: {nav.Beacons.Count}, чанков: {nav.Chunks.Count}, Bar: {(bar?.ToString() ?? "НЕТ")}");

            foreach (var target in new[] { "AME", "Engineering", "Atmos", "Bridge", "Arrivals" })
            {
                var t = TileOf(target);
                if (bar == null || t == null)
                {
                    lines.Add($"{target}: проходимого тайла нет");
                    continue;
                }

                var path = BorgPathfinder.FindPath(nav, bar.Value, t.Value);
                lines.Add($"{target} {t}: {(path == null ? "ПУТИ НЕТ" : path.Count + " тайлов")}");
            }

            return string.Join("\n", lines);
        });

        TestContext.Out.WriteLine("СВЯЗНОСТЬ:\n" + report);
        Assert.Pass();
    }

    /// <summary>
    /// The robot walks from the bar to the reactor on a live map.
    /// </summary>
    /// <remarks>
    /// Explicit: the rotation map takes a long time to load, and there's no need to keep this in
    /// the general run. But it's exactly this route that caught the combination of two limits —
    /// our own pathfinder and upstream's steering — which is why it was recorded as a test rather
    /// than staying a manual check.
    /// </remarks>
    [Test]
    [Explicit("длинный маршрут на карте ротации")]
    public async Task Borg_WalksFromBarToTheReactor()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;

        var borg = EntityUid.Invalid;
        await w.Pair.Server.WaitPost(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(sys.TrySpawnBorg("Bar", out borg, out var placed), Is.True, placed);
            Assert.That(sys.TryClaim(borg, out var why), Is.True, why);
        });

        // The steering navmesh is built asynchronously; without it the very first leg is NoPath.
        for (var i = 0; i < 80; i++)
        {
            var ready = await w.Read(() =>
            {
                var pf = w.Pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
                return pf.GetPoly(ent.GetComponent<TransformComponent>(borg).Coordinates) != null;
            });

            if (ready)
                break;

            await w.Pair.Server.WaitRunTicks(10);
        }

        var start = await w.Read(() => ent.GetComponent<TransformComponent>(borg).LocalPosition);

        var r = await w.InvokeOn(borg, "goto", "{\"to\":\"AME\"}");
        Assert.That(r.Ok, Is.True, $"goto отказал: {r.Error} {r.Detail}");

        var target = await w.Read(() =>
        {
            var nav = ent.GetComponent<Content.Shared.Pinpointer.NavMapComponent>(w.Grid);
            foreach (var b in nav.Beacons.Values)
            {
                if (!string.IsNullOrWhiteSpace(b.Text) && b.Text!.Contains("AME", StringComparison.OrdinalIgnoreCase))
                    return b.Position;
            }

            return Vector2.Zero;
        });

        var best = (start - target).Length();

        for (var i = 0; i < 200; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);

            var d = await w.Read(() =>
                (ent.GetComponent<TransformComponent>(borg).LocalPosition - target).Length());

            best = MathF.Min(best, d);

            if (best < 3f)
                break;
        }

        var от = (start - target).Length();
        TestContext.Out.WriteLine($"РЕАКТОР: было {от:F1} тайлов до цели, стало {best:F1}");

        // Exactly where it stopped and what's blocking it: the robot's access and the doors around it.
        var stuck = await w.Read(() =>
        {
            var access = ent.TryGetComponent<Content.Shared.Access.Components.AccessComponent>(borg, out var acc)
                ? $"доступ включён={acc.Enabled} групп={string.Join("/", acc.Groups)} тегов={string.Join("/", acc.Tags)}"
                : "нет AccessComponent";

            var lookup = w.Pair.Server.System<EntityLookupSystem>();
            var xform = w.Pair.Server.System<SharedTransformSystem>();

            var doors = new System.Collections.Generic.HashSet<Entity<Content.Shared.Doors.Components.DoorComponent>>();
            lookup.GetEntitiesInRange(xform.GetMapCoordinates(borg), 4f, doors,
                LookupFlags.Static | LookupFlags.Approximate);

            var near = doors.Select(d =>
            {
                var st = d.Comp.State;
                var reader = ent.HasComponent<Content.Shared.Access.Components.AccessReaderComponent>(d.Owner);
                return $"{ent.GetComponent<MetaDataComponent>(d.Owner).EntityName}[{st}{(reader ? ",замок" : "")}]";
            });

            // Anything standing right up against it: the obstacle need not be a door.
            var solid = lookup.GetEntitiesInRange(xform.GetMapCoordinates(borg), 1.8f,
                LookupFlags.Static | LookupFlags.Dynamic | LookupFlags.Approximate);

            var blockers = solid
                .Where(u => u != borg && ent.HasComponent<Robust.Shared.Physics.Components.PhysicsComponent>(u))
                .Select(u => ent.GetComponent<MetaDataComponent>(u).EntityName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .Take(12);

            var pos = ent.GetComponent<TransformComponent>(borg).LocalPosition;

            return $"тайл ({MathF.Floor(pos.X)},{MathF.Floor(pos.Y)}) | " + access +
                   " | двери: " + string.Join(", ", near) +
                   " | рядом: " + string.Join(", ", blockers);
        });

        TestContext.Out.WriteLine("ЗАСТРЯЛ: " + stuck);

        Assert.That(best, Is.LessThan(3f),
            $"робот не дошёл до реактора: с {от:F1} тайлов подобрался только на {best:F1}");
    }

    /// <summary>
    /// The robot moves itself, not through upstream steering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This guards an architectural decision that wasn't reached right away. At first our own
    /// pathfinder built the route, and <c>NPCSteeringSystem</c> was supposed to walk it leg by
    /// short leg. On the rotation map this locked up dead: the robot got through 27 of 47 tiles
    /// and reported "no path" at a point where our route had been built and verified by ITS OWN
    /// passability rule.
    /// </para>
    /// <para>
    /// So movement is our own now, and along with the steering system went its props too —
    /// <c>ActiveNPCComponent</c> and an empty HTN task in the prototype, which were needed only
    /// to make it agree to run. The test keeps them removed: if they come back, someone is
    /// dragging in someone else's steering again.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Borg_MovesWithoutUpstreamSteering()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        var (steering, npc, htn) = await w.Read(() => (
            ent.HasComponent<Content.Server.NPC.Components.NPCSteeringComponent>(borg),
            ent.HasComponent<Content.Shared.NPC.ActiveNPCComponent>(borg),
            ent.HasComponent<Content.Server.NPC.HTN.HTNComponent>(borg)));

        Assert.Multiple(() =>
        {
            Assert.That(steering, Is.False, "на роботе висит апстримовый рулевой — движение раздвоилось");
            Assert.That(npc, Is.False, "ActiveNPCComponent нужен был только рулевому");
            Assert.That(htn, Is.False, "HTN нужен был только ради флагов чужого путепоиска");
        });
    }

    /// <summary>
    /// The robot starts the reactor: gets there, finds the console, inserts fuel, turns on injection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explicit: rotation map, a long road. This is the ultimate point of the whole body
    /// endeavor — checking that the robot can not only get somewhere but also DO manual work in
    /// the right order.
    /// </para>
    /// <para>
    /// <b>Be careful reading the verdict.</b> The scenario runs to completion and all of its
    /// assertions pass, but NUnit still reports a failure: the bench treats ANY ERROR-level line
    /// in the server log as a failure, and upstream's <c>SharedDoAfterSystem.ShouldCancel</c>,
    /// after unpacking a crate, resolves the transform of an entity it deleted itself. This is
    /// someone else's pitfall, upstream can't be fixed, and the bench has no "expected error"
    /// mechanism. Look at the LAUNCH line and at the assertion about injection.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Chargers are searched for across the whole station, not just what's visible, and only ones
    /// the robot actually fits into.
    /// </summary>
    /// <remarks>
    /// In round 137 the robot went through five compartments, reported "BorgCharger found
    /// nowhere, task impossible," and sat down at zero charge — while there were three charging
    /// stations on the map. It had no way of seeing them from anywhere: they stand in Robotics,
    /// and it sits down wherever it happened to be working.
    /// </remarks>
    [Test]
    public async Task FindCharger_SeesTheWholeStation_AndOnlyOnesThatFit()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;
        var borg = await SpawnAndClaim(w);

        var result = await w.InvokeOn(borg, "find_charger");
        TestContext.Out.WriteLine("зарядки: " + result.EffectJson());

        Assert.That(result.Ok, Is.True, $"поиск не сработал: {result.Error} {result.Detail}");

        var effect = JsonDocument.Parse(result.EffectJson()).RootElement;
        var rows = effect.GetProperty("зарядки").EnumerateArray().Select(x => x.GetString()!).ToList();

        // How many stations the chassis actually fits into are really standing on the grid.
        var real = await w.Read(() =>
        {
            var whitelist = ent.System<EntityWhitelistSystem>();
            var grid = ent.GetComponent<TransformComponent>(borg).GridUid;
            var count = 0;

            var q = ent.EntityQueryEnumerator<ChargerComponent>();

            while (q.MoveNext(out var uid, out var charger))
            {
                if (ent.GetComponent<TransformComponent>(uid).GridUid != grid)
                    continue;

                if (charger.Whitelist != null && whitelist.IsValid(charger.Whitelist, borg))
                    count++;
            }

            return count;
        });

        TestContext.Out.WriteLine($"на сетке станций для шасси: {real}");

        Assert.Multiple(() =>
        {
            Assert.That(real, Is.GreaterThan(0), "на карте ротации нет ни одной станции для киборгов — сцена не та");
            Assert.That(rows, Has.Count.EqualTo(real), "найдено не столько станций, сколько стоит на сетке");

            // Desktop battery chargers must not make it into the list: they have their own slot
            // and their own whitelist, and a robot that goes to one wastes turns for nothing.
            foreach (var row in rows)
                Assert.That(row, Does.Match(@"^-?\d+,-?\d+ \| \d+ тайлов \| (запитана|ОБЕСТОЧЕНА)$"),
                    $"строка не разбирается на координаты: «{row}»");
        });

        // The nearest one must come first — otherwise a robot at zero charge would walk across
        // half the station past the one right next door.
        var distances = rows
            .Select(r => int.Parse(r.Split('|')[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture))
            .ToList();

        Assert.That(distances, Is.Ordered, "список не отсортирован по расстоянию");
    }

    /// <summary>
    /// The AME layout: the console stays outside, the approach to it is clear, and placement
    /// retreats toward the exit.
    /// </summary>
    /// <remarks>
    /// Three conditions — three live runs, each of which ended in nothing: a ring around the
    /// console (zero cores), a blocked approach (reactor assembled, nothing to turn injection on
    /// with), and a robot that walled itself in with the ninth shield. Here they're all checked
    /// at once and without a model.
    /// </remarks>
    [Test]
    public async Task AmePlan_KeepsControllerOutside_AndLeavesAWayOut()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;

        var borg = EntityUid.Invalid;
        await w.Pair.Server.WaitPost(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(sys.TrySpawnBorg("AME", out borg, out var placed), Is.True, placed);
            Assert.That(sys.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(30);

        var plan = await w.InvokeOn(borg, "ame_plan");
        TestContext.Out.WriteLine("план: " + plan.EffectJson());

        Assert.That(plan.Ok, Is.True, $"план не построился: {plan.Error} {plan.Detail}");

        var effect = JsonDocument.Parse(plan.EffectJson()).RootElement;

        Vector2i Tile(string raw)
        {
            var parts = raw.Split(',');
            return new Vector2i(int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        var order = effect.GetProperty("порядок").EnumerateArray().Select(x => Tile(x.GetString()!)).ToList();
        var ctrl = Tile(effect.GetProperty("пульт").GetString()!);
        var exit = Tile(effect.GetProperty("выход").GetString()!);
        var approach = Tile(effect.GetProperty("подход_к_пульту").GetString()!);

        Assert.Multiple(() =>
        {
            Assert.That(order, Has.Count.EqualTo(9), "квадрат 3x3 — это девять клеток, иначе ядра не будет");
            Assert.That(order.Distinct().Count(), Is.EqualTo(9), "в раскладке повторяются клетки");
            Assert.That(order, Does.Not.Contain(ctrl), "пульт попал внутрь квадрата — ядром станет он, то есть никто");
            Assert.That(order, Does.Not.Contain(exit), "выход внутри квадрата — это не выход");
            Assert.That(order, Does.Not.Contain(approach), "подход к пульту застроен — до консоли будет не добраться");

            // The square must touch the console edge-to-edge, otherwise it won't end up in the
            // same node network as the console.
            var touches = order.Any(c => (Math.Abs(c.X - ctrl.X) + Math.Abs(c.Y - ctrl.Y)) == 1);
            Assert.That(touches, Is.True, "квадрат не примыкает к пульту");

            // The approach tile must be right at the console, not "somewhere nearby."
            Assert.That(Math.Abs(approach.X - ctrl.X) + Math.Abs(approach.Y - ctrl.Y), Is.EqualTo(1),
                "клетка подхода не примыкает к пульту");

            // The main point: each next tile is no farther from the exit than the previous one.
            // The robot retreats rather than working its way deeper in, and on the last tile it
            // stands right at the exit.
            for (var i = 1; i < order.Count; i++)
            {
                var prev = (order[i - 1] - exit).Length;
                var now = (order[i] - exit).Length;
                Assert.That(now, Is.LessThanOrEqualTo(prev + 0.001f),
                    $"шаг {i}: укладка идёт вглубь ({prev:F2} → {now:F2} до выхода), робот запрёт себя");
            }

            Assert.That((order[^1] - exit).Length, Is.LessThanOrEqualTo(1.5f),
                "последняя клетка далеко от выхода — с неё робот наружу не шагнёт");
        });
    }

    /// <summary>
    /// The robot still opens a closed airlock it has no access to, but not a bolted one.
    /// </summary>
    /// <remarks>
    /// A fork-owner rule: we go through any airlock that isn't bolted. The reason — the route
    /// from the engineering wing to the AME on the rotation map goes through
    /// <c>AirlockAtmosphericsGlassLocked</c>, which the chassis has no access to: the robot kept
    /// walking into the door, replanning its route, walking into it again, and over half an hour
    /// of round 135 never made it back to the reactor it had assembled itself. Bolts, meanwhile,
    /// must still hold — otherwise "bolt the compartment shut" stops meaning anything to the
    /// robot, and that's no longer a relaxation of the rules but a hole in them.
    /// </remarks>
    [Test]
    public async Task ClosedAirlock_OpensWithoutAccess_ButNotWhenBolted()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;
        var borg = await SpawnAndClaim(w);

        // The door needs to be POWERED: an unpowered airlock won't open by anything but a
        // crowbar, and that would be testing electricity, not the robot's decision about the door.
        var door = await w.Read(() =>
        {
            var xforms = w.Pair.Server.System<SharedTransformSystem>();

            // The door needs to be a SINGLE one. An airlock tambour can have five at once, the
            // robot presses the one nearest to it, and a test watching one door would read the
            // state of its neighbor — the run would look like "forcing doesn't work" even though
            // the door next to it was genuinely opening.
            var doors = new List<(EntityUid Uid, Vector2 At)>();
            var all = ent.EntityQueryEnumerator<DoorComponent>();

            while (all.MoveNext(out var uid, out _))
                doors.Add((uid, xforms.GetMapCoordinates(uid).Position));

            var q = ent.EntityQueryEnumerator<DoorComponent, AirlockComponent>();

            while (q.MoveNext(out var uid, out var comp, out var airlock))
            {
                if (comp.State != DoorState.Closed || !airlock.Powered)
                    continue;

                var here = xforms.GetMapCoordinates(uid).Position;
                var alone = doors.All(d => d.Uid == uid || (d.At - here).Length() > 2.5f);

                if (alone)
                    return uid;
            }

            return EntityUid.Invalid;
        });

        if (!door.IsValid())
            Assert.Ignore("на карте не нашлось закрытого запитанного шлюза");

        // Whether the robot's permissions let it through gets logged, but the assertion isn't
        // about that. What's checked is the fork-owner rule: bolts hold, everything else opens.
        var allowed = await w.Read(() => ent.System<AccessReaderSystem>().IsAllowed(borg, door));
        TestContext.Out.WriteLine($"права робота на эту створку: {(allowed ? "есть" : "нет")}");

        var at = await w.Read(() => ent.GetComponent<TransformComponent>(door).LocalPosition);

        await w.Post(() =>
        {
            var xforms = w.Pair.Server.System<SharedTransformSystem>();
            xforms.SetLocalPosition(borg, at + new Vector2(0, 1.2f));
        });
        await w.Pair.Server.WaitRunTicks(3);

        // First pass: no bolts — the door must give way.
        var system = w.Pair.Server.System<AiBorgSystem>();

        var pressed = false;
        await w.Post(() => pressed = system.PressDoorForTest(borg));
        await w.Pair.Server.WaitRunTicks(30);

        var gap = await w.Read(() =>
            (ent.GetComponent<TransformComponent>(borg).LocalPosition
             - ent.GetComponent<TransformComponent>(door).LocalPosition).Length());

        TestContext.Out.WriteLine($"нажатие={pressed} расстояние={gap:F2}");

        var direct = await w.Read(() =>
        {
            var doors = ent.System<SharedDoorSystem>();
            var comp = ent.GetComponent<DoorComponent>(door);
            var air = ent.GetComponent<AirlockComponent>(door);
            return $"state={comp.State} powered={air.Powered} bolted={doors.IsBolted(door)} " +
                   $"canOpen={doors.CanOpen(door, comp, null, quiet: true)}";
        });

        TestContext.Out.WriteLine("дверь: " + direct);


        var opened = await w.Read(() => ent.GetComponent<DoorComponent>(door).State);
        TestContext.Out.WriteLine($"без болтов: {opened}");

        Assert.That(opened, Is.Not.EqualTo(DoorState.Closed),
            "незаболченный шлюз не открылся — робот снова упрётся в него на маршруте");

        // Second pass: close it and bolt it. Now the door must hold.
        await w.Post(() =>
        {
            var doors = ent.System<SharedDoorSystem>();
            doors.TryClose(door);
        });
        await w.Pair.Server.WaitRunTicks(30);

        await w.Post(() =>
        {
            var doors = ent.System<SharedDoorSystem>();
            var bolts = ent.EnsureComponent<DoorBoltComponent>(door);
            doors.SetBoltsDown((door, bolts), true);
        });
        await w.Pair.Server.WaitRunTicks(3);

        await w.Post(() => system.PressDoorForTest(borg));
        await w.Pair.Server.WaitRunTicks(30);

        var bolted = await w.Read(() => ent.GetComponent<DoorComponent>(door).State);
        TestContext.Out.WriteLine($"с болтами: {bolted}");

        Assert.That(bolted, Is.EqualTo(DoorState.Closed),
            "заболченный шлюз открылся — болты перестали что-либо значить для робота");
    }

    /// <summary>
    /// The console can be reached from two tiles away, not just at arm's length.
    /// </summary>
    /// <remarks>
    /// The reason is the AME controller on the rotation map: all four orthogonally adjacent
    /// tiles are occupied by cables and a wall, the robot can only stand diagonally, and at 1.5
    /// tiles <c>console</c> answered <c>not_visible</c> while standing right next to the reactor
    /// it had assembled itself. What's checked is precisely the boundary: the console opens from
    /// two tiles, not from four, and the refusal comes back as a meaningful code, not an
    /// exception. The gate remains "arm's length": the obstruction check stays in place, and the
    /// console can't be reached through a wall from any distance.
    /// </remarks>
    [Test]
    public async Task Console_ReachesTwoTiles_ButNotFour()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;
        var borg = await SpawnAndClaim(w);

        var found = await w.Read(() =>
        {
            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeControllerComponent>();
            return q.MoveNext(out var uid, out _) ? uid : EntityUid.Invalid;
        });

        Assert.That(found.IsValid(), Is.True, "на карте нет пульта АМЭ — проверять нечего");

        var handle = await w.Read(() => w.System.HandleFor(borg, found));
        var at = await w.Read(() => ent.GetComponent<TransformComponent>(found).LocalPosition);

        // Eight directions: some of them will run into a wall, and that's fine — the assertion is
        // that AT LEAST one spot at the required distance opens the console.
        var around = new[]
        {
            new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1),
            new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1),
        };

        async Task<bool> AnyOpensAt(float tiles)
        {
            foreach (var dir in around)
            {
                var step = Vector2.Normalize(dir) * tiles;

                await w.Post(() =>
                {
                    var xforms = w.Pair.Server.System<SharedTransformSystem>();
                    xforms.SetLocalPosition(borg, at + step);
                });

                await w.Pair.Server.WaitRunTicks(3);

                var read = await w.InvokeOn(borg, "console", "{\"target\":\"" + handle + "\"}");

                TestContext.Out.WriteLine(
                    $"{tiles:F1} тайла в сторону ({dir.X},{dir.Y}): ok={read.Ok} {read.Error} {read.Detail}");

                if (read.Ok)
                    return true;
            }

            return false;
        }

        Assert.That(await AnyOpensAt(2f), Is.True,
            "с двух тайлов пульт не открылся ни с одной стороны — ворота console снова жмут");

        Assert.That(await AnyOpensAt(4f), Is.False,
            "пульт открылся с четырёх тайлов — это уже не «протянутая рука», а телеуправление");
    }

    [Test]
    [Explicit("длинный сценарий на карте ротации")]
    public async Task Borg_StartsTheReactor()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;

        var borg = EntityUid.Invalid;
        await w.Pair.Server.WaitPost(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(sys.TrySpawnBorg("AME", out borg, out var placed), Is.True, placed);
            Assert.That(sys.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(120);

        // What actually exists on the station: the AME console and fuel jars.
        var found = await w.Read(() =>
        {
            var ctrl = EntityUid.Invalid;
            var jar = EntityUid.Invalid;

            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeControllerComponent>();
            while (q.MoveNext(out var uid, out _))
            {
                ctrl = uid;
                break;
            }

            var j = ent.EntityQueryEnumerator<Content.Shared.Ame.Components.AmeFuelContainerComponent>();
            while (j.MoveNext(out var uid, out _))
            {
                jar = uid;
                break;
            }

            return (ctrl, jar);
        });

        TestContext.Out.WriteLine($"РЕАКТОР: пульт={found.ctrl} канистра={found.jar}");
        Assert.That(found.ctrl.IsValid(), Is.True, "на карте нет пульта АМЭ — сценарий невозможен");

        // State before any intervention.
        var before = await w.Read(() =>
        {
            var c = ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(found.ctrl);
            var injecting = c.Injecting;
            var fuel = c.FuelSlot.Item;
            return $"впрыск={injecting} топливо={(fuel == null ? "нет" : "есть")}";
        });

        TestContext.Out.WriteLine("ДО: " + before);

        var handle = await w.Read(() => w.System.HandleFor(borg, found.ctrl));

        // Walk over to the console.
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + handle + "\"}");

        for (var i = 0; i < 120; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);

            var close = await w.Read(() =>
            {
                var a = ent.GetComponent<TransformComponent>(borg).LocalPosition;
                var bpos = ent.GetComponent<TransformComponent>(found.ctrl).LocalPosition;
                return (a - bpos).Length() < 1.4f;
            });

            if (close)
                break;
        }

        var reached = await w.Read(() =>
        {
            var a = ent.GetComponent<TransformComponent>(borg).LocalPosition;
            var bpos = ent.GetComponent<TransformComponent>(found.ctrl).LocalPosition;
            return (a - bpos).Length();
        });

        TestContext.Out.WriteLine($"ДОШЁЛ: {reached:F1} тайлов до пульта");

        // Read the console.
        var read = await w.InvokeOn(borg, "console", "{\"target\":\"" + handle + "\"}");
        TestContext.Out.WriteLine($"ПУЛЬТ: ok={read.Ok} {read.Error} {read.Detail} {read.EffectJson()}"[..Math.Min(600, $"ПУЛЬТ: ok={read.Ok} {read.Error} {read.Detail} {read.EffectJson()}".Length)]);

        Assert.That(read.Ok, Is.True, $"пульт не читается: {read.Error} {read.Detail}");

        // What actually exists around the reactor: whether the core is assembled and where the fuel is.
        var scene = await w.Read(() =>
        {
            var shields = 0;
            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeShieldComponent>();
            while (q.MoveNext(out _, out _))
                shields++;

            var jars = 0;
            var j = ent.EntityQueryEnumerator<Content.Shared.Ame.Components.AmeFuelContainerComponent>();
            while (j.MoveNext(out _, out _))
                jars++;

            var ctrlPos = ent.GetComponent<TransformComponent>(found.ctrl).LocalPosition;
            var jarPos = found.jar.IsValid()
                ? ent.GetComponent<TransformComponent>(found.jar).LocalPosition.ToString()
                : "нет";

            var flatpacks = 0;
            var f = ent.EntityQueryEnumerator<MetaDataComponent>();
            while (f.MoveNext(out _, out var meta))
            {
                if (meta.EntityPrototype?.ID == "AmePartFlatpack")
                    flatpacks++;
            }

            // Where the flatpacks are: on the floor they can be picked up, in a crate they need
            // opening first.
            var loose = 0;
            var packed = 0;
            var container = ent.System<Robust.Shared.Containers.SharedContainerSystem>();
            var f2 = ent.EntityQueryEnumerator<MetaDataComponent>();
            while (f2.MoveNext(out var uid2, out var m2))
            {
                if (m2.EntityPrototype?.ID != "AmePartFlatpack")
                    continue;

                if (container.IsEntityInContainer(uid2))
                    packed++;
                else
                    loose++;
            }

            return $"экранов={shields} канистр={jars} упаковок={flatpacks} (на полу {loose}, в таре {packed}) пульт={ctrlPos}";
        });

        TestContext.Out.WriteLine("СЦЕНА: " + scene);

        // ---- step 1: get a shielding flatpack out of the crate ----
        var crate = await w.Read(() =>
        {
            var container = ent.System<Robust.Shared.Containers.SharedContainerSystem>();
            var q = ent.EntityQueryEnumerator<MetaDataComponent>();

            while (q.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityPrototype?.ID != "AmePartFlatpack")
                    continue;

                if (!container.TryGetContainingContainer((uid, null, null), out var c))
                    continue;

                return (Crate: c.Owner, Pack: uid);
            }

            return (Crate: EntityUid.Invalid, Pack: EntityUid.Invalid);
        });

        Assert.That(crate.Crate.IsValid(), Is.True, "не нашёл тару с упаковками AME");

        var crateHandle = await w.Read(() => w.System.HandleFor(borg, crate.Crate));
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + crateHandle + "\"}");

        for (var i = 0; i < 150; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);
            var close = await w.Read(() =>
                (ent.GetComponent<TransformComponent>(borg).LocalPosition
                 - ent.GetComponent<TransformComponent>(crate.Crate).LocalPosition).Length() < 1.4f);
            if (close)
                break;
        }

        var openRes = await w.InvokeOn(borg, "use", "{\"target\":\"" + crateHandle + "\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var packLoose = await w.Read(() =>
            !ent.System<Robust.Shared.Containers.SharedContainerSystem>().IsEntityInContainer(crate.Pack));

        TestContext.Out.WriteLine($"ЯЩИК: use ok={openRes.Ok} {openRes.Error}; упаковка доступна={packLoose}");

        // Only the SELECTED module provides hands: while the tool module is selected, every hand
        // is occupied by an unremovable tool and nothing can be picked up.
        var mod = await w.InvokeOn(borg, "module", "{\"name\":\"manipulator\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var hands = await w.Read(() =>
        {
            var hs = w.Pair.Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
            var free = hs.TryGetEmptyHand(borg, out _);
            var chassis = ent.GetComponent<Content.Shared.Silicons.Borgs.Components.BorgChassisComponent>(borg);
            var sel = chassis.SelectedModule;
            return $"модуль={(sel == null ? "нет" : ent.GetComponent<MetaDataComponent>(sel.Value).EntityName)} свободная рука={free}";
        });

        TestContext.Out.WriteLine($"МОДУЛЬ: ok={mod.Ok} {mod.Error} {mod.Detail} | {hands}");

        var packHandle = await w.Read(() => w.System.HandleFor(borg, crate.Pack));
        var got = await w.InvokeOn(borg, "pickup", "{\"target\":\"" + packHandle + "\"}");

        TestContext.Out.WriteLine($"ВЗЯЛ УПАКОВКУ: ok={got.Ok} {got.Error} {got.Detail}");
        Assert.That(got.Ok, Is.True, "робот не смог взять упаковку");

        // ---- step 2: carry it to the console and deploy it into a shield ----
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + handle + "\"}");

        for (var i = 0; i < 150; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);
            var close = await w.Read(() =>
                (ent.GetComponent<TransformComponent>(borg).LocalPosition
                 - ent.GetComponent<TransformComponent>(found.ctrl).LocalPosition).Length() < 1.6f);
            if (close)
                break;
        }

        var dropped = await w.InvokeOn(borg, "drop", "{}");
        await w.Pair.Server.WaitRunTicks(5);

        // The crowbar belongs to the tool module, but the robot carried the item with the
        // manipulator: it needs to switch back to tools before unpacking.
        var back = await w.InvokeOn(borg, "module", "{\"name\":\"tool\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var packHandle2 = await w.Read(() => w.System.HandleFor(borg, crate.Pack));
        // The flatpack needs to be PROBED — a multitool, not a crowbar. The tool is named explicitly.
        var unpacked = await w.InvokeOn(borg, "use",
            "{\"target\":\"" + packHandle2 + "\",\"tool\":\"multitool\"}");

        await w.Pair.Server.WaitRunTicks(30);

        var shields = await w.Read(() =>
        {
            var n = 0;
            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeShieldComponent>();
            while (q.MoveNext(out _, out _))
                n++;
            return n;
        });

        TestContext.Out.WriteLine(
            $"РАЗВЕРНУЛ: drop={dropped.Ok} module={back.Ok} use={unpacked.Ok} {unpacked.Error} {unpacked.Detail}; экранов теперь {shields}");

        Assert.That(shields, Is.GreaterThan(0), "упаковка не развернулась в экранирование");

        // ---- step 3: fuel ----
        var jarInfo = await w.Read(() =>
        {
            var q = ent.EntityQueryEnumerator<Content.Shared.Ame.Components.AmeFuelContainerComponent>();
            return q.MoveNext(out var uid, out _) ? uid : EntityUid.Invalid;
        });

        Assert.That(jarInfo.IsValid(), Is.True, "на карте нет канистры с топливом");

        await w.InvokeOn(borg, "module", "{\"name\":\"manipulator\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var jarHandle = await w.Read(() => w.System.HandleFor(borg, jarInfo));
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + jarHandle + "\"}");

        for (var i = 0; i < 200; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);
            var close = await w.Read(() =>
                (ent.GetComponent<TransformComponent>(borg).LocalPosition
                 - _worldOf(ent, jarInfo)).Length() < 1.5f);
            if (close)
                break;
        }

        var tookJar = await w.InvokeOn(borg, "pickup", "{\"target\":\"" + jarHandle + "\"}");
        TestContext.Out.WriteLine($"ТОПЛИВО: взял={tookJar.Ok} {tookJar.Error} {tookJar.Detail}");

        // ---- step 4: insert it and turn on injection ----
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + handle + "\"}");

        for (var i = 0; i < 200; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);
            var close = await w.Read(() =>
                (ent.GetComponent<TransformComponent>(borg).LocalPosition
                 - ent.GetComponent<TransformComponent>(found.ctrl).LocalPosition).Length() < 1.5f);
            if (close)
                break;
        }

        var inserted = await w.InvokeOn(borg, "use",
            "{\"target\":\"" + handle + "\",\"with_item\":true}");

        await w.Pair.Server.WaitRunTicks(10);

        var fuelIn = await w.Read(() =>
            ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(found.ctrl).FuelSlot.Item != null);

        TestContext.Out.WriteLine($"ВСТАВИЛ: ok={inserted.Ok} {inserted.Error}; топливо в пульте={fuelIn}");

        var toggled = await w.InvokeOn(borg, "console",
            "{\"target\":\"" + handle + "\",\"action\":\"ui_button_pressed\",\"args\":{\"button\":\"ToggleInjection\"}}");

        await w.Pair.Server.WaitRunTicks(30);

        var final = await w.Read(() =>
        {
            var c = ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(found.ctrl);
            return (c.Injecting, Fuel: c.FuelSlot.Item != null);
        });

        TestContext.Out.WriteLine(
            $"ЗАПУСК: кнопка ok={toggled.Ok} {toggled.Error} {toggled.Detail}; впрыск={final.Injecting} топливо={final.Fuel}");

        Assert.That(final.Injecting, Is.True, "реактор не запущен: впрыск не включился");

        TestContext.Out.WriteLine("ИТОГ: РЕАКТОР ЗАПУЩЕН РОБОТОМ — экранирование собрано, топливо " +
                                  "вставлено, впрыск включён.");
    }

    /// <summary>An item's position in grid coordinates, even if it's sitting inside a container.</summary>

    /// <summary>
    /// Full shielding assembly: nine flatpacks turn into a 3×3 square that gets a core.
    ///
    /// <para>
    /// Why this is separate from <see cref="Borg_StartsTheReactor"/>. That test proves the second
    /// half of the job — fuel and starting injection — but gets by with a SINGLE flatpack, and one
    /// flatpack doesn't produce a core: a tile becomes the core only when all eight of its
    /// neighbors are also shielding (<c>AmeNodeGroup.LoadNodes</c>). In other words injection
    /// would turn on while output stayed at zero, and that is exactly what the agent could never
    /// pull off in live runs.
    /// </para>
    /// <para>
    /// This test uses the same tools as the model, in the same order recorded in the skill: from
    /// the far tile toward the exit, stepping back before unpacking. If it's green, the robot has
    /// all the tools it needs, and a live run only tests the model's judgment from there. If it's
    /// red, it names exactly the missing step.
    /// </para>
    /// <para>
    /// <b>Right now it's red, and that's its job.</b> It caught something invisible in every other
    /// scenario: <c>goto</c> by coordinates does NOT PLACE the robot on the requested tile. Lines
    /// like "DID NOT REACH (29,-41): stopped at (28,-41)" repeat all nine times, while <c>goto</c>
    /// still reports success. For "approach the door" a one-tile miss is unnoticeable and never
    /// surfaced; for construction it's fatal — flatpacks land in the wrong spot, the square never
    /// closes, no core appears. Part of the cause has already been found and removed (the arrival
    /// threshold for the last tile was more than half a tile), but the miss remains, and the root
    /// sits deeper — in how the route picks its target tile. The test is left red on purpose: it
    /// is itself the statement of the problem.
    /// </para>
    /// </summary>
    [Test]
    [Explicit("длинный сценарий на карте ротации")]
    public async Task Borg_AssemblesTheReactor_AndStartsIt()
    {
        await using var w = await AiStation.CreateOnMap("Packed");
        var ent = w.Ent;

        var borg = EntityUid.Invalid;
        await w.Pair.Server.WaitPost(() =>
        {
            var sys = w.Pair.Server.System<AiBorgSystem>();
            Assert.That(sys.TrySpawnBorg("AME", out borg, out var placed), Is.True, placed);
            Assert.That(sys.TryClaim(borg, out var why), Is.True, why);
        });

        await w.Pair.Server.WaitRunTicks(120);

        var controller = await w.Read(() =>
        {
            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeControllerComponent>();
            return q.MoveNext(out var uid, out _) ? uid : EntityUid.Invalid;
        });

        Assert.That(controller.IsValid(), Is.True, "на карте нет пульта АМЭ");

        // ---- step 1: open the crate with the flatpacks ----
        var crate = await w.Read(() =>
        {
            var container = ent.System<Robust.Shared.Containers.SharedContainerSystem>();
            var q = ent.EntityQueryEnumerator<MetaDataComponent>();

            while (q.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityPrototype?.ID != "AmePartFlatpack")
                    continue;

                if (!container.TryGetContainingContainer((uid, null, null), out var c))
                    continue;

                return c.Owner;
            }

            return EntityUid.Invalid;
        });

        Assert.That(crate.IsValid(), Is.True, "не нашёл тару с упаковками AME");

        var crateHandle = await w.Read(() => w.System.HandleFor(borg, crate));
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + crateHandle + "\"}");
        await WalkUntilNear(w, borg, crate, 1.4f);
        // Pressing the crate is a toggle, and the first press removes the ID lock rather than
        // opening it. This is exactly where the agent used to waste turns: pressing over and over
        // and seeing an empty floor.
        var loose = 0;

        for (var attempt = 0; attempt < 4 && loose == 0; attempt++)
        {
            var press = await w.InvokeOn(borg, "use", "{\"target\":\"" + crateHandle + "\"}");
            await w.Pair.Server.WaitRunTicks(10);
            loose = await w.Read(() => LoosePacks(ent).Count);
            TestContext.Out.WriteLine($"ЯЩИК: нажатие {attempt + 1} ok={press.Ok} {press.Error}; на полу {loose}");
        }

        TestContext.Out.WriteLine($"ЯЩИК: упаковок на полу {loose}");
        Assert.That(loose, Is.GreaterThanOrEqualTo(9), "для квадрата 3×3 нужно девять упаковок");

        // ---- step 2: pick a spot for the square ----
        var square = await w.Read(() => FindSquare(ent, w.Grid, controller));
        Assert.That(square, Is.Not.Null, "рядом с пультом нет свободного места 3×3");

        TestContext.Out.WriteLine("КВАДРАТ: " + string.Join(" ", square!.Select(c => $"({c.X},{c.Y})")));

        var shieldsBefore = await w.Read(() => CountShields(ent));
        TestContext.Out.WriteLine($"ЩИТОВ ДО НАЧАЛА: {shieldsBefore}");

        // ---- step 3: lay out and unpack, retreating toward the exit ----
        var built = 0;

        foreach (var cell in square!)
        {
            var pack = await w.Read(() => LoosePacks(ent).FirstOrDefault());
            TestContext.Out.WriteLine($"КЛЕТКА ({cell.X},{cell.Y}): беру упаковку {pack}");

            if (!pack.IsValid())
            {
                TestContext.Out.WriteLine("упаковки кончились");
                break;
            }

            var packHandle = await w.Read(() => w.System.HandleFor(borg, pack));

            await w.InvokeOn(borg, "module", "{\"name\":\"manipulator\"}");
            await w.InvokeOn(borg, "goto", "{\"to\":\"" + packHandle + "\"}");
            await WalkUntilNear(w, borg, pack, 1.4f);

            var took = await w.InvokeOn(borg, "pickup", "{\"target\":\"" + packHandle + "\"}");
            if (!took.Ok)
            {
                TestContext.Out.WriteLine($"({cell.X},{cell.Y}): не взял упаковку — {took.Error} {took.Detail}");
                break;
            }

            // Three attempts to get there, not one.
            //
            // Walking sometimes doesn't get there on the first try: the robot runs into another
            // body, a door, or cargo it just set down itself. Dropping the flatpack wherever it
            // stopped would silently ruin the square; retrying is exactly what a sensible agent
            // would do.
            var arrived = false;

            for (var tries = 0; tries < 3 && !arrived; tries++)
            {
                var goRes = await w.InvokeOn(borg, "goto", "{\"to\":\"" + cell.X + "," + cell.Y + "\"}");
                arrived = await WalkUntilAt(w, borg, cell);

                if (!arrived)
                {
                    var stoppedAt = await w.Read(() => ToTile(_worldOf(ent, borg)));
                    TestContext.Out.WriteLine(
                        $"НЕ ДОШЁЛ до ({cell.X},{cell.Y}), попытка {tries + 1}: встал на ({stoppedAt.X},{stoppedAt.Y}); goto ok={goRes.Ok} {goRes.Error} {goRes.Detail}");
                }
            }

            await w.InvokeOn(borg, "drop");
            await w.Pair.Server.WaitRunTicks(5);

            // A step back BEFORE unpacking: unpack it right under yourself and you end up inside the wall.
            await w.InvokeOn(borg, "step", "{\"dir\":\"юг\",\"count\":1}");
            await w.Pair.Server.WaitRunTicks(20);

            await w.InvokeOn(borg, "module", "{\"name\":\"tool\"}");

            // The waiting version — the same one seen as use in scripted mode. The plain one
            // returns as soon as the action HAS STARTED, and a test that only looks at ok would
            // mistake "started" for "done."
            var unpacked = await w.InvokeOn(borg, "use_wait",
                "{\"target\":\"" + packHandle + "\",\"tool\":\"multitool\"}");
            await w.Pair.Server.WaitRunTicks(30);

            var shields = await w.Read(() => CountShields(ent));
            TestContext.Out.WriteLine(
                $"({cell.X},{cell.Y}): распаковка ok={unpacked.Ok} {unpacked.Error} {unpacked.EffectJson()}; щитов теперь {shields}");

            built = shields;
        }

        // ---- step 4: did a core appear ----
        var cores = await w.Read(() =>
        {
            var n = 0;
            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeShieldComponent>();

            while (q.MoveNext(out _, out var shield))
            {
                if (shield.IsCore)
                    n++;
            }

            return n;
        });

        var where = await w.Read(() =>
        {
            var list = new List<string>();
            var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeShieldComponent>();

            while (q.MoveNext(out var uid, out var shield))
            {
                var at = ToTile(_worldOf(ent, uid));
                list.Add($"({at.X},{at.Y}){(shield.IsCore ? "*" : "")}");
            }

            return string.Join(" ", list);
        });

        TestContext.Out.WriteLine("ЩИТЫ СТОЯТ: " + where);
        TestContext.Out.WriteLine($"ИТОГ: щитов {built}, ядер {cores}");

        Assert.Multiple(() =>
        {
            Assert.That(built, Is.GreaterThanOrEqualTo(9), "квадрат не собрался: щитов меньше девяти");
            Assert.That(cores, Is.GreaterThanOrEqualTo(1), "щиты есть, а ядра нет — квадрат сложен неправильно");
        });

        // ---- step 5: fuel and startup ----
        var jar = await w.Read(() =>
        {
            var q = ent.EntityQueryEnumerator<Content.Shared.Ame.Components.AmeFuelContainerComponent>();
            return q.MoveNext(out var uid, out _) ? uid : EntityUid.Invalid;
        });

        Assert.That(jar.IsValid(), Is.True, "на карте нет канистры с топливом");

        // The fuel jar can be sitting in its own crate — that needs opening too.
        var jarCrate = await w.Read(() =>
        {
            var container = ent.System<Robust.Shared.Containers.SharedContainerSystem>();
            return container.TryGetContainingContainer((jar, null, null), out var c) ? c.Owner : EntityUid.Invalid;
        });

        if (jarCrate.IsValid())
        {
            var jarCrateHandle = await w.Read(() => w.System.HandleFor(borg, jarCrate));
            await w.InvokeOn(borg, "goto", "{\"to\":\"" + jarCrateHandle + "\"}");
            await WalkUntilNear(w, borg, jarCrate, 1.4f);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                await w.InvokeOn(borg, "use", "{\"target\":\"" + jarCrateHandle + "\"}");
                await w.Pair.Server.WaitRunTicks(10);

                var out2 = await w.Read(() =>
                    !ent.System<Robust.Shared.Containers.SharedContainerSystem>().IsEntityInContainer(jar));

                if (out2)
                    break;
            }
        }

        await w.InvokeOn(borg, "module", "{\"name\":\"manipulator\"}");

        var jarHandle = await w.Read(() => w.System.HandleFor(borg, jar));
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + jarHandle + "\"}");
        await WalkUntilNear(w, borg, jar, 1.4f);

        var tookJar = await w.InvokeOn(borg, "pickup", "{\"target\":\"" + jarHandle + "\"}");
        TestContext.Out.WriteLine($"КАНИСТРА: взял ok={tookJar.Ok} {tookJar.Error} {tookJar.Detail}");

        var ctrlHandle = await w.Read(() => w.System.HandleFor(borg, controller));
        await w.InvokeOn(borg, "goto", "{\"to\":\"" + ctrlHandle + "\"}");
        await WalkUntilNear(w, borg, controller, 1.4f);

        // Only using what you're holding inserts it: a plain click opens the UI screen, and the
        // fuel jar stays in hand. On live runs the agent used to waste a dozen turns on this.
        var inserted = await w.InvokeOn(borg, "use_wait",
            "{\"target\":\"" + ctrlHandle + "\",\"with_item\":true}");

        var fuelIn = await w.Read(() =>
            ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(controller).FuelSlot.Item != null);

        TestContext.Out.WriteLine($"ТОПЛИВО: вставка ok={inserted.Ok} {inserted.EffectJson()}; в слоте={fuelIn}");

        // Injection is dialed in to exactly a safe value — twice the core count.
        //
        // Not "press it a couple times": the console has its own starting value, each press
        // changes the strength by ±2, and it will accept up to CoreCount * 8. Cranking it to the
        // ceiling means assembling the reactor and immediately blowing it up. So we read the
        // current value and nudge it in the right direction — exactly what an engineer would do,
        // not "mash the button and walk away."
        var safe = cores * 2;

        for (var i = 0; i < 6; i++)
        {
            var now = await w.Read(() =>
                ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(controller).InjectionAmount);

            if (now == safe)
                break;

            var button = now < safe ? "IncreaseFuel" : "DecreaseFuel";

            await w.InvokeOn(borg, "console",
                "{\"target\":\"" + ctrlHandle + "\",\"action\":\"ui_button_pressed\",\"args\":{\"button\":\"" + button + "\"}}");
            await w.Pair.Server.WaitRunTicks(5);
        }

        var toggled = await w.InvokeOn(borg, "console",
            "{\"target\":\"" + ctrlHandle + "\",\"action\":\"ui_button_pressed\",\"args\":{\"button\":\"ToggleInjection\"}}");

        await w.Pair.Server.WaitRunTicks(30);

        var final = await w.Read(() =>
        {
            var c = ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(controller);
            return (c.Injecting, c.InjectionAmount, Fuel: c.FuelSlot.Item != null);
        });

        TestContext.Out.WriteLine(
            $"ЗАПУСК: кнопка ok={toggled.Ok} {toggled.Error} {toggled.Detail}; впрыск={final.Injecting} " +
            $"сила={final.InjectionAmount} топливо={final.Fuel}");

        Assert.Multiple(() =>
        {
            Assert.That(final.Fuel, Is.True, "канистра не встала в контроллер");
            Assert.That(final.Injecting, Is.True, "реактор собран, но впрыск не включился");
            Assert.That(final.InjectionAmount, Is.GreaterThan(0),
                "впрыск включён, но сила нулевая — щиты и пульт в разных узловых сетях");
            Assert.That(final.InjectionAmount, Is.LessThanOrEqualTo(cores * 2),
                $"сила {final.InjectionAmount} выше безопасной при {cores} ядрах — это перегрев");
        });
    }


    /// <summary>Flatpacks lying on the floor, not in a crate.</summary>
    private static List<EntityUid> LoosePacks(IEntityManager ent)
    {
        var container = ent.System<Robust.Shared.Containers.SharedContainerSystem>();
        var found = new List<EntityUid>();
        var q = ent.EntityQueryEnumerator<MetaDataComponent>();

        while (q.MoveNext(out var uid, out var meta))
        {
            if (meta.EntityPrototype?.ID == "AmePartFlatpack" && !container.IsEntityInContainer(uid))
                found.Add(uid);
        }

        return found;
    }

    private static int CountShields(IEntityManager ent)
    {
        var n = 0;
        var q = ent.EntityQueryEnumerator<Content.Server.Ame.Components.AmeShieldComponent>();

        while (q.MoveNext(out _, out _))
            n++;

        return n;
    }

    /// <summary>
    /// Nine 3×3 tiles near the console, in "far row to exit" order.
    ///
    /// The order here is not decoration: it's exactly what's recorded in the skill, and it's
    /// precisely what keeps the robot from walling itself in. The far row is placed first, the
    /// near row last, and after that the robot is already outside.
    /// </summary>
    private static List<Vector2i>? FindSquare(IEntityManager ent, EntityUid grid, EntityUid controller)
    {
        var maps = ent.System<SharedMapSystem>();
        var lookup = ent.System<EntityLookupSystem>();
        var gridComp = ent.GetComponent<MapGridComponent>(grid);
        var origin = ToTile(_worldOf(ent, controller));

        var navMap = ent.GetComponent<Content.Shared.Pinpointer.NavMapComponent>(grid);

        bool Free(Vector2i tile)
        {
            // By the same yardstick the robot uses.
            //
            // The first version checked a tile its own way — has floor, no static collision — and
            // picked a square some of whose tiles the router considered impassable. The robot
            // genuinely detoured to a neighboring tile, flatpacks landed in the wrong spot, and it
            // looked like the robot's mistake. A test that measures with a different instrument
            // than the one it's checking only finds its own bugs.
            if (!Content.Server.AiAgent.Borg.BorgPathfinder.Passable(navMap, tile))
                return false;

            if (!maps.TryGetTileRef(grid, gridComp, tile, out var tileRef) || tileRef.Tile.IsEmpty)
                return false;

            var box = new Box2(tile.X + 0.1f, tile.Y + 0.1f, tile.X + 0.9f, tile.Y + 0.9f);
            var here = new HashSet<EntityUid>();
            lookup.GetLocalEntitiesIntersecting(grid, box, here, LookupFlags.Static | LookupFlags.Approximate);

            foreach (var uid in here)
            {
                if (!ent.TryGetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(uid, out var body))
                    continue;

                if (body.CanCollide && body.Hard && body.BodyType == Robust.Shared.Physics.BodyType.Static)
                    return false;
            }

            return true;
        }

        // Squares around the console, nearest ones first.
        for (var dy = -4; dy <= 2; dy++)
        {
            for (var dx = -4; dx <= 2; dx++)
            {
                var corner = new Vector2i(origin.X + dx, origin.Y + dy);
                var cells = new List<Vector2i>();
                var ok = true;

                for (var y = 2; y >= 0 && ok; y--)
                {
                    for (var x = 0; x < 3 && ok; x++)
                    {
                        var tile = new Vector2i(corner.X + x, corner.Y + y);
                        if (!Free(tile))
                            ok = false;
                        else
                            cells.Add(tile);
                    }
                }

                if (!ok)
                    continue;

                // A free tile is needed below the square: the robot retreats there before unpacking.
                if (!Free(new Vector2i(corner.X + 1, corner.Y - 1)))
                    continue;

                // And the square must be ADJACENT to the console.
                //
                // Otherwise the shields and the controller end up in different node networks: the
                // shields have a core, but the console doesn't know about it, and
                // GetMaxInjectionAmount (CoreCount * 8 for the CONSOLE's group) returns zero. On
                // the surface this looks like mockery: the reactor is assembled, injection is on,
                // strength is zero. The skill states the requirement, and the test wasn't checking it.
                var touches = cells.Any(c =>
                    Math.Abs(c.X - origin.X) + Math.Abs(c.Y - origin.Y) == 1);

                if (!touches)
                    continue;

                // And there must still be a way to approach the console.
                //
                // The square must be adjacent — but if it eats up ALL of the console's free
                // neighbors, there's no way to get to the console itself: no inserting the fuel
                // jar, no pressing the button. The robot diligently assembles the reactor and
                // locks itself out of it. It's the same mistake as "don't build around yourself,"
                // just from the other side.
                var approach = new[]
                {
                    new Vector2i(origin.X + 1, origin.Y), new Vector2i(origin.X - 1, origin.Y),
                    new Vector2i(origin.X, origin.Y + 1), new Vector2i(origin.X, origin.Y - 1),
                };

                if (approach.Any(a => Free(a) && !cells.Contains(a)))
                    return cells;
            }
        }

        return null;
    }

    /// <summary>Wait until the robot gets closer to the target than <paramref name="range"/>.</summary>
    private static async Task WalkUntilNear(AiStation w, EntityUid borg, EntityUid target, float range)
    {
        for (var i = 0; i < 200; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);

            var close = await w.Read(() =>
                (_worldOf(w.Ent, borg) - _worldOf(w.Ent, target)).Length() < range);

            if (close)
                return;
        }
    }

    /// <summary>Wait until the robot lands exactly on the tile.</summary>
    private static async Task<bool> WalkUntilAt(AiStation w, EntityUid borg, Vector2i cell)
    {
        for (var i = 0; i < 200; i++)
        {
            await w.Pair.Server.WaitRunTicks(10);

            var there = await w.Read(() => ToTile(_worldOf(w.Ent, borg)) == cell);
            if (there)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Position converted to a TILE NUMBER, rounding down.
    ///
    /// A cast to (Vector2i) truncates the fractional part TOWARD ZERO, not downward: -41.5
    /// becomes -41, even though the tile is -42. Half the station on this map lives in negative
    /// coordinates, so this kind of cast fails exactly where all the work happens. The assembly
    /// test silently laid its flatpacks off to the side because of this: the "landed on the tile"
    /// check never once matched.
    /// </summary>
    private static Vector2i ToTile(Vector2 position) =>
        new((int) MathF.Floor(position.X), (int) MathF.Floor(position.Y));

    private static Vector2 _worldOf(IEntityManager ent, EntityUid uid)
    {
        var xform = ent.GetComponent<TransformComponent>(uid);
        var parent = xform.ParentUid;

        while (parent.IsValid() && !ent.HasComponent<MapGridComponent>(parent))
        {
            xform = ent.GetComponent<TransformComponent>(parent);
            parent = xform.ParentUid;
        }

        return xform.LocalPosition;
    }

    /// <summary>
    /// <c>use</c> explains the outcome instead of answering with a bare "ok."
    /// </summary>
    /// <remarks>
    /// A direct regression from a run where the robot made 520 calls in a row hitting a crate
    /// with a crowbar when the crate opens with a click. The tool responded <c>ok</c> and "state
    /// unchanged" — but that's three different outcomes under one label: a long action started;
    /// something changed; the action doesn't apply. The model picked the wrong approach and got
    /// no signal about it whatsoever.
    /// </remarks>
    [Test]
    public async Task Use_ExplainsWhatHappened()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        var crate = EntityUid.Invalid;
        var door = EntityUid.Invalid;

        await w.Pair.Server.WaitPost(() =>
        {
            var where = ent.GetComponent<TransformComponent>(borg).Coordinates;
            crate = ent.SpawnEntity("CrateGenericSteel", where.Offset(new Vector2(1, 0)));
            // The airlock is placed ON THE ROBOT'S OWN TILE, and that's not carelessness.
            //
            // The adjacent tile is guaranteed nothing: the robot spawns at an arbitrary beacon,
            // and above/below/beside it can easily be a wall. An airlock placed into a wall looks
            // healthy on every field — Powered=True, State=Closed, ClickOpen=True, anchored=True,
            // distance exactly one tile — but InteractionActivate never reaches it:
            // InRangeUnobstructed's ray runs into that same wall, silently returns false, and the
            // tool honestly reports "nothing changed." This is exactly what diagnostics caught on
            // 21.08.2026.
            //
            // The robot's own tile is passable by construction: TryFreeTileNear picked it for
            // exactly that reason. This test is about WHAT the tool reports about the outcome,
            // not about map geometry, and there's no reason to prop it up with luck on coordinates.
            door = ent.SpawnEntity("Airlock", where);

            // THE AIRLOCK'S POWER IS SET EXPLICITLY, and that's a fix, not a convenience.
            //
            // An unpowered airlock doesn't open on a click AT ALL: SharedAirlockSystem.CanChangeState
            // requires Powered, otherwise BeforeDoorOpenedEvent gets cancelled. And a freshly
            // spawned airlock placed on an arbitrary tile isn't connected to the local APC —
            // there's no cable under it.
            //
            // The test used to not know this and was green PURELY BY LUCK: the robot spawned at a
            // different point on the map where the tile happened to be powered. On 20.08.2026
            // TryFindGrid started filtering the grid by StationMemberComponent, the spawn point
            // shifted — and the assertion "the door must open" stopped holding. The test should
            // check the tool, not luck with coordinates.
            if (ent.TryGetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(
                    door, out var recv))
            {
                recv.NeedsPower = false;
            }
        });

        // Ten ticks, not five: power reaches the door via a separate event from the power grid,
        // and at five ticks it sometimes didn't make it in time.
        await w.Pair.Server.WaitRunTicks(10);

        var handle = await w.Read(() => w.System.HandleFor(borg, crate));

        // THE FAILURE PATH — and now it's genuinely about the tool.
        //
        // The previous version claimed in its comment "crowbar on the crate," but actually
        // clicked the crate and waited for the click to be refused. The crate opens on a click,
        // so the assertion was checking its own typo and stayed green only for as long as
        // clicking hadn't started working. This was a false green, not a regression.
        await w.InvokeOn(borg, "module", "{\"name\":\"tool\"}");

        var wrong = await w.InvokeOn(borg, "use",
            "{\"target\":\"" + handle + "\",\"tool\":\"multitool\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var wrongJson = wrong.EffectJson();
        TestContext.Out.WriteLine("МУЛЬТИТУЛОМ: " + wrongJson[..Math.Min(300, wrongJson.Length)]);

        Assert.That(wrong.Ok, Is.True, $"use отказал: {wrong.Error} {wrong.Detail}");
        Assert.That(wrongJson, Does.Contain("НЕ ПОЛУЧИЛОСЬ").And.Contain("почему"),
            "ничего не вышло, а причина не названа");

        // THE SUCCESS PATH, the crate. Clicking goes through InteractionActivate, so a tool in
        // hand doesn't get in its way — the selected module is irrelevant here.
        var pressed = await w.InvokeOn(borg, "use", "{\"target\":\"" + handle + "\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var json = pressed.EffectJson();
        TestContext.Out.WriteLine("НАЖАЛ: " + json[..Math.Min(300, json.Length)]);

        Assert.That(pressed.Ok, Is.True, $"use отказал: {pressed.Error} {pressed.Detail}");

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("итог"),
                "в ответе нет исхода — модель снова увидит голое ok");
            Assert.That(json, Does.Contain("получилось"),
                "ящик открылся, а инструмент об этом не сказал");
            Assert.That(json, Does.Contain("открылось"),
                "не назван характер изменения");
        });

        // THE SUCCESS PATH, the door: clicking changes the door's state, and that must be reported.
        //
        // The door on the robot's tile opens on its own, from body contact (DoorBumpOpener), so
        // the click actually CLOSES it: "Open → Closing." What's checked is exactly the point of
        // this test — that the tool names the state change instead of answering with a bare ok.
        // Which direction the change goes doesn't matter here and is deliberately not pinned
        // down: if we pinned down "opened," the test would once again be resting on an incidental
        // detail.
        var doorHandle = await w.Read(() => w.System.HandleFor(borg, door));
        var opened = await w.InvokeOn(borg, "use", "{\"target\":\"" + doorHandle + "\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var openJson = opened.EffectJson();
        TestContext.Out.WriteLine("ДВЕРЬ: " + openJson[..Math.Min(280, openJson.Length)]);

        Assert.Multiple(() =>
        {
            Assert.That(openJson, Does.Contain("получилось"),
                "створка сменила состояние, а инструмент об этом не сказал");
            Assert.That(openJson, Does.Contain("дверь:"),
                "не назван характер изменения — модель снова не поймёт, сработало ли");
        });
    }
}
