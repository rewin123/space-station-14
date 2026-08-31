using System.Collections.Generic;
using System.Text.Json.Serialization;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// Everything the debugger needs, in one answer.
///
/// <paramref name="Seq"/> is what makes this composable with the event stream: the snapshot is the
/// state as of that sequence number, and applying every event after it converges on the present.
///
/// Converges, not "reproduces exactly" — the capture is deliberately not atomic, and
/// <see cref="AgentDebugState.Capture"/> explains at length why reading the sequence number first
/// makes the resulting failure a harmless replay rather than a silent loss. A client must therefore
/// treat every event as idempotent and must check <c>index</c> on an appended message.
///
/// <paramref name="Session"/> is null when no agent holds a core: between rounds, or on a station
/// where nobody claimed one. That is a normal answer, not an error.
/// </summary>
public sealed record AgentStateSnapshot(
    [property: JsonPropertyName("instance")] string Instance,
    [property: JsonPropertyName("seq")] long Seq,

    /// <summary>Раунд: раньше его можно было узнать только из сессии, которой может не быть.</summary>
    [property: JsonPropertyName("round")] int Round,

    /// <summary>
    /// Кто сейчас жив. Дешёвые строки: ни системного промпта, ни истории.
    ///
    /// Ростер едет вместе с процессным снимком, чтобы первый запрос клиента был ОДИН. Тяжёлое —
    /// промпт и вся переписка — качается отдельно и поштучно, см. <see cref="AgentSessionSnapshot"/>:
    /// четыре агента с историей под сотню тысяч токенов в одном ответе весили бы мегабайты, из
    /// которых смотрят на один.
    /// </summary>
    [property: JsonPropertyName("agents")] IReadOnlyList<AgentRosterEntryDto> Agents,

    [property: JsonPropertyName("memory")] AgentMemoryDto Memory,
    [property: JsonPropertyName("skills")] IReadOnlyList<AgentSkillDto> Skills,
    [property: JsonPropertyName("notes")] IReadOnlyList<AgentPlayerNoteDto> Notes,

    /// <summary>
    /// Потолок ОДНОЙ заметки в символах. Здесь, а не в каждой заметке: он общий на хранилище, и
    /// повторять его на каждого человека значило бы врать о том, что он бывает разным.
    /// </summary>
    [property: JsonPropertyName("note_limit")] int NoteLimit);

/// <summary>
/// Live memory entries and the frozen zone-0 text, side by side and labelled.
///
/// They diverge by design — a write lands on disk immediately but only reaches the system prompt at
/// the next prefix rebuild — and that divergence is the single most confusing property of this
/// system. An operator who edits memory, watches the agent behave identically, and has no way to
/// see why concludes the endpoint is broken. Showing both is most of the debugging value here.
/// </summary>
/// <remarks>
/// The limits are here because they are otherwise only recoverable by regexing the capacity header
/// out of the frozen block — <c>MemoryStore.RenderBlock</c> prints <c>[N% — used/limit]</c> into it
/// — and a debug client should not have to parse prose to draw a gauge.
/// </remarks>
public sealed record AgentMemoryDto(
    [property: JsonPropertyName("memory_live")] IReadOnlyList<string> MemoryLive,
    [property: JsonPropertyName("memory_frozen")] string MemoryFrozen,
    [property: JsonPropertyName("memory_limit")] int MemoryLimit);

/// <summary>
/// Снимок ОДНОГО агента.
/// </summary>
/// <remarks>
/// <paramref name="Agent"/> равен null со статусом <b>200</b>, а не 404, и это не небрежность:
/// агент мог уйти между кадром <c>session.started</c> и этим запросом — штатная гонка, а не
/// ошибка. Клиент считает 404 терминальным и после него навсегда останавливает опрос, то есть
/// «правильный» код здесь погасил бы весь отладчик из-за нормального события.
/// </remarks>
public sealed record AgentSessionSnapshot(
    [property: JsonPropertyName("instance")] string Instance,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("agent")] AgentSessionDto? Agent);

/// <summary>
/// Строка ростера: столько, сколько нужно, чтобы нарисовать вкладку с индикатором здоровья.
/// </summary>
/// <remarks>
/// Чего здесь намеренно НЕТ: системного промпта, описания инструментов, сообщений и памяти — то
/// есть всего, из-за чего снимок весит мегабайты. Ростер запрашивается на каждом длинном опросе,
/// и любое тяжёлое поле здесь стоило бы этого веса ежеминутно.
///
/// <paramref name="StartedSeq"/> отличает «тот же агент» от «тот же идентификатор, новая сессия
/// после переклейма». Без него клиент угадывал бы это по номеру раунда, который посреди раунда
/// не меняется.
/// </remarks>
public sealed record AgentRosterEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("brain")] int Brain,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("started_seq")] long StartedSeq,
    [property: JsonPropertyName("alive")] bool Alive,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("turns")] int Turns,
    [property: JsonPropertyName("messages")] int Messages,
    [property: JsonPropertyName("body_epoch")] int BodyEpoch,
    [property: JsonPropertyName("last_prompt_tokens")] int LastPromptTokens,
    [property: JsonPropertyName("context_limit")] int ContextLimit,
    [property: JsonPropertyName("queue_depth")] int QueueDepth,
    [property: JsonPropertyName("pending_input")] bool PendingInput,
    [property: JsonPropertyName("last_error")] string? LastError);

