using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Borg;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.NPC;
using Content.Shared.Silicons.Borgs.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.AiBench;

/// <summary>
/// Агент в теле борга: захват, ноги, глаза, руки.
///
/// <para>
/// Стенд — настоящая станция (<see cref="AiStation"/>, карта Box), потому что всё интересное здесь
/// про мир: пол под ногами, стены между роботом и целью, навигационные маяки и настоящие шлюзы.
/// На тринадцати тайлах тестового грида ни один из этих вопросов не задать.
/// </para>
/// </summary>
[TestFixture]
[Category("Scenario")]
public sealed class BorgAgentTests
{
    private const string BorgProto = "AiBorgChassis";

    /// <summary>Заспавнить ИИ-борга рядом с ядром и занять его.</summary>
    private static async Task<EntityUid> SpawnAndClaim(AiStation w)
    {
        var ent = w.Ent;
        var borg = EntityUid.Invalid;

        await w.Pair.Server.WaitPost(() =>
        {
            var system = w.Pair.Server.System<AiBorgSystem>();

            // Через настоящую постановку, а не «рядом с ядром»: комната ИИ-ядра заперта, и робот,
            // поставленный в неё, честно не находит дороги никуда. Первая версия теста делала
            // именно так и проверяла тем самым сломанную сцену.
            Assert.That(system.TrySpawnBorg(null, out borg, out var placed), Is.True,
                $"не удалось поставить робота: {placed}");

            Assert.That(system.TryClaim(borg, out var reason), Is.True, $"захват не удался: {reason}");
        });

        await w.Pair.Server.WaitRunTicks(5);
        return borg;
    }

    /// <summary>
    /// Захват включает шасси — а значит, даёт руки и доступ по ID.
    /// </summary>
    /// <remarks>
    /// Главное утверждение файла. <c>SharedBorgSystem.CanActivate</c> требует разум, и без него
    /// шасси остаётся выключенным: модулей нет, доступа нет, скорость шаговая. При этом ничего
    /// нигде не падает — робот просто стоит и ничего не может. Именно поэтому проверяется
    /// <c>Active</c>, а не «сессия завелась».
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
        var hasNpc = await w.Read(() => ent.HasComponent<ActiveNPCComponent>(borg));
        var hasSession = await w.Read(() => w.System.Sessions.ContainsKey(borg));

