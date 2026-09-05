using System;

namespace Content.Server.AiAgent.Locale;

/// <summary>
/// The language of the agent's prompt, observations and tool replies.
///
/// Frozen on the body at assembly time, same as script mode: the frozen prefix, the tool schemas
/// and the JSON keys the Lua prelude reads must stay one language for the whole session. Flipping
/// <c>ai.language</c> mid-round takes effect on the next claimed session.
/// </summary>
public enum AgentLang : byte
{
    Ru,
    En,
}

public static class AgentLangUtil
{
    public static AgentLang Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return AgentLang.Ru;

        var value = raw.Trim();

        if (value.Equals("en", StringComparison.OrdinalIgnoreCase)
            || value.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            || value.Equals("english", StringComparison.OrdinalIgnoreCase))
            return AgentLang.En;

        return AgentLang.Ru;
    }
}
