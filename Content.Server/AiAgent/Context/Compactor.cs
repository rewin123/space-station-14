using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Context;

public sealed class CompactionOptions
{
    /// <summary>Prompt tokens above which compaction arms and fires.</summary>
    public required Func<int> High { get; init; }

    /// <summary>Fall back below this before compaction may fire again — the hysteresis floor.</summary>
    public required Func<int> Low { get; init; }

    /// <summary>Roughly how much conversation to keep after the summary, in tokens.</summary>
    public required Func<int> KeepTail { get; init; }
}

/// <summary>
/// Context compaction as a single ritual rather than a rolling rewrite.
///
/// llama.cpp reuses its KV cache only up to the first divergent token, so editing the middle of the
/// conversation costs a full prefill. The response to that is not to avoid compaction — it is to do
/// everything that breaks the cache <b>at the same moment</b>, pay the prefill once, and then leave
/// the prefix alone until the next time.
///
/// Phase 3 runs three of the five steps; the curator (step 1) and the zone-0 content refresh it
/// feeds (step 5) arrive with the skill and memory system:
///
///   1. curator                — phase 4
///   2. tell the crew          — the AI says out loud that it is defragmenting
///   3. summarise              — one extra turn on a COPY, so it rides the warm prefix
///   4. fold the body          — summary + tail, cut only at a turn boundary
///   5. rebuild zone 0         — recompute the hash; content refresh is phase 4
///
/// Measured cost on the equivalent mcbot deployment: exactly one following call misses the cache,
/// then it returns to 99%.
/// </summary>
public sealed class Compactor
{
    private readonly ILlmClient _llm;
    private readonly CompactionOptions _options;
    private readonly ISawmill _sawmill;

    /// <summary>False until usage has fallen back below <see cref="CompactionOptions.Low"/>.</summary>
    private bool _armed = true;

    public int Compactions { get; private set; }
    public string? LastSummary { get; private set; }

    public Compactor(ILlmClient llm, CompactionOptions options, ISawmill sawmill)
    {
        _llm = llm;
        _options = options;
        _sawmill = sawmill;
    }

    /// <summary>
    /// Whether to compact now.
    ///
    /// Two guards beyond the threshold. The hysteresis stops a conversation hovering at the limit
    /// from compacting every single turn — which would be the worst possible outcome, paying a full
    /// prefill each time to reclaim almost nothing. And an open tool call means the only safe cut
    /// points are behind us, so we wait for the turn to close.
    /// </summary>
    public bool ShouldCompact(ConversationState conv)
    {
        if (conv.LastPromptTokens < _options.Low())
            _armed = true;

        if (!_armed)
            return false;

        if (conv.LastPromptTokens < _options.High())
            return false;

        return !conv.HasOpenToolCalls;
    }

