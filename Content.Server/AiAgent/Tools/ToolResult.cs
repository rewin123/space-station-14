using System.Collections.Generic;
using System.Text.Json;

namespace Content.Server.AiAgent.Tools;

/// <summary>Failure vocabulary. Fixed and small so the model learns one mental model, not forty.</summary>
public static class ToolError
{
    public const string BadArgs = "bad_args";
    public const string StaleHandle = "stale_handle";
    public const string NotVisible = "not_visible";

    /// <summary>
    /// The device is not wired to the AI at all — a blast door, a hand-cranked shutter, a firelock.
    ///
    /// Distinct from <see cref="NotVisible"/> on purpose: reporting "no cameras" for something the
    /// AI could never operate sends the model chasing camera coverage that would not help if it
    /// found it. Observed live — it moved the eye repeatedly trying to fix an unfixable refusal.
    /// </summary>
    public const string NotControllable = "not_controllable";
    public const string NoAccess = "no_access";
    public const string Unpowered = "unpowered";
    public const string WireCut = "wire_cut";
    public const string Carded = "carded";
    public const string Dead = "dead";
    public const string Timeout = "timeout";
    public const string ReviewMode = "review_mode";
    public const string TurnBudget = "turn_budget";
    public const string Internal = "internal";
    public const string UnknownTool = "unknown_tool";
}

/// <summary>
/// The single response envelope every tool returns.
///
/// One shape for success and failure means the model does not have to learn per-tool result
/// formats. Three rules learned the hard way on the mcbot deployment are baked in here:
/// never return a bare exception string (a result of "Stack trace: undefined" made the model
/// loop on the same broken call); always suggest the nearest valid values on a bad argument; and
/// never echo the whole tool array back — only the offending tool, if anything.
/// </summary>
public sealed class ToolResult
{
    public bool Ok { get; private init; }
    public string? Error { get; private init; }
    public string? Detail { get; private init; }
    public string? Retry { get; private init; }
    public IReadOnlyList<string>? Alternatives { get; private init; }

    /// <summary>
    /// World state read back on the main thread <em>after</em> the mutation.
    ///
    /// This is what makes skill learning honest: the transcript ends up holding what the server
    /// actually observed, not the model's account of what it believes it did.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Effect { get; private init; }

    /// <summary>Lines that arrived while the model was mid-turn and therefore deaf.</summary>
    public IReadOnlyList<string>? Unread { get; set; }

    public static ToolResult Success(IReadOnlyDictionary<string, object?>? effect = null) =>
        new() { Ok = true, Effect = effect };

    public static ToolResult Effected(string handle, object? state) =>
        new() { Ok = true, Effect = new Dictionary<string, object?> { [handle] = state } };

    public static ToolResult Fail(string error, string? detail = null, string? retry = null,
        IReadOnlyList<string>? alternatives = null) =>
        new() { Ok = false, Error = error, Detail = detail, Retry = retry, Alternatives = alternatives };

    /// <summary>
    /// Report an unexpected exception usefully: type and message, plus which tool blew up.
    /// Never the raw stack — it is thousands of tokens the model cannot act on.
    /// </summary>
    public static ToolResult FromException(string tool, Exception e) =>
        Fail(ToolError.Internal, $"{tool}: {e.GetType().Name}: {e.Message}", retry: "later");

    /// <summary>Just the effect, for a one-line log entry that does not repeat the whole envelope.</summary>
    public string EffectJson()
    {
        if (Effect == null || Effect.Count == 0)
            return "";

        var buffer = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Llm.LlmJson.WriterOptions))
        {
            w.WriteStartObject();
            foreach (var (k, v) in Effect)
            {
                w.WritePropertyName(k);
                JsonSerializer.Serialize(w, v, Llm.LlmJson.Options);
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public string ToJson()
    {
        var buffer = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Llm.LlmJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", Ok);

            if (Error != null)
                w.WriteString("error", Error);
            if (Detail != null)
                w.WriteString("detail", Detail);
            if (Retry != null)
                w.WriteString("retry", Retry);

            if (Alternatives is { Count: > 0 })
            {
                w.WriteStartArray("alternatives");
                foreach (var a in Alternatives)
                    w.WriteStringValue(a);
                w.WriteEndArray();
            }

            if (Effect is { Count: > 0 })
            {
                w.WritePropertyName("effect");
                JsonSerializer.Serialize(w, Effect, Llm.LlmJson.Options);
            }

            if (Unread is { Count: > 0 })
            {
                w.WriteStartArray("unread");
                foreach (var u in Unread)
                    w.WriteStringValue(u);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
