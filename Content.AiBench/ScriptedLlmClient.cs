using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;

namespace Content.AiBench;

/// <summary>
/// A deterministic stand-in for the model.
///
/// The tool layer, the main-thread marshalling, the gate chain and the compaction logic all have
/// to be testable without a GPU, without llama-server running, and without the run-to-run variance
/// a real model brings. So the regression suite scripts the assistant turns and asserts on world
/// state; the behavioural suite (which does need a live model) is a separate category.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<LlmResponse> _script = new();

    /// <summary>Every prompt this client was asked to complete, for asserting on what the agent sent.</summary>
    public List<IReadOnlyList<ChatMessageDto>> SeenPrompts { get; } = new();

    public int Calls { get; private set; }

    public ScriptedLlmClient Then(string content)
    {
        _script.Enqueue(new LlmResponse(content, Array.Empty<ToolCallDto>(), 100, 90, 10, 0.1));
        return this;
    }

    public ScriptedLlmClient ThenCall(string tool, string argsJson)
    {
        _script.Enqueue(new LlmResponse(null, new[]
        {
            new ToolCallDto
            {
                Id = $"call_{_script.Count + 1}",
                Type = "function",
                Function = new FunctionCallDto { Name = tool, Arguments = argsJson },
            },
        }, 100, 90, 10, 0.1));

        return this;
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto> tools,
        CancellationToken ct)
    {
        Calls++;
        SeenPrompts.Add(new List<ChatMessageDto>(messages));

        // Running dry means "say nothing further", which ends the turn cleanly rather than
        // looping — the same thing a real model does when it is finished.
        return Task.FromResult(_script.Count > 0
            ? _script.Dequeue()
            : new LlmResponse(string.Empty, Array.Empty<ToolCallDto>(), 100, 100, 1, 0.01));
    }

    public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(131072);
}
