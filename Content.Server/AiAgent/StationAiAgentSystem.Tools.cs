using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent;

/// <summary>
/// Tool registration and the shared plumbing every tool uses.
///
/// Fourteen game-facing tools. Held down deliberately: the mcbot deployment on this box measured
/// that 46 narrow commands drowned this exact model on this exact quant while ~13 worked. Breadth
/// comes from consolidation, not from more entries — <c>inspect</c> absorbs what would otherwise
/// be twenty readers, <c>device_action</c> fifteen verbs, and <c>device_ui</c> the long tail of
/// forty-odd whitelisted consoles. Adding a console is one dictionary entry, so the schema (and
/// therefore the frozen prefix) never grows.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>Radio channels, verbatim from the AiHeld prototype's IntrinsicRadioTransmitter list.</summary>
    private static readonly string[] AiRadioChannels =
    {
        "Binary", "Common", "Command", "Engineering", "Medical",
        "Science", "Security", "Service", "Supply",
    };

    private void RegisterTools(AgentSession s, AiToolRegistry r)
    {
        // ---------------------------------------------------------------- perception

        r.Register(new AiTool
        {
            Name = "look",
            Description = "Осмотреть станцию через камеры вокруг своего глаза. Возвращает список " +
                          "объектов с хендлами — этими хендлами потом адресуются остальные инструменты. " +
                          "С параметром near список считается ОТ человека: ближайшие первыми, у каждого " +
                          "сторона света и расстояние. Так отвечают на «открой дверь рядом со мной».",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "expand":{"type":"integer","minimum":0,"maximum":3,"default":0,"description":"Расширить обзор сверх стандартного."},
                "near":{"type":"string","description":"Имя человека или хендл. Список пересчитается от него: направления и расстояния будут относительно него, ближайшее первым."},
                "via_skill":{"type":"string","description":"Имя скилла, если действуешь по нему."}}}
                """,
            Handler = (a, ct) => LookAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "inspect",
            Description = "Подробное состояние одного объекта по хендлу: дверь (открыта, болты, " +
                          "электризация), APC (рубильник, заряд), воздушная тревога, турель, " +
                          "перерезан ли твой провод к устройству, какой доступ требует замок. " +
                          "С параметром by отвечает, пустит ли эта дверь конкретного человека.",
            SchemaJson = """
                {"type":"object","required":["handle"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл из look, например door-3."},
                "by":{"type":"string","description":"Имя или хендл человека. Ответит, открывает ли его карта этот замок. Человек должен быть виден камерами."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => InspectAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "crew_status",
            Description = "Монитор экипажа: имя, должность, отдел, жив ли, урон и координаты — " +
                          "по тем, у кого включён датчик костюма.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "filter":{"type":"string","description":"Подстрока имени, должности или отдела."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => CrewStatusAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "identify",
            Description = "Кто перед тобой: предъявленное имя, должность с ID-карты и значок " +
                          "должности над головой. Это ровно то, что видит живой ИИ — и это можно подделать.",
            SchemaJson = """
                {"type":"object","required":["handle"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл существа из look, например crew-2."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => IdentifyAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "records",
            Description = "Учётные записи станции: имя, возраст, должность, вид, отпечатки, ДНК. " +
                          "Это официальная база — она может расходиться с тем, что человек предъявляет.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "query":{"type":"string","description":"Подстрока имени или должности."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => RecordsAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "laws",
            Description = "Перечитать свои законы. Делай это, если сомневаешься или если пришло уведомление о смене законов.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{"via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => LawsAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "station_status",
            Description = "Сводка по станции: уровень тревоги, состояние ядра, питание, целостность.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{"via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => StationStatusAsync(s, a, ct),
        });

        // -------------------------------------------------------------------- speech

        r.Register(new AiTool
        {
            Name = "say",
            Description = "Сказать вслух рядом со своим ядром. Слышат только те, кто рядом с ядром. " +
                          "Чтобы обратиться к экипажу по станции, используй radio.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["text"],"additionalProperties":false,"properties":{
                "text":{"type":"string","maxLength":400},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => SayAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "radio",
            Description = "Передать по радиоканалу станции. Common слышат все, Binary — только силиконы.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["channel","text"],"additionalProperties":false,"properties":{
                "channel":{"type":"string","enum":["Binary","Common","Command","Engineering","Medical","Science","Security","Service","Supply"]},
                "text":{"type":"string","maxLength":400},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => RadioAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "announce",
            Description = "Общестанционное объявление и/или смена уровня тревоги. Это громко и " +
                          "видно всем — не для мелочей. Вызвать шаттл ты не можешь.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "text":{"type":"string","maxLength":800,"description":"Текст объявления."},
                "alert_level":{"type":"string","enum":["green","blue","violet","yellow","orange","red"],"description":"Новый уровень тревоги."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => AnnounceAsync(s, a, ct),
        });

        // ------------------------------------------------------------------ movement

        r.Register(new AiTool
        {
            Name = "move_camera",
            Description = "Переместить свой глаз — к объекту по хендлу либо в точку по координатам " +
                          "(например к координатам человека из crew_status), чтобы увидеть, что там, " +
                          "и управлять этим. В точку без покрытия камерами глаз не пойдёт.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"К чему переместиться."},
                "x":{"type":"number","description":"Координата X. Задаётся вместе с y вместо handle."},
                "y":{"type":"number","description":"Координата Y. Задаётся вместе с x вместо handle."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => MoveCameraAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "jump_to_core",
            Description = "Вернуть глаз к своему ядру.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{"via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => JumpToCoreAsync(s, a, ct),
        });

        // ------------------------------------------------------------------- devices

        r.Register(new AiTool
        {
            Name = "device_action",
            Description = "Управление оборудованием станции: двери, болты, электризация, аварийный " +
                          "доступ, рубильник APC, режим воздушной тревоги, лампа камеры. " +
                          "Ответ содержит effect — реально считанное состояние после действия.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["handle","action"],"additionalProperties":false,"properties":{
                "handle":{"type":"string","description":"Хендл устройства из look."},
                "action":{"type":"string","enum":["open","close","bolt","unbolt","electrify","unelectrify","emergency_access_on","emergency_access_off","light_on","light_off","apc_breaker_on","apc_breaker_off","air_alarm_mode"],"description":"Что сделать."},
                "value":{"type":"string","description":"Только для air_alarm_mode: filtering, panic, replace, none."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => DeviceActionAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "device_ui",
            Description = "Команда консоли, для которой нет отдельного действия. Список доступных " +
                          "команд для конкретного устройства показывает inspect.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["handle","command"],"additionalProperties":false,"properties":{
                "handle":{"type":"string"},
                "command":{"type":"string","description":"Имя команды, см. inspect."},
                "text":{"type":"string","description":"Текстовый аргумент, если команда его требует."},
                "via_skill":{"type":"string"}}}
                """,
            Handler = (a, ct) => DeviceUiAsync(s, a, ct),
        });

        // ------------------------------------------------- skills and memory
        RegisterMemoryTools(s, r);
    }

    // ---------------------------------------------------------------- observation

    /// <summary>Drain perception on the main thread and format the one user message for this turn.</summary>
    private async Task<string?> BuildObservationAsync(EntityUid brain, bool force, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(brain, out var session))
            return null;

        var generation = session.Generation;

        return await _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("observation");

            var (items, dropped) = session.Queue.Drain();

            // Remember how the AI was addressed while the raw observations still exist — the
            // formatted string is for the model, not for us to parse back out.
            session.HeardOnChannel = items.LastOrDefault(i => i.Kind == ObsKind.Radio)?.Channel;
            session.HeardSpeech = items.Any(i => i.Kind == ObsKind.Speech);

            return ObservationFormatter.Format(items, dropped, RoundTime(), SelfLine(session), force);
        }, generation, () => GenerationOf(brain), ct, what: "observation").ConfigureAwait(false);
    }

    /// <summary>
    /// The SELF line: same fields, same order, every turn. Working out what changed is the model's
    /// job — omitting an unchanged field would just make it guess.
    /// </summary>
    private string SelfLine(AgentSession session)
    {
        var brain = session.Brain;

        if (!IsPlayable(brain))
            return "state=dead";

        var sb = new StringBuilder();
        sb.Append("mode=").Append(session.Mode.ToString().ToLowerInvariant());

        if (_stationAi.TryGetCore(brain, out var core) && core.Comp != null)
        {
            var eye = core.Comp.RemoteEntity ?? core.Owner;
            var pos = _xform.GetMapCoordinates(eye);
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" eye=({pos.X:F0},{pos.Y:F0})"));
            sb.Append(" core=").Append(core.Comp.Remote ? "remote" : "projected");
            sb.Append(" power=").Append(_power.IsPowered(core.Owner) ? "ok" : "lost");
        }
        else
        {
            sb.Append(" core=none");
        }

        sb.Append(" turn=").Append(session.Turns.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // -------------------------------------------------------------- tool plumbing

    /// <summary>Run a tool body on the main thread with the session's generation guard.</summary>
    private Task<ToolResult> OnMainAsync(AgentSession s, string what, Func<ToolResult> body,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var brain = s.Brain;
        var generation = s.Generation;

        return _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread(what);
            return !IsPlayable(brain)
                ? ToolResult.Fail(ToolError.Dead, "ИИ больше не в игре")
                : body();
        }, generation, () => GenerationOf(brain), ct, timeout, what);
    }

    /// <summary>
    /// Resolve a handle, or produce a <c>stale_handle</c> failure that names the nearest live
    /// handles of the same kind. Guessing wrong is normal; leaving the model to guess blindly
    /// again is what burns turns.
    /// </summary>
    private bool TryResolve(AgentSession s, JsonElement args, out EntityUid uid, out ToolResult? failure)
    {
        uid = default;
        failure = null;

        if (!TryGetString(args, "handle", out var handle) || string.IsNullOrWhiteSpace(handle))
        {
            failure = ToolResult.Fail(ToolError.BadArgs, "нужен параметр 'handle' — возьми его из look");
            return false;
        }

        if (!s.Handles.TryResolve(handle!, out uid))
        {
            failure = ToolResult.Fail(ToolError.StaleHandle, $"хендл '{handle}' неизвестен",
                retry: "other_target", alternatives: s.Handles.Nearest(handle!));
            return false;
        }

        if (!Exists(uid) || TerminatingOrDeleted(uid))
        {
            failure = ToolResult.Fail(ToolError.StaleHandle, $"объект '{handle}' больше не существует",
                retry: "other_target", alternatives: s.Handles.Nearest(handle!));
            return false;
        }

        return true;
    }

    private static bool TryGetString(JsonElement args, string name, out string? value)
    {
        value = null;
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;

        value = el.GetString();
        return value != null;
    }

    private static bool TryGetFloat(JsonElement args, string name, out float value)
    {
        value = 0f;
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;

        if (!el.TryGetSingle(out value))
            return false;

        return float.IsFinite(value);
    }

    private static int GetInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return fallback;
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return fallback;

        return el.TryGetInt32(out var v) ? v : fallback;
    }
}
