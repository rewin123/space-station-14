using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Bus;

/// <summary>One answer: a status code and a JSON body.</summary>
public readonly record struct AgentDebugResponse(int Status, string Json)
{
    public static AgentDebugResponse Ok(object payload) =>
        new(200, JsonSerializer.Serialize(payload, AgentDebugJson.Options));

    public static AgentDebugResponse Error(int status, string message) =>
        new(status, JsonSerializer.Serialize(new { ok = false, error = message }, AgentDebugJson.Options));
}

/// <summary>
/// What the debug API does, with no socket anywhere in sight.
///
/// A pure function of <c>(method, path, query, body, token)</c>, so the entire behaviour — routing,
/// auth, cursors, resync, command validation — is testable at the speed of a unit test, and
/// <see cref="AgentDebugServer"/> is left with nothing but the plumbing. The alternative is a suite
/// that binds real ports, which in a pooled test process means a listener outliving the test that
/// created it and the next rebind failing with "address already in use".
/// </summary>
public sealed class AgentDebugRouter
{
    /// <summary>
    /// How long a long-poll waits before answering with an empty list.
    ///
    /// Under the thirty seconds most proxies and browsers use as an idle timeout, so a poll that
    /// finds nothing looks like a normal fast response rather than a hang.
    /// </summary>
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(25);

    private readonly AgentEventBus _bus;
    private readonly AgentDirectory _agents;
    /// <summary>Файловая система ядра. Может быть <c>null</c> до первой сессии.</summary>
    private readonly Func<Vfs.Vfs?> _vfs;

    private readonly Func<int> _round;

    private readonly Func<string, string, string, MemoryResult> _changeMemory;
    private readonly Func<string, string?, string?, string?, string?, SkillResult> _changeSkill;
    private readonly string _token;

    public AgentDebugRouter(
        AgentEventBus bus,
        string token,
        AgentDirectory agents,
        Func<Vfs.Vfs?> vfs,
        Func<int> round,
        Func<string, string, string, MemoryResult> changeMemory,
        Func<string, string?, string?, string?, string?, SkillResult> changeSkill)
    {
        _bus = bus;
        _token = token;
        _agents = agents;
        _vfs = vfs;
        _round = round;
        _changeMemory = changeMemory;
        _changeSkill = changeSkill;
    }

