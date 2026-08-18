using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Examine;
using Content.Shared.IdentityManagement;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Набор инструментов борга.
///
/// <para>
/// Общие инструменты (законы, <c>noop</c>, таймеры, память, навыки, заметки) берутся у хоста
/// <see cref="StationAiAgentSystem.RegisterCommonTools"/> — двенадцать штук, ни одного дубля.
/// Здесь только то, чего у неподвижного глаза нет и быть не может: ноги, глаза тела и руки.
/// </para>
/// <para>
/// Чего у борга <b>нет</b> намеренно: <c>announce</c>, <c>device_action</c>, <c>device_ui</c>,
/// <c>move_camera</c>, <c>jump_to_core</c>, <c>crew_status</c>, <c>station_status</c>. Все семь
/// опираются либо на встроенные консоли тела Station AI, либо на вайтлист «ИИ может управлять
/// этим устройством». У борга ни того, ни другого: он не «управляет» дверью удалённо, он до неё
/// доходит и открывает её рукой.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    private void RegisterBorgTools(AgentSession s, AiToolRegistry r, AiBorgComponent comp)
    {
        var channelEnum = string.Join(",", comp.Channels.Select(c => $"\"{c}\""));

        // ------------------------------------------------------------------ ноги

        r.Register(new AiTool
        {
            Name = "goto",
            Description = "Пойти к цели: к объекту по хендлу, к названию отсека (как на указателях " +
                          "станции) или к координатам. Ты НЕ стоишь и не ждёшь — инструмент " +
                          "отвечает сразу, а о прибытии придёт строка ARRIVED. Если дороги нет, " +
                          "придёт NOPATH. Чтобы остановиться на полпути, вызови с stop.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "to":{"type":"string","description":"Хендл (door-3), название отсека (мостик) или координаты вида 12,-34."},
                "stop":{"type":"boolean","description":"Остановиться там, где стоишь, и забыть текущую цель."}}}
                """,
            Handler = (a, ct) => GotoAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "step",
            Description = "Сделать несколько шагов в одну сторону. Для точной доводки в комнате, " +
                          "когда идти через полстанции не нужно. На дальние расстояния — goto.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["dir"],"additionalProperties":false,"properties":{
                "dir":{"type":"string","enum":["север","юг","запад","восток"],"description":"Куда шагать."},
                "count":{"type":"integer","minimum":1,"maximum":10,"default":1,"description":"Сколько тайлов."}}}
                """,
            Handler = (a, ct) => StepAsync(s, a, ct),
        });

        // ----------------------------------------------------------------- глаза

        r.Register(new AiTool
        {
            Name = "look",
            Description = "Осмотреться вокруг СЕБЯ. Видишь то, что видел бы человек на твоём " +
                          "месте: рядом и не за стеной. Возвращает список с хендлами — ими потом " +
                          "адресуются остальные инструменты.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "kind":{"type":"string","enum":["door","crew","apc","camera","airalarm","power","canister","computer","locker","device","obj"],"description":"Показать только объекты этого вида."}}}
                """,
            Handler = (a, ct) => BorgLookAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "examine",
            Description = "Рассмотреть одну вещь вблизи и прочитать её описание — то же, что видит " +
                          "игрок, когда осматривает предмет. Так узнают, сварен ли болт, заряжена " +
                          "ли батарея и что вообще перед тобой.",
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл из look."}}}
                """,
            Handler = (a, ct) => ExamineAsync(s, a, ct),
        });

        // ------------------------------------------------------------------ руки

        r.Register(new AiTool
        {
            Name = "use",
            Description = "Нажать на цель: открыть дверь, включить машину, нажать кнопку. Надо " +
                          "стоять рядом — сначала goto, потом use. Чтобы ПРИМЕНИТЬ инструмент " +
                          "(отжать, сварить, вскрыть, прозвонить), назови его в 'tool' — робот сам " +
                          "возьмёт его в руку. Инструменты у тебя из выбранного модуля; какие есть, " +
                          "видно в строке SELF.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл из look."},
                "tool":{"type":"string","description":"Часть названия инструмента: лом, мультитул, сварка, ключ. Робот переложит его в рабочую руку."},
                "with_item":{"type":"boolean","default":false,"description":"Применить то, что уже в руке, не выбирая инструмент."}}}
                """,
            Handler = (a, ct) => UseAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "pickup",
            Description = "Взять предмет в свободную руку. Свободные руки зависят от выбранного " +
                          "модуля — см. module.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл предмета из look."}}}
                """,
            Handler = (a, ct) => PickupAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "drop",
            Description = "Положить то, что держишь в активной руке, себе под ноги.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
                """,
            Handler = (a, ct) => DropAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "hit",
            Description = "Ударить цель тем, что в активной руке. Это применение силы: у него " +
                          "бывают последствия, и законы силикона на тебя распространяются.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл цели из look."}}}
                """,
            Handler = (a, ct) => HitAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "module",
            Description = "Сменить рабочий модуль — это меняет набор инструментов у тебя в руках. " +
                          "Без нужного модуля соответствующая работа просто не делается.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Часть названия модуля, например «инструмент»."}}}
                """,
            Handler = (a, ct) => ModuleAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "console",
            Description = "Пульт машины: без 'action' показывает показания и список кнопок, с " +
                          "'action' — нажимает кнопку. Так управляют реактором, шлюзовыми " +
                          "контроллерами, консолями. Надо стоять рядом.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл машины из look."},
                "action":{"type":"string","description":"Имя кнопки. Без него — только показания и список."},
                "args":{"type":"object","description":"Аргументы кнопки, если она их требует."}}}
                """,
            // Драйвер общий с ядром: он строится отражением по типам BUI-сообщений и про тело не
            // знает ничего. Различаются только ворота — у ядра вайтлист и камеры, у борга
            // «дотянулся рукой».
            Handler = (a, ct) => _host.DeviceUiAsync(s, a, ct, param: "target", gate: (sess, uid) =>
                _interaction.InRangeUnobstructed(sess.Brain, uid, 1.5f)
                    ? null
                    : ToolResult.Fail(ToolError.NotVisible,
                        "отсюда не дотянуться — сначала goto к этой машине", retry: "move_first")),
        });

        // ------------------------------------------------------------------ речь
        //
        // Обработчики берутся у хоста, а схемы и описания пишутся здесь: у борга другой перечень
        // каналов и другая слышимость («рядом с собой», а не «рядом со своим ядром»). Описание
        // инструмента едет в замороженный префикс и для модели является единственным источником
        // правды о её возможностях, так что общая формулировка была бы не экономией, а враньём.

        r.Register(new AiTool
        {
            Name = "say",
            Description = "Сказать вслух рядом с собой. Слышат те, кто стоит рядом с тобой. " +
                          "Чтобы обратиться к экипажу по станции — radio.",
            GameAction = true,
            Speech = true,
            SpokenText = AiTool.TextArgument,
            SchemaJson = """
                {"type":"object","required":["text"],"additionalProperties":false,"properties":{
                "text":{"type":"string","maxLength":400}}}
                """,
            Handler = (a, ct) => _host.SayAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "radio",
            Description = "Передать по радиоканалу станции. Без 'channel' уходит в текущий канал " +
                          "(он всегда написан в строке SELF).",
            GameAction = true,
            Speech = true,
            SpokenText = AiTool.TextArgument,
            // Собирается конкатенацией, а не интерполяцией: перечень каналов у каждого шасси свой,
            // а JSON-схема кончается тремя подряд закрывающими скобками, которые в интерполируемом
            // литерале пришлось бы экранировать до нечитаемости.
            SchemaJson = "{\"type\":\"object\",\"required\":[\"text\"],\"additionalProperties\":false,\"properties\":{"
                         + "\"channel\":{\"type\":\"string\",\"enum\":[" + channelEnum + "]},"
                         + "\"text\":{\"type\":\"string\",\"maxLength\":400}}}",
            Handler = (a, ct) => _host.RadioAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "set_channel",
            Description = "Переключить канал, в который уходит твоя речь по умолчанию. Текущий " +
                          "канал всегда виден в строке SELF.",
            SchemaJson = "{\"type\":\"object\",\"required\":[\"channel\"],\"additionalProperties\":false,\"properties\":{"
                         + "\"channel\":{\"type\":\"string\",\"enum\":[" + channelEnum + "]}}}",
            Handler = (a, ct) => _host.SetChannelAsync(s, a, ct),
        });

        // ------------------------------------------------------- общее для всех тел
        _host.RegisterCommonTools(s, r);

        // Ждущие версии ходьбы и применения. На проводе их нет — они существуют только для
        // скрипта, где «дойти и продолжить» это одна строка, а не четыре хода.
        RegisterWaitingTools(s, r);
    }

    // ===================================================================== handlers

    private bool TryTarget(AgentSession s, JsonElement args, out EntityUid uid, out ToolResult? failure)
    {
        uid = default;
        failure = null;

        if (!StationAiAgentSystem.TryGetString(args, "target", out var handle) || string.IsNullOrWhiteSpace(handle))
        {
            failure = ToolResult.Fail(ToolError.BadArgs, "нужен параметр 'target' — возьми хендл из look");
            return false;
        }

        if (!s.Handles.TryResolve(handle!, out uid) || !Exists(uid) || TerminatingOrDeleted(uid))
        {
            failure = ToolResult.Fail(ToolError.StaleHandle, $"объекта '{handle}' больше нет",
                retry: "other_target", alternatives: s.Handles.Nearest(handle!));
            return false;
        }

        return true;
    }

    private Task<ToolResult> GotoAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "goto", () =>
        {
            if (args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("stop", out var stopEl)
                && stopEl.ValueKind == JsonValueKind.True)
            {
                StopSteering(borg);
                return ToolResult.Effected("self", new Dictionary<string, object?> { ["остановился"] = true });
            }

            if (!StationAiAgentSystem.TryGetString(args, "to", out var to) || string.IsNullOrWhiteSpace(to))
                return ToolResult.Fail(ToolError.BadArgs, "goto: нужен 'to' либо stop:true");

            if (!TryResolveDestination(s, borg, to!, out var coords, out var what, out var why))
                return ToolResult.Fail(ToolError.BadArgs, why, retry: "other_target");

            // Через маршрут, а не напрямую: длинный переход не укладывается в лимит A*, и
            // прямая цель через полстанции вернула бы «дороги нет» из совершенно проходимого места.
            if (!TryStartRoute(borg, coords, what, out var routeWhy))
                return ToolResult.Fail(ToolError.Refused, routeWhy, retry: "other_target");

            return ToolResult.Effected("self", new Dictionary<string, object?>
            {
                ["иду_к"] = what,
                ["как_узнать"] = "придёт строка ARRIVED, когда дойду, или NOPATH, если дороги нет",
            });
        }, ct);
    }

    private Task<ToolResult> StepAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "step", () =>
        {
            if (!StationAiAgentSystem.TryGetString(args, "dir", out var dir) || string.IsNullOrWhiteSpace(dir))
                return ToolResult.Fail(ToolError.BadArgs, "step: нужен 'dir'");

            var count = 1;
            if (args.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                count = Math.Clamp(cEl.GetInt32(), 1, 10);

            var delta = dir!.ToLowerInvariant() switch
            {
                "север" => new Vector2(0, 1),
                "юг" => new Vector2(0, -1),
                "запад" => new Vector2(-1, 0),
                "восток" => new Vector2(1, 0),
                _ => Vector2.Zero,
            };

            if (delta == Vector2.Zero)
                return ToolResult.Fail(ToolError.BadArgs, $"step: не знаю направления '{dir}'");

            var xform = Transform(borg);
            var target = new EntityCoordinates(
                xform.ParentUid.IsValid() ? xform.ParentUid : borg,
                xform.LocalPosition + delta * count);

            // Через тот же маршрут, что и goto: свой код шага означал бы второй, тихо
            // расходящийся способ передвижения.
            if (!TryStartRoute(borg, target, $"{count} шаг(ов) на {dir}", out var stepWhy))
                return ToolResult.Fail(ToolError.Refused, stepWhy, retry: "other_target");

            return ToolResult.Effected("self", new Dictionary<string, object?>
            {
                ["шагаю"] = $"{dir} ×{count}",
            });
        }, ct);
    }

    private Task<ToolResult> BorgLookAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "look", () =>
        {
            StationAiAgentSystem.TryGetString(args, "kind", out var kind);

            var rows = new List<string>();
            var origin = _xform.GetMapCoordinates(borg);

            foreach (var uid in VisibleFrom(borg))
            {
                var k = _host.KindOf(uid);
                if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                var handle = s.Handles.GetOrCreate(uid, k);
                var there = _xform.GetMapCoordinates(uid);
                var d = there.Position - origin.Position;

                rows.Add($"{handle} | {Identity.Name(uid, EntityManager)} | {_host.ShortState(uid)} " +
                         $"| Δ({d.X:F0},{d.Y:F0})");
            }

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["видно"] = rows.Count,
                ["объекты"] = rows,
            });
        }, ct);
    }

    private Task<ToolResult> ExamineAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "examine", () =>
        {
            if (!TryTarget(s, args, out var target, out var failure))
                return failure!;

            if (!_examine.InRangeUnOccluded(borg, target, 8.5f))
                return ToolResult.Fail(ToolError.NotVisible, "отсюда не видно — подойди ближе",
                    retry: "move_first");

            // Та же строка, что читает игрок. FormattedMessage несёт разметку, модели она не нужна.
            var text = _examine.GetExamineText(target, borg).ToString();

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["это"] = Identity.Name(target, EntityManager),
                ["описание"] = text,
            });
        }, ct);
    }

    private Task<ToolResult> UseAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "use", () =>
        {
            if (!TryTarget(s, args, out var target, out var failure))
                return failure!;

            var beforeSnap = Snapshot(target);
            var before = _host.ShortState(target);

            // Номер следующего отложенного действия — так узнаём, что оно вообще началось.
            // Приём взят у апстримового InteractWithOperator: у DoAfter нет события «я стартовал»,
            // зато счётчик на компоненте увеличивается ровно на запуск.
            var beforeDoAfter = TryComp<Content.Shared.DoAfter.DoAfterComponent>(borg, out var da)
                ? da.NextId
                : (ushort) 0;

            var withItem = args.ValueKind == JsonValueKind.Object
                           && args.TryGetProperty("with_item", out var wi)
                           && wi.ValueKind == JsonValueKind.True;

            // Назван инструмент — переложить его в рабочую руку.
            //
            // Без этого набор инструментов модуля бесполезен наполовину: рабочая рука одна, а
            // инструментов шесть, и «применить» уходило тем, что оказалось активным. На запуске
            // реактора это стоило отладки — упаковка экранирования требует ПРОЗВОНКИ (мультитул),
            // а в руке был лом, и вскрытие молча не срабатывало.
            string? toolUsed = null;

            if (StationAiAgentSystem.TryGetString(args, "tool", out var toolName)
                && !string.IsNullOrWhiteSpace(toolName))
            {
                toolUsed = toolName;

                var picked = false;

                foreach (var held in _hands.EnumerateHeld(borg))
                {
                    if (!Name(held).Contains(toolName!, StringComparison.OrdinalIgnoreCase))
                        continue;

                    picked = _hands.TrySelect(borg, held);
                    break;
                }

                if (!picked)
                {
                    var have = string.Join(", ", _hands.EnumerateHeld(borg).Select(h => Name(h)));

                    // Не просто «нет», а КАКОЙ модуль его даёт.
                    //
                    // На боевом прогоне модель выбрала модуль prying — он даёт свойство, а не
                    // руки, — осталась с пустыми руками и десять ходов перебирала названия
                    // инструмента, потому что отказ «смени модуль» не говорил, на какой. Отказ
                    // обязан указывать следующий шаг, иначе он просто съедает ход.
                    var where = FindModuleWithTool(borg, toolName!);

                    var detail = where != null
                        ? $"инструмент «{toolName}» лежит в модуле «{where}» — сначала module {where}"
                        : string.IsNullOrEmpty(have)
                            ? $"инструмента «{toolName}» нет, и руки пусты. Список модулей — в строке SELF"
                            : $"инструмента «{toolName}» нет в руках. Сейчас в руках: {have}";

                    return ToolResult.Fail(ToolError.Refused, detail, retry: "other_target");
                }

                withItem = true;
            }

            if (withItem)
            {
                // Полный путь клика игрока: движок сам решит, пустая рука это или предмет в руке.
                _interaction.UserInteraction(borg, Transform(target).Coordinates, target);
            }
            else
            {
                // Нажатие, а НЕ клик, и это не мелочь.
                //
                // У борга в руке почти всегда несъёмный инструмент модуля, а клик с инструментом
                // означает «применить инструмент»: лом по шлюзу — это отжатие с долгим DoAfter,
                // а не «открой». На бою это выглядело так — робот стоит вплотную к двери, use
                // отвечает «ok», дверь закрыта. Человек в этом случае жмёт E, что и делает
                // InteractionActivate.
                _interaction.InteractionActivate(borg, target);
            }

            var after = _host.ShortState(target);
            var afterSnap = Snapshot(target);

            var started = TryComp<Content.Shared.DoAfter.DoAfterComponent>(borg, out var da2)
                          && da2.NextId != beforeDoAfter;

            // Запомнить начатое действие: ждущая версия use (её зовёт скрипт) досмотрит его до
            // конца и посчитает разницу заново — уже по факту, а не по первому мгновению.
            if (started)
                _pending[borg] = new PendingAction(beforeDoAfter, target, beforeSnap);

            var changes = Diff(beforeSnap, afterSnap);

            // Три разных исхода под одной вывеской «ok» — это и был главный дефект инструмента.
            // Теперь они называются по-разному: началось долгое действие; что-то изменилось (и
            // ЧТО именно); ничего не вышло (и ПОЧЕМУ).
            var result = new Dictionary<string, object?>
            {
                ["было"] = before,
                ["стало"] = after,
            };

            if (started)
            {
                result["итог"] = "действие НАЧАЛОСЬ и занимает время. СТОЙ НА МЕСТЕ и жди " +
                                 "наблюдения: шаг в сторону отменяет его, и всё придётся начинать заново";
            }
            else if (changes.Count > 0)
            {
                result["итог"] = "получилось";
                result["изменилось"] = changes;
            }
            else
            {
                result["итог"] = "НЕ ПОЛУЧИЛОСЬ";
                result["почему"] = Explain(target, toolUsed, beforeSnap, afterSnap);
            }

            return ToolResult.Effected(Identity.Name(target, EntityManager), result);
        }, ct);
    }

    private Task<ToolResult> PickupAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "pickup", () =>
        {
            if (!TryTarget(s, args, out var target, out var failure))
                return failure!;

            var name = Identity.Name(target, EntityManager);

            return TryPickUp(borg, target, out var why)
                ? ToolResult.Effected(name, new Dictionary<string, object?> { ["взял"] = name })
                : ToolResult.Fail(ToolError.Refused, why, retry: "other_target");
        }, ct);
    }

    private Task<ToolResult> DropAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "drop", () =>
        {
            if (!_hands.TryGetActiveItem(borg, out var item) || item == null)
                return ToolResult.Fail(ToolError.Refused, "в активной руке ничего нет");

            var name = Identity.Name(item.Value, EntityManager);

            return _hands.TryDrop(borg)
                ? ToolResult.Effected(name, new Dictionary<string, object?> { ["положил"] = name })
                : ToolResult.Fail(ToolError.Refused, $"{name} не выпускается из руки — возможно, это несъёмный инструмент модуля");
        }, ct);
    }

    private Task<ToolResult> HitAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "hit", () =>
        {
            if (!TryTarget(s, args, out var target, out var failure))
                return failure!;

            // Боевое действие идёт тем же путём клика, но с боевым режимом: отдельного «удара»
            // мимо InteractionSystem у игрока тоже нет.
            _interaction.UserInteraction(borg, Transform(target).Coordinates, target);

            return ToolResult.Effected(Identity.Name(target, EntityManager), new Dictionary<string, object?>
            {
                ["ударил"] = Identity.Name(target, EntityManager),
            });
        }, ct);
    }

    private Task<ToolResult> ModuleAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "module", () =>
        {
            if (!StationAiAgentSystem.TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
                return ToolResult.Fail(ToolError.BadArgs, "module: нужен 'name'");

            return TrySelectModule(borg, name!, out var why)
                ? ToolResult.Effected("self", new Dictionary<string, object?> { ["модуль"] = name })
                : ToolResult.Fail(ToolError.Refused, why);
        }, ct);
    }
}