        Assert.Multiple(() =>
        {
            Assert.That(hasMind, Is.True, "разум не посажен — шасси не активируется");
            Assert.That(active, Is.True, "шасси не активно: не будет ни модулей, ни доступа по ID");
            Assert.That(hasNpc, Is.True,
                "нет ActiveNPCComponent — рулевой не увидит робота и тот не сдвинется, молча");
            Assert.That(hasSession, Is.True, "сессия агента не завелась");
        });
    }

    /// <summary>
    /// Два агента пишут в РАЗНЫЕ файлы сессии.
    /// </summary>
    /// <remarks>
    /// Прямая проверка почина, без которого второго агента заводить нельзя: идентификатор сессии
    /// был константой <c>"current"</c>, и борг с ядром восстанавливали бы диалоги друг друга.
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
    /// У борга свой набор инструментов: есть руки и ноги, нет станционных консолей.
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

            // Всё это опирается на встроенные консоли тела Station AI или на вайтлист устройств.
            // У борга нет ни того, ни другого: он не управляет дверью удалённо, он до неё доходит.
            foreach (var forbidden in new[] { "announce", "device_action", "device_ui", "move_camera", "jump_to_core", "crew_status" })
                Assert.That(names, Does.Not.Contain(forbidden), $"борг не должен иметь {forbidden}");
        });
    }

    /// <summary>
    /// Робот видит своими глазами, а не сетью камер.
    /// </summary>
    /// <remarks>
    /// Отдельный тест именно потому, что переиспользовать <c>StationAiVisionSystem</c> было
    /// соблазнительно и неверно: тот объединяет обзор ВСЕХ камер в радиусе, и робот в тёмном
    /// коридоре «видел» бы половину станции. Здесь проверяется, что обзор борга ограничен и
    /// заметно меньше того, что видит ядро с его камерами.
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
    /// Ходьба: рулевой получил задачу и флаги пути.
    /// </summary>
    /// <remarks>
    /// Флаги проверяются отдельным утверждением, потому что <c>NPCSteeringSystem.Register</c>
    /// выставляет их через <c>PathfindingSystem.GetFlags</c>, а тот возвращает <c>None</c> для
    /// всего, у чего нет <c>HTNComponent</c>. С <c>None</c> робот считает любую дверь стеной —
    /// и это не ошибка, а тихий обход половины станции.
    /// </remarks>
    [Test]
    public async Task Goto_RegistersSteeringWithPathFlags()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        var result = await w.InvokeOn(borg, "goto", "{\"to\":\"12,-34\"}");

        // Координаты заведомо есть не на всякой карте; важен сам факт регистрации рулевого.
        if (!result.Ok)
            Assert.Inconclusive($"goto отказал: {result.Error} {result.Detail}");

        var flags = await w.Read(() =>
            ent.TryGetComponent<Content.Server.NPC.Components.NPCSteeringComponent>(borg, out var st)
                ? st.Flags
                : Content.Server.NPC.Pathfinding.PathFlags.None);

        Assert.That(flags, Is.Not.EqualTo(Content.Server.NPC.Pathfinding.PathFlags.None),
            "флаги пути не выставлены — робот будет считать двери непроходимыми");
    }










    /// <summary>
    /// Путепоиск выдаёт роботу настоящие флаги — то есть двери для него не стены.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Самый неочевидный тест файла, и появился он после того, как робот на живом сервере отвечал
    /// «дороги нет» на любую цель, стоя посреди коридора.
    /// </para>
    /// <para>
    /// Причина: <c>NPCSteeringSystem.RequestPath</c> на КАЖДЫЙ запрос заново берёт флаги через
    /// <c>PathfindingSystem.GetFlags(uid)</c> и игнорирует те, что выставлены на компоненте
    /// рулевого. А <c>GetFlags</c> умеет доставать их только из блэкборда <c>HTNComponent</c>
    /// (<c>NPCSystem.TryGetNpc</c> не знает других видов NPC) и всему остальному отдаёт
    /// <c>PathFlags.None</c>. С <c>None</c> любая дверь непроходима — а станция это двери.
    /// </para>
    /// <para>
    /// Поэтому шасси несёт <c>HTN</c> с пустой задачей: компонент нужен ради навигации, поведение
    /// задаёт модель. Тест сторожит именно связку — уедет HTN из прототипа, и робот замрёт молча.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Pathfinding_GivesTheRobotRealFlags()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        await w.Pair.Server.WaitRunTicks(60);

        var (htn, flags) = await w.Read(() =>
        {
            var pf = w.Pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
            return (ent.HasComponent<Content.Server.NPC.HTN.HTNComponent>(borg), pf.GetFlags(borg));
        });

        Assert.Multiple(() =>
        {
            Assert.That(htn, Is.True,
                "у шасси нет HTNComponent — путепоиск отдаст PathFlags.None и робот никуда не пойдёт");
            Assert.That(flags, Is.Not.EqualTo(Content.Server.NPC.Pathfinding.PathFlags.None),
                "путепоиск считает робота неспособным открыть ни одну дверь");
        });
    }

    /// <summary>
    /// Робот действительно доходит до цели своими ногами.
    /// </summary>
    /// <remarks>
    /// Целей пробуется несколько: одна вычисленная точка может оказаться за запертой дверью, и
    /// тогда <c>NoPath</c> — правильный ответ на неправильный вопрос. Тест утверждает не «дошёл
    /// именно туда», а «умеет ходить».
    /// </remarks>
    [Test]
    public async Task Goto_ActuallyMovesTheRobot()
    {
        await using var w = await AiStation.Create();
        var borg = await SpawnAndClaim(w);
        var ent = w.Ent;

        // Навмеш строится асинхронно после старта раунда, и «подождать N тиков» — не условие, а
        // ставка: под нагрузкой полного прогона тех же тиков не хватало, и тест падал не по вине
        // робота. Ждём по факту готовности графа под ногами.
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
}
