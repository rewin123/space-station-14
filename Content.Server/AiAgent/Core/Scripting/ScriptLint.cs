using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>
/// Проверка на опечатки до запуска: какие функции скрипт зовёт и все ли они существуют.
///
/// <para>
/// Это плата за динамический язык, внесённая заранее. У C#-скриптинга было одно настоящее
/// преимущество — опечатка в имени инструмента не компилируется, и мир не успевает измениться.
/// В Lua она превращается в «attempt to call a nil value» на середине работы, когда робот уже
/// прошёл полстанции и что-то взял. Линтер возвращает эту гарантию для того единственного класса
/// ошибок, ради которого её стоило бы хотеть.
/// </para>
/// <para>
/// Правило одно: <b>сомневаешься — молчи</b>. Ложная тревога здесь дороже пропуска, потому что
/// она отнимает у модели ход на спор с несуществующей проблемой. Поэтому всё, что хоть где-то
/// объявлено в самом скрипте — локальная, функция, параметр, переменная цикла, — считается
/// известным, даже если объявлено ниже по тексту.
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

    /// <summary>Ключевые слова Lua: они попадают в «вызовы» из-за <c>if x then f() end</c> и подобного.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if",
        "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    /// <summary>
    /// Имена, которые скрипт зовёт, но которых нет ни среди глобалов, ни среди его собственных
    /// объявлений. Пустой список — можно запускать.
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

    /// <summary>Из <c>обj.метод</c> нужна голова: объявлять метод — значит объявлять и таблицу.</summary>
    private static string Head(string name)
    {
        var cut = name.IndexOfAny(new[] { '.', ':' });
        return cut < 0 ? name : name[..cut];
    }
}
