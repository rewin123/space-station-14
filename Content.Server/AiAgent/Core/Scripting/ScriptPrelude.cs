namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// Lua code the agent gets ready-made — on top of the tools, but below its own script.
///
/// <para>
/// Lives as source, not as a set of C# bindings, for one reason: everything here is expressed
/// through tools that already exist, and what's written in Lua is visible to the model exactly as
/// it calls it. A C# binding would add a second place where behavior could diverge, and not a
/// single line of benefit.
/// </para>
/// <para>
/// The prelude runs before the script, in the same sandbox. A syntax error here is a build defect,
/// not a behavioral one: it fails a test rather than leaving the agent empty-handed in the field.
/// </para>
/// </summary>
public static class ScriptPrelude
{
    /// <summary>Names the prelude declares. The typo linter must know about them.</summary>
    public static readonly string[] Names = { "find" };

    public const string Source = @"
-- find(текст [, вид]) -> список хендлов
--
-- Обёртка над look: осмотреться и оставить только то, в чьей строке встречается подстрока.
-- Сравнение точное, без приведения регистра: в Lua string.lower работает побайтово и кириллицу
-- не трогает, так что мнимая нечувствительность к регистру обманывала бы в самом частом случае.
function find(what, kind)
    local r = look(kind and { kind = kind } or {})
    local rows = r.effect and r.effect['объекты'] or {}
    local out = {}

    for _, row in ipairs(rows) do
        if string.find(row, what, 1, true) then
            out[#out + 1] = string.match(row, '^([^ |]+)')
        end
    end

    return out
end
";
}
