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
    /// <summary>A long action started via <c>use</c>: its id, target, and the snapshot before it began.</summary>
    private readonly record struct PendingAction(ushort Id, EntityUid Target, TargetSnapshot Before);

    private readonly Dictionary<EntityUid, PendingAction> _pending = new();

    /// <summary>How often to ask the world whether the action is done. Half a second — four frames per check.</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The wait ceiling. Not about fairness — about making sure a stuck script still returns
    /// control: crossing the whole station fits in a minute, forcing an airlock takes seconds.
    /// </summary>
    private static readonly TimeSpan WaitCap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The waiting versions of walking and using — the ones a script sees as <c>go</c> and <c>use</c>.
    ///
    /// <para>
    /// They are not on the wire and never will be (<see cref="AiTool.Wire"/> = false): a model that
    /// walks via separate calls has nothing to wait for — a turn hanging for half a minute on
    /// transit would leave the agent deaf for the whole transit. For a script it's the opposite: it
    /// runs on its own thread, and "walk there and continue" is just an ordinary line of code. This
    /// is exactly where script mode pays off: the "walk over, pick up, carry, unpack" cycle stops
    /// splitting into four turns with observation waits in between.
    /// </para>
    /// <para>
    /// The waiting lives here, not in the Lua prelude, because of call accounting. Polling from the
    /// script every quarter second would burn two hundred forty of the four hundred allowed calls
    /// over a minute of walking — the loop-guard would trip on legitimate work.
    /// </para>
    /// </summary>
    private void RegisterWaitingTools(AgentSession s, AiToolRegistry registry)
    {
        var L = s.Locale;

        registry.Register(new AiTool
        {
            Name = "goto_wait",
            Description = L.T(
                "Дойти и дождаться прибытия. В скрипте называется go.",
                "Walk there and wait until you arrive. In a script it is called go."),
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
            Description = L.T(
                "Применить и дождаться конца долгого действия. В скрипте называется use.",
                "Apply and wait until a long action finishes. In a script it is called use."),
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
            Description = L.T(
                "Иду ли я сейчас и чем кончилась прошлая ходьба.",
                "Whether I am walking now and how the last walk ended."),
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

            return ToolResult.Success(new Dictionary<string, object?> { [s.Locale.Outcome] = s.Locale.OutcomeArrived });
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

        // The action was instant — nothing to wait for, the report is already ready.
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

        // The diff is computed AGAIN, after the fact: what use saw in that first instant meant
        // nothing yet — that's the whole point of a long action being long.
        return await _host.OnMainAsync(s, "use_wait", () =>
        {
            var report = new Dictionary<string, object?>();

            if (!Exists(pending.Target) || TerminatingOrDeleted(pending.Target))
            {
                report[s.Locale.Outcome] = s.Locale.OutcomeOk;
                report[s.Locale.Changed] = new List<string>
                {
                    s.Locale.T("цель исчезла — она превратилась во что-то другое",
                        "the target is gone — it turned into something else"),
                };
                return ToolResult.Success(report);
            }

            var after = Snapshot(pending.Target);
            var changes = Diff(pending.Before, after);

            if (status == DoAfterStatus.Cancelled)
            {
                report[s.Locale.Outcome] = s.Locale.OutcomeInterrupted;
                report[s.Locale.Why] = s.Locale.T(
                    "действие сорвалось — скорее всего ты сдвинулся с места или тебе помешали",
                    "the action was interrupted — you probably moved or something got in the way");
                if (changes.Count > 0)
                    report[s.Locale.Changed] = changes;

                return ToolResult.Success(report);
            }

            if (changes.Count > 0)
            {
                report[s.Locale.Outcome] = s.Locale.OutcomeOk;
                report[s.Locale.Changed] = changes;
                return ToolResult.Success(report);
            }

            report[s.Locale.Outcome] = s.Locale.OutcomeFailed;
            report[s.Locale.Why] = s.Locale.T(
                "действие досчиталось до конца, но цель не изменилась",
                "the action ran to the end but the target did not change");
            return ToolResult.Success(report);
        }, ct).ConfigureAwait(false);
    }

    private Task<ToolResult> WalkStatusAsync(AgentSession s, CancellationToken ct)
    {
        var borg = s.Brain;

        return _host.OnMainAsync(s, "walk_status",
            () => ToolResult.Success(new Dictionary<string, object?> { [s.Locale.Status] = WalkStatus(borg) }), ct);
    }

    /// <summary>
    /// Read a single value from the main thread.
    ///
    /// A local variable captured by the closure, not a field: the memory barrier is provided by
    /// the await itself, whereas a field would persist across calls and end up shared between two
    /// scripts at once.
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
