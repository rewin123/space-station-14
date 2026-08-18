using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;
using Content.Shared.DoAfter;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.Server.AiAgent.Borg;

public sealed partial class AiBorgSystem
{
    /// <summary>Долгое действие, начатое через <c>use</c>: его номер, цель и снимок до начала.</summary>
    private readonly record struct PendingAction(ushort Id, EntityUid Target, TargetSnapshot Before);

    private readonly Dictionary<EntityUid, PendingAction> _pending = new();

    /// <summary>Как часто спрашивать мир, кончилось ли дело. Полсекунды — четыре кадра на проверку.</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Потолок ожидания. Не про паритет, а про то, чтобы застрявший скрипт всё-таки вернул
    /// управление: переход через всю станцию укладывается в минуту, отжатие шлюза — в секунды.
    /// </summary>
    private static readonly TimeSpan WaitCap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Ждущие версии ходьбы и применения — те, что скрипт видит как <c>go</c> и <c>use</c>.
    ///
    /// <para>
    /// На проводе их нет и не будет (<see cref="AiTool.Wire"/> = false): модели, которая ходит
    /// отдельными вызовами, ждать нечем — ход, висящий полминуты на переходе, это агент, глухой
    /// весь переход. Скрипту наоборот: он исполняется на своём потоке, и «дойти и продолжить»
    /// для него — обычная строка кода. Ровно здесь режим скрипта и окупается: цикл «дойти, взять,
    /// донести, распаковать» перестаёт распадаться на четыре хода с ожиданием наблюдений между ними.
    /// </para>
    /// <para>
    /// Ожидание живёт здесь, а не в прелюдии на Lua, из-за счёта вызовов. Опрос из скрипта раз в
    /// четверть секунды съел бы за минуту ходьбы двести сорок вызовов из четырёхсот разрешённых —
    /// предохранитель от зацикливания сработал бы на честной работе.
    /// </para>
    /// </summary>
    private void RegisterWaitingTools(AgentSession s, AiToolRegistry registry)
    {
        registry.Register(new AiTool
        {
            Name = "goto_wait",
            Description = "Дойти и дождаться прибытия. В скрипте называется go.",
            SchemaJson =
                """
                {"type":"object","required":["to"],"additionalProperties":false,"properties":{
                "to":{"type":"string"}}}
                """,
            Wire = false,
            GameAction = true,
            Handler = (args, ct) => GotoWaitAsync(s, args, ct),
        });

        registry.Register(new AiTool
        {
            Name = "use_wait",
            Description = "Применить и дождаться конца долгого действия. В скрипте называется use.",
            SchemaJson =
                """
                {"type":"object","required":["target"],"additionalProperties":false,"properties":{
                "target":{"type":"string"},"tool":{"type":"string"},"with_item":{"type":"boolean"}}}
                """,
            Wire = false,
            GameAction = true,
            Handler = (args, ct) => UseWaitAsync(s, args, ct),
        });

        registry.Register(new AiTool
        {
            Name = "walk_status",
            Description = "Иду ли я сейчас и чем кончилась прошлая ходьба.",
            SchemaJson = """{"type":"object","additionalProperties":false,"properties":{}}""",
            Wire = false,
            Handler = (_, ct) => WalkStatusAsync(s, ct),
        });
    }

    private async Task<ToolResult> GotoWaitAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var start = await GotoAsync(s, args, ct).ConfigureAwait(false);
        if (!start.Ok)
            return start;

        var borg = s.Brain;
        var deadline = DateTime.UtcNow + WaitCap;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await ReadAsync(s, "walk_status", () => WalkStatus(borg), ct).ConfigureAwait(false);

            if (status.StartsWith("идёт", StringComparison.Ordinal))
            {
                await Task.Delay(PollEvery, ct).ConfigureAwait(false);
                continue;
            }

            if (status.StartsWith("нет пути", StringComparison.Ordinal))
            {
                return ToolResult.Fail(ToolError.NotVisible, status,
                    retry: "other_target");
            }

            return ToolResult.Success(new Dictionary<string, object?> { ["итог"] = "дошёл" });
        }

        return ToolResult.Fail(ToolError.Timeout,
            $"иду дольше {WaitCap.TotalMinutes:0} мин и всё ещё не дошёл — посмотри, где застрял",
            retry: "later");
    }

    private async Task<ToolResult> UseWaitAsync(AgentSession s, JsonElement args, CancellationToken ct)
    {
        var borg = s.Brain;
        _pending.Remove(borg);

        var started = await UseAsync(s, args, ct).ConfigureAwait(false);
        if (!started.Ok)
            return started;

        // Действие мгновенное — ждать нечего, отчёт уже готов.
        if (!_pending.TryGetValue(borg, out var pending))
            return started;

        var deadline = DateTime.UtcNow + WaitCap;
        var status = DoAfterStatus.Running;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            status = await ReadAsync(s, "doafter_status",
                () => _doAfter.GetStatus(borg, pending.Id), ct).ConfigureAwait(false);

            if (status != DoAfterStatus.Running)
                break;

            await Task.Delay(PollEvery, ct).ConfigureAwait(false);
        }

        _pending.Remove(borg);

        // Разница считается ЗАНОВО и по факту: то, что use увидел в первое мгновение, ещё ничего
        // не значило — долгое действие на то и долгое.
        return await _host.OnMainAsync(s, "use_wait", () =>
        {
            var report = new Dictionary<string, object?>();

            if (!Exists(pending.Target) || TerminatingOrDeleted(pending.Target))
            {
                report["итог"] = "получилось";
                report["изменилось"] = new List<string> { "цель исчезла — она превратилась во что-то другое" };
                return ToolResult.Success(report);
            }

            var after = Snapshot(pending.Target);
            var changes = Diff(pending.Before, after);

            if (status == DoAfterStatus.Cancelled)
            {
                report["итог"] = "ПРЕРВАНО";
                report["почему"] = "действие сорвалось — скорее всего ты сдвинулся с места или тебе помешали";
                if (changes.Count > 0)
                    report["изменилось"] = changes;

                return ToolResult.Success(report);
            }

            if (changes.Count > 0)
            {
                report["итог"] = "получилось";
                report["изменилось"] = changes;
                return ToolResult.Success(report);
            }

            report["итог"] = "НЕ ПОЛУЧИЛОСЬ";
            report["почему"] = "действие досчиталось до конца, но цель не изменилась";
            return ToolResult.Success(report);
        }, ct).ConfigureAwait(false);
    }

    private Task<ToolResult> WalkStatusAsync(AgentSession s, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "walk_status",
            () => ToolResult.Success(new Dictionary<string, object?> { ["статус"] = WalkStatus(borg) }), ct);
    }

    /// <summary>
    /// Прочитать одно значение с главного потока.
    ///
    /// Локальная переменная под замыканием, а не поле: барьер памяти даёт сам await, а поле жило
    /// бы между вызовами и стало бы общим для двух скриптов сразу.
    /// </summary>
    private async Task<T> ReadAsync<T>(AgentSession s, string what, Func<T> read, CancellationToken ct)
    {
        var value = default(T)!;

        await _host.OnMainAsync(s, what, () =>
        {
            value = read();
            return ToolResult.Success();
        }, ct).ConfigureAwait(false);

        return value;
    }
}
