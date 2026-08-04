using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Llm;

/// <summary>
/// Everything the agent loop needs from a model backend.
///
/// It is an interface purely so benchmarks can swap in a scripted client: deterministic
/// regression tests must exercise the tool layer, the marshalling and the compaction logic
/// without a GPU or a running llama-server. See <c>AiTestHooks.LlmFactory</c>.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessageDto> messages,
        IReadOnlyList<ToolDto>? tools,
        CancellationToken ct);

    /// <summary>
    /// The backend's real context window, or null if it could not be determined.
    /// Read from llama-server's <c>/props</c> so compaction thresholds are checked against the
    /// truth rather than against a guess that silently drifts when the server is reconfigured.
    /// </summary>
    Task<int?> GetContextSizeAsync(CancellationToken ct);
}