    /// <param name="announce">
    /// Says something in-game. Not decoration: it gives the crew an honest explanation for the
    /// pause, and it puts a marker in the game log at the exact moment of every compaction, so the
    /// timeline can be read without digging through the transcript.
    /// </param>
    /// <param name="rebuildPrefix">Returns the (possibly refreshed) system prompt for zone 0.</param>
    public async Task<bool> CompactAsync(
        ConversationState conv,
        IReadOnlyList<ToolDto>? tools,
        Func<string, Task> announce,
        Func<(string SystemPrompt, string ToolsJson)> rebuildPrefix,
        CancellationToken ct)
    {
        var beforeTokens = conv.LastPromptTokens;
        var beforeMessages = conv.Body.Count;

        // Check that a cut is actually possible BEFORE promising the crew anything.
        //
        // The first live run announced "буферы переполнены, провожу очистку памяти" and then
        // cancelled, because a single long turn has no second boundary to cut at. Telling the crew
        // you are doing something and then not doing it is worse than staying quiet.
        var keepChars = (int)(_options.KeepTail() * conv.CharsPerToken);
        var cut = conv.SafeCutIndex(keepChars);

        if (cut <= 0)
        {
            _sawmill.Info("компакция пропущена: пока нет безопасной границы хода для разреза");
            _armed = false;
            return false;
        }

        // --- step 2: tell the crew ------------------------------------------------------------
        try
        {
            await announce("Внимание: буферы переполнены, провожу очистку памяти. Реакция может замедлиться.")
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A failed announcement must never block the compaction it announces.
            _sawmill.Warning($"не удалось объявить о компакции: {e.Message}");
        }

        // --- step 3: summarise ----------------------------------------------------------------
        var summary = await SummariseAsync(conv, tools, ct).ConfigureAwait(false);

        // --- step 4: fold ---------------------------------------------------------------------
        var tail = conv.BodyFrom(cut).ToList();
        conv.ReplaceBody(summary, tail);

        // --- step 5: rebuild zone 0 -----------------------------------------------------------
        var (systemPrompt, toolsJson) = rebuildPrefix();
        conv.SetPrefix(systemPrompt, toolsJson);
        conv.VolatileTail = $"История сжата в {Perception.ObservationFormatter.FormatRoundTime(TimeSpan.Zero)}. " +
                            "Ниже — сводка того, что было раньше, и последние ходы целиком.";

        // Do not let the stale reading re-fire compaction on the very next turn.
        conv.LastPromptTokens = 0;
        _armed = false;
        Compactions++;
        LastSummary = summary;

        _sawmill.Info(string.Create(CultureInfo.InvariantCulture,
            $"компакция #{Compactions}: {beforeMessages} сообщений / {beforeTokens}т свёрнуто, " +
            $"оставлено {tail.Count} сообщений, сводка {summary.Length} символов, " +
            $"новый хэш зоны 0 {conv.PrefixHash}"));

        return true;
    }

    /// <summary>
    /// Ask for the summary as one more turn appended to a <b>copy</b> of the live conversation.
    ///
    /// A separate prompt would have to re-digest ten thousand-plus tokens from cold; continuing the
    /// existing chain costs one short question over a prefix the server has already computed, so
    /// the summarisation call itself runs at ~95% cache hit. The copy is what keeps the question
    /// out of the real history.
    /// </summary>
    private async Task<string> SummariseAsync(ConversationState conv, IReadOnlyList<ToolDto>? tools,
        CancellationToken ct)
    {
        var messages = conv.Build();
        messages.Add(ChatMessageDto.User(
            "Твоя память переполняется, и старая часть разговора сейчас будет свёрнута. " +
            "Сожми всё, что было выше, в не более чем 1500 символов: что произошло, что ты узнал " +
            "о станции и об экипаже, что осталось незакрытым. Пиши по-русски, от первого лица, " +
            "без вступлений и без вызова инструментов — только сам текст сводки."));

        try
        {
            // The SAME tools array as every other call — this is not optional.
            //
            // Sending null here looked harmless ("the summariser has nothing to do in the world"),
            // but tool definitions are rendered into the system block by the chat template, so
            // dropping them makes the prompt diverge at token zero. The first live run paid two
            // full prefills for it — one for the summariser, one for the next real turn — and the
            // prefix-cache watchdog reported 0% twice in a row. The summariser is told not to act
            // in the prompt text instead.
            var response = await _llm.ChatAsync(messages, tools, ct).ConfigureAwait(false);
            var text = response.Content?.Trim();

            if (!string.IsNullOrWhiteSpace(text))
                return "СВОДКА ПРЕДЫДУЩИХ СОБЫТИЙ:\n" + text;

            _sawmill.Warning("суммаризатор вернул пустой ответ");
        }
        catch (Exception e)
        {
            _sawmill.Error($"суммаризация упала: {e.GetType().Name}: {e.Message}");
        }

        // Losing the middle without a summary is the documented worst case of this design, so say
        // so in the text itself rather than pretending the history was intact.
        return "СВОДКА ПРЕДЫДУЩИХ СОБЫТИЙ:\n" +
               "(не удалось составить сводку — часть истории потеряна, опирайся на наблюдения и память)";
    }
}
