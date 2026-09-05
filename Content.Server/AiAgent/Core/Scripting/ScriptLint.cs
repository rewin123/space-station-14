using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// A pre-run typo check: which functions the script calls, and whether all of them exist.
///
/// <para>
/// This is the price of a dynamic language, paid up front. C# scripting had one real advantage —
/// a typo in a tool name doesn't compile, and the world doesn't get a chance to change. In Lua it
/// turns into "attempt to call a nil value" in the middle of a run, after the robot has already
/// crossed half the station and picked something up. The linter restores that guarantee for the
/// one class of errors worth wanting it for.
/// </para>
/// <para>
/// There is one rule: <b>when in doubt, stay quiet</b>. A false alarm here is more expensive than
/// a miss, because it costs the model a turn arguing with a problem that doesn't exist. So anything
/// declared anywhere in the script itself — a local, a function, a parameter, a loop variable — is
/// treated as known, even if it's declared later in the text.
/// </para>
/// </summary>
public static class ScriptLint
{
    private static readonly Regex Strings = new("\"[^\"\n]*\"|'[^'\n]*'|\\[\\[.*?\\]\\]", RegexOptions.Singleline);
    private static readonly Regex Comments = new("--\\[\\[.*?\\]\\]|--[^\n]*", RegexOptions.Singleline);
    private static readonly Regex Called = new(@"(?<![\w.:])([A-Za-z_]\w*)\s*[({'""]");
    private static readonly Regex NamedFunction = new(@"\bfunction\s+([A-Za-z_][\w.:]*)");
    private static readonly Regex Locals = new(@"\blocal\s+(?:function\s+)?([A-Za-z_][\w\s,]*)");
    private static readonly Regex Parameters = new(@"\bfunction\s*[\w.:]*\s*\(([^)]*)\)");
    private static readonly Regex ForNames = new(@"\bfor\s+([A-Za-z_][\w\s,]*?)\s*(?:=|\bin\b)");
    private static readonly Regex Assigned = new(@"(?<![\w.:=~<>])([A-Za-z_]\w*)\s*=(?!=)");

    /// <summary>Lua keywords: they end up looking like "calls" because of <c>if x then f() end</c> and the like.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if",
        "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    /// <summary>
    /// Names the script calls that are neither among the globals nor among its own declarations.
    /// An empty list means it's safe to run.
    /// </summary>
    public static IReadOnlyList<string> Unknown(string code, IEnumerable<string> globals)
    {
        var text = Comments.Replace(code, " ");
        text = Strings.Replace(text, "\"\"");

        var known = new HashSet<string>(globals, StringComparer.Ordinal);
        foreach (var keyword in Keywords)
            known.Add(keyword);

        foreach (Match match in NamedFunction.Matches(text))
            known.Add(Head(match.Groups[1].Value));

        foreach (Match match in Locals.Matches(text))
            AddList(known, match.Groups[1].Value);

        foreach (Match match in Parameters.Matches(text))
            AddList(known, match.Groups[1].Value);

        foreach (Match match in ForNames.Matches(text))
            AddList(known, match.Groups[1].Value);

        foreach (Match match in Assigned.Matches(text))
            known.Add(match.Groups[1].Value);

        var unknown = new List<string>();

        foreach (Match match in Called.Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!known.Contains(name) && !unknown.Contains(name))
                unknown.Add(name);
        }

        return unknown;
    }

    private static void AddList(HashSet<string> known, string names)
    {
        foreach (var part in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = Head(part.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");
            if (name.Length > 0)
                known.Add(name);
        }
    }

    /// <summary>From <c>obj.method</c> we need the head: declaring the method means declaring the table too.</summary>
    private static string Head(string name)
    {
        var cut = name.IndexOfAny(new[] { '.', ':' });
        return cut < 0 ? name : name[..cut];
    }
}
