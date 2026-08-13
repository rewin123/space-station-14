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
/// Будильники агента: три инструмента, которыми он сам назначает себе следующий ход.
///
/// До них у петли было ровно два повода проснуться — кто-то заговорил либо истёк тик простоя, — и
/// поэтому «посмотрю через десять минут» агент физически не мог выполнить: следующий ход приходил
/// по чужой реплике, в другом контексте, и о своём обещании он не вспоминал. Экипаж читал это как
/// враньё, а не как отсутствие механизма.
///
/// Все три хода марширують на главный поток, хотя мира не касаются. Причина одна: срок считается от
/// раундового времени, а раундовые часы принадлежат главному потоку — <see cref="RoundTime"/>
/// достаёт GameTicker из EntityManager, а тянуть EntityManager с потока агента здесь нельзя
/// (см. большой комментарий в <see cref="AgentSession"/>). Стоит это доли миллисекунды.
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>Длиннее имя незачем: оно печатается в строке SELF на каждом ходу.</summary>
    private const int MaxTimerNameLength = 32;

    private const int MaxTimerMessageLength = 200;

    private void RegisterTimerTools(AgentSession s, AiToolRegistry r)
    {
        r.Register(new AiTool
        {
            Name = "new_timer",
            Description = "Завести себе будильник: через 'duration' секунд ты получишь событие " +
                          "TIMER с текстом 'msg' и сделаешь ход, даже если на станции всё это время " +
                          "было тихо. Так выполняют «проверю через десять минут»: сначала скажи " +
                          "экипажу, потом поставь таймер. Имя с уже занятым именем — переставляет " +
                          "старый, а не заводит второй. Заведённые таймеры видны в строке SELF.",

            // Не GameAction: это собственная память о будущем, а не действие с оборудованием.
            // Должно работать и из интелликарты, и во время разбора — карденье не отменяет обещаний.
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
            Description = "Удалить свой таймер по имени: дело сделано или отменилось.",
            SchemaJson = """
                {"type":"object","required":["name"],"additionalProperties":false,"properties":{
                "name":{"type":"string","description":"Имя из строки SELF или из list_timers."}}}
                """,
            Handler = (a, ct) => DelTimerAsync(s, a, ct),
        });

        r.Register(new AiTool
        {
            Name = "list_timers",
            Description = "Все свои таймеры целиком: имя, текст, через сколько сработает, повторный " +
                          "ли. В строке SELF видны только имена и сроки — это чтобы вспомнить текст.",
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

        // Границы не отказывают, а поджимают, и это осознанный выбор в пользу хода.
        //
        // Отказ стоил бы модели второго вызова из отпущенных на ход — за ошибку, которую всё равно
        // некуда исправлять, кроме как в разрешённый диапазон. Скрытого состояния при этом не
        // возникает: ответ несёт настоящий срок срабатывания, а не запрошенный.
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
                ["таймер"] = trimmedName,
                ["сработает"] = ObservationFormatter.FormatRoundTime(result.Timer.DueAt),
                ["через_секунд"] = clamped,
                ["повтор"] = repeat,
                ["всего_таймеров"] = s.State.Timers.Count,
            };

            // Названо словом, а не выведено из числа таймеров: перезапись существующего и заведение
            // нового отличаются только этим полем, а спутать их — значит потерять чужое напоминание.
            if (result.Replaced)
                effect["замена"] = true;

            if (clamped != seconds)
                effect["срок_поправлен"] = $"запрошено {seconds}с, допустимо от {min}с до {max}с";

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
                ["снят"] = removed.Name,
                ["текст_был"] = removed.Message,
                ["всего_таймеров"] = s.State.Timers.Count,
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
                ["имя"] = t.Name,
                ["текст"] = t.Message,
                // Может быть отрицательным ровно на один тик — между сроком и ближайшим обходом
                // таймеров, — и это честнее, чем показать ноль.
                ["через_секунд"] = (int)(t.DueAt - now).TotalSeconds,
                ["сработает"] = ObservationFormatter.FormatRoundTime(t.DueAt),
                ["повтор_секунд"] = t.Every.HasValue ? (int)t.Every.Value.TotalSeconds : 0,
            }).ToList();

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["таймеры"] = rows,
                ["сейчас"] = ObservationFormatter.FormatRoundTime(now),
            });
        }, ct);
    }

    // --------------------------------------------------------------------- тик

    /// <summary>
    /// Обойти таймеры всех сессий и отправить сработавшие в наблюдение.
    ///
    /// Здесь, в тике, а не в петле агента: петля спит на реальном времени и просыпается по сигналу,
    /// а сроки считаются по раундовым часам — то есть проверять их обязан тот, кто эти часы двигает.
    /// Побочно это даёт правильное поведение на паузе: мир стоит, тика нет, раундовое время не
    /// растёт, и таймеры не срабатывают на станции, где по устройству ничего не может произойти.
    ///
    /// Сработавшее уходит в ту же <see cref="ObservationQueue"/>, что и речь экипажа, — значит
    /// будит петлю тем же <c>Arrived</c> и приезжает к модели тем же наблюдением, наравне с
    /// остальным миром. Отдельного канала для собственных напоминаний у агента нет намеренно:
    /// он и не должен уметь отличать свой будильник от оклика по рации иначе, чем по строке.
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

    // ------------------------------------------------------------------- мелочи

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static bool GetBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        return args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
    }

    /// <summary>Имена и сроки таймеров для строки SELF, или пусто, когда ни одного нет.</summary>
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