    public async Task<AgentDebugResponse> RouteAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string> query,
        string body,
        string? authorization,
        CancellationToken ct)
    {
        // CORS preflight, answered ABOVE the token check — and that ordering is the whole point.
        //
        // `Authorization` is not a CORS-safelisted request header, so a browser preflights every
        // single request this API takes. The preflight is an OPTIONS that deliberately carries no
        // Authorization header, so checking the token first answers it 401 — and a preflight needs
        // a 2xx or the browser blocks the real request. The symptom is that a cross-origin page
        // cannot even do GET /state, with nothing in the server log to say why.
        //
        // This is the only path here that reaches a response without passing Authorised, which is
        // exactly why it must stay above it. Tidying it back below reintroduces the bug silently.
        // A 200 with an empty object rather than a 204: the server unconditionally sets a content
        // type and writes a body, and a 204 carrying Content-Length is a protocol violation. The
        // browser discards a preflight body unread either way.
        if (method == "OPTIONS")
            return new AgentDebugResponse(200, "{}");

        if (!Authorised(authorization))
            return AgentDebugResponse.Error(401, "нужен заголовок Authorization: Bearer <ai.debug_token>");

        return (method, path) switch
        {
            ("GET", "/health") => Health(),
            ("GET", "/state") => State(),
            ("GET", "/session") => Session(query),
            ("GET", "/events") => await Events(query, ct).ConfigureAwait(false),
            ("POST", "/command") => Command(body),

            _ => AgentDebugResponse.Error(404, $"нет такого пути: {method} {path}"),
        };
    }

    /// <summary>
    /// Constant-time comparison over the raw bytes.
    ///
    /// An ordinary string compare returns as soon as it finds a difference, which leaks the token
    /// one character at a time to anyone willing to measure. Cheap to do right.
    /// </summary>
    private bool Authorised(string? authorization)
    {
        const string scheme = "Bearer ";

        if (authorization == null || !authorization.StartsWith(scheme, StringComparison.Ordinal))
            return false;

        var offered = Encoding.UTF8.GetBytes(authorization[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(_token);

        return CryptographicOperations.FixedTimeEquals(offered, expected);
    }

    private AgentDebugResponse Health() =>
        AgentDebugResponse.Ok(new
        {
            ok = true,
            instance = _bus.Instance,
            seq = _bus.Seq,
            ring = _bus.Capacity,
            ring_used = _bus.Count,
            round = _round(),

            // Раньше здесь стояло поле session с константой «current» и одиночный pending_input.
            // Оба врали ровно с того дня, как агентов стало двое: показывали того, кто занял тело
            // последним, и его же ящик. Теперь и то, и другое — свойство строки ростера.
            agents = _agents.Roster(),
        });

    private AgentDebugResponse State() =>
        AgentDebugResponse.Ok(AgentDebugState.CaptureGlobal(_bus, _agents, _vfs(), _round()));

    /// <summary>
    /// Снимок одного агента.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Неизвестный идентификатор — <b>200 с полем agent: null</b>, а не 404. Агент мог уйти между
    /// кадром <c>session.started</c> и этим запросом, и это штатная гонка, а не ошибка клиента.
    /// Клиент считает 404 терминальным и после него навсегда останавливает опрос — то есть
    /// «правильный» код погасил бы весь отладчик из-за нормального события.
    /// </para>
    /// <para>
    /// А вот отсутствие параметра — 400, и это не непоследовательность. Молча выбрать «первого
    /// попавшегося» значило бы вернуть ту самую ошибку, ради устранения которой этот маршрут и
    /// появился: показать не того агента, ничем не выдав подмены.
    /// </para>
    /// </remarks>
    private AgentDebugResponse Session(IReadOnlyDictionary<string, string> query)
    {
        var id = query.GetValueOrDefault("agent");

        if (string.IsNullOrWhiteSpace(id))
            return AgentDebugResponse.Error(400, "нужен параметр agent — список даёт GET /state");

        return AgentDebugResponse.Ok(AgentDebugState.CaptureAgent(_bus, _agents.Find(id)));
    }

    private async Task<AgentDebugResponse> Events(IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var instance = query.GetValueOrDefault("instance");

        if (!long.TryParse(query.GetValueOrDefault("since") ?? "0", out var since) || since < 0)
            return AgentDebugResponse.Error(400, "since должен быть неотрицательным целым");

        var read = await _bus.ReadAsync(instance, since, PollTimeout, ct).ConfigureAwait(false);

        // Hand-built rather than serialised from a DTO: each payload is ALREADY a JSON string,
        // produced at publish time, and round-tripping it through a parse only to re-emit it would
        // cost more than everything else here put together.
        var sb = new StringBuilder(256);
        sb.Append("{\"instance\":").Append(JsonSerializer.Serialize(read.Instance, AgentDebugJson.Options));
        sb.Append(",\"seq\":").Append(read.Seq);
        sb.Append(",\"resync\":").Append(read.Resync ? "true" : "false");

        // Ростер едет вместе с лентой, но КАДРОМ НЕ ЯВЛЯЕТСЯ: к нему не относятся ни курсор, ни
        // resync, и применять его как событие нельзя. Он снимается в момент возврата длинного
        // опроса, то есть может быть моложе запроса на все двадцать пять секунд. Сеять агента по
        // ростеру поэтому нельзя — только по кадру session.started.
        sb.Append(",\"agents\":")
          .Append(JsonSerializer.Serialize(_agents.Roster(), AgentDebugJson.Options));

        sb.Append(",\"events\":[");

        for (var i = 0; i < read.Events.Count; i++)
        {
            var e = read.Events[i];
            if (i > 0)
                sb.Append(',');

            sb.Append("{\"seq\":").Append(e.Seq);
            sb.Append(",\"type\":").Append(JsonSerializer.Serialize(AgentEventNames.Of(e.Kind), AgentDebugJson.Options));
            sb.Append(",\"session\":").Append(JsonSerializer.Serialize(e.SessionId, AgentDebugJson.Options));
            sb.Append(",\"payload\":").Append(e.PayloadJson).Append('}');
        }

        sb.Append("]}");
        return new AgentDebugResponse(200, sb.ToString());
    }

    private AgentDebugResponse Command(string body)
    {
        JsonElement root;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return AgentDebugResponse.Error(400, $"тело не разобралось как JSON: {e.Message}");
        }

        var type = Str(root, "type");

        return type switch
        {
            "message.send" => SendMessage(root),
            "memory.change" => ChangeMemory(root),
            "skill.change" => ChangeSkill(root),

            null or "" => AgentDebugResponse.Error(400, "нужно поле type"),
            _ => AgentDebugResponse.Error(400,
                $"неизвестная команда '{type}' — есть message.send, memory.change, skill.change"),
        };
    }

    private AgentDebugResponse SendMessage(JsonElement root)
    {
        var text = Str(root, "text") ?? "";
        var id = Str(root, "agent");

        // Адресат обязателен, и подставлять умолчание нельзя. Сообщение оператора уходит в ящик
        // конкретного агента и всплывает у него в следующем ходу; отправленное «кому-нибудь», оно
        // с равным успехом попало бы боевому роботу вместо ядра, и заметить это было бы негде.
        if (string.IsNullOrWhiteSpace(id))
            return AgentDebugResponse.Error(400, "нужно поле agent — список даёт GET /state");

        var handle = _agents.Find(id);

        if (handle == null)
            return AgentDebugResponse.Error(409, $"нет агента '{id}'");

        // 409 rather than a queue: text that outlived a round restart would be delivered into a
        // fresh conversation, out of context and with nothing to attribute it to.
        var (ok, reason) = handle.Send(text);

        if (!ok)
            return AgentDebugResponse.Error(409, reason);

        return AgentDebugResponse.Ok(new
        {
            ok = true,
            agent = handle.Id,
            message = reason,
            applied = "next_turn",
            seq = _bus.Seq,
        });
    }

    private AgentDebugResponse ChangeMemory(JsonElement root)
    {
        // Параметра target больше нет: хранилище одно. Заметки о людях правятся своей ручкой.
        var result = _changeMemory(Str(root, "action") ?? "add", Str(root, "match") ?? "",
            Str(root, "content") ?? "");

        if (!result.Ok)
            return AgentDebugResponse.Error(400, result.Message);

        return AgentDebugResponse.Ok(new
        {
            ok = true,
            message = result.Message,
            usage = result.Usage,
            applied = "disk",

            // The single most misleading thing this API could omit. A write lands on disk at once,
            // but the model goes on reading the frozen zone-0 text until the next prefix rebuild —
            // so an operator edits memory, sees the agent behave identically, and concludes the
            // endpoint does not work.
            // Компакция теперь у каждого агента своя, и «следующая» — это четыре разных момента.
            visible_to_model = "next_compaction_of_each_agent",
            scope = "process",
            seq = _bus.Seq,
        });
    }

    private AgentDebugResponse ChangeSkill(JsonElement root)
    {
        var name = Str(root, "name");

        if (string.IsNullOrWhiteSpace(name))
            return AgentDebugResponse.Error(400, "нужно поле name");

        var result = _changeSkill(name, Str(root, "when"), Str(root, "body"),
            Str(root, "match"), Str(root, "replacement"));

        if (!result.Ok)
            return AgentDebugResponse.Error(400, result.Message);

        return AgentDebugResponse.Ok(new
        {
            ok = true,
            message = result.Message,
            applied = "disk",

            // Only `when` reaches zone 0, and only at a rebuild; the body is fetched on demand
            // through skill_view, so an edited body IS live immediately. Worth stating exactly.
            visible_to_model = "body_now_index_next_compaction",
            scope = "process",
            seq = _bus.Seq,
        });
    }

    private static string? Str(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
