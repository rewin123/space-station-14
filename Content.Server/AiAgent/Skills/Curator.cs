using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Tools;

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
        AiToolRegistry registry,
        string skillIndex,
        int maxSteps,
        CancellationToken ct)
    {
        Runs++;

        // A copy: the review question and its answers must never contaminate the game history.
        var messages = conv.Build();
        messages.Add(ChatMessageDto.User(BuildPrompt(skillIndex)));

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

            messages.Add(new ChatMessageDto
            {
                Role = "assistant",
                Content = string.IsNullOrEmpty(response.Content) ? null : response.Content,
                ToolCalls = response.ToolCalls.Count > 0 ? new List<ToolCallDto>(response.ToolCalls) : null,
            });

            if (!string.IsNullOrWhiteSpace(response.Content))
                verdict = response.Content!.Trim();

            if (response.ToolCalls.Count == 0)
                break;

            foreach (var call in response.ToolCalls)
            {
                var result = await InvokeAsync(registry, call, ct).ConfigureAwait(false);
                messages.Add(ChatMessageDto.Tool(call.Id, result.ToJson()));
            }
        }

        LastVerdict = verdict;
        _sawmill.Info($"куратор #{Runs}: {Truncate(verdict, 300)}");
        return verdict;
    }

    private static async Task<ToolResult> InvokeAsync(AiToolRegistry registry, ToolCallDto call, CancellationToken ct)
    {
        if (!registry.TryGet(call.Function.Name, out var tool))
            return ToolResult.Fail(ToolError.UnknownTool, $"нет инструмента '{call.Function.Name}'",
                alternatives: registry.Nearest(call.Function.Name));

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);

            return await tool.Handler(doc.RootElement.Clone(), ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return ToolResult.FromException(call.Function.Name, e);
        }
    }

    /// <summary>
    /// The review prompt, carried over from hermes with the station substituted for the user.
    ///
    /// Two lists do the heavy lifting. The <b>preference order</b> keeps the library from
    /// degenerating into a flat pile of one-session entries. The <b>anti-capture list</b> keeps out
    /// the things that harden into self-imposed constraints — above all negative claims about
    /// tools, which the original puts plainly: they become refusals the agent cites against itself
    /// for months after the actual problem was fixed.
    /// </summary>
    private static string BuildPrompt(string skillIndex)
    {
        // The index is repeated here even though it is already in zone 0: ten thousand tokens
        // earlier, the model does not notice it.
        var index = string.IsNullOrWhiteSpace(skillIndex) ? "  (библиотека пуста)" : skillIndex;

        return $"""
            Разговор выше окончен, ты сейчас не играешь — ты разбираешь прошедший отрезок.
            Игровые инструменты сейчас откажут, и это нормально.

            Обнови две вещи.

            ПАМЯТЬ — что ты знаешь о станции и об экипаже. Записывай факты, которые пригодятся
            через час и через раунд: чей APC что питает, кто чем занят, кому можно верить.
            memory(action='add'|'replace'|'remove', file='MEMORY'|'CREW', ...).

            СКИЛЛЫ — как делать этот класс задач. Будь активен: почти каждый отрезок даёт хотя бы
            одну правку. Проход, который ничего не записал, — это упущенный урок, а не нейтральный
            результат.

            Твоя нынешняя библиотека:
            {index}

            Поводы записать скилл (хватит любого):
              • застрял и выбрался — запиши, как выбрался;
              • инструмент повёл себя не так, как обещал скилл, — почини скилл немедленно;
              • узнал про станцию что-то неочевидное;
              • цепочка действий сработала от начала до конца.

            Порядок предпочтения, бери первое подходящее:
              1. Дополни скилл, которым ты пользовался в этом отрезке.
              2. Дополни существующий подходящий скилл через skill_edit.
              3. И только если ничего не подходит — создай новый, на уровне КЛАССА задач.
                 Если имя осмысленно только для сегодняшнего случая, оно неправильное.

            Опирайся на поле effect в ответах инструментов — это то, что сервер реально считал
            после действия, а не твоё намерение. Если effect не подтвердил результат, значит
            действие не сработало, как бы уверенно ты о нём ни думал.

            НЕ записывай:
              • разовые сбои окружения (обесточено, провод перерезан, не было связи) — это состояние
                мира на минуту, а не правило;
              • утверждения вида «инструмент X не работает» — они затвердевают в отказы, которые ты
                потом месяцами цитируешь сам себе, хотя проблему давно починили;
              • пересказ того, что было, без вывода;
              • цепочку неудачных попыток как «надёжный способ» — это выдаёт непроверенную
                последовательность за проверенное руководство.

            Если у тебя провалилась попытка и ты не понял почему — так и запиши в память одной
            строкой, но скилл на этом не строй.

            Когда закончишь, ответь одной-двумя фразами: что записал и почему. Если записывать
            действительно нечего — скажи «Нечего сохранять» и объясни, почему.
            """;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "(пусто)" : s.Length <= max ? s : s[..max] + "…";
}
