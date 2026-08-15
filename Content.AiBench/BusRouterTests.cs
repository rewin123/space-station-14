using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// The debug API's behaviour, with no socket.
///
/// The router is a pure function, which is the whole reason these run in milliseconds and do not
/// bind a port: a pooled test process that leaked a listener would fail the <em>next</em> test's
/// bind with "address already in use", and the failure would name the wrong test.
/// </summary>
[TestFixture]
[Category("AiBus")]
public sealed class BusRouterTests
{
    private const string Token = "секретный-токен-для-теста";

    private static ISawmill Sawmill => new LogManager().GetSawmill("bus-router-test");

    private string _dir = "";
    private AgentEventBus _bus = null!;
    private MemoryStore _memory = null!;
    private SkillStore _skills = null!;
    private PlayerNoteStore _notes = null!;
    private ConversationState _conv = null!;
    private bool _hasSession;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aibench-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _bus = new AgentEventBus(512);
        _memory = new MemoryStore(_dir, Sawmill);
        _memory.AttachSink(_bus.ForProcess());
        _memory.LoadFromDisk();

        _skills = new SkillStore(_dir, Sawmill);
        _notes = new PlayerNoteStore(_dir, Sawmill);
        _skills.AttachSink(_bus.ForProcess());
        _skills.LoadFromDisk();

        _conv = new ConversationState();
        _conv.AttachSink(_bus.ForSession("current"));
        _conv.SetPrefix("ПРОМПТ", "[]");

        _hasSession = false;
        _sent.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Records what the router asked the system to do, so commands can be asserted.</summary>
    private readonly List<string> _sent = new();

    private AgentDebugRouter Router()
    {
        return new AgentDebugRouter(
            _bus,
            Token,
            "current",
            // The router never looks a session up; it is handed one. Null models "nobody has a core".
            () => null,
            () => _memory,
            () => _skills,
            () => _notes,
            () => 42,
            text =>
            {
                if (!_hasSession)
                    return (false, "нет активного агента");

                _sent.Add(text);
                return (true, "доставлено следующим ходом");
            },
            (action, match, content) => action switch
            {
                "add" => _memory.Add(content),
                "replace" => _memory.Replace(match, content),
                "remove" => _memory.Remove(match),
                _ => new MemoryResult(false, $"неизвестное действие '{action}'"),
            },
            (name, when, body, match, replacement) =>
                match != null || replacement != null
                    ? _skills.Edit(name, match ?? "", replacement ?? "")
                    : _skills.Write(name, when ?? "", body ?? ""));
    }

    private Task<AgentDebugResponse> Get(string path, params (string Key, string Value)[] query)
    {
        var q = new Dictionary<string, string>();
        foreach (var (key, value) in query)
            q[key] = value;

        return Router().RouteAsync("GET", path, q, "", "Bearer " + Token, CancellationToken.None);
    }

    private Task<AgentDebugResponse> Post(string body) =>
        Router().RouteAsync("POST", "/command", new Dictionary<string, string>(), body,
            "Bearer " + Token, CancellationToken.None);

    private static JsonElement Body(AgentDebugResponse response) =>
        JsonDocument.Parse(response.Json).RootElement.Clone();

    // ------------------------------------------------------------------- auth

