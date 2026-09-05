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
/// The borg's tool set.
///
/// <para>
/// Common tools (laws, <c>noop</c>, timers, memory, skills, notes) come from the host
/// <see cref="StationAiAgentSystem.RegisterCommonTools"/> — twelve of them, no duplicates.
/// This only has what a stationary eye doesn't have and can't have: legs, a body's eyes, and hands.
/// </para>
/// <para>
/// What the borg deliberately does <b>not</b> have: <c>announce</c>, <c>device_action</c>,
/// <c>device_ui</c>, <c>move_camera</c>, <c>jump_to_core</c>, <c>crew_status</c>,
/// <c>station_status</c>. All seven rely either on the Station AI body's built-in consoles or on the
/// "AI is allowed to control this device" whitelist. The borg has neither: it doesn't "control" a
/// door remotely, it walks up to it and opens it by hand.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    private void RegisterBorgTools(AgentSession s, AiToolRegistry r, AiBorgComponent comp)
    {
        var channelEnum = string.Join(",", comp.Channels.Select(c => $"\"{c}\""));
        var L = s.Locale;

        // ------------------------------------------------------------------ legs

        r.Register(new AiTool
        {
            Name = "goto",
            Description = L.T(
                "Пойти к цели: к объекту по хендлу, к названию отсека (как на указателях " +
                "станции) или к координатам. Ты НЕ стоишь и не ждёшь — инструмент " +
                "отвечает сразу, а о прибытии придёт строка ARRIVED. Если дороги нет, " +
                "придёт NOPATH. Чтобы остановиться на полпути, вызови с stop.",
                "Walk to a target: an object by handle, a compartment name (as on station " +
                "signs), or coordinates. You do NOT stand still and wait — the tool replies " +
                "at once, and arrival comes as an ARRIVED line. If there is no path, you get " +
                "NOPATH. To stop halfway, call it with stop."),
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
            Name = "find_charger",
            Description = L.T(
                "Где зарядиться: ищет по ВСЕЙ станции станции для киборгов, в которые " +
                "влезаешь именно ты, и отдаёт их координатами от ближней к дальней, " +
                "с пометкой, запитана или обесточена. Глазами их не найти — стоят они " +
                "в робототехнике, а садишься ты там, где работал.",
                "Where to recharge: searches the WHOLE station for cyborg stations you " +
                "actually fit into, and returns them by coordinates nearest to farthest, " +
                "marked powered or unpowered. You will not find them by eye — they sit in " +
                "robotics, and you dock wherever you were working."),
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
                """,
            Handler = (a, ct) => FindChargerAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "ame_plan",
            Description = L.T(
                "Раскладка экранирования АМЭ: находит пульт и отдаёт девять клеток " +
                "квадрата в том порядке, в каком их занимать, плюс клетку выхода и " +
                "клетку подхода к пульту. Проверено заранее: пульт остаётся снаружи, " +
                "к нему есть подход, а укладка отступает наружу и не запирает тебя " +
                "внутри. Считай геометрию этим инструментом, а не в уме.",
                "AME shielding layout: finds the controller and returns the nine cells of " +
                "the square in the order to occupy them, plus the exit cell and the cell " +
                "for approaching the controller. Checked in advance: the controller stays " +
                "outside, it has an approach path, and the packing steps outward so you are " +
                "not locked inside. Count the geometry with this tool, not in your head."),
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
                """,
            Handler = (a, ct) => AmePlanAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "step",
            Description = L.T(
                "Сделать несколько шагов в одну сторону. Для точной доводки в комнате, " +
                "когда идти через полстанции не нужно. На дальние расстояния — goto.",
                "Take several steps in one direction. For fine positioning in a room when " +
                "you do not need to cross half the station. For long distances — goto."),
            GameAction = true,
            SchemaJson =
                "{\"type\":\"object\",\"required\":[\"dir\"],\"additionalProperties\":false,\"properties\":{" +
                "\"dir\":{\"type\":\"string\",\"enum\":" + s.Locale.DirEnumJson +
                ",\"description\":\"" + s.Locale.T("Куда шагать.", "Which way to step.") + "\"}," +
                "\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10,\"default\":1,\"description\":\"" +
                s.Locale.T("Сколько тайлов.", "How many tiles.") + "\"}}}",
            Handler = (a, ct) => StepAsync(s, a, ct),
        });

        // ----------------------------------------------------------------- eyes

        r.Register(new AiTool
        {
            Name = "look",
            Description = L.T(
                "Осмотреться вокруг СЕБЯ. Видишь то, что видел бы человек на твоём " +
                "месте: рядом и не за стеной. Возвращает список с хендлами — ими потом " +
                "адресуются остальные инструменты. У каждой строки две пары чисел: " +
                "Δ(dx,dy) — смещение от тебя на момент вызова, и следом абсолютные " +
                "координаты сетки. В goto подставляй ВТОРУЮ пару как есть, складывать " +
                "ничего не надо.",
                "Look around YOURSELF. You see what a human in your place would see: nearby " +
                "and not behind a wall. Returns a list with handles — the other tools address " +
                "those. Each line has two number pairs: Δ(dx,dy) is the offset from you at " +
                "call time, then absolute grid coordinates. For goto, plug in the SECOND pair " +
                "as-is; do not add anything."),
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "kind":{"type":"string","enum":["door","crew","apc","camera","airalarm","power","canister","computer","locker","device","obj"],"description":"Показать только объекты этого вида."}}}
                """,
            Handler = (a, ct) => BorgLookAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "examine",
            Description = L.T(
                "Рассмотреть одну вещь вблизи и прочитать её описание — то же, что видит " +
                "игрок, когда осматривает предмет. Так узнают, сварен ли болт, заряжена " +
                "ли батарея и что вообще перед тобой.",
                "Examine one thing up close and read its description — the same as a player " +
                "sees when examining an item. That is how you learn whether a bolt is welded, " +
                "whether a battery is charged, and what is in front of you."),
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл из look."}}}
                """,
            Handler = (a, ct) => ExamineAsync(s, a, ct),
        });

        // ------------------------------------------------------------------ hands

        r.Register(new AiTool
        {
            Name = "use",
            Description = L.T(
                "Нажать на цель: открыть дверь, включить машину, нажать кнопку. Надо " +
                "стоять рядом — сначала goto, потом use. Чтобы ПРИМЕНИТЬ инструмент " +
                "(отжать, сварить, вскрыть, прозвонить), назови его в 'tool' — робот сам " +
                "возьмёт его в руку. Инструменты у тебя из выбранного модуля; какие есть, " +
                "видно в строке SELF.",
                "Press the target: open a door, turn on a machine, press a button. You must " +
                "stand next to it — goto first, then use. To APPLY a tool (pry, weld, hack, " +
                "probe), name it in 'tool' — the borg will put it in hand itself. Your tools " +
                "come from the selected module; what you have is visible in the SELF line."),
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
            Description = L.T(
                "Взять предмет в свободную руку. Свободные руки зависят от выбранного " +
                "модуля — см. module.",
                "Pick up an item into a free hand. Free hands depend on the selected " +
                "module — see module."),
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
            Description = L.T(
                "Положить то, что держишь в активной руке, себе под ноги.",
                "Drop what you are holding in the active hand at your feet."),
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
                """,
            Handler = (a, ct) => DropAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "hit",
            Description = L.T(
                "Ударить цель тем, что в активной руке, а если оружия нет — корпусом. " +
                "Это применение силы: у него бывают последствия, и законы силикона на " +
                "тебя распространяются.",
                "Hit the target with what is in the active hand, or with the chassis if " +
                "there is no weapon. This is use of force: it has consequences, and silicon " +
                "laws apply to you."),
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл цели из look."}}}
                """,
            Handler = (a, ct) => HitAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "shoot",
            Description = L.T(
                "Выстрелить в цель из встроенного ствола или из оружия в руке. " +
                "Нужна прямая видимость: сквозь стену не попасть. Это применение " +
                "силы, и законы силикона на тебя распространяются.",
                "Shoot the target with the built-in gun or with a weapon in hand. " +
                "You need line of sight: you cannot hit through a wall. This is use of " +
                "force, and silicon laws apply to you."),
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл цели из look."}}}
                """,
            Handler = (a, ct) => ShootAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "module",
            Description = L.T(
                "Сменить рабочий модуль — это меняет набор инструментов у тебя в руках. " +
                "Без нужного модуля соответствующая работа просто не делается.",
                "Switch the working module — this changes the set of tools in your hands. " +
                "Without the right module the matching job simply is not done."),
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
            Description = L.T(
                "Пульт машины: без 'action' показывает показания и список кнопок, с " +
                "'action' — нажимает кнопку. Так управляют реактором, шлюзовыми " +
                "контроллерами, консолями. Надо стоять рядом — до двух тайлов, считая " +
                "по диагонали, и без стены между вами.",
                "Machine panel: without 'action' it shows readings and the button list, " +
                "with 'action' it presses a button. That is how you operate a reactor, " +
                "airlock controllers, consoles. You must stand next to it — up to two tiles " +
                "including diagonally, with no wall between you."),
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string","description":"Хендл машины из look."},
                "action":{"type":"string","description":"Имя кнопки. Без него — только показания и список."},
                "args":{"type":"object","description":"Аргументы кнопки, если она их требует."}}}
                """,
            // The driver is shared with the core: it's built via reflection over BUI message types
            // and knows nothing about the body. Only the gate differs — the core has a whitelist and
            // cameras, the borg has "reached it by hand."
            Handler = (a, ct) => _host.DeviceUiAsync(s, a, ct, param: "target", gate: (sess, uid) =>
                _interaction.InRangeUnobstructed(sess.Brain, uid, ConsoleReachTiles)
                    ? null
                    : ToolResult.Fail(ToolError.NotVisible, Unreachable(sess.Brain, uid, ConsoleReachTiles),
                        retry: "move_first")),
        });

        // ------------------------------------------------------------------ speech
        //
        // Handlers come from the host, but the schemas and descriptions are written here: the borg
        // has a different channel list and different audibility ("near yourself" rather than "near
        // your core"). The tool description rides in the frozen prefix and is the model's only
        // source of truth about its own capabilities, so a shared wording would be a lie, not savings.

        r.Register(new AiTool
        {
            Name = "say",
            Description = L.T(
                "Сказать вслух рядом с собой. Слышат те, кто стоит рядом с тобой. " +
                "Чтобы обратиться к экипажу по станции — radio.",
                "Speak aloud next to yourself. Those standing next to you hear it. " +
                "To address the crew across the station — radio."),
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
            Description = L.T(
                "Передать по радиоканалу станции. Без 'channel' уходит в текущий канал " +
                "(он всегда написан в строке SELF).",
                "Transmit on a station radio channel. Without 'channel' it goes to the " +
                "current channel (always written in the SELF line)."),
            GameAction = true,
            Speech = true,
            SpokenText = AiTool.TextArgument,
            // Built by concatenation, not interpolation: the channel list is different for each
            // chassis, and the JSON schema ends with three closing brackets in a row, which in an
            // interpolated literal would have to be escaped into unreadability.
            SchemaJson = "{\"type\":\"object\",\"required\":[\"text\"],\"additionalProperties\":false,\"properties\":{"
                         + "\"channel\":{\"type\":\"string\",\"enum\":[" + channelEnum + "]},"
                         + "\"text\":{\"type\":\"string\",\"maxLength\":400}}}",
            Handler = (a, ct) => _host.RadioAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "set_channel",
            Description = L.T(
                "Переключить канал, в который уходит твоя речь по умолчанию. Текущий " +
                "канал всегда виден в строке SELF.",
                "Switch the channel your speech goes to by default. The current channel " +
                "is always visible in the SELF line."),
            SchemaJson = "{\"type\":\"object\",\"required\":[\"channel\"],\"additionalProperties\":false,\"properties\":{"
                         + "\"channel\":{\"type\":\"string\",\"enum\":[" + channelEnum + "]}}}",
            Handler = (a, ct) => _host.SetChannelAsync(s, a, ct),
        });

        // ------------------------------------------------------- common to all bodies
        _host.RegisterCommonTools(s, r);

        // Waiting versions of walking and using. They aren't exposed over the wire — they exist only
        // for the script, where "walk over and continue" is one line, not four turns.
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

            // Via the route system, not directly: a long trip doesn't fit within the A* limit, and a
            // direct target halfway across the station would return "no path" from a perfectly
            // traversable place.
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

            // English names are accepted on equal footing with Russian ones.
            //
            // The prompt and schema ask for Russian, but the model thinks in a mix and regularly
            // writes step{dir='north'}: caught twice in one run, and each time it cost a turn on the
            // failure and a turn on the correction. Accepting both spellings is cheaper than teaching
            // it not to make the mistake.
            var delta = dir!.ToLowerInvariant() switch
            {
                "север" or "north" => new Vector2(0, 1),
                "юг" or "south" => new Vector2(0, -1),
                "запад" or "west" => new Vector2(-1, 0),
                "восток" or "east" => new Vector2(1, 0),
                _ => Vector2.Zero,
            };

            if (delta == Vector2.Zero)
                return ToolResult.Fail(ToolError.BadArgs, $"step: не знаю направления '{dir}'. Годятся: север, юг, запад, восток (или north, south, west, east)");

            var xform = Transform(borg);
            var target = new EntityCoordinates(
                xform.ParentUid.IsValid() ? xform.ParentUid : borg,
                xform.LocalPosition + delta * count);

            // Through the same route system as goto: a separate stepping code path would mean a
            // second way of moving, quietly diverging from the first.
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

            // Δ is computed in GRID coordinates, not map coordinates.
            //
            // A mismatch between coordinate systems only shows up on a rotated grid — and shows up
            // expensively. The model reads its own position from the SELF line (grid coordinates),
            // adds Δ from look, and walks to the resulting point via goto, which also works in grid
            // coordinates. While Δ was computed in map coordinates, this arithmetic silently produced
            // the wrong tile: on a live run the robot kept wandering out of the AME into the
            // neighboring TEG compartment and couldn't figure out why.
            var grid = Transform(borg).GridUid;
            var toGrid = grid != null ? _xform.GetInvWorldMatrix(grid.Value) : Matrix3x2.Identity;
            var origin = Vector2.Transform(_xform.GetMapCoordinates(borg).Position, toGrid);

            foreach (var uid in VisibleFrom(borg))
            {
                var k = _host.KindOf(uid);
                if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                var handle = s.Handles.GetOrCreate(uid, k);
                var there = Vector2.Transform(_xform.GetMapCoordinates(uid).Position, toGrid);
                var d = there - origin;

                // Two pairs of numbers, like in the core's look: the offset and the absolute point.
                //
                // A single Δ isn't enough, and that cost round 133. Δ is measured from where the
                // robot stood at the MOMENT look was called, but by the next script step it had
                // already moved — and it kept adding the old Δ to its new position. Its AME
                // controller ended up, in turn, at (29,-40), (28,-40), and (28,-39), the shielding
                // square went up in the wrong place, and the robot itself walked around trying
                // console from every side. The absolute pair needs no addition: it's plugged into
                // goto as-is.
                rows.Add($"{handle} | {Identity.Name(uid, EntityManager)} | {_host.ShortState(uid)} " +
                         $"| Δ({d.X:F0},{d.Y:F0}) ({there.X:F0},{there.Y:F0})");
            }

            return ToolResult.Success(new Dictionary<string, object?>
            {
                [s.Locale.Visible] = rows.Count,
                [s.Locale.Objects] = rows,
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

            // The same string the player reads. FormattedMessage carries markup, and the model
            // doesn't need it.
            var text = _examine.GetExamineText(target, borg).ToString();

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["это"] = Identity.Name(target, EntityManager),
                ["описание"] = text,
            });
        }, ct);
    }

    /// <summary>
    /// Why the machine can't be reached — with a distance, not just "go there first."
    ///
    /// The failure "goto that machine first" reached a robot on a live run AFTER it had already gone
    /// there: the cells around it were occupied by crates, it couldn't get any closer, and advice to
    /// repeat what was already done sent it on a second lap. The distance tells "you're far away"
    /// apart from "you're close, but something is standing between you," and those are two different
    /// next steps.
    /// </summary>
    private string Unreachable(EntityUid borg, EntityUid target, float reach = ReachTiles)
    {
        var gap = (_xform.GetMapCoordinates(target).Position - _xform.GetMapCoordinates(borg).Position).Length();

        return gap > reach
            ? $"не дотянуться: до цели {gap:F1} тайла, надо ближе"
            : $"не дотянуться, хотя до цели всего {gap:F1} тайла — между вами что-то стоит. " +
              "Обойди с другой стороны или убери помеху";
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

            // The id of the next deferred action — this is how we learn it even started.
            // The trick is taken from the upstream InteractWithOperator: DoAfter has no "I started"
            // event, but the counter on the component increments by exactly one per launch.
            var beforeDoAfter = TryComp<Content.Shared.DoAfter.DoAfterComponent>(borg, out var da)
                ? da.NextId
                : (ushort) 0;

            var withItem = args.ValueKind == JsonValueKind.Object
                           && args.TryGetProperty("with_item", out var wi)
                           && wi.ValueKind == JsonValueKind.True;

            // A tool was named — move it into the working hand.
            //
            // Without this, the module's tool set is half useless: there's one working hand and six
            // tools, and "use" went out with whatever happened to be active. During reactor startup
            // this cost debugging time — a shielding flatpack requires ZAPPING with a multitool, and
            // the crowbar was in hand instead, and the unpacking silently failed to trigger.
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

                    // Not just "no," but WHICH module provides it.
                    //
                    // On a live run the model picked the prying module — it grants a property, not
                    // hands — was left with empty hands, and spent ten turns cycling through tool
                    // names because the failure "switch modules" didn't say to which one. A failure
                    // must point to the next step, otherwise it just burns a turn.
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
                // The full player-click path: the engine itself decides whether the hand is empty or
                // holding an item.
                _interaction.UserInteraction(borg, Transform(target).Coordinates, target);
            }
            else
            {
                // Activation, NOT a click, and that's not a minor detail.
                //
                // The borg almost always has a module's unremovable tool in hand, and clicking with a
                // tool means "apply the tool": a crowbar on an airlock is prying with a long DoAfter,
                // not "open." On a live run this looked like this — the robot stands right next to
                // the door, use responds with "ok," the door stays closed. A human in this case
                // presses E, which is exactly what InteractionActivate does.
                _interaction.InteractionActivate(borg, target);
            }

            var after = _host.ShortState(target);
            var afterSnap = Snapshot(target);

            var started = TryComp<Content.Shared.DoAfter.DoAfterComponent>(borg, out var da2)
                          && da2.NextId != beforeDoAfter;

            // Remember the started action: the waiting version of use (called by the script) will
            // watch it through to completion and recompute the diff — based on the final result, not
            // the first instant.
            if (started)
                _pending[borg] = new PendingAction(beforeDoAfter, target, beforeSnap);

            var changes = Diff(beforeSnap, afterSnap);

            // Three different outcomes under one "ok" label — that was the tool's main defect.
            // Now they're named differently: a long action started; something changed (and WHAT
            // exactly); nothing worked (and WHY).
            var result = new Dictionary<string, object?>
            {
                [s.Locale.Was] = before,
                [s.Locale.Became] = after,
            };

            if (started)
            {
                result[s.Locale.Outcome] = s.Locale.OutcomeStarted;
            }
            else if (changes.Count > 0)
            {
                result[s.Locale.Outcome] = s.Locale.OutcomeOk;
                result[s.Locale.Changed] = changes;
            }
            else
            {
                result[s.Locale.Outcome] = s.Locale.OutcomeFailed;
                result[s.Locale.Why] = Explain(target, toolUsed, beforeSnap, afterSnap);
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
