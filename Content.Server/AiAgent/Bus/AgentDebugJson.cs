using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// Serializer settings for the debug API — deliberately not <c>LlmJson.Options</c>.
///
/// They differ in exactly one thing that matters: <c>LlmJson.Options</c> sets
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/>, because the bytes it produces go to the model
/// server and every omitted null is a token not spent. Here that setting is actively wrong. A
/// debugger asking for state when nobody holds a core would get a body with no <c>session</c> key
/// at all, and a client has no way to tell "the field is null" from "this server is too old to
/// have the field" — so it guesses, and eventually guesses wrong.
///
/// The Cyrillic encoder is kept, for the same reason as there: escaped Russian is six times the
/// bytes and unreadable in a browser's network tab.
/// </summary>
public static class AgentDebugJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // Nulls are written. An absent key and a null key mean different things to a client.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
