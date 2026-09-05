using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent;

/// <summary>
/// The agent's timers: three tools it uses to schedule its own next turn.
///
/// Before these, the loop had exactly two reasons to wake up — someone spoke, or the idle tick
/// expired — which meant the agent physically could not follow through on "I'll check back in ten
/// minutes": the next turn would arrive on someone else's line, in a different context, and it
/// wouldn't recall its own promise. The crew read this as lying, not as a missing mechanism.
///
/// All three calls dispatch to the main thread even though they don't touch the world. The one
/// reason: the deadline is computed from round time, and the round clock belongs to the main thread —
/// <see cref="RoundTime"/> pulls GameTicker from EntityManager, and EntityManager cannot be touched
/// from the agent's thread (see the long comment in <see cref="AgentSession"/>). This costs a
/// fraction of a millisecond.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>No point making the name longer: it gets printed in the SELF line on every turn.</summary>
    private const int MaxTimerNameLength = 32;

    private const int MaxTimerMessageLength = 200;

    private void RegisterTimerTools(AgentSession s, AiToolRegistry r)
    {
        var L = s.Locale;

        r.Register(new AiTool
        {
            Name = "new_timer",
            Description = L.T(
                "Завести себе будильник: через 'duration' секунд ты получишь событие " +
                "TIMER с текстом 'msg' и сделаешь ход, даже если на станции всё это время " +
                "было тихо. Так выполняют «проверю через десять минут»: сначала скажи " +
                "экипажу, потом поставь таймер. Имя с уже занятым именем — переставляет " +
                "старый, а не заводит второй. Заведённые таймеры видны в строке SELF.",
                "Set yourself an alarm: after 'duration' seconds you get a TIMER event " +
                "with the text 'msg' and take a turn, even if the station was quiet the " +
                "whole time. That is how you keep \"I'll check in ten minutes\": tell the " +
                "crew first, then set the timer. A name that is already taken moves the old " +
                "timer rather than starting a second one. Set timers are visible in the SELF line."),

            // Not a GameAction: this is the agent's own memory of the future, not an action on
            // equipment. It must work from an intellicard too, and during a court-martial — being
            // carded doesn't cancel promises.
            SchemaJson = """
                {"type":"object","required":["name","msg","duration"],"additionalProperties":false,"properties":{
                "name":{"type":"string","maxLength":32,"description":"Короткое имя, по нему таймер удаляют."},
                "msg":{"type":"string","maxLength":200,"description":"Что ты должен вспомнить, когда он сработает. Пиши так, чтобы понять себя без контекста."},
                "duration":{"type":"integer","minimum":1,"description":"Через сколько секунд сработает."},
                "repeat":{"type":"boolean","default":false,"description":"Повторять с тем же интервалом, пока не удалишь через del_timer."}}}
                """,
            Handler = (a, ct) => NewTimerAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "del_timer",
            Description = L.T(
                "Удалить свой таймер по имени: дело сделано или отменилось.",
                "Delete your timer by name: the job is done or cancelled."),
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Имя из строки SELF или из list_timers."}}}
                """,
            Handler = (a, ct) => DelTimerAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "list_timers",
            Description = L.T(
                "Все свои таймеры целиком: имя, текст, через сколько сработает, повторный " +
                "ли. В строке SELF видны только имена и сроки — это чтобы вспомнить текст.",
                "All of your timers in full: name, text, how soon it fires, whether it " +
                "repeats. The SELF line only shows names and deadlines — this is to recall the text."),
            SchemaJson = """
                {"type":"object","additionalProperties":false,"properties":{}}
                """,
            Handler = (a, ct) => ListTimersAsync(s, a, ct),
        });
    }

    // ------------------------------------------------------------------- new_timer

    private Task<ToolResult> NewTimerAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs,
                "new_timer: нужно 'name' — короткое имя, этим же именем таймер потом удаляют"));

        if (!TryGetString(args, "msg", out var message) || string.IsNullOrWhiteSpace(message))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs,
                "new_timer: нужно 'msg' — что именно ты должен вспомнить, когда таймер сработает"));

        var seconds = GetInt(args, "duration", 0);
        if (seconds <= 0)
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs,
                "new_timer: 'duration' — целое число секунд, больше нуля"));

        var repeat = GetBool(args, "repeat");

        // Out-of-range values are clamped, not rejected, and that's a deliberate choice in favor of
        // the turn.
        //
        // A rejection would cost the model a second call out of its turn's allowance — for a mistake
        // that has nowhere to be corrected to anyway except back into the allowed range. And no
        // hidden state results from this: the response carries the actual firing deadline, not the
        // requested one.
        var min = Math.Max(1, _cfg.GetCVar(AiCVars.TimerMinSeconds));
        var max = (int)TimerStore.MaxDelay.TotalSeconds;

        var clamped = Math.Clamp(seconds, min, max);
        var after = TimeSpan.FromSeconds(clamped);

        var trimmedName = Truncate(name!.Trim(), MaxTimerNameLength);
        var trimmedMessage = Truncate(message!.Trim(), MaxTimerMessageLength);

        return OnMainAsync(s, "new_timer", () =>
        {
            var now = RoundTime();
            var result = s.State.Timers.Set(
                trimmedName, trimmedMessage, after, repeat ? after : null, now,
                _cfg.GetCVar(AiCVars.MaxTimers));

            if (!result.Ok)
                return ToolResult.Fail(ToolError.BadArgs, result.Message,
                    retry: "other_target", alternatives: s.State.Timers.Names());

            _sawmill.Info($"[LLM] таймер «{trimmedName}» {result.Message} на " +
                          $"{ObservationFormatter.FormatRoundTime(result.Timer!.DueAt)}" +
                          (repeat ? $", повтор каждые {clamped}с" : ""));

            var effect = new Dictionary<string, object?>
            {
                [s.Locale.Timer] = trimmedName,
                [s.Locale.FiresAt] = ObservationFormatter.FormatRoundTime(result.Timer.DueAt),
                [s.Locale.InSeconds] = clamped,
                [s.Locale.Repeat] = repeat,
                [s.Locale.TimerCount] = s.State.Timers.Count,
            };

            // Named with a word, not derived from the timer count: overwriting an existing timer and
            // setting a new one differ only by this field, and confusing the two means losing
            // someone else's reminder.
            if (result.Replaced)
                effect[s.Locale.Replaced] = true;

            if (clamped != seconds)
                effect[s.Locale.DurationClamped] = s.Locale.T(
                    $"запрошено {seconds}с, допустимо от {min}с до {max}с",
                    $"asked {seconds}s, allowed from {min}s to {max}s");

            return ToolResult.Success(effect);
        }, ct);
    }

    // ------------------------------------------------------------------- del_timer

    private Task<ToolResult> DelTimerAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        if (!TryGetString(args, "name", out var name) || string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail(ToolError.BadArgs, "del_timer: нужно 'name'",
                retry: "other_target", alternatives: s.State.Timers.Names()));

        return OnMainAsync(s, "del_timer", () =>
        {
            if (!s.State.Timers.Remove(name!.Trim(), out var removed))
            {
                return ToolResult.Fail(ToolError.BadArgs, $"нет таймера «{name!.Trim()}»",
                    retry: "other_target", alternatives: s.State.Timers.Nearest(name!.Trim()));
            }

            _sawmill.Info($"[LLM] таймер «{removed!.Name}» снят");

            return ToolResult.Success(new Dictionary<string, object?>
            {
                [s.Locale.Removed] = removed.Name,
                [s.Locale.WasText] = removed.Message,
                [s.Locale.TimerCount] = s.State.Timers.Count,
            });
        }, ct);
    }

    // ----------------------------------------------------------------- list_timers

    private Task<ToolResult> ListTimersAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        _ = args;

        return OnMainAsync(s, "list_timers", () =>
        {
            var now = RoundTime();
            var timers = s.State.Timers.All();

            var rows = timers.Select(t => new Dictionary<string, object?>
            {
                [s.Locale.Name] = t.Name,
                [s.Locale.Text] = t.Message,
                // Can be negative by exactly one tick — between the deadline and the nearest timer
                // sweep — and that's more honest than showing zero.
                [s.Locale.InSeconds] = (int)(t.DueAt - now).TotalSeconds,
                [s.Locale.FiresAt] = ObservationFormatter.FormatRoundTime(t.DueAt),
                [s.Locale.RepeatSeconds] = t.Every.HasValue ? (int)t.Every.Value.TotalSeconds : 0,
            }).ToList();

            return ToolResult.Success(new Dictionary<string, object?>
            {
                [s.Locale.Timers] = rows,
                [s.Locale.Now] = ObservationFormatter.FormatRoundTime(now),
            });
        }, ct);
    }

    // --------------------------------------------------------------------- tick

    /// <summary>
    /// Sweep every session's timers and send the ones that fired into observation.
    ///
    /// This lives here, in the tick, not in the agent loop: the loop sleeps on real time and wakes on
    /// a signal, while deadlines are computed on the round clock — so whoever moves that clock is the
    /// one obligated to check them. As a side effect this gives correct behaviour on pause: the world
    /// is stopped, there's no tick, round time isn't advancing, and timers don't fire on a station
    /// where, by design, nothing can happen.
    ///
    /// A fired timer goes into the same <see cref="ObservationQueue"/> as crew speech — meaning it
    /// wakes the loop through the same <c>Arrived</c> and arrives at the model as the same kind of
    /// observation, on equal footing with the rest of the world. The agent deliberately has no
    /// separate channel for its own reminders: it isn't supposed to be able to tell its own timer
    /// apart from a radio hail except by the text.
    /// </summary>
    private void FireDueTimers()
    {
        if (_sessions.Count == 0)
            return;

        var now = RoundTime();

        foreach (var session in _sessions.Values)
        {
            foreach (var timer in session.State.Timers.TakeDue(now))
            {
                session.Queue.Push(Observation.Timer(timer.Name, timer.Message, now));
                _sawmill.Info($"таймер «{timer.Name}» сработал" +
                              (timer.Every.HasValue ? " (повторный)" : ""));
            }
        }
    }

    // ------------------------------------------------------------------- misc

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static bool GetBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        return args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
    }

    /// <summary>Timer names and deadlines for the SELF line, or empty when there are none.</summary>
    private static string TimersForSelf(AgentSession session)
    {
        var timers = session.State.Timers.All();
        if (timers.Count == 0)
            return string.Empty;

        return string.Join(",", timers.Select(t =>
            string.Create(CultureInfo.InvariantCulture,
                $"{t.Name}@{ObservationFormatter.FormatRoundTime(t.DueAt)}")));
    }
}
