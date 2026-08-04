using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;
using Content.Shared.Chat;
using Content.Shared.Mobs.Components;
using Content.Shared.Radio;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>
/// Phase 1 tool surface: <c>say</c>, <c>radio</c>, <c>look</c>.
///
/// Deliberately three. The loop, the marshalling, the envelope and the cache accounting all have
/// to be proven working before the surface grows to eighteen — otherwise a failure anywhere is
/// indistinguishable from a failure everywhere.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>Radio channels, verbatim from the AiHeld prototype's IntrinsicRadioTransmitter list.</summary>
    private static readonly string[] AiRadioChannels =
    {
        "Binary", "Common", "Command", "Engineering", "Medical",
        "Science", "Security", "Service", "Supply",
    };

    private void RegisterTools(AgentSession session, AiToolRegistry registry)
    {
        registry.Register(new AiTool
        {
            Name = "say",
            Description =
                "Сказать вслух рядом со своим ядром или голограммой. Слышат только те, кто рядом. " +
                "Для связи с экипажем по станции используй radio.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["text"],"additionalProperties":false,"properties":{
                "text":{"type":"string","maxLength":400,"description":"Что сказать."}}}
                """,
            Handler = (args, ct) => SayAsync(session, args, ct),
        });

        registry.Register(new AiTool
        {
            Name = "radio",
            Description =
                "Передать сообщение по радиоканалу станции. Common слышат все, Binary — только силиконы, " +
                "остальные каналы — соответствующие отделы.",
            GameAction = true,
            SchemaJson = """
                {"type":"object","required":["channel","text"],"additionalProperties":false,"properties":{
                "channel":{"type":"string","enum":["Binary","Common","Command","Engineering","Medical","Science","Security","Service","Supply"],"description":"Канал."},
                "text":{"type":"string","maxLength":400,"description":"Что передать."}}}
                """,
            Handler = (args, ct) => RadioAsync(session, args, ct),
        });

        registry.Register(new AiTool
        {
            Name = "look",
            Description =
                "Осмотреться вокруг своего глаза: что и кто рядом. Возвращает список объектов с расстоянием.",
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{
                "radius":{"type":"number","minimum":1,"maximum":16,"default":8,"description":"Радиус обзора в тайлах."}}}
                """,
            Handler = (args, ct) => LookAsync(session, args, ct),
        });
    }

    // ------------------------------------------------------------------------ say

    private async Task<ToolResult> SayAsync(AgentSession session, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "text", out var text) || string.IsNullOrWhiteSpace(text))
            return ToolResult.Fail(ToolError.BadArgs, "say: нужен непустой параметр 'text'");

        var brain = session.Brain;
        var generation = session.Generation;

        return await _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("say");

            if (!IsPlayable(brain))
                return ToolResult.Fail(ToolError.Dead, "ИИ больше не в игре");

            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected("self", new Dictionary<string, object?> { ["dry_run"] = true, ["said"] = text });

            // checkRadioPrefix is false on purpose: a stray ":c" typed by the model must not
            // silently become a station-wide Command broadcast. Radio goes through the radio tool,
            // where the channel is an explicit enum the model has to choose.
            _chat.TrySendInGameICMessage(
                brain,
                text!,
                InGameICChatType.Speak,
                ChatTransmitRange.Normal,
                hideLog: false,
                shell: null,
                player: null,
                nameOverride: null,
                checkRadioPrefix: false,
                ignoreActionBlocker: true);

            _sawmill.Info($"[LLM] say: {text}");
            return ToolResult.Effected("self", new Dictionary<string, object?> { ["said"] = text });
        }, generation, () => GenerationOf(brain), ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------- radio

    private async Task<ToolResult> RadioAsync(AgentSession session, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "text", out var text) || string.IsNullOrWhiteSpace(text))
            return ToolResult.Fail(ToolError.BadArgs, "radio: нужен непустой параметр 'text'");

        if (!TryGetString(args, "channel", out var channel) || string.IsNullOrWhiteSpace(channel))
            return ToolResult.Fail(ToolError.BadArgs, "radio: нужен параметр 'channel'",
                alternatives: AiRadioChannels);

        var match = AiRadioChannels.FirstOrDefault(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            // Suggest the nearest valid values instead of leaving the model to guess again.
            var near = AiRadioChannels
                .OrderBy(c => AiToolRegistry.Distance(c.ToLowerInvariant(), channel!.ToLowerInvariant()))
                .Take(3)
                .ToList();

            return ToolResult.Fail(ToolError.BadArgs, $"radio: нет канала '{channel}'",
                retry: "other_target", alternatives: near);
        }

        var brain = session.Brain;
        var generation = session.Generation;

        return await _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("radio");

            if (!IsPlayable(brain))
                return ToolResult.Fail(ToolError.Dead, "ИИ больше не в игре");

            if (_cfg.GetCVar(AiCVars.DryRun))
                return ToolResult.Effected("self",
                    new Dictionary<string, object?> { ["dry_run"] = true, ["channel"] = match, ["said"] = text });

            _radio.SendRadioMessage(brain, text!, new ProtoId<RadioChannelPrototype>(match), brain);

            _sawmill.Info($"[LLM] radio {match}: {text}");
            return ToolResult.Effected("self", new Dictionary<string, object?> { ["channel"] = match, ["said"] = text });
        }, generation, () => GenerationOf(brain), ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------- look

    /// <summary>
    /// Phase 1 placeholder: a plain radius lookup around the eye.
    ///
    /// Phase 2 replaces the body with the real bridge — <c>StationAiVisionSystem.GetView</c> for
    /// the visible tile set, then <c>EntityLookupSystem.GetLocalEntitiesIntersecting(grid, tiles)</c>
    /// — so that line of sight and camera coverage are respected. Until then this deliberately
    /// over-reports rather than inventing an approximation of vision that would be wrong in a
    /// different way each time.
    /// </summary>
    private async Task<ToolResult> LookAsync(AgentSession session, JsonElement args, CancellationToken ct)
    {
        var radius = 8f;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("radius", out var radiusEl)
            && radiusEl.ValueKind == JsonValueKind.Number)
        {
            radius = Math.Clamp((float)radiusEl.GetDouble(), 1f, 16f);
        }

        var brain = session.Brain;
        var generation = session.Generation;

        return await _dispatcher.RunAsync(() =>
        {
            _dispatcher.AssertMainThread("look");

            if (!IsPlayable(brain))
                return ToolResult.Fail(ToolError.Dead, "ИИ больше не в игре");

            if (!_stationAi.TryGetCore(brain, out var core) || core.Comp == null)
                return ToolResult.Fail(ToolError.Carded, "нет доступа к ядру — камеры недоступны");

            var eye = core.Comp.RemoteEntity ?? core.Owner;
            var origin = _xform.GetMapCoordinates(eye);

            // Two typed queries rather than one broad sweep plus a filter: the broadphase can
            // return hundreds of walls and floor tiles, and this runs inside the server tick.
            var mobs = new HashSet<Entity<MobStateComponent>>();
            _lookup.GetEntitiesInRange(origin, radius, mobs);

            var devices = new HashSet<Entity<StationAiWhitelistComponent>>();
            _lookup.GetEntitiesInRange(origin, radius, devices);

            var rows = new List<string>();

            void Add(EntityUid uid, string kind)
            {
                if (uid == eye || uid == brain || uid == core.Owner)
                    return;

                var pos = _xform.GetMapCoordinates(uid);
                var dist = (pos.Position - origin.Position).Length();
                rows.Add(string.Create(CultureInfo.InvariantCulture, $"{Name(uid)} ({kind}, {dist:F1} тайлов)"));
            }

            foreach (var mob in mobs)
                Add(mob.Owner, "существо");

            foreach (var device in devices)
                Add(device.Owner, "устройство");

            // Sorted so identical world states produce identical bytes.
            rows.Sort(StringComparer.Ordinal);

            var effect = new Dictionary<string, object?>
            {
                ["radius"] = radius,
                ["count"] = rows.Count,
                ["seen"] = rows.Take(40).ToList(),
            };

            return ToolResult.Success(effect);
        }, generation, () => GenerationOf(brain), ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
            return ObservationFormatter.Format(items, dropped, RoundTime(), SelfLine(session), force);
        }, generation, () => GenerationOf(brain), ct).ConfigureAwait(false);
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
        }
        else
        {
            sb.Append(" core=none");
        }

        sb.Append(" turn=").Append(session.Turns.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // -------------------------------------------------------------------- helpers

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
}
