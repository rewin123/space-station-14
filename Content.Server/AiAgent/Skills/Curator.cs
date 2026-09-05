using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vfs;

namespace Content.Server.AiAgent.Skills;

/// <summary>
/// Self-evolution: after a stretch of play, the agent reviews what just happened and writes down
/// what it learned.
///
/// Ported from <c>hermes-agent/agent/background_review.py</c>, including the two structural
/// decisions that make it work:
///
/// <b>It is one more turn appended to a COPY of the same message chain</b>, not a separate agent
/// with its own prompt. Two reasons, both load-bearing. Cache: a separate prompt would re-digest
/// ten thousand-plus tokens from cold on every visit, while continuing the chain costs one short
/// question over a prefix the server already holds. Material: a separate curator gets a retelling,
/// while this one sees the actual history — which call failed, and what the world answered.
///
/// <b>The tool array is identical to play.</b> A different tool set would diverge the prompt at
/// token zero and zero the cache — the exact bug the first live compaction run cost us. So the
/// game-acting tools stay in the schema and refuse at dispatch with <c>review_mode</c> instead.
/// </summary>
public sealed class Curator
{
    private readonly ILlmClient _llm;
    private readonly ISawmill _sawmill;

    public int Runs { get; private set; }
    public string? LastVerdict { get; private set; }

    /// <summary>
    /// How many times the last review actually wrote something.
    ///
    /// <para>
    /// Counts successful write calls, not model responses. The report goes into the dialogue only
    /// when this number is greater than zero: "read it and decided there was nothing worth writing"
    /// is a legitimate review outcome, and telling the agent about it would spend a line on "did
    /// nothing" at every single compaction.
    /// </para>
    /// </summary>
    public int LastWrites { get; private set; }

    public Curator(ILlmClient llm, ISawmill sawmill)
    {
        _llm = llm;
        _sawmill = sawmill;
    }

    /// <summary>
    /// Run one review. Returns the model's closing text, or null if it produced nothing.
    ///
    /// The caller is responsible for putting the session into <see cref="AgentMode.Review"/> first
    /// so the acting tools refuse.
    /// </summary>
    public async Task<string?> ReviewAsync(
        ConversationState conv,
        IReadOnlyList<ToolDto>? tools,
        ToolDispatcher dispatcher,
        Vfs.Vfs vfs,
        int maxSteps,
        CancellationToken ct)
    {
        Runs++;
        LastWrites = 0;

        // A snapshot of the write counter BEFORE the review. Writes used to be counted by call names
        // on the wire — write_file and edit_file — but in script mode those names aren't on the wire
        // at all: there are four names there, and everything else lives inside Lua functions. The
        // counter sits below both paths, so it counts either one.
        var writesBefore = vfs.Writes;

        // A copy: the review question and its answers must never contaminate the game history.
        var messages = conv.Build();
        messages.Add(ChatMessageDto.User(BuildPrompt(vfs)));

        string? verdict = null;

        for (var step = 0; step < maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            LlmResponse response;
            try
            {
                response = await _llm.ChatAsync(messages, tools, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _sawmill.Warning($"ревью куратора упало: {e.GetType().Name}: {e.Message}");
                return verdict;
            }

            // Same trap as ConversationState.AppendAssistant, and worth repeating rather than
            // sharing: an assistant message with neither content nor tool calls makes the provider
            // reject the whole conversation, and this one is a copy of the live history. Nothing to
            // append means nothing to say, which for a review is simply the end of it.
            var content = string.IsNullOrEmpty(response.Content) ? null : response.Content;
            var calls = response.ToolCalls.Count > 0 ? new List<ToolCallDto>(response.ToolCalls) : null;

            if (content == null && calls == null)
            {
                _sawmill.Warning("куратор вернул пустой ответ — разбор прекращён");
                return verdict;
            }

            messages.Add(new ChatMessageDto
            {
                Role = "assistant",
                Content = content,
                ToolCalls = calls,
            });

            if (!string.IsNullOrWhiteSpace(response.Content))
                verdict = response.Content!.Trim();

            if (response.ToolCalls.Count == 0)
                break;

            foreach (var call in response.ToolCalls)
            {
                // NoGameActions unconditionally, and not because the caller remembered to set the
                // session's mode first. The gate used to live only in the agent loop's private
                // dispatcher, so this — the one path it existed for — bypassed it entirely and a
                // curator that decided to call announce simply announced, mid-round.
                var inv = await dispatcher.InvokeAsync(call, DispatchGate.NoGameActions, ct).ConfigureAwait(false);
                messages.Add(ChatMessageDto.Tool(call.Id, inv.Result.ToJson()));
            }
        }

        LastWrites = vfs.Writes - writesBefore;
        LastVerdict = verdict;
        _sawmill.Info($"куратор #{Runs}: записей {LastWrites}, вердикт {Truncate(verdict, 300)}");
        return verdict;
    }

    /// <summary>
    /// Name of the file holding the review prompt, and the substitution inside it.
    /// </summary>
    /// <remarks>
    /// A file, not a constant in code, because editing a file next to <c>SOUL.md</c> is the project's
    /// main debugging affordance, and the review prompt gets edited more often than the personality
    /// does. Mounted read-only: a review instruction that the review could rewrite for itself stops
    /// being an instruction.
    /// </remarks>
    public const string PromptFile = "CURATOR.md";

    /// <summary>Where the filesystem root goes in the file.</summary>
    public const string RootPlaceholder = "{{КОРЕНЬ}}";

    /// <summary>
    /// Build the review question: the text from <c>CURATOR.md</c> plus the substituted tree root.
    ///
    /// <para>
    /// The root is repeated here even though it already sits in zone 0, for the same reason the
    /// index used to be repeated: ten thousand tokens earlier, the model doesn't notice it. The
    /// difference is in cost: the index cost 16 kilobytes on every review, the root costs about
    /// seven hundred characters.
    /// </para>
    /// </summary>
    private string BuildPrompt(Vfs.Vfs vfs)
    {
        var text = vfs.Curator?.Text() ?? string.Empty;

        if (text.Length == 0)
        {
            // Silently skipping the review is not an option: from the outside it looks like "the
            // agent stopped learning" and produces not a single log line. The fallback text is short
            // on purpose — it needs to work, not stand in for the file that needs to be put back.
            _sawmill.Error($"{PromptFile} не найден — разбор идёт по встроенному запасному тексту");
            text = Fallback;
        }

        return text.Replace(RootPlaceholder, vfs.RenderRoot(), StringComparison.Ordinal);
    }

    /// <summary>Fallback text for when the file goes missing. Not a replacement for it, just a way not to stay silent.</summary>
    private const string Fallback = """
        Разговор выше окончен, ты сейчас не играешь — ты разбираешь прошедший отрезок.
        Игровые инструменты сейчас откажут, и это нормально.

        Обнови три вещи: память (/memory.md), заметки о людях (/players) и свои записи (/skills).
        Сначала посмотри, что там уже есть: sh {"cmd":"ls /skills"}.

        {{КОРЕНЬ}}

        Когда закончишь, ответь одной-двумя фразами: что записал и почему.
        """;

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "(пусто)" : s.Length <= max ? s : s[..max] + "…";
}
