using System.Collections.Generic;
using System.Text.Json.Serialization;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// One message, as the debugger sees it.
///
/// A separate type from <see cref="ChatMessageDto"/> on purpose. That one is the wire DTO: its
/// property order is load-bearing for the model server's KV cache, and the first person who wants a
/// timestamp in the debug UI would add it there and cost a full prefill on every turn. This one is
/// immutable, free to grow, and never leaves the debug path.
///
/// Identity is <c>(body_epoch, index)</c> rather than a minted id. The codebase already prefers
/// counters to GUIDs wherever a value crosses a snapshot boundary — see <c>NextCallId</c> — because
/// two runs of the same conversation should diff to nothing.
/// </summary>
public sealed record AgentMessageDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<AgentToolCallDto>? ToolCalls,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId)
{
    public static AgentMessageDto From(int index, ChatMessageDto message)
    {
        AgentToolCallDto[]? calls = null;

        if (message.ToolCalls is { Count: > 0 } source)
        {
            calls = new AgentToolCallDto[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var call = source[i];
                calls[i] = new AgentToolCallDto(
                    call.Id ?? "",
                    call.Function?.Name ?? "",
                    call.Function?.Arguments ?? "");
            }
        }

        return new AgentMessageDto(index, message.Role, message.Content, calls, message.ToolCallId);
    }
}

/// <summary>
/// A tool call. <c>Arguments</c> stays a raw JSON string, exactly as the model emitted it — the
/// tool layer does not re-parse it either, and a debugger showing normalised JSON would hide
/// precisely the malformed argument you opened the debugger to find.
/// </summary>
public sealed record AgentToolCallDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>
/// Everything countable about the agent, sampled whole at a turn boundary.
///
/// Note the three turn counters that are genuinely different and are all worth showing:
/// <paramref name="Turns"/> counts turns the loop ran, <paramref name="ConvTurns"/> counts user
/// messages appended (so nudges raise it), and <paramref name="Compactions"/> counts folds. When
/// they disagree, that disagreement is the diagnosis.
/// </summary>
public sealed record AgentStatsDto(
    [property: JsonPropertyName("turns")] int Turns,
    [property: JsonPropertyName("conv_turns")] int ConvTurns,
    [property: JsonPropertyName("untooled_replies")] int UntooledReplies,
    [property: JsonPropertyName("consecutive_failures")] int ConsecutiveFailures,
    [property: JsonPropertyName("broken_promises")] int BrokenPromises,
    [property: JsonPropertyName("compactions")] int Compactions,
    [property: JsonPropertyName("compaction_armed")] bool CompactionArmed,
    [property: JsonPropertyName("last_prompt_tokens")] int LastPromptTokens,
    [property: JsonPropertyName("chars_per_token")] double CharsPerToken,
    [property: JsonPropertyName("body_chars")] int BodyChars,
    [property: JsonPropertyName("context_limit")] int ContextLimit,
    [property: JsonPropertyName("cache_last_ratio")] double CacheLastRatio,
    [property: JsonPropertyName("cache_mean_ratio")] double CacheMeanRatio,
    [property: JsonPropertyName("cache_alarms")] int CacheAlarms,
    [property: JsonPropertyName("queue_depth")] int QueueDepth,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("volatile_tail")] string? VolatileTail);
