using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.RogueAi;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.Station.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Silicons.Laws;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Station.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// Rogue AI mode: access, jobs, laws, personality.
///
/// <para>
/// The bench is a real station (<see cref="AiStation"/>, the Box map), and that's not a luxury
/// here but a requirement: the file's central check is that access grants do NOT leak beyond
/// the station, and for "beyond the station" to even exist, the world needs a second grid. On a
/// thirteen-tile test rig there would be nothing to check.
/// </para>
/// <para>
/// The rule is started via the real <c>GameTicker.StartGameRule</c> on the real prototype, not a
/// component constructed in the test. This also exercises the YAML: a typo in the lawset id or
/// the personality file name is the quietest way this mode can break, because the round starts
/// fine with it — the AI just turns out to be an ordinary one.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class RogueAiTests
{
    private static readonly EntProtoId OpenRule = "RogueAiOpenRule";
    private static readonly EntProtoId HiddenRule = "RogueAiHiddenRule";

    /// <summary>
    /// Access is granted on the station and NOWHERE else.
    /// </summary>
    /// <remarks>
    /// The most likely and most inconspicuous way this mode can break is access quietly spreading
    /// to other grids: CentCom, the evac shuttle, outposts. In game it looks like "the AI can
    /// somehow open doors on CentCom" — meaning it looks like nothing at all, until someone flies
    /// there.
    ///
    /// That's why there are two assertions, and the second matters more than the first: it's not
    /// enough to check that no new components appeared off-station — we need to confirm there was
    /// something off-station TO grab in the first place. Otherwise the test is green on an empty
    /// world and guards nothing.
    /// </remarks>
    [Test]
    public async Task GrantsAccess_OnStationOnly()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var ent = server.ResolveDependency<IEntityManager>();

        var before = await w.Read(() => Whitelisted(ent));

        var offStationDoorsBefore = await w.Read(() => OffStationDoors(ent, w));

        var grant = await w.Read(() =>
        {
            var rogue = StartRule(server, OpenRule);
            return rogue.System.GrantAccess(w.Station, rogue.Rule);
        });

        var after = await w.Read(() => Whitelisted(ent));

        var added = after.Except(before).ToList();

        var strayed = await w.Read(() => added.Where(uid => !OnStationGrid(ent, w, uid)).ToList());

        Assert.Multiple(() =>
        {
            Assert.That(grant.Doors, Is.GreaterThan(0), "ни одна дверь не получила доступ — обход не сработал");
            Assert.That(added, Is.Not.Empty, "доступ не роздан вообще никому");

            // Bench sanity check. Without it, the first assertion would be green even on a
            // single-grid world.
            Assert.That(offStationDoorsBefore, Is.GreaterThan(0),
                "в мире нет ни одной двери вне станции — проверять фильтр не на чем, стенд сломан");

            Assert.That(strayed, Is.Empty,
                $"доступ уехал за пределы станции: {strayed.Count} сущностей на чужих гридах");
        });
    }

    /// <summary>
    /// Already-marked entities are left alone, including ones the crew disabled.
    /// </summary>
    /// <remarks>
    /// <c>StationAiWhitelist</c> has an <c>Enabled</c> field that goes false when the crew cuts
    /// the AI control wire. Re-applying the component would amount to repairing that cut wire —
    /// taking away the crew's one counterplay, which works precisely and silently. There is no
    /// way to catch this by eye: in game, the only visible difference is that a door that was
    /// "disabled" keeps obeying anyway.
    /// </remarks>
    [Test]
    public async Task GrantsAccess_DoesNotRepairCutWires()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;
        var ent = server.ResolveDependency<IEntityManager>();

        var victim = await w.Read(() =>
        {
            var stationAi = server.System<SharedStationAiSystem>();

            var query = ent.EntityQueryEnumerator<StationAiWhitelistComponent, DoorComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var whitelist, out _, out var xform))
            {
                if (!xform.Anchored || !OnStationGrid(ent, w, uid))
                    continue;

                stationAi.SetWhitelistEnabled((uid, whitelist), false);
                return uid;
            }

            return EntityUid.Invalid;
        });

        Assert.That(victim, Is.Not.EqualTo(EntityUid.Invalid),
            "на станции не нашлось ни одной размеченной двери — стенд сломан");

        await w.Read(() =>
        {
            var rogue = StartRule(server, OpenRule);
            return rogue.System.GrantAccess(w.Station, rogue.Rule);
        });

        var stillCut = await w.Read(() =>
            !ent.GetComponent<StationAiWhitelistComponent>(victim).Enabled);

        Assert.That(stillCut, Is.True,
            "режим «починил» перерезанный экипажем провод управления");
    }

    /// <summary>
    /// Open mode closes every job except overflow — meaning every job except assistant.
    /// </summary>
    /// <remarks>
    /// This checks the system's decision itself, not the outcome of a spawn. Raising
    /// <c>RulePlayerSpawningEvent</c> by hand isn't possible, for the same reason
    /// <c>RulePlayerJobsAssignedEvent</c> can't be raised either (see <c>BackupPowerTests</c>):
    /// <c>AntagSelectionSystem</c> is subscribed to these events, and outside the round-start
    /// sequence it crashes on its own.
    /// </remarks>
    [Test]
    public async Task OpenMode_ClosesEveryJobButOverflow()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        var jobs = server.System<StationJobsSystem>();
        var rogue = server.System<RogueAiRuleSystem>();

        var overflow = await w.Read(() => jobs.GetOverflowJobs(w.Station).ToHashSet());

        var openBefore = await w.Read(() =>
            jobs.GetJobs(w.Station).Count(j => !overflow.Contains(j.Key) && j.Value != 0));

        await w.Read(() =>
        {
            rogue.ForcePassengerJobs(w.Station);
            return 0;
        });

        var openAfter = await w.Read(() =>
            jobs.GetJobs(w.Station).Where(j => !overflow.Contains(j.Key) && j.Value != 0)
                .Select(j => j.Key.Id).ToList());

        var overflowLeft = await w.Read(() => jobs.GetOverflowJobs(w.Station).Count);

        Assert.Multiple(() =>
        {
            Assert.That(overflow, Is.Not.Empty, "на станции нет overflow-должности — ассистентом стать некем");
            Assert.That(openBefore, Is.GreaterThan(0), "должностей и так не было — проверять нечего");

            Assert.That(openAfter, Is.Empty,
                $"остались открытые должности помимо ассистента: {string.Join(", ", openAfter)}");

            // Closing the assistant job too would mean letting no one onto the station at all.
            Assert.That(overflowLeft, Is.EqualTo(overflow.Count), "overflow-должность тоже закрылась");
        });
    }

    /// <summary>
    /// Claiming the core in this mode applies the mode's lawset, not the stock Crewsimov one.
    /// </summary>
    [Test]
    public async Task Claim_AppliesTheRogueLawset()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        var laws = server.System<Content.Server.Silicons.Laws.SiliconLawSystem>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        var before = await w.Read(() => LawStrings(laws.GetLaws(w.Brain)));

        var reclaimed = await w.Read(() =>
        {
            StartRule(server, HiddenRule);

            // Reclaimed the same way `aiagent claim` does on a live server: the brain already in
            // the core is reused, and the laws are applied to it.
            w.System.ReleaseAll("тест режима злого ИИ");
            return w.System.TryClaimAnyCore(out _);
        });

        var after = await w.Read(() =>
        {
            var brain = w.System.Sessions.Keys.First();
            return LawStrings(laws.GetLaws(brain));
        });

        var wanted = await w.Read(() =>
        {
            var rule = server.System<RogueAiRuleSystem>().ActiveRule!;
            return laws.GetLawset(rule.Lawset).Laws.Select(l => l.LawString).ToList();
        });

        Assert.Multiple(() =>
        {
            Assert.That(reclaimed, Is.True, "агент не смог занять ядро заново");
            Assert.That(wanted, Is.Not.Empty, "лоусет режима пуст — проверь rogue_ai.yml");
            Assert.That(after, Is.EqualTo(wanted), "агент остался со штатными законами");
            Assert.That(after, Is.Not.EqualTo(before), "законы не изменились вовсе");
        });
    }

    /// <summary>
    /// The system prompt is assembled from the MODE's personality file, not the ordinary SOUL.md.
    /// </summary>
    /// <remarks>
    /// The prompt is a frozen prefix: assemble it from the wrong file, and the agent plays the
    /// wrong role for the whole round without showing any sign of it. No errors occur either —
    /// the ordinary SOUL.md exists and reads just fine.
    /// </remarks>
    [Test]
    public async Task Prompt_UsesTheRogueSoul()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        const string sentinel = "МЕТКА-РЕЖИМА-ЗЛОГО-ИИ";

        var rogueSoul = await w.Read(() =>
        {
            var rule = StartRule(server, OpenRule).Rule;
            return rule.SoulFile;
        });

        global::System.IO.Directory.CreateDirectory(w.DataDir);
        await global::System.IO.File.WriteAllTextAsync(
            global::System.IO.Path.Combine(w.DataDir, rogueSoul), sentinel);

        var prompt = await w.Read(() => w.System.BuildSystemPromptForTest());

        Assert.That(prompt, Does.Contain(sentinel),
            $"промпт собран не из {rogueSoul} — агент играет обычного ИИ в режиме злого");
    }

    /// <summary>
    /// The mode's presets and rules are consistent with each other and with upstream prototypes.
    /// </summary>
    /// <remarks>
    /// This catches typos that don't crash anything on a live server but simply switch the mode
    /// off: a nonexistent lawset, a rule that doesn't exist, a preset missing the mode's rule.
    /// Each one looks like "the round ran fine, but the AI was ordinary".
    /// </remarks>
    [Test]
    public async Task Prototypes_AreWiredUp()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        var presets = await w.Read(() => protoMan.EnumeratePrototypes<GamePresetPrototype>()
            .Where(p => p.ID.StartsWith("RogueAi", global::System.StringComparison.Ordinal))
            .ToList());

        Assert.That(presets, Is.Not.Empty, "пресеты режима не загрузились");

        await w.Read(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(presets, Has.Count.EqualTo(2), "режимов должно быть два: скрытый и открытый");

                foreach (var preset in presets)
                {
                    Assert.That(preset.ShowInVote, Is.True, $"{preset.ID}: пресет не попадёт в голосование");

                    var rules = preset.Rules
                        .Where(r => protoMan.TryIndex(r, out var proto)
                                    && proto.Components.ContainsKey("RogueAiRule"))
                        .ToList();

                    Assert.That(rules, Has.Count.EqualTo(1),
                        $"{preset.ID}: в пресете должно быть ровно одно правило режима");
                }

                foreach (var ruleId in new[] { OpenRule, HiddenRule })
                {
                    Assert.That(protoMan.TryIndex(ruleId, out var proto), Is.True, $"{ruleId}: правила нет");

                    var rule = (RogueAiRuleComponent) proto!.Components["RogueAiRule"].Component;

                    Assert.That(protoMan.HasIndex(rule.Lawset), Is.True,
                        $"{ruleId}: лоусет '{rule.Lawset}' не существует — агент останется с Crewsimov");

                    Assert.That(rule.SoulFile, Does.EndWith(".md"), $"{ruleId}: имя файла личности не похоже на файл");
                }
            });

            return 0;
        });

        // Open mode is the only one that touches jobs and announces itself.
        var open = await w.Read(() =>
        {
            protoMan.TryIndex(OpenRule, out var proto);
            return (RogueAiRuleComponent) proto!.Components["RogueAiRule"].Component;
        });

        var hidden = await w.Read(() =>
        {
            protoMan.TryIndex(HiddenRule, out var proto);
            return (RogueAiRuleComponent) proto!.Components["RogueAiRule"].Component;
        });

        Assert.Multiple(() =>
        {
            Assert.That(open.AllJobsPassenger, Is.True, "открытый режим не раздаёт ассистента");
            Assert.That(open.AnnounceOnStart, Is.True, "открытый режим не объявляет о себе");
            Assert.That(open.Announcement, Is.Not.Null, "открытому режиму нечего объявить");

            Assert.That(hidden.AnnounceOnStart, Is.False, "скрытый режим объявляет о себе — он не скрытый");
            Assert.That(hidden.AllJobsPassenger, Is.False, "скрытый режим раздаёт ассистента — экипаж догадается сразу");
            Assert.That(hidden.Lawset.Id, Is.Not.EqualTo(open.Lawset.Id), "у режимов один лоусет на двоих");
        });
    }

    /// <summary>
    /// Open mode raises three borgs, and each one gets its own identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This checks not "the spawn didn't crash" but composition and separation. Composition,
    /// because the roster is a list in YAML, and a typo in a prototype name doesn't crash the
    /// round — it quietly gives you two robots instead of three. Separation, because a shared
    /// identifier means a shared memory folder and a shared dialogue file, and it only shows up a
    /// whole round later, once there's no way left to connect cause and effect.
    /// </para>
    /// <para>
    /// The spawn lives on job assignment, so this raises the real ticker event rather than
    /// calling a method directly: the ordering relative to nav-map readiness is part of what's
    /// being checked.
    /// </para>
    /// </remarks>
    [Test]
    public async Task OpenMode_RaisesEverySupportBorgFromTheRule()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        var result = await w.Read(() =>
        {
            var (rogue, rule) = StartRule(server, OpenRule);

            // The method is called directly rather than through an event: AntagSelectionSystem is
            // subscribed to RulePlayerJobsAssignedEvent and crashes on its own outside the
            // round-start sequence. Access granting above is checked with the same trick.
            rogue.SpawnSupportBorgs(rule);

            // Claiming is a separate step because the identifier is assigned right there, not at
            // spawn time. In a real round, auto-claim does this on the transition into InRound;
            // here it's done by hand, so the test doesn't depend on whatever else the ticker is
            // busy with.
            var borgs = server.System<Content.Server.AiAgent.Borg.AiBorgSystem>();
            var found = new List<string>();

            var query = server.EntMan.EntityQueryEnumerator<Content.Server.AiAgent.Borg.AiBorgComponent>();
            while (query.MoveNext(out var uid, out var comp))
            {
                Assert.That(borgs.TryClaim(uid, out var why), Is.True, $"захват не удался: {why}");
                found.Add(comp.AgentId);
            }

            return (Ids: found, Wanted: rule.SupportBorgs.Count);
        });

        var ids = result.Ids;
        var wanted = result.Wanted;

        Assert.Multiple(() =>
        {
            // Checked against the rule's LIST, not a hardcoded number. Squad composition is a
            // YAML decision: on 31.08 there were three, on 01.09 it became seven, and a test that
            // hardcodes the number turns red on every such decision without checking anything.
            Assert.That(wanted, Is.GreaterThan(0), "открытому режиму не прописано ни одного киборга");

            Assert.That(ids, Has.Count.EqualTo(wanted),
                $"поднято {ids.Count} роботов из {wanted}: {string.Join(", ", ids)}");

            Assert.That(ids.Distinct(global::System.StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(wanted),
                $"идентификаторы совпали: {string.Join(", ", ids)} — каталоги памяти теперь общие");
        });
    }

    /// <summary>
    /// Hidden mode does not raise a single borg.
    /// </summary>
    /// <remarks>
    /// Three robots under AI command is a statement louder than any action, and in hidden mode
    /// that would blow the whole plan in the first minute. Checked separately because the list is
    /// empty in YAML, and an empty list is easy to "fix" by copy-pasting from the neighboring
    /// rule.
    /// </remarks>
    [Test]
    public async Task HiddenMode_RaisesAtMostOneNonCombatBorg()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        var count = await w.Read(() =>
        {
            var (rogue, rule) = StartRule(server, HiddenRule);

            // On 01.09.2026 the decision changed: hidden mode now has EXACTLY ONE body, and what
            // needs checking now isn't "zero" but the thing zero used to stand for — that the
            // disguise holds. An empty list no longer guarantees that, but it does guarantee the
            // absence of combat chassis: an engineering borg at robotics is a normal sight on any
            // shift, but six "Klin"s with no manufacturer markings isn't "almost normal" — it's
            // an announcement.
            Assert.That(rule.SupportBorgs, Has.Count.LessThanOrEqualTo(1),
                "скрытому режиму прописали отряд, а не одиночное тело");

            Assert.That(rule.SupportBorgs.Select(b => b.Id), Has.None.Contains("Combat"),
                "скрытому режиму прописали БОЕВОЙ корпус — маскировка сорвана на первой минуте");

            rogue.SpawnSupportBorgs(rule);

            var n = 0;
            var query = server.EntMan.EntityQueryEnumerator<Content.Server.AiAgent.Borg.AiBorgComponent>();
            while (query.MoveNext(out _, out _))
                n++;

            return n;
        });

        Assert.That(count, Is.LessThanOrEqualTo(1),
            "скрытый режим поставил отряд вместо одиночного тела");
    }

    /// <summary>
    /// Support borg prototypes exist and are assembled the way the code expects.
    /// </summary>
    /// <remarks>
    /// The quietest way this set can break is a YAML typo: the whole file fails to load, and the
    /// round starts as if nothing happened, just without any robots. This was caught exactly once
    /// and cost a test run; hence checking the prototypes separately from checking behavior.
    /// </remarks>
    [Test]
    public async Task SupportBorgPrototypes_AreWiredUp()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        await w.Read(() =>
        {
            protoMan.TryIndex(OpenRule, out var ruleProto);
            var rule = (RogueAiRuleComponent) ruleProto!.Components["RogueAiRule"].Component;

            Assert.Multiple(() =>
            {
                Assert.That(rule.LlmChain, Is.Not.Empty, "у режима нет своей цепочки моделей");

                foreach (var proto in rule.SupportBorgs)
                {
                    Assert.That(protoMan.TryIndex<Robust.Shared.Prototypes.EntityPrototype>(proto.Id, out var entity),
                        Is.True, $"прототипа {proto} нет — файл мог не загрузиться целиком");

                    Assert.That(entity!.Components.ContainsKey("AiBorg"), Is.True,
                        $"{proto}: это не ИИ-борг, агент в него не сядет");

                    Assert.That(entity.Components.ContainsKey("SiliconLawProvider"), Is.True,
                        $"{proto}: без своего лоусета робот остаётся с Crewsimov и ИИ не подчиняется");

                    var borg = (Content.Server.AiAgent.Borg.AiBorgComponent)
                        entity.Components["AiBorg"].Component;

                    Assert.That(borg.AgentId, Is.Empty,
                        $"{proto}: id прописан руками — два одинаковых робота получат общий каталог");
                    Assert.That(borg.SoulFile, Does.StartWith("SOUL_ROGUE"),
                        $"{proto}: личность не режимная — робот будет вести себя как обычный");
                }
            });

            return 0;
        });
    }

    /// <summary>
    /// The core gets claimed even when three robots are already running on the station.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A direct regression test for a bug that was actually found. The <c>ai.max_agents</c> cap
    /// sat on core claiming and counted ALL sessions, including borg ones, while the mode places
    /// robots during job assignment — that is, earlier than the brain sits down in the core on
    /// <c>InRound</c>. With the default of one, this produced a rogue AI mode with no AI: an
    /// honest line about the limit in the log, and silence in the game.
    /// </para>
    /// <para>
    /// The order in the test matches the round: robots first, then the core. The reverse order
    /// would pass even on the broken version.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CoreIsClaimed_EvenWithThreeBorgsAlreadyRunning()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        var claimed = await w.Read(() =>
        {
            var host = server.System<StationAiAgentSystem>();

            // The bench claims the core at startup, but in a real round it's claimed LAST. We
            // release it so the order matches the real one: otherwise the test would pass even on
            // the broken version.
            host.ReleaseAll("порядок раунда: сначала роботы");

            var (rogue, rule) = StartRule(server, OpenRule);
            rogue.SpawnSupportBorgs(rule);

            var borgs = server.System<Content.Server.AiAgent.Borg.AiBorgSystem>();
            var query = server.EntMan.EntityQueryEnumerator<Content.Server.AiAgent.Borg.AiBorgComponent>();
            var live = 0;

            while (query.MoveNext(out var uid, out _))
            {
                if (borgs.TryClaim(uid, out _))
                    live++;
            }

            // As many as the rule specifies, not a hardcoded number: squad composition is a YAML
            // decision, and the test shouldn't break just because the owner added a seventh
            // chassis. If fewer than that show up here, the ai.max_agents cap doesn't fit both
            // the squad and the core, and that's a bug in the default, not in the test.
            Assert.That(live, Is.EqualTo(rule.SupportBorgs.Count),
                "роботы не поднялись — проверять нечего");

            return host.TryClaimAnyCore(out var why) ? "" : why;
        });

        Assert.That(claimed, Is.Empty, $"мозг не сел в ядро при живых роботах: {claimed}");
    }

    /// <summary>
    /// A robot's personality falls back to the shared folder when its own folder has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent's folder is chosen by its identifier, and support borg identifiers are handed
    /// out by an allocator — <c>combat-1</c>, <c>combat-2</c>, … The folder is therefore unknown
    /// in advance, and keeping the personality inside it would mean copying the same file under
    /// every possible number. The personality is tied to the ROLE instead: two combat robots read
    /// the same file.
    /// </para>
    /// <para>
    /// The precedence is checked too: a robot's own folder must win over the shared one,
    /// otherwise the only remaining way to tell one specific robot apart from its siblings
    /// disappears.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BorgSoul_FallsBackToTheSharedFolder_ButOwnFolderWins()
    {
        await using var w = await AiStation.Create();

        const string file = "SOUL_ROGUE_BORG_COMBAT.md";
        const string shared = "ЛИЧНОСТЬ-ИЗ-ОБЩЕГО";
        const string own = "ЛИЧНОСТЬ-ИЗ-СВОЕГО";

        var agentDir = await w.Read(() => w.System.AgentDir("combat-1"));

        global::System.IO.Directory.CreateDirectory(w.DataDir);
        await global::System.IO.File.WriteAllTextAsync(
            global::System.IO.Path.Combine(w.DataDir, file), shared);

        var fromShared = await w.Read(() => w.System.ReadSoul(file, agentDir));
        Assert.That(fromShared, Is.EqualTo(shared),
            "личность не нашлась в общем каталоге — каждому роботу понадобится своя копия");

        global::System.IO.Directory.CreateDirectory(agentDir);
        await global::System.IO.File.WriteAllTextAsync(
            global::System.IO.Path.Combine(agentDir, file), own);

        var fromOwn = await w.Read(() => w.System.ReadSoul(file, agentDir));
        Assert.That(fromOwn, Is.EqualTo(own),
            "общий каталог перебил свой — отличить конкретного робота от собратьев стало нечем");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Start the mode's rule via the real ticker and return it together with the system.</summary>
    private static (RogueAiRuleSystem System, RogueAiRuleComponent Rule) StartRule(
        Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        EntProtoId ruleId)
    {
        var ticker = server.System<GameTicker>();
        Assert.That(ticker.StartGameRule(ruleId.Id), Is.True, $"{ruleId}: правило не запустилось");

        var rogue = server.System<RogueAiRuleSystem>();
        Assert.That(rogue.ActiveRule, Is.Not.Null, $"{ruleId}: правило стартовало, но режим не включился");

        return (rogue, rogue.ActiveRule!);
    }

    private static HashSet<EntityUid> Whitelisted(IEntityManager ent)
    {
        var set = new HashSet<EntityUid>();
        var query = ent.EntityQueryEnumerator<StationAiWhitelistComponent>();
        while (query.MoveNext(out var uid, out _))
            set.Add(uid);

        return set;
    }

    private static bool OnStationGrid(IEntityManager ent, AiStation w, EntityUid uid)
    {
        if (!ent.TryGetComponent<TransformComponent>(uid, out var xform) || xform.GridUid is not { } grid)
            return false;

        if (!ent.TryGetComponent<StationDataComponent>(w.Station, out var data))
            return false;

        // Copying into a local variable is mandatory: RA0002 treats a method call on someone
        // else's field as an "Execute" access, and this test assembly only has read access to
        // StationDataComponent.
        var grids = data.Grids;
        return grids.Contains(grid);
    }

    private static int OffStationDoors(IEntityManager ent, AiStation w)
    {
        var count = 0;
        var query = ent.EntityQueryEnumerator<DoorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.Anchored && !OnStationGrid(ent, w, uid))
                count++;
        }

        return count;
    }

    private static List<string> LawStrings(SiliconLawset lawset) =>
        lawset.Laws.Select(l => l.LawString).ToList();
}
