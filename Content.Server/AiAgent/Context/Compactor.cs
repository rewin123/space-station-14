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

    /// <summary>Roughly how much conversation to keep after the summary, in tokens.</summary>
    /// <summary>
    /// How many event lines the fold keeps. Replaces a character budget over retained messages:
    /// the messages were the expensive part, and counting them in characters made the cost of a
    /// fold depend on what happened to be in the last one.
    /// </summary>
    public required Func<int> KeepEvents { get; init; }
}

/// <summary>
/// Context compaction as a single ritual rather than a rolling rewrite.
///
/// llama.cpp reuses its KV cache only up to the first divergent token, so editing the middle of the
/// conversation costs a full prefill. The response to that is not to avoid compaction — it is to do
/// everything that breaks the cache <b>at the same moment</b>, pay the prefill once, and then leave
/// the prefix alone until the next time.
///
/// All five steps:
///
///   0. feasibility            — is there a cut point at all? if not, stay quiet and skip
///   1. tell the crew          — BEFORE the slow work, because it explains the pause
///   2. curator                — reviews the stretch and writes skills and memory to disk
///   3. summarise              — one extra turn on a COPY, so it rides the warm prefix
///   4. fold the body          — summary + tail, cut only at a turn boundary
///   5. rebuild zone 0         — pick up exactly what the curator just wrote, recompute the hash
///
/// Order matters twice over. The feasibility check comes before the announcement so the AI never
/// promises a cleanup it then cancels. The announcement comes before the curator because the
/// curator is several model calls — about a minute — and an explanation that arrives after the
/// silence is not an explanation. And the prefix rebuild comes last so that what was learned this
/// stretch is already in zone 0 when the agent resumes, all for the single prefill we were going
/// to pay anyway.
///
/// Measured cost on the equivalent mcbot deployment: exactly one following call misses the cache,
/// then it returns to 99%.
/// </summary>
public sealed class Compactor
{
    private readonly ILlmClient _llm;
    private readonly CompactionOptions _options;
    private readonly CacheMetrics _cache;
    private readonly ISawmill _sawmill;

    private readonly Journal _journal;

    public Compactor(
        ILlmClient llm,
        CompactionOptions options,
        CacheMetrics cache,
        ISawmill sawmill,
        Journal? journal = null)
    {
        _llm = llm;
        _options = options;
        _cache = cache;
        _sawmill = sawmill;

        // The event ring the fold rebuilds history from. Optional so a test that only exercises the
        // threshold logic does not have to supply one; a fold against an empty ring simply keeps
        // the summary alone, which is the honest outcome when nothing was recorded.
        _journal = journal ?? Journal.Disabled;
    }

    /// <summary>
    /// Whether to compact now.
    ///
    /// One threshold and one guard. The low-water mark is gone: it existed so a conversation
    /// hovering at the limit would not fold every turn, and it did that by refusing to re-arm until
    /// usage fell back under it — which silently assumed a fold can always get that far down. It
    /// could not, and on a live shift one fold left 162k against a low of 45k, so the agent never
    /// compacted again and climbed to 236k before dying on a truncated completion.
    ///
    /// Nothing replaces it, because nothing needs to. The fold now discards the whole body for a
    /// summary and a page of event lines, so it lands far below the threshold by construction, and
    /// <c>LastPromptTokens</c> is zeroed on commit so the reading that decides is always a fresh
    /// one. An open tool call still defers: cutting there would orphan a result from its call.
    /// </summary>
    public bool ShouldCompact(AgentState state)
    {
        var conv = state.Conv;

        if (conv.LastPromptTokens < _options.High())
            return false;

        return !conv.HasOpenToolCalls;
    }

    /// <summary>
    /// Walk the ritual. Returns true when it committed.
    ///
    /// The <c>finally</c> is the part that did not exist. If the prefix rebuild threw, the body was
    /// already folded, the prefix was stale, arming was still true and the token reading was
    /// unchanged — so the very next turn tried to compact an already-folded body, and the cache
    /// watchdog screamed "PREFIX CHANGED OUTSIDE A COMPACTION" about a bug that was not there.
    /// </summary>
    public async Task<bool> CompactAsync(
        AgentState state,
        IReadOnlyList<ToolDto>? tools,
        CompactionHooks hooks,
        string roundStamp,
        CancellationToken ct)
    {
        var conv = state.Conv;

        var tokensAtEntry = conv.LastPromptTokens;
        var hashAtEntry = conv.PrefixHash;
        var promptAtEntry = conv.SystemPrompt;
        var toolsJsonAtEntry = conv.ToolsJson;

        var ctx = new CompactionContext
        {
            State = state,
            Journal = _journal,
            Tools = tools,
            Hooks = hooks,
            RoundStamp = roundStamp,
            Llm = _llm,
            Options = _options,
            Sawmill = _sawmill,
        };

        var committed = false;

        try
        {
            foreach (var step in CompactionSteps.Ritual)
            {
                try
                {
                    if (!await step.RunAsync(ctx, ct).ConfigureAwait(false))
                        return false;
                }
                catch (OperationCanceledException)
                {
                    // Never swallowed, at any fatality. Cancellation is not "a step that failed";
                    // it is the ritual not happening, and the body must be left untouched.
                    throw;
                }
                catch (Exception e) when (!step.Fatal)
                {
                    _sawmill.Warning(
                        $"шаг компакции '{step.Name}' не отработал: {e.GetType().Name}: {e.Message}");
                }
            }

            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
            {
                // The token reading, so the next turn re-evaluates honestly instead of on a zero.
                conv.LastPromptTokens = tokensAtEntry;

                // Zone 0 byte-for-byte, if the rebuild got that far and then threw.
                if (conv.PrefixHash != hashAtEntry)
                    conv.SetPrefix(promptAtEntry, toolsJsonAtEntry);
            }

            // Tell the watchdog the truth about the prefix, whatever path we took. This is the one
            // place that may do so: the old code reported only on success, so a throwing rebuild
            // left the metrics expecting a hash that no longer existed.
            if (conv.PrefixHash != hashAtEntry)
            {
                _cache.SetExpectedPrefix(conv.PrefixHash);
                _cache.ExpectMiss = true;
            }
        }
    }
}
