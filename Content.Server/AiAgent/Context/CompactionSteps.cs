using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Context;

/// <summary>The three doors the ritual has to the outside world. Two required, one optional.</summary>
public sealed class CompactionHooks
{
    /// <summary>
    /// Says something in-game. Not decoration: it gives the crew an honest explanation for the
    /// pause, and it puts a marker in the game log at the exact moment of every compaction, so the
    /// timeline can be read without digging through the transcript.
    /// </summary>
    public required Func<string, Task> Announce { get; init; }

    /// <summary>Returns the (possibly refreshed) system prompt and tool array for zone 0.</summary>
    public required Func<(string SystemPrompt, string ToolsJson)> RebuildPrefix { get; init; }

    /// <summary>The review. Optional because <c>ai.curator_enabled</c> can turn it off.</summary>
    public Func<Task>? Curate { get; init; }
}

/// <summary>Everything one compaction may read or write. A step cannot reach for anything else.</summary>
public sealed class CompactionContext
{
    public required AgentState State { get; init; }
    public required IReadOnlyList<ToolDto>? Tools { get; init; }
    public required CompactionHooks Hooks { get; init; }

    /// <summary>Round time, already formatted, from the perception that opened this turn.</summary>
    public required string RoundStamp { get; init; }

    public required ILlmClient Llm { get; init; }

    /// <summary>Where the retained tail comes from now: short event lines, not whole messages.</summary>
    public required Journal Journal { get; init; }
    public required CompactionOptions Options { get; init; }
    public required ISawmill Sawmill { get; init; }

    public ConversationState Conv => State.Conv;

    // Written by one step, read by a later one.
    public int CutIndex { get; set; } = -1;
    public string Summary { get; set; } = string.Empty;
    public int BeforeMessages { get; set; }
    public int BeforeTokens { get; set; }
    public int TailCount { get; set; }
}

/// <summary>One step of the ritual.</summary>
public interface ICompactionStep
{
    /// <summary>As it appears in the log and in a failure message.</summary>
    string Name { get; }

    /// <summary>
    /// True: an exception aborts the ritual and the runner's <c>finally</c> rolls back.
    /// False: the exception is logged and the ritual continues — because the context still has to
    /// shrink, announced or reviewed or not.
    /// </summary>
    bool Fatal { get; }

    /// <summary>False aborts the ritual cleanly — not an error. Only feasibility does this.</summary>
    Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct);
}

/// <summary>
/// The ritual, as an ordered array.
///
/// The order used to be the order of statements in an eighty-eight-line method with the steps
/// marked by comment banners. Nothing but that layout stopped somebody moving the announcement
/// below the curator — which is exactly the bug one earlier commit had to fix — and only one of the
/// five edges had a test. Here the order IS the array, and fatality is a property rather than a
/// convention about which try/catch you happened to write.
/// </summary>
public static class CompactionSteps
{
    public static readonly ICompactionStep[] Ritual =
    {
        new FeasibilityStep(),
        new AnnounceStep(),
        new CurateStep(),
        new SummariseStep(),
        new FoldStep(),
        new RebuildPrefixStep(),
        new CommitStep(),
    };

    /// <summary>
    /// Is a cut possible at all? Checked BEFORE promising the crew anything.
    ///
    /// The first live run announced "буферы переполнены, провожу очистку памяти" and then cancelled,
    /// because a single long turn has no second boundary to cut at. Telling the crew you are doing
    /// something and then not doing it is worse than staying quiet.
    /// </summary>
    private sealed class FeasibilityStep : ICompactionStep
    {
        public string Name => "feasibility";
        public bool Fatal => true;

        public Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            ctx.BeforeTokens = ctx.Conv.LastPromptTokens;
            ctx.BeforeMessages = ctx.Conv.Body.Count;

            // There is no cut point to find any more: the body is replaced wholesale, not sliced.
            // The only thing that can make a fold pointless is there being nothing to fold — which
            // happens when a fold has just run and the next turn has not landed yet.
            if (ctx.BeforeMessages > 1)
                return Task.FromResult(true);

            ctx.Sawmill.Info("компакция пропущена: сворачивать нечего");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Tell the crew, before the slow work rather than after it.
    ///
    /// The curator is several model calls back to back — measured at about a minute — and the
    /// summariser adds another. Announcing afterwards would leave the crew talking to a silent AI
    /// for that whole stretch and only then hearing why. The announcement is the explanation for
    /// the pause, so it has to precede the pause.
    /// </summary>
    private sealed class AnnounceStep : ICompactionStep
    {
        public string Name => "announce";
        public bool Fatal => false;

        public async Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            await ctx.Hooks
                .Announce("Внимание: буферы переполнены, провожу очистку памяти. Реакция может замедлиться.")
                .ConfigureAwait(false);

            return true;
        }
    }

    private sealed class CurateStep : ICompactionStep
    {
        public string Name => "curator";
        public bool Fatal => false;

        public async Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            if (ctx.Hooks.Curate != null)
                await ctx.Hooks.Curate().ConfigureAwait(false);

