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
    /// Сколько раз последний разбор реально что-то записал.
    ///
    /// <para>
    /// Считаются успешные вызовы записи, а не ответы модели. Отчёт в диалог уходит только когда
    /// это число больше нуля: «прочитал и решил, что записывать нечего» — законный исход разбора,
    /// и сообщать о нём агенту значило бы каждую компакцию тратить строку на «ничего не сделал».
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

        // Снимок счётчика записей ДО разбора. Раньше записи считались по именам вызовов на
        // проводе — write_file и edit_file, — но в режиме скриптов этих имён на проводе нет вовсе:
        // там четыре имени, и всё остальное живёт функциями Lua. Счётчик стоит ниже обеих дорог,
        // поэтому считает и то и другое.
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
    /// Имя файла с промптом разбора и подстановка внутри него.
    /// </summary>
    /// <remarks>
    /// Файл, а не константа в коде, потому что правка файла рядом с <c>SOUL.md</c> — главный
    /// отладочный аффорданс этого проекта, а промпт разбора правится чаще личности. Смонтирован он
    /// только на чтение: инструкция разбора, которую разбор может себе переписать, перестаёт быть
    /// инструкцией.
    /// </remarks>
    public const string PromptFile = "CURATOR.md";

    /// <summary>Куда в файле встаёт корень файловой системы.</summary>
    public const string RootPlaceholder = "{{КОРЕНЬ}}";

    /// <summary>
    /// Собрать вопрос разбора: текст из <c>CURATOR.md</c> плюс подставленный корень дерева.
    ///
    /// <para>
    /// Корень повторяется здесь, хотя он уже стоит в зоне 0, — по той же причине, по которой
    /// раньше повторялся индекс: десятью тысячами токенов раньше модель его не замечает. Разница в
    /// цене: индекс стоил 16 килобайт на каждый разбор, корень стоит около семисот символов.
    /// </para>
    /// </summary>
    private string BuildPrompt(Vfs.Vfs vfs)
    {
        var text = vfs.Curator?.Text() ?? string.Empty;

        if (text.Length == 0)
        {
            // Молча не разбирать нельзя: снаружи это выглядит как «агент перестал учиться» и не
            // даёт ни строки в лог. Запасной текст короткий намеренно — он должен работать, а не
            // подменять собой файл, который надо вернуть на место.
            _sawmill.Error($"{PromptFile} не найден — разбор идёт по встроенному запасному тексту");
            text = Fallback;
        }

        return text.Replace(RootPlaceholder, vfs.RenderRoot(), StringComparison.Ordinal);
    }

    /// <summary>Запасной текст на случай пропажи файла. Не замена ему, а способ не молчать.</summary>
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
