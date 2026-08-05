using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Threading;

namespace Content.Server.AiAgent.Tools;

/// <summary>What the dispatcher refuses before the handler ever runs.</summary>
public enum DispatchGate : byte
{
    /// <summary>Play: the full tool surface.</summary>
    None,

    /// <summary>Review: anything marked <see cref="AiTool.GameAction"/> refuses with review_mode.</summary>
    NoGameActions,
}

/// <summary>One dispatched call: the tool that ran, its parsed arguments, and what came back.</summary>
public sealed record ToolInvocation(string Name, ToolResult Result, AiTool? Tool, JsonElement Args);

/// <summary>
/// Resolve, gate, parse, run — in one place, for every caller.
///
/// There used to be two near-identical copies of this: one in the agent loop and one inside the
/// curator. They had diverged in exactly the way that mattered — the review gate lived only in the
/// loop's copy, so the curator, the single caller the gate was written for, walked straight past it
/// and could act on the station mid-review. Both class comments described a behaviour the code did
/// not have.
///
/// The gate is a <b>parameter</b> rather than something read off a session. That is what lets the
/// curator, which has no session, go through the same door as the loop.
/// </summary>
public sealed class ToolDispatcher
{
    private readonly AiToolRegistry _registry;
    private readonly ISawmill _sawmill;

    public ToolDispatcher(AiToolRegistry registry, ISawmill sawmill)
    {
        _registry = registry;
        _sawmill = sawmill;
    }

    /// <summary>
    /// Run one tool call. Every failure becomes a <see cref="ToolResult"/>;
    /// <see cref="OperationCanceledException"/> is the only exception that escapes.
    /// </summary>
    public async Task<ToolInvocation> InvokeAsync(ToolCallDto call, DispatchGate gate, CancellationToken ct)
    {
        var name = call.Function.Name;

        if (!_registry.TryGet(name, out var tool))
        {
            return new ToolInvocation(name, ToolResult.Fail(
                ToolError.UnknownTool,
                $"нет инструмента '{name}'",
                retry: "other_target",
                alternatives: _registry.Nearest(name)), null, default);
        }

        if (gate == DispatchGate.NoGameActions && tool.GameAction)
        {
            return new ToolInvocation(name, ToolResult.Fail(ToolError.ReviewMode,
                "сейчас идёт разбор прошедшего отрезка — действовать на станции нельзя",
                retry: "later"), tool, default);
        }

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments)
                ? "{}"
                : call.Function.Arguments);
            args = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return new ToolInvocation(name, ToolResult.Fail(ToolError.BadArgs,
                $"{name}: аргументы не разобрались как JSON ({e.Message})", retry: "other_target"),
                tool, default);
        }

        try
        {
            return new ToolInvocation(name, await tool.Handler(args, ct).ConfigureAwait(false), tool, args);
        }
        catch (StaleGenerationException)
        {
            return new ToolInvocation(name,
                ToolResult.Fail(ToolError.Dead, "ты больше не в игре", retry: "none"), tool, args);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            // retry:"none", not "later". The timed-out delegate is still queued for the main thread
            // and will run when the tick gets to it, so the action may well have happened; telling
            // the model to repeat it is how a door ends up bolted twice.
            return new ToolInvocation(name, ToolResult.Fail(ToolError.Timeout,
                $"{name} не успел ответить. Действие могло всё-таки пройти — проверь состояние, " +
                "прежде чем повторять", retry: "none"), tool, args);
        }
        catch (Exception e)
        {
            _sawmill.Error($"tool {name} threw: {e}");
            return new ToolInvocation(name, ToolResult.FromException(name, e), tool, args);
        }
    }
}