            return true;
        }
    }

    /// <summary>
    /// Ask for the summary as one more turn appended to a <b>copy</b> of the live conversation.
    ///
    /// A separate prompt would have to re-digest ten thousand-plus tokens from cold; continuing the
    /// existing chain costs one short question over a prefix the server has already computed, so
    /// the summarisation call itself runs at ~95% cache hit. The copy is what keeps the question
    /// out of the real history.
    /// </summary>
    private sealed class SummariseStep : ICompactionStep
    {
        public string Name => "summary";
        public bool Fatal => false;

        public async Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            var messages = ctx.Conv.Build();
            messages.Add(ChatMessageDto.User(
                "Твоя память переполняется, и старая часть разговора сейчас будет свёрнута. " +
                "Сожми всё, что было выше, в не более чем 1500 символов: что произошло, что ты узнал " +
                "о станции и об экипаже, что осталось незакрытым. Пиши по-русски, от первого лица, " +
                "без вступлений и без вызова инструментов — только сам текст сводки."));

            try
            {
                // The SAME tools array as every other call — this is not optional.
                //
                // Sending null here looked harmless ("the summariser has nothing to do in the
                // world"), but tool definitions are rendered into the system block by the chat
                // template, so dropping them makes the prompt diverge at token zero. The first live
                // run paid two full prefills for it — one for the summariser, one for the next real
                // turn — and the prefix-cache watchdog reported 0% twice in a row. The summariser is
                // told not to act in the prompt text instead.
                var response = await ctx.Llm.ChatAsync(messages, ctx.Tools, ct).ConfigureAwait(false);
                var text = response.Content?.Trim();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ctx.Summary = "СВОДКА ПРЕДЫДУЩИХ СОБЫТИЙ:\n" + text;
                    return true;
                }

                ctx.Sawmill.Warning("суммаризатор вернул пустой ответ");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Everything except cancellation degrades to the honest placeholder below.
                //
                // Cancellation must NOT: it means the server is going down, and the fold step is
                // about to replace the body with whatever this produced and then persist it.
                // Catching it here turned "shut the server down during a compaction" into
                // "permanently destroy the middle of the conversation and write the damage to disk".
                ctx.Sawmill.Error($"суммаризация упала: {e.GetType().Name}: {e.Message}");
            }

            // Losing the middle without a summary is the documented worst case of this design, so
            // say so in the text itself rather than pretending the history was intact.
            ctx.Summary = "СВОДКА ПРЕДЫДУЩИХ СОБЫТИЙ:\n" +
                          "(не удалось составить сводку — часть истории потеряна, опирайся на наблюдения и память)";
            return true;
        }
    }

    private sealed class FoldStep : ICompactionStep
    {
        public string Name => "fold";
        public bool Fatal => true;

        public Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            // The tail used to be the last few messages, and messages carry their payloads: one
            // look of a crowded room is thousands of tokens of crates, and retaining it meant
            // paying for that room for the rest of the round. A measured turn took the conversation
            // from 27k to 183k tokens this way, and the fold that followed could not get back under
            // any threshold because the weight was inside the part it was keeping.
            //
            // What replaces it is the event log: what was heard, what was called, what refused —
            // one line each, no payloads. look stays expensive to make, which is correct, and stops
            // being expensive to remember, which is the part that was wrong.
            var events = ctx.Journal.Recent(ctx.Options.KeepEvents());
            ctx.TailCount = events.Count;

            var body = ctx.Summary;

            if (events.Count > 0)
            {
                body += "\n\nПОСЛЕДНИЕ СОБЫТИЯ (свёрнуто, без содержимого ответов):\n"
                        + string.Join("\n", events);
            }

            // One message, and no tail after it. Two adjacent user messages would fabricate a turn
            // boundary, and an orphaned tool result would be a protocol error outright.
            ctx.Conv.ReplaceBody(body, Array.Empty<ChatMessageDto>());
            return Task.FromResult(true);
        }
    }

    private sealed class RebuildPrefixStep : ICompactionStep
    {
        public string Name => "prefix";
        public bool Fatal => true;

        public Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            var (systemPrompt, toolsJson) = ctx.Hooks.RebuildPrefix();
            ctx.Conv.SetPrefix(systemPrompt, toolsJson);

            ctx.Conv.VolatileTail = $"История сжата в {ctx.RoundStamp}. " +
                                    "Ниже — сводка того, что было раньше, и последние ходы целиком.";
            return Task.FromResult(true);
        }
    }

    private sealed class CommitStep : ICompactionStep
    {
        public string Name => "commit";
        public bool Fatal => true;

        public Task<bool> RunAsync(CompactionContext ctx, CancellationToken ct)
        {
            // Do not let the stale reading re-fire compaction on the very next turn.
            ctx.Conv.LastPromptTokens = 0;
            ctx.State.Compactions++;
            ctx.State.LastSummary = ctx.Summary;

            ctx.Sawmill.Info(string.Create(CultureInfo.InvariantCulture,
                $"компакция #{ctx.State.Compactions}: {ctx.BeforeMessages} сообщений / {ctx.BeforeTokens}т свёрнуто, " +
                $"оставлено {ctx.TailCount} сообщений, сводка {ctx.Summary.Length} символов, " +
                $"новый хэш зоны 0 {ctx.Conv.PrefixHash}"));

            return Task.FromResult(true);
        }
    }
}
