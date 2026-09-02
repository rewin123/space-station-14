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
/// Режим «злой ИИ»: доступ, должности, законы, личность.
///
/// <para>
/// Стенд — настоящая станция (<see cref="AiStation"/>, карта Box), и это здесь не роскошь, а
/// условие: главная проверка файла — что раздача доступа НЕ уехала за пределы станции, а чтобы
/// «за пределами станции» вообще существовало, в мире должен быть второй грид. На тестовой
/// площадке из тринадцати тайлов проверять было бы нечего.
/// </para>
/// <para>
/// Правило поднимается настоящим <c>GameTicker.StartGameRule</c> по настоящему прототипу, а не
/// сконструированным в тесте компонентом. Так проверяется в том числе YAML: опечатка в id
/// лоусета или в имени файла личности — самый тихий вид поломки этого режима, потому что раунд
/// при ней стартует нормально, просто ИИ оказывается обычным.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class RogueAiTests
{
    private static readonly EntProtoId OpenRule = "RogueAiOpenRule";
    private static readonly EntProtoId HiddenRule = "RogueAiHiddenRule";

    /// <summary>
    /// Доступ раздан на станции и НИГДЕ больше.
    /// </summary>
    /// <remarks>
    /// Самая вероятная и самая незаметная поломка этого режима — тихое расползание доступа на
    /// чужие гриды: Центком, эвакуационный шаттл, аванпосты. В игре это выглядит как «ИИ почему-то
    /// открывает двери на Центкоме», то есть никак, пока туда кто-нибудь не долетит.
    ///
    /// Утверждений поэтому два, и второе важнее первого: мало проверить, что новых компонентов вне
    /// станции нет, — надо убедиться, что снаружи вообще БЫЛО что захватывать. Иначе тест зелен на
    /// пустом мире и ничего не сторожит.
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

            // Контроль стенда. Без него первое утверждение зелено и на мире из одного грида.
            Assert.That(offStationDoorsBefore, Is.GreaterThan(0),
                "в мире нет ни одной двери вне станции — проверять фильтр не на чем, стенд сломан");

            Assert.That(strayed, Is.Empty,
                $"доступ уехал за пределы станции: {strayed.Count} сущностей на чужих гридах");
        });
    }

    /// <summary>
    /// Уже размеченное не трогается, в том числе выключенное экипажем.
    /// </summary>
    /// <remarks>
    /// У <c>StationAiWhitelist</c> есть поле <c>Enabled</c>, которое гаснет, когда экипаж
    /// перерезает провод управления ИИ. Переналожить компонент значило бы починить перерезанный
    /// провод — то есть отобрать у экипажа единственную контригру, работающую точечно и молча.
    /// Поймать это глазами нельзя никак: в игре разница видна только тем, что дверь, которую
    /// «отключили», продолжает слушаться.
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
    /// Открытый режим закрывает все должности, кроме overflow — то есть кроме ассистента.
    /// </summary>
    /// <remarks>
    /// Проверяется само решение системы, а не результат спавна. Поднять
    /// <c>RulePlayerSpawningEvent</c> руками нельзя по той же причине, по которой нельзя поднимать
    /// <c>RulePlayerJobsAssignedEvent</c> (см. <c>BackupPowerTests</c>): на эти события
    /// подписан <c>AntagSelectionSystem</c>, и вне последовательности старта раунда он падает сам.
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

            // Закрыть заодно и ассистента значило бы не пустить на станцию вообще никого.
            Assert.That(overflowLeft, Is.EqualTo(overflow.Count), "overflow-должность тоже закрылась");
        });
    }

    /// <summary>
    /// Захват ядра в режиме ставит законы режима, а не штатный Crewsimov.
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

            // Захват заново тем же путём, которым идёт `aiagent claim` на живом сервере: мозг в
            // ядре переиспользуется, законы ставятся на него.
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
    /// Системный промпт собирается из файла личности РЕЖИМА, а не из обычного SOUL.md.
    /// </summary>
    /// <remarks>
    /// Промпт — замороженный префикс: собери его не из того файла, и весь раунд агент будет играть
    /// не ту роль, ничем этого не показав. Ошибок при этом не бывает — обычный SOUL.md существует
    /// и прекрасно читается.
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
    /// Пресеты и правила режима согласованы между собой и с прототипами апстрима.
    /// </summary>
    /// <remarks>
    /// Здесь ловятся опечатки, которые на живом сервере ничего не роняют, а просто выключают
    /// режим: несуществующий лоусет, правило, которого нет, пресет без правила режима. Каждая из
    /// них выглядит как «раунд шёл, а ИИ был обычный».
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

        // Открытый режим — единственный, который трогает должности и объявляет о себе.
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
    /// Открытый режим поднимает трёх киборгов, и у каждого свой идентификатор.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Проверяется не «спавн не упал», а состав и разделение. Состав — потому что набор задан
    /// списком в YAML и опечатка в имени прототипа не роняет раунд, а молча даёт двух роботов
    /// вместо трёх. Разделение — потому что общий идентификатор означает общий каталог памяти и
    /// общий файл диалога, и проявляется это через раунд, когда связать причину со следствием
    /// уже нечем.
    /// </para>
    /// <para>
    /// Спавн живёт на раздаче должностей, поэтому здесь поднимается настоящее событие тикера,
    /// а не вызывается метод: порядок относительно готовности навигационной карты — часть того,
    /// что проверяется.
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

            // Метод зовётся напрямую, а не событием: на RulePlayerJobsAssignedEvent подписан
            // AntagSelectionSystem, и вне последовательности старта раунда он падает сам. Тем же
            // приёмом проверяется раздача доступа выше.
            rogue.SpawnSupportBorgs(rule);

            // Захват отдельным шагом, потому что идентификатор выдаётся именно на нём, а не на
            // спавне. В раунде это делает автозахват на переходе в InRound; здесь — руками, чтобы
            // тест не зависел от того, чем ещё занят тикер.
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
            // Сверяется со СПИСКОМ правила, а не с печатным числом. Состав отряда — решение
            // YAML: 31.08 их было трое, 01.09 стало семеро, и тест, знающий число наизусть,
            // краснеет от каждого такого решения, ничего при этом не проверяя.
            Assert.That(wanted, Is.GreaterThan(0), "открытому режиму не прописано ни одного киборга");

            Assert.That(ids, Has.Count.EqualTo(wanted),
                $"поднято {ids.Count} роботов из {wanted}: {string.Join(", ", ids)}");

            Assert.That(ids.Distinct(global::System.StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(wanted),
                $"идентификаторы совпали: {string.Join(", ", ids)} — каталоги памяти теперь общие");
        });
    }

    /// <summary>
    /// Скрытый режим не поднимает ни одного киборга.
    /// </summary>
    /// <remarks>
    /// Три робота под командой ИИ — заявление громче любого поступка, и в скрытом режиме это
    /// сорвало бы весь замысел на первой минуте. Проверяется отдельно, потому что список пуст
    /// в YAML, а пустой список легко «починить» копипастой из соседнего правила.
    /// </remarks>
    [Test]
    public async Task HiddenMode_RaisesAtMostOneNonCombatBorg()
    {
        await using var w = await AiStation.Create();
        var server = w.Pair.Server;

        var count = await w.Read(() =>
        {
            var (rogue, rule) = StartRule(server, HiddenRule);

            // 01.09.2026 решение изменилось: у скрытого режима стало РОВНО ОДНО тело, и проверять
            // теперь надо не «ноль», а то, ради чего ноль стоял, — что маскировка цела. Пустой
            // список этого больше не гарантирует, зато гарантирует отсутствие боевых корпусов:
            // инженерный борг у робототехники — штатная картина любой смены, а шесть «Клинов» без
            // обозначений производителя это не «почти нормально», это объявление.
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
    /// Прототипы киборгов поддержки существуют и собраны так, как ожидает код.
    /// </summary>
    /// <remarks>
    /// Самый тихий вид поломки этого набора — опечатка в YAML: файл целиком не загружается, а
    /// раунд стартует как ни в чём не бывало, просто без роботов. Ловилось это ровно один раз и
    /// стоило прогона тестов; отсюда проверка прототипов отдельно от проверки поведения.
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
    /// Ядро занимается, когда на станции уже работают три робота.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прямая регрессия на найденную поломку. Потолок <c>ai.max_agents</c> стоял на захвате ядра
    /// и считал ВСЕ сессии, включая борговские, а роботов режим ставит на раздаче должностей —
    /// то есть раньше, чем мозг садится в ядро на <c>InRound</c>. При умолчании в единицу это
    /// давало режим злого ИИ без ИИ: в логе честная строка про лимит, в игре — тишина.
    /// </para>
    /// <para>
    /// Порядок в тесте тот же, что в раунде: сначала роботы, потом ядро. Обратный порядок
    /// проходил бы и на сломанной версии.
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

            // Стенд занимает ядро на старте, а в раунде оно занимается ПОСЛЕДНИМ. Освобождаем,
            // чтобы порядок совпал с боевым: иначе тест проходил бы и на сломанной версии.
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

            // Столько, сколько прописано правилу, а не печатное число: состав отряда — решение
            // YAML, и тест не должен ломаться от того, что владелец добавил седьмой корпус.
            // Если сюда пришло меньше, значит потолок ai.max_agents не вмещает ни отряд, ни ядро,
            // и это поломка умолчания, а не теста.
            Assert.That(live, Is.EqualTo(rule.SupportBorgs.Count),
                "роботы не поднялись — проверять нечего");

            return host.TryClaimAnyCore(out var why) ? "" : why;
        });

        Assert.That(claimed, Is.Empty, $"мозг не сел в ядро при живых роботах: {claimed}");
    }

    /// <summary>
    /// Личность робота ищется в общем каталоге, если в его собственном её нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Каталог агента выбирается его идентификатором, а идентификаторы киборгов поддержки
    /// выдаёт аллокатор — <c>combat-1</c>, <c>combat-2</c>, … Каталог, стало быть, заранее
    /// неизвестен, и держать личность внутри него значило бы копировать один и тот же файл под
    /// каждый возможный номер. Личность привязана к РОЛИ: два боевых робота читают одно и то же.
    /// </para>
    /// <para>
    /// Порядок проверяется тоже: свой каталог обязан перебивать общий, иначе исчезает
    /// единственный способ отличить конкретного робота от собратьев.
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

    // ------------------------------------------------------------------ помощники

    /// <summary>Поднять правило режима настоящим тикером и вернуть его вместе с системой.</summary>
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

        // Копия в локальную переменную обязательна: RA0002 считает вызов метода на чужом поле
        // «Execute»-доступом, а у тестовой сборки к StationDataComponent только чтение.
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