/// <summary>
/// Один узел файловой системы агента: путь, вид, описание, размер, права.
///
/// <para>
/// Тела файла здесь нет намеренно — дерево запрашивают, чтобы посмотреть на состав, а тела статей
/// справочника весят полтора мегабайта на всех. Тело открывается отдельным запросом, как и раньше
/// открывалось тело скилла.
/// </para>
/// <para>
/// Права отдаются строкой <c>r--</c>/<c>rw-</c>, а не булевым флагом: в интерфейсе это подпись, а
/// не логика, и одинаковая с тем, что видит сам агент в зоне 0.
/// </para>
/// </summary>
public sealed record AgentFileDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("dir")] bool IsDir,
    [property: JsonPropertyName("desc")] string Desc,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("access")] string Access);

public sealed record AgentSkillDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("when")] string When,
    [property: JsonPropertyName("body")] string Body);

/// <summary>
/// Заметка об одном человеке: ключ, отображаемое имя и записи целиком.
///
/// Заморожённого двойника, в отличие от памяти, здесь нет и быть не может: заметки в системный
/// промпт не вклеиваются вовсе. Агент узнаёт о них только строкой NOTE, когда знакомый впервые за
/// смену заговорил, и читает инструментом. Расхождения «живое против замороженного», которое у
/// памяти составляет половину отладочной ценности, тут просто не существует.
///
/// <paramref name="Slug"/> отдаётся вместе с именем, потому что именно он — ключ: два написания
/// одного имени дают один файл, и без слага в отладчике это выглядит как пропавшая запись.
/// </summary>
public sealed record AgentPlayerNoteDto(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("entries")] IReadOnlyList<string> Entries);

public sealed record AgentSessionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("brain")] int Brain,
    // Otherwise learnable only from the session.started payload — i.e. only by a client that
    // happened not to miss one event.
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("prefix_hash")] string PrefixHash,
    [property: JsonPropertyName("system_prompt")] string SystemPrompt,
    [property: JsonPropertyName("tools_json")] string ToolsJson,
    [property: JsonPropertyName("body_epoch")] int BodyEpoch,
    [property: JsonPropertyName("messages")] IReadOnlyList<AgentMessageDto> Messages,
    /// <summary>
    /// Дерево файлов ЭТОГО агента, на два уровня от корня.
    ///
    /// <para>
    /// Появилось, когда библиотеки перестали быть общими: процессный снимок с одной памятью и
    /// одним списком записей стал неправдой — у ядра и у каждого киборга они свои. Глубже двух
    /// уровней не ходим: полное дерево справочника это те самые 226 строк, ради избавления от
    /// которых всё и затевалось.
    /// </para>
    /// </summary>
    [property: JsonPropertyName("files")] IReadOnlyList<AgentFileDto> Files,
    [property: JsonPropertyName("stats")] AgentStatsDto Stats,
    [property: JsonPropertyName("last_turn")] AgentTurnDto? LastTurn);

/// <summary>
/// The shape of the last turn.
///
/// This already existed in full — <c>TurnContext</c> names every phase, exit and delivery form the
/// loop has — and was surfaced absolutely nowhere. It is the most informative thing the agent
/// produces: "the model stopped, owing an answer, and delivery was declined" is a diagnosis, where
/// "turn 41" is not.
/// </summary>
public sealed record AgentTurnDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("tool_calls")] int ToolCalls,
    [property: JsonPropertyName("spoke")] bool Spoke,
    [property: JsonPropertyName("nudged")] bool Nudged,
    [property: JsonPropertyName("promised")] string? Promised,
    [property: JsonPropertyName("exit")] string Exit,
    [property: JsonPropertyName("delivery")] string Delivery,
    [property: JsonPropertyName("cache_ratio")] double CacheRatio,
    [property: JsonPropertyName("radio_channel")] string? RadioChannel,
    [property: JsonPropertyName("addressed")] bool Addressed,
    [property: JsonPropertyName("forced")] bool Forced,
    [property: JsonPropertyName("perception")] string Perception);

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

    /// <summary>Ходы, закрытые явным noop, — молчание по решению, а не по поломке.</summary>
    [property: JsonPropertyName("idle_turns")] int IdleTurns,

    [property: JsonPropertyName("consecutive_failures")] int ConsecutiveFailures,
    [property: JsonPropertyName("broken_promises")] int BrokenPromises,
    [property: JsonPropertyName("compactions")] int Compactions,
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