    [Test]
    public async Task WrongTokenIsRejected()
    {
        var wrong = await Router().RouteAsync("GET", "/state", new Dictionary<string, string>(), "",
            "Bearer не-тот-токен", CancellationToken.None);

        var missing = await Router().RouteAsync("GET", "/state", new Dictionary<string, string>(), "",
            null, CancellationToken.None);

        var noScheme = await Router().RouteAsync("GET", "/state", new Dictionary<string, string>(), "",
            Token, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(wrong.Status, Is.EqualTo(401));
            Assert.That(missing.Status, Is.EqualTo(401), "запрос без заголовка обязан отвергаться");
            Assert.That(noScheme.Status, Is.EqualTo(401), "токен без схемы Bearer — тоже отказ");
        });
    }

    /// <summary>
    /// The preflight is answered BEFORE the token is checked, and this test is named for that
    /// ordering because the ordering is the whole fix.
    ///
    /// `Authorization` is not CORS-safelisted, so a browser preflights every request this API
    /// takes, and the preflight deliberately carries no Authorization header. Check the token
    /// first and it answers 401; a preflight needs 2xx or the browser blocks the real request.
    /// The symptom is a cross-origin page that cannot even do GET /state, with nothing in the
    /// server log explaining why. Whoever "tidies" the early return back below the auth check
    /// breaks the debugger silently — this is what stops them.
    /// </summary>
    [Test]
    public async Task PreflightIsAnsweredBeforeTheTokenIsChecked()
    {
        var preflight = await Router().RouteAsync("OPTIONS", "/state", new Dictionary<string, string>(), "",
            null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(preflight.Status, Is.EqualTo(200),
                "preflight обязан быть 2xx — иначе браузер заблокирует настоящий запрос");
            Assert.That(preflight.Json, Is.Not.Null);
        });

        // And the exemption must be exactly that narrow: everything else still needs the token.
        var unauthorised = await Router().RouteAsync("GET", "/state", new Dictionary<string, string>(), "",
            null, CancellationToken.None);

        Assert.That(unauthorised.Status, Is.EqualTo(401),
            "исключение для OPTIONS не должно было открыть остальные маршруты");
    }

    [Test]
    public async Task StateCarriesMemoryLimitsAndRound()
    {
        var body = Body(await Get("/state"));
        var memory = body.GetProperty("memory");

        Assert.Multiple(() =>
        {
            Assert.That(memory.GetProperty("memory_limit").GetInt32(), Is.EqualTo(_memory.MemoryLimit),
                "без лимита заполненность живой памяти выводится только регуляркой из шапки замороженного блока");
        });
    }

    [Test]
    public async Task UnknownPathIs404()
    {
        var response = await Get("/что-то-другое");
        Assert.That(response.Status, Is.EqualTo(404));
    }

    // ------------------------------------------------------------------ state

    [Test]
    public async Task StateCarriesTheWholeAgent()
    {
        _memory.Add("капитан доверяет мне");
        _skills.Write("restore-core-power", "когда ядро обесточено", "звать инженеров");

        var body = Body(await Get("/state"));

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("instance").GetString(), Is.EqualTo(_bus.Instance));
            Assert.That(body.GetProperty("session").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "агента нет — это нормальный ответ, а не ошибка");
            Assert.That(body.GetProperty("memory").GetProperty("memory_live")[0].GetString(),
                Is.EqualTo("капитан доверяет мне"));
            Assert.That(body.GetProperty("skills")[0].GetProperty("name").GetString(),
                Is.EqualTo("restore-core-power"));
        });
    }

    [Test]
    public async Task HealthReportsTheCursor()
    {
        _conv.AppendUser("что-то произошло");

        var body = Body(await Get("/health"));

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("seq").GetInt64(), Is.EqualTo(_bus.Seq));
            Assert.That(body.GetProperty("ring").GetInt32(), Is.EqualTo(_bus.Capacity));
        });
    }

    // ----------------------------------------------------------------- events

    [Test]
    public async Task EventsCarryPayloadsAsRealJsonNotStrings()
    {
        // The payload is already a JSON string when it enters the ring. If the router emitted it
        // through a serializer it would arrive double-encoded and every client would have to parse
        // twice — a papercut that is very easy to ship and very annoying to discover.
        _conv.AppendUser("наблюдение");

        var body = Body(await Get("/events", ("since", "0"), ("instance", _bus.Instance)));
        var types = new List<string>();
        JsonElement? appended = null;

        foreach (var e in body.GetProperty("events").EnumerateArray())
        {
            var type = e.GetProperty("type").GetString()!;
            types.Add(type);

            if (type == "message.appended")
                appended = e;
        }

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("resync").GetBoolean(), Is.False);
            Assert.That(types, Does.Contain("prefix.replaced").And.Contain("message.appended"));
            Assert.That(appended, Is.Not.Null);
            Assert.That(appended!.Value.GetProperty("payload").ValueKind, Is.EqualTo(JsonValueKind.Object),
                "payload приехал строкой — клиенту пришлось бы парсить дважды");
            Assert.That(appended.Value.GetProperty("payload").GetProperty("message")
                    .GetProperty("content").GetString(),
                Is.EqualTo("наблюдение"));
        });
    }

    [Test]
    public async Task EventsReportResyncWhenTheCursorIsUnusable()
    {
        _conv.AppendUser("наблюдение");

        var future = Body(await Get("/events", ("since", "99999"), ("instance", _bus.Instance)));
        var alien = Body(await Get("/events", ("since", "0"), ("instance", "другой-процесс")));

        Assert.Multiple(() =>
        {
            Assert.That(future.GetProperty("resync").GetBoolean(), Is.True,
                "курсор из будущего — обычно клиент, переживший перезапуск процесса");
            Assert.That(alien.GetProperty("resync").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task BadSinceIsRejected()
    {
        var response = await Get("/events", ("since", "не-число"));
        Assert.That(response.Status, Is.EqualTo(400));
    }

    // --------------------------------------------------------------- commands

    [Test]
    public async Task MessageWithNoSessionIs409()
    {
        var response = await Post("{\"type\":\"message.send\",\"text\":\"открой шлюз\"}");

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo(409),
                "очередь, пережившая рестарт раунда, въехала бы в свежий разговор");
            Assert.That(_sent, Is.Empty);
        });
    }

    [Test]
    public async Task MessageWithASessionIsQueuedForTheNextTurn()
    {
        _hasSession = true;

        var body = Body(await Post("{\"type\":\"message.send\",\"text\":\"открой шлюз в атмос\"}"));

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("applied").GetString(), Is.EqualTo("next_turn"));
            Assert.That(_sent, Is.EqualTo(new[] { "открой шлюз в атмос" }));
        });
    }

    [Test]
    public async Task MemoryChangeWorksWithoutASessionAndSaysWhenTheModelWillSeeIt()
    {
        // Memory and skills are process-wide: they exist from Initialize and outlive every round.
        var body = Body(await Post(
            "{\"type\":\"memory.change\",\"action\":\"add\",\"content\":\"SMES в инженерном разряжается быстрее\"}"));

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("applied").GetString(), Is.EqualTo("disk"));
            Assert.That(body.GetProperty("visible_to_model").GetString(), Is.EqualTo("next_compaction"),
                "без этого оператор правит память, видит то же поведение и решает, что эндпоинт сломан");
            Assert.That(_memory.Entries(), Does.Contain("SMES в инженерном разряжается быстрее"));
        });
    }

    [Test]
    public async Task SkillChangeWritesAndEdits()
    {
        var written = Body(await Post(
            "{\"type\":\"skill.change\",\"name\":\"bolt-armoury\",\"when\":\"когда вскрывают оружейную\"," +
            "\"body\":\"опустить болты\"}"));

        var edited = Body(await Post(
            "{\"type\":\"skill.change\",\"name\":\"bolt-armoury\",\"match\":\"\",\"replacement\":\"и объявить\"}"));

        _skills.TryGet("bolt-armoury", out var skill);

        Assert.Multiple(() =>
        {
            Assert.That(written.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(edited.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(skill.Body, Does.Contain("опустить болты"));
            Assert.That(skill.Body, Does.Contain("и объявить"));
        });
    }

    [Test]
    public async Task MalformedCommandsAreRejectedClearly()
    {
        var broken = await Post("{это не json");
        var noType = await Post("{}");
        var unknown = await Post("{\"type\":\"выключи-станцию\"}");
        var noName = await Post("{\"type\":\"skill.change\",\"body\":\"тело без имени\"}");

        Assert.Multiple(() =>
        {
            Assert.That(broken.Status, Is.EqualTo(400));
            Assert.That(noType.Status, Is.EqualTo(400));
            Assert.That(unknown.Status, Is.EqualTo(400));
            Assert.That(Body(unknown).GetProperty("error").GetString(), Does.Contain("message.send"),
                "отказ обязан перечислять, что вообще бывает");
            Assert.That(noName.Status, Is.EqualTo(400));
        });
    }

    [Test]
    public async Task RefusedMemoryChangeIs400AndChangesNothing()
    {
        var full = new MemoryStore(_dir, Sawmill) { MemoryLimit = 20 };
        full.LoadFromDisk();
        _memory = full;

        var response = await Post(
            "{\"type\":\"memory.change\",\"action\":\"add\",\"content\":\"" +
            new string('щ', 100) + "\"}");

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo(400));
            Assert.That(full.Entries(), Is.Empty);
        });
    }
}
