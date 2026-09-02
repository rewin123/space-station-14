using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.Doors.Components;
using Content.Shared.Silicons.StationAi;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// One integration test per tool, against a live in-process server.
///
/// Every test drives the real registry, the real main-thread marshalling and the real gate chain,
/// then asserts on <b>world state</b> rather than on the tool's own report. A tool that returns
/// <c>ok:true</c> while the door stayed shut is exactly the bug worth catching, and only reading
/// the world afterwards catches it.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class ToolTests
{
    // ------------------------------------------------------------------ perception

    [Test]
    public async Task Look_SeesNearbyDoor_AndMintsHandle()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");

        var result = await w.Invoke("look");

        Assert.That(result.Ok, Is.True, $"look упал: {result.ToJson()}");
        Assert.That(result.Effect, Is.Not.Null);
        Assert.That(result.ToJson(), Does.Contain("door-"),
            $"look не выдал хендл двери. Ответ: {result.ToJson()}");
    }

    [Test]
    public async Task Inspect_ReportsDoorState_AndAvailableActions()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        var json = result.ToJson();
        Assert.That(json, Does.Contain("door_state"), json);
        Assert.That(json, Does.Contain("\"open\""), $"inspect не перечислил доступные действия: {json}");
    }

    [Test]
    public async Task Inspect_ReportsCutAiWire()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        await w.Post(() =>
        {
            // Go through the same call the wire action makes rather than poking the field:
            // the analyzer forbids the write, and more importantly the method is what carries
            // upstream's announce-to-the-AI behaviour.
            var ai = w.Pair.Server.System<Content.Shared.Silicons.StationAi.SharedStationAiSystem>();
            ai.SetWhitelistEnabled((door, ent.GetComponent<StationAiWhitelistComponent>(door)), false);
        });

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("перерезан"), result.ToJson());
    }

    [Test]
    public async Task CrewStatus_Answers_EvenWithNoSensors()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("crew_status");

        // An empty crew monitor is a legitimate answer; a crash or a missing console is not.
        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("crew"), result.ToJson());
    }

    [Test]
    public async Task Identify_ReturnsPresentedNameAndIdCard()
    {
        await using var w = await AiWorld.Create();
        var mob = await w.Spawn("MobHuman", 1);
        var handle = await w.Handle(mob);

        var result = await w.Invoke("identify", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        var json = result.ToJson();
        Assert.That(json, Does.Contain("presented"), json);
        Assert.That(json, Does.Contain("id_card"), json);
        Assert.That(json, Does.Contain("job_icon"), json);
    }

    [Test]
    public async Task Records_Answers()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("records");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("records"), result.ToJson());
    }

    [Test]
    public async Task Laws_ReturnsCrewsimov()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("laws");

        Assert.That(result.Ok, Is.True, result.ToJson());
        var json = result.ToJson();
        Assert.That(json, Does.Contain("laws"), json);
        Assert.That(json, Does.Not.Contain("\"count\":0"),
            $"у ИИ не оказалось ни одного закона: {json}");
    }

    [Test]
    public async Task StationStatus_Answers()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("station_status");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("round_time"), result.ToJson());
    }

    // ---------------------------------------------------------------------- speech

    [Test]
    public async Task Say_Succeeds()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("say", """{"text":"Проверка связи."}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("said"), result.ToJson());
    }

    [Test]
    public async Task Radio_Succeeds_OnBinary()
    {
        await using var w = await AiWorld.Create();

        // Binary is longRange, so it needs no telecom server — Common would silently go nowhere
        // on a bare test grid and the failure would look like an agent bug.
        var result = await w.Invoke("radio", """{"channel":"Binary","text":"Проверка."}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("Binary"), result.ToJson());
    }

    [Test]
    public async Task Radio_RejectsUnknownChannel_AndSuggestsNearest()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("radio", """{"channel":"Comon","text":"опечатка"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.BadArgs));
        Assert.That(result.Alternatives, Does.Contain("Common"),
            $"не подсказан ближайший канал: {result.ToJson()}");
    }

    [Test]
    public async Task Announce_Succeeds()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("announce", """{"text":"Внимание экипажу."}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
    }

    // ------------------------------------------------------------ ничего не делать

    [Test]
    public async Task Noop_WorksInEveryMode()
    {
        // Единственный инструмент без причин отказать, и это намеренно. Сказать «делать нечего»
        // агент должен уметь всегда: и когда идёт разбор, и когда он лежит в чужом кармане. Отказ
        // здесь означал бы «ты обязан что-то сделать», а обязан он ровно наоборот — не спамить.
        await using var w = await AiWorld.Create();

        var core = await w.Invoke("noop", """{"reason":"чужой разговор по рации"}""");
        Assert.That(core.Ok, Is.True, core.ToJson());

        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = Content.Server.AiAgent.AgentMode.Carded);
        var carded = await w.Invoke("noop", "{}");

        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = Content.Server.AiAgent.AgentMode.Review);
        var review = await w.Invoke("noop", "{}");

        Assert.Multiple(() =>
        {
            Assert.That(carded.Ok, Is.True, $"из интелликарты закрыть ход нельзя: {carded.ToJson()}");
            Assert.That(review.Ok, Is.True, $"во время разбора закрыть ход нельзя: {review.ToJson()}");
        });
    }

    // -------------------------------------------------------------------- movement

    [Test]
    public async Task MoveCamera_MovesEyeToTarget()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand", 3);
        var handle = await w.Handle(door);

        var result = await w.Invoke("move_camera", $$"""{"handle":"{{handle}}"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("eye_x"), result.ToJson());
    }

    [Test]
    public async Task JumpToCore_Succeeds()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("jump_to_core");

        Assert.That(result.Ok, Is.True, result.ToJson());
    }

    // --------------------------------------------------------------------- devices

    [Test]
    public async Task DeviceAction_OpensDoor_AndEffectMatchesWorld()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"open"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());

        // The point of the effect field: what it claims must equal what the server holds.
        await w.Pair.Server.WaitRunTicks(20);
        var state = await w.Read(() => ent.GetComponent<DoorComponent>(door).State);

        Assert.That(state, Is.EqualTo(DoorState.Open).Or.EqualTo(DoorState.Opening),
            $"дверь не открылась, состояние {state}. Ответ инструмента: {result.ToJson()}");
    }

    [Test]
    public async Task DeviceAction_BoltsDoor()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"bolt"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        await w.Pair.Server.WaitRunTicks(10);

        var bolted = await w.Read(() => ent.GetComponent<DoorBoltComponent>(door).BoltsDown);
        Assert.That(bolted, Is.True, $"болты не опустились. Ответ: {result.ToJson()}");
    }

    [Test]
    public async Task DeviceAction_Electrifies()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"electrify"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("electrified"), result.ToJson());
    }

    [Test]
    public async Task DeviceAction_RejectsUnknownAction_AndSuggestsNearest()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"opeen"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.BadArgs));
        Assert.That(result.Alternatives, Does.Contain("open"), result.ToJson());
    }

    [Test]
    public async Task DeviceAction_RefusesWhenAiWireCut()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;

        await w.Post(() =>
        {
            var ai = w.Pair.Server.System<Content.Shared.Silicons.StationAi.SharedStationAiSystem>();
            ai.SetWhitelistEnabled((door, ent.GetComponent<StationAiWhitelistComponent>(door)), false);
        });

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"open"}""");

        Assert.That(result.Ok, Is.False, "перерезанный провод должен запрещать управление");
        Assert.That(result.Error, Is.EqualTo(ToolError.WireCut), result.ToJson());
    }

    [Test]
    public async Task DeviceAction_ReportsStaleHandle_WithSuggestions()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        await w.Handle(door);

        var result = await w.Invoke("device_action", """{"handle":"door-999","action":"open"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.StaleHandle), result.ToJson());
        Assert.That(result.Alternatives, Is.Not.Empty, "не подсказан ни один живой хендл");
    }

    [Test]
    public async Task DeviceUi_DoesNotOfferTheWirePanel()
    {
        // An airlock's only bound interface is its maintenance panel, and its messages land on the
        // same entity as any console's would — reflection cannot tell them apart, because the UI
        // key each handler wants is hidden inside the subscription closure.
        //
        // So the panel is excluded by name, and this is the test that says so. Cutting wires is a
        // screwdriver against an opened hatch; the AI has no hands, cannot open one in vanilla, and
        // the wires behind that particular hatch include the one that cuts AI control of the door.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var listing = await w.Invoke("device_ui", $$"""{"handle":"{{handle}}"}""");
        Assert.That(listing.ToJson(), Does.Not.Contain("wires"), listing.ToJson());

        var cut = await w.Invoke("device_ui",
            $$$"""{"handle":"{{{handle}}}","action":"wires_action","args":{"id":1,"action":"Cut"}}""");

        Assert.That(cut.Ok, Is.False, cut.ToJson());
        Assert.That(cut.Error, Is.EqualTo(ToolError.BadArgs), cut.ToJson());
    }

    // ------------------------------------------------------------------ dispatcher

    [Test]
    public async Task UnknownTool_SuggestsNearest()
    {
        await using var w = await AiWorld.Create();
        var result = await w.Invoke("radiio", """{"channel":"Binary","text":"x"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ToolError.UnknownTool));
        Assert.That(result.Alternatives, Does.Contain("radio"), result.ToJson());
    }

    [Test]
    public async Task DryRun_DoesNotMutateTheWorld()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);
        var ent = w.Ent;
        var cfg = w.Pair.Server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();

        await w.Post(() => cfg.SetCVar(Content.Server.AiAgent.AiCVars.DryRun, true));

        var result = await w.Invoke("device_action", $$"""{"handle":"{{handle}}","action":"bolt"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("dry_run"), result.ToJson());

        await w.Pair.Server.WaitRunTicks(10);
        var bolted = await w.Read(() => ent.GetComponent<DoorBoltComponent>(door).BoltsDown);

        Assert.That(bolted, Is.False, "в режиме dry_run мир меняться не должен");
    }

    // ------------------------------------------------------------ заметки о людях

    [Ignore("Проверяют инструменты read_player_related_memory / edit_player_related_memory, снесённые рефактором памяти агента 31.08.2026: заметки о людях переехали в файловую систему (sh, write_file, edit_file). Поведение, которое здесь описано, по-прежнему нужно — переписать под новые инструменты, а не удалять.")]
    [Test]
    public async Task PlayerNote_ToolsRoundTripThroughTheRealDispatcher()
    {
        // Через настоящий диспетчер: это проверяет регистрацию, схему, разбор аргументов и то, что
        // хендлер не маршалится на игровой поток.
        await using var w = await AiWorld.Create();

        var written = await w.Invoke("edit_player_related_memory",
            """{"name":"Иван Петров","new":"Инженер, просил открыть атмос."}""");
        Assert.That(written.Ok, Is.True, written.ToJson());

        var read = await w.Invoke("read_player_related_memory", """{"name":"иван петров"}""");

        Assert.Multiple(() =>
        {
            Assert.That(read.Ok, Is.True, read.ToJson());
            Assert.That(read.ToJson(), Does.Contain("просил открыть атмос"), read.ToJson());
            Assert.That(read.ToJson(), Does.Contain("[раунд"), "штамп обязан стоять: " + read.ToJson());
        });
    }

    [Ignore("Проверяют инструменты read_player_related_memory / edit_player_related_memory, снесённые рефактором памяти агента 31.08.2026: заметки о людях переехали в файловую систему (sh, write_file, edit_file). Поведение, которое здесь описано, по-прежнему нужно — переписать под новые инструменты, а не удалять.")]
    [Test]
    public async Task PlayerNote_ReadOfAnUnknownName_SuggestsTheNearest()
    {
        await using var w = await AiWorld.Create();
        await w.Invoke("edit_player_related_memory", """{"name":"Иван Петров","new":"Инженер."}""");

        var result = await w.Invoke("read_player_related_memory", """{"name":"Иван Птров"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Alternatives, Does.Contain("Иван Петров"),
                "промах в одну букву обязан чиниться за один ход: " + result.ToJson());
        });
    }

    [Ignore("Проверяют инструменты read_player_related_memory / edit_player_related_memory, снесённые рефактором памяти агента 31.08.2026: заметки о людях переехали в файловую систему (sh, write_file, edit_file). Поведение, которое здесь описано, по-прежнему нужно — переписать под новые инструменты, а не удалять.")]
    [Test]
    public async Task PlayerNote_SearchOnGarbage_IsSuccessNotFailure()
    {
        // «Никого похожего нет» — законный ответ. Отказ научил бы модель, что искать было ошибкой.
        await using var w = await AiWorld.Create();
        await w.Invoke("edit_player_related_memory", """{"name":"Иван Петров","new":"Инженер."}""");

        var result = await w.Invoke("search_player_related_notes", """{"approx_name":"кхзщыв"}""");

        Assert.That(result.Ok, Is.True, result.ToJson());
        Assert.That(result.ToJson(), Does.Contain("ни на одно похожее"), result.ToJson());
    }

    [Ignore("Проверяют инструменты read_player_related_memory / edit_player_related_memory, снесённые рефактором памяти агента 31.08.2026: заметки о людях переехали в файловую систему (sh, write_file, edit_file). Поведение, которое здесь описано, по-прежнему нужно — переписать под новые инструменты, а не удалять.")]
    [Test]
    public async Task PlayerNote_ToolsWorkDuringReviewAndWhileCarded()
    {
        // Это то, что делает возможной работу куратора: он разбирает отрезок в режиме Review, и
        // если кто-нибудь припишет тулам GameAction, разбор молча перестанет записывать людей.
        await using var w = await AiWorld.Create();

        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = Content.Server.AiAgent.AgentMode.Review);
        var inReview = await w.Invoke("edit_player_related_memory",
            """{"name":"Иван Петров","new":"Записано на разборе."}""");

        await w.Post(() => w.System.GetSession(w.Brain)!.Mode = Content.Server.AiAgent.AgentMode.Carded);
        var carded = await w.Invoke("read_player_related_memory", """{"name":"Иван Петров"}""");

        Assert.Multiple(() =>
        {
            Assert.That(inReview.Ok, Is.True, "на разборе заметки обязаны писаться: " + inReview.ToJson());
            Assert.That(carded.Ok, Is.True, "из интелликарты заметки обязаны читаться: " + carded.ToJson());
        });
    }
}
