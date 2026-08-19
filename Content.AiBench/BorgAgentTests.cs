using System.Collections.Generic;
using System.Linq;
using System;
using System.Globalization;
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
using Robust.Shared.Maths;
using Robust.Shared.Map.Components;

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

    [Test]
    public async Task LookDelta_IsInTheSameFrameAsSelfAndGoto()
    {
        // Три числа обязаны жить в одной системе координат: «я» из SELF, Δ из look и точка,
        // которую понимает goto. Пока Δ считалась в координатах КАРТЫ, а всё остальное в
        // координатах СЕТКИ, арифметика модели молча давала чужой тайл — расхождение видно только
        // на повёрнутой сетке, поэтому сетка здесь поворачивается нарочно. На боевом прогоне это
        // выглядело так: робот считал координату соседней клетки, шёл по ней и оказывался в
        // соседнем отсеке, раз за разом.
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

        // Берём первый объект с ненулевой Δ: на нулевой поворот не проверить.
        var checkedAny = false;

        foreach (var row in rows)
        {
            var handle = row[..row.IndexOf(' ')];
            var delta = row[(row.IndexOf("Δ(", StringComparison.Ordinal) + 2)..].TrimEnd(')');
            var parts = delta.Split(',');
            var dx = float.Parse(parts[0], CultureInfo.InvariantCulture);
            var dy = float.Parse(parts[1], CultureInfo.InvariantCulture);

            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
                continue;

            var expected = await w.Read(() =>
            {
                var session = w.System.GetSession(borg);
                if (session == null || !session.Handles.TryResolve(handle, out var uid))
                    return (Vector2?) null;

                var xforms = w.Pair.Server.System<SharedTransformSystem>();
                var toGrid = xforms.GetInvWorldMatrix(w.Grid);
                var here = Vector2.Transform(xforms.GetMapCoordinates(borg).Position, toGrid);
                var there = Vector2.Transform(xforms.GetMapCoordinates(uid).Position, toGrid);
                return there - here;
            });

            if (expected == null)
                continue;

            checkedAny = true;

            Assert.Multiple(() =>
            {
                Assert.That(dx, Is.EqualTo(expected.Value.X).Within(0.6f),
                    $"{handle}: Δx разъехалась — look={dx}, сетка={expected.Value.X}");
                Assert.That(dy, Is.EqualTo(expected.Value.Y).Within(0.6f),
                    $"{handle}: Δy разъехалась — look={dy}, сетка={expected.Value.Y}");
            });

            break;
        }

        Assert.That(checkedAny, Is.True, "не нашлось ни одного объекта с ненулевой Δ — проверять нечего");
    }

    [Test]
    public async Task CarriedItem_SurvivesAModuleSwitch_AndCanStillBeDropped()
    {
        // Живая поломка, стоившая роботу всей работы с предметами. Апстрим вешает
        // UnremoveableComponent на всё, что оказалось в руке модуля без белого списка
        // (SharedBorgSystem.Module.cs, IsItemInHandUnremovable). Для штатных модулей это верно —
        // лом приварен к руке. Для пустого манипулятора это значило: взял флэтпак, переключил
        // модуль — и груз приварился навсегда, дальше «нет свободной руки» на каждую попытку
        // что-либо взять. На бою робот полтора десятка ходов перебирал модули, пытаясь его
        // выложить.
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

        // Круг по модулям — именно он и приваривал груз.
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

        // Лишний цикл модулей после выкладывания — не ритуал, а уборка чужой бухгалтерии.
        //
        // Апстрим помнит содержимое рук деселектнутого модуля в StoredItems и не чистит эту запись
        // при выкладывании предмета. На разборе стенда он пытается вынуть уже удалённую сущность
        // из контейнера и пишет ERRO про пропавший TransformComponent — а пул считает провалом
        // ЛЮБОЙ ERRO в логе, и падал от этого СЛЕДУЮЩИЙ тест фикстуры, а не этот.
        await w.InvokeOn(borg, "module", """{"name":"tool"}""");
        await w.InvokeOn(borg, "module", """{"name":"manipulator"}""");
        await w.Post(() => w.Ent.DeleteEntity(crowbar));
        await w.Pair.Server.WaitRunTicks(5);
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
        var hasSession = await w.Read(() => w.System.Sessions.ContainsKey(borg));

        Assert.Multiple(() =>
        {
            Assert.That(hasMind, Is.True, "разум не посажен — шасси не активируется");
            Assert.That(active, Is.True, "шасси не активно: не будет ни модулей, ни доступа по ID");
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

    /// <summary>
    /// Свой поиск находит дорогу через всю станцию — там, где апстримовый сдаётся.
    /// </summary>
    /// <remarks>
    /// Смысл теста в сравнении. Апстримовый <c>PathfindingSystem</c> обрывает разворот графа на
    /// <c>NodeLimit = 512</c>, и переход через станцию для него «дороги нет» — это не поломка, а
    /// его рабочий диапазон: штатные NPC живут в пределах комнаты. Наш поиск идёт по побитовой
    /// карте <c>NavMapComponent</c> и обязан находить путь между далёкими отсеками.
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

            // Две самые далёкие друг от друга проходимые точки, которые дают маяки: это и есть
            // «через станцию», выраженное в терминах самой карты, а не в наших числах.
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
    /// <c>goto</c> по хендлу ведёт К ЦЕЛИ, а не в начало координат станции.
    /// </summary>
    /// <remarks>
    /// Регрессия, пойманная на боевом сервере. Цель по хендлу задаётся как
    /// <c>EntityCoordinates(target, Vector2.Zero)</c>, чтобы следовать за движущейся целью, и
    /// её <c>Position</c> — смещение относительно САМОЙ ЦЕЛИ, то есть (0,0). Прочитанное как
    /// координаты сетки, это отправляло робота в точку (0,0) станции: на «подойди к двери в двух
    /// шагах» он молча уходил за полстанции. Баг тихий — маршрут строится, робот идёт, всё
    /// выглядит рабочим.
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

        // Мишень в нескольких тайлах: достаточно далеко, чтобы «в начало координат» и «к цели»
        // расходились, и достаточно близко, чтобы маршрут был коротким.
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
    /// Строка SELF: без задвоенного тега и в координатах СЕТКИ.
    /// </summary>
    /// <remarks>
    /// Обе грани пойманы вживую. Тег <c>SELF</c> добавляет <c>ObservationFormatter</c>, и своя
    /// добавка давала «SELF SELF mode=…». Координаты же обязаны совпадать с тем, что понимает
    /// <c>goto {"to":"x,y"}</c>, то есть быть координатами сетки: печатая координаты карты, робот
    /// сообщал о себе «я=(-521,435)», а goto по этим же числам увёл бы его в пустоту. Модель
    /// читает свою позицию отсюда и расхождения систем координат заметить не может.
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
    /// Робот слышит рацию и речь рядом с собой.
    /// </summary>
    /// <remarks>
    /// Пойман вживую и был полностью немым отказом. Приём эфира висит на паре
    /// <c>(LlmStationAiComponent, RadioReceiveEvent)</c> — маркере, названном по первому телу, —
    /// а слух вблизи в <c>OnEntitySpoke</c> начинался с «нет ядра → пропустить». Борг не имел ни
    /// того, ни другого: приказ ушёл в Common, Station AI ответил, а робот взял НОЛЬ ходов и
    /// остался стоять в баре. Ни ошибки, ни строчки в логе — просто глухой агент.
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

        // Замер В ТОМ ЖЕ тике, что и передача.
        //
        // Очередь наблюдений — не накопитель: петля агента просыпается на её пополнении и тут же
        // вычерпывает. Первая версия теста считала через десять тиков и видела 0 → 0 у ОБОИХ
        // агентов, то есть обвиняла приём, когда на самом деле измеряла собственную гонку с петлёй.
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
    /// Диагностика: связность отсеков боевой карты глазами нашего поиска.
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
    /// Робот доходит от бара до реактора на боевой карте.
    /// </summary>
    /// <remarks>
    /// Explicit: карта ротации грузится долго, и держать это в общем прогоне незачем. Но именно
    /// этот маршрут ловил связку двух пределов — нашего поиска и апстримового рулевого, — поэтому
    /// он записан тестом, а не остался ручной проверкой.
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

        // Навмеш рулевого строится асинхронно; без него первая же нога — NoPath.
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

        // Где именно встал и что мешает: доступ робота и двери вокруг.
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

            // Всё, что стоит вплотную: препятствием может быть не только дверь.
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
    /// Робот ведёт себя сам, а не через апстримовый рулевой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сторожит архитектурное решение, к которому пришли не сразу. Сначала маршрут строил наш
    /// поиск, а вести по нему должен был <c>NPCSteeringSystem</c> по коротким ногам. На карте
    /// ротации это встало намертво: робот проходил 27 тайлов из 47 и отвечал «дороги нет» там,
    /// где наш путь был построен и проверен ЕГО ЖЕ правилом проходимости.
    /// </para>
    /// <para>
    /// Поэтому движение своё, а вместе с рулевым ушли и подпорки под него — <c>ActiveNPCComponent</c>
    /// и пустая HTN-задача в прототипе, которые нужны были ровно затем, чтобы он согласился
    /// работать. Тест держит их снятыми: вернутся — значит кто-то снова тащит чужой рулевой.
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
    /// Робот запускает реактор: доходит, находит пульт, вставляет топливо, включает впрыск.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explicit: карта ротации, длинная дорога. Это конечная цель всей затеи с телом — проверить,
    /// что робот способен не только дойти, но и СДЕЛАТЬ работу руками в правильном порядке.
    /// </para>
    /// <para>
    /// <b>Осторожно при чтении вердикта.</b> Сценарий доходит до конца и все его утверждения
    /// проходят, но NUnit всё равно показывает падение: стенд считает провалом ЛЮБУЮ строчку
    /// уровня ERROR в логе сервера, а апстримовый <c>SharedDoAfterSystem.ShouldCancel</c> после
    /// вскрытия упаковки резолвит трансформ у сущности, которую сам же и удалил. Это чужая грабля,
    /// править апстрим нельзя, а механизма «ожидаемая ошибка» у стенда нет. Смотреть надо на
    /// строку ЗАПУСК и на утверждение про впрыск.
    /// </para>
    /// </remarks>
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

        // Что вообще есть на станции: пульт АМЭ и канистры с топливом.
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

        // Состояние до вмешательства.
        var before = await w.Read(() =>
        {
            var c = ent.GetComponent<Content.Server.Ame.Components.AmeControllerComponent>(found.ctrl);
            var injecting = c.Injecting;
            var fuel = c.FuelSlot.Item;
            return $"впрыск={injecting} топливо={(fuel == null ? "нет" : "есть")}";
        });

        TestContext.Out.WriteLine("ДО: " + before);

        var handle = await w.Read(() => w.System.HandleFor(borg, found.ctrl));

        // Дойти до пульта.
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

        // Прочитать пульт.
        var read = await w.InvokeOn(borg, "console", "{\"target\":\"" + handle + "\"}");
        TestContext.Out.WriteLine($"ПУЛЬТ: ok={read.Ok} {read.Error} {read.Detail} {read.EffectJson()}"[..Math.Min(600, $"ПУЛЬТ: ok={read.Ok} {read.Error} {read.Detail} {read.EffectJson()}".Length)]);

        Assert.That(read.Ok, Is.True, $"пульт не читается: {read.Error} {read.Detail}");

        // Что вообще есть вокруг реактора: собрано ли ядро и где топливо.
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

            // Где упаковки: на полу их можно взять, в ящике — сначала надо открыть.
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

        // ---- шаг 1: добыть упаковку экранирования из ящика ----
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

        // Руки даёт только ВЫБРАННЫЙ модуль: пока выбран инструментальный, все руки заняты
        // несъёмными инструментами и взять ничего нельзя.
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

        // ---- шаг 2: донести к пульту и развернуть в экран ----
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

        // Ломом владеет инструментальный модуль, а нёс робот манипулятором: перед вскрытием надо
        // вернуться к инструментам.
        var back = await w.InvokeOn(borg, "module", "{\"name\":\"tool\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var packHandle2 = await w.Read(() => w.System.HandleFor(borg, crate.Pack));
        // Упаковке нужна ПРОЗВОНКА — мультитул, а не лом. Инструмент называем явно.
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

        // ---- шаг 3: топливо ----
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

        // ---- шаг 4: вставить и включить впрыск ----
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

    /// <summary>Позиция предмета в координатах сетки, даже если он лежит в таре.</summary>

    /// <summary>
    /// Полная сборка экранирования: девять упаковок превращаются в квадрат 3×3, у которого
    /// появляется ядро.
    ///
    /// <para>
    /// Зачем отдельно от <see cref="Borg_StartsTheReactor"/>. Тот доказывает вторую половину дела —
    /// топливо и запуск впрыска — но обходится ОДНОЙ упаковкой, а одна упаковка ядра не даёт:
    /// ядром становится клетка, у которой все восемь соседей тоже экранирование
    /// (<c>AmeNodeGroup.LoadNodes</c>). То есть впрыск включался, а мощность оставалась нулевой, и
    /// именно этого на живых прогонах агент добиться не мог.
    /// </para>
    /// <para>
    /// Тест ходит теми же инструментами, что и модель, в том же порядке, который записан в навыке:
    /// от дальней клетки к выходу, с шагом назад перед распаковкой. Если он зелёный — инструментов
    /// роботу хватает, и живой прогон проверяет уже только сообразительность модели. Если красный —
    /// он называет ровно тот шаг, которого не хватает.
    /// </para>
    /// <para>
    /// <b>Сейчас он красный, и это его работа.</b> Он поймал то, чего не видно ни на одном другом
    /// сценарии: <c>goto</c> по координатам НЕ СТАВИТ робота на заказанную клетку. Строки
    /// «НЕ ДОШЁЛ до (29,-41): встал на (28,-41)» повторяются все девять раз, а <c>goto</c> при
    /// этом отвечает успехом. Для «подойти к двери» промах на клетку незаметен и никогда не
    /// всплывал; для стройки он смертелен — упаковки ложатся мимо, квадрат не сходится, ядро не
    /// появляется. Часть причины уже найдена и убрана (порог прибытия к последней клетке был
    /// больше половины клетки), но промах остался, и корень сидит глубже — в выборе целевого
    /// тайла маршрутом. Тест оставлен красным намеренно: он и есть постановка задачи.
    /// </para>
    /// </summary>
    [Test]
    [Explicit("длинный сценарий на карте ротации")]
    public async Task Borg_BuildsShieldingSquare_AndGetsACore()
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

        // ---- шаг 1: вскрыть тару с упаковками ----
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
        // Нажатие на тару — переключатель, и первое снимает замок по ID, а не открывает.
        // Ровно на этом агент терял ходы: жал по кругу и видел пустой пол.
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

        // ---- шаг 2: выбрать место под квадрат ----
        var square = await w.Read(() => FindSquare(ent, w.Grid, controller));
        Assert.That(square, Is.Not.Null, "рядом с пультом нет свободного места 3×3");

        TestContext.Out.WriteLine("КВАДРАТ: " + string.Join(" ", square!.Select(c => $"({c.X},{c.Y})")));

        var shieldsBefore = await w.Read(() => CountShields(ent));
        TestContext.Out.WriteLine($"ЩИТОВ ДО НАЧАЛА: {shieldsBefore}");

        // ---- шаг 3: разложить и распаковать, отступая к выходу ----
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

            var goRes = await w.InvokeOn(borg, "goto", "{\"to\":\"" + cell.X + "," + cell.Y + "\"}");
            var arrived = await WalkUntilAt(w, borg, cell);

            if (!arrived)
            {
                var stoppedAt = await w.Read(() => ToTile(_worldOf(ent, borg)));
                TestContext.Out.WriteLine(
                    $"НЕ ДОШЁЛ до ({cell.X},{cell.Y}): встал на ({stoppedAt.X},{stoppedAt.Y}); goto ok={goRes.Ok} {goRes.Error} {goRes.Detail}");
            }

            await w.InvokeOn(borg, "drop");
            await w.Pair.Server.WaitRunTicks(5);

            // Шаг назад ПЕРЕД распаковкой: распакуешь под собой — окажешься внутри стены.
            await w.InvokeOn(borg, "step", "{\"dir\":\"юг\",\"count\":1}");
            await w.Pair.Server.WaitRunTicks(20);

            await w.InvokeOn(borg, "module", "{\"name\":\"tool\"}");

            // Ждущая версия — та самая, которую в режиме скрипта видно как use. Обычная
            // возвращается на «действие НАЧАЛОСЬ», и тест, смотрящий только на ok, принял бы
            // начатое за сделанное.
            var unpacked = await w.InvokeOn(borg, "use_wait",
                "{\"target\":\"" + packHandle + "\",\"tool\":\"multitool\"}");
            await w.Pair.Server.WaitRunTicks(30);

            var shields = await w.Read(() => CountShields(ent));
            TestContext.Out.WriteLine(
                $"({cell.X},{cell.Y}): распаковка ok={unpacked.Ok} {unpacked.Error} {unpacked.EffectJson()}; щитов теперь {shields}");

            built = shields;
        }

        // ---- шаг 4: появилось ли ядро ----
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
    }

    /// <summary>Упаковки, лежащие на полу, а не в таре.</summary>
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
    /// Девять клеток 3×3 рядом с пультом, в порядке «от дальней к выходу».
    ///
    /// Порядок здесь не украшение: он ровно тот, что записан в навыке, и именно он не даёт роботу
    /// замуровать себя. Дальний ряд кладётся первым, ближний — последним, и после него робот уже
    /// снаружи.
    /// </summary>
    private static List<Vector2i>? FindSquare(IEntityManager ent, EntityUid grid, EntityUid controller)
    {
        var maps = ent.System<SharedMapSystem>();
        var lookup = ent.System<EntityLookupSystem>();
        var gridComp = ent.GetComponent<MapGridComponent>(grid);
        var origin = ToTile(_worldOf(ent, controller));

        bool Free(Vector2i tile)
        {
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

        // Квадраты вокруг пульта, ближние сначала.
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

                // Ниже квадрата нужна свободная клетка: туда робот отступает перед распаковкой.
                if (ok && Free(new Vector2i(corner.X + 1, corner.Y - 1)))
                    return cells;
            }
        }

        return null;
    }

    /// <summary>Дождаться, пока робот подойдёт к цели ближе, чем на <paramref name="range"/>.</summary>
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

    /// <summary>Дождаться, пока робот встанет ровно на клетку.</summary>
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
    /// Позиция в НОМЕР КЛЕТКИ, с округлением вниз.
    ///
    /// Приведение (Vector2i) отсекает дробную часть К НУЛЮ, а не вниз: -41.5 превращается в -41,
    /// хотя клетка это -42. Половина станции на карте живёт в отрицательных координатах, поэтому
    /// такое приведение ошибается ровно там, где идёт вся работа. Тест сборки на этом молча
    /// разложил упаковки по дороге: ожидание «встал на клетку» не совпадало никогда.
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
    /// <c>use</c> объясняет исход, а не отвечает голым «ok».
    /// </summary>
    /// <remarks>
    /// Прямая регрессия на прогон, где робот 520 вызовов подряд бил ломом по ящику, который
    /// открывается нажатием. Инструмент отвечал <c>ok</c> и «состояние не изменилось» — а это три
    /// разных исхода под одной вывеской: началось долгое действие; что-то изменилось; действие
    /// неприменимо. Модель выбрала неверный способ и не получила ни одного сигнала об этом.
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
            door = ent.SpawnEntity("Airlock", where.Offset(new Vector2(0, 1)));
        });

        await w.Pair.Server.WaitRunTicks(5);

        var handle = await w.Read(() => w.System.HandleFor(borg, crate));

        // Нажатием — ящик должен открыться, и инструмент обязан назвать ЧТО изменилось.
        var pressed = await w.InvokeOn(borg, "use", "{\"target\":\"" + handle + "\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var json = pressed.EffectJson();
        TestContext.Out.WriteLine("НАЖАЛ: " + json[..Math.Min(300, json.Length)]);

        Assert.That(pressed.Ok, Is.True, $"use отказал: {pressed.Error} {pressed.Detail}");
        Assert.That(json, Does.Contain("итог"), "в ответе нет исхода — модель снова увидит голое ok");

        // Путь УСПЕХА: дверь на нажатие обязана поменять состояние, и это обязано быть сказано.
        var doorHandle = await w.Read(() => w.System.HandleFor(borg, door));
        var opened = await w.InvokeOn(borg, "use", "{\"target\":\"" + doorHandle + "\"}");
        await w.Pair.Server.WaitRunTicks(5);

        var openJson = opened.EffectJson();
        TestContext.Out.WriteLine("ДВЕРЬ: " + openJson[..Math.Min(280, openJson.Length)]);

        Assert.Multiple(() =>
        {
            Assert.That(openJson, Does.Contain("получилось"),
                "дверь открылась, а инструмент об этом не сказал");
            Assert.That(openJson, Does.Contain("дверь:"),
                "не назван характер изменения — модель снова не поймёт, сработало ли");

            // Путь ОТКАЗА: по ящику ломом. Инструмент обязан сказать, что лом тут ни при чём.
            Assert.That(json, Does.Contain("НЕ ПОЛУЧИЛОСЬ").And.Contain("почему"),
                "ничего не вышло, а причина не названа");
        });
    }
}
