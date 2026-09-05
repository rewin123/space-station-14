using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Content.Server.AiAgent.Vfs;

/// <summary>Shell response: text for the model plus a flag that something changed on disk.</summary>
/// <param name="Ok">The command ran. An empty search result is also a success.</param>
/// <param name="Text">What to show the model.</param>
/// <param name="Mutated">Disk changed. The curator uses this flag to decide whether to write a report.</param>
/// <param name="Hints">Nearest correct values, in case the name was wrong.</param>
public sealed record ShellResult(bool Ok, string Text, bool Mutated = false, IReadOnlyList<string>? Hints = null)
{
    public static ShellResult Fine(string text, bool mutated = false) => new(true, text, mutated);

    public static ShellResult No(string text, IReadOnlyList<string>? hints = null) =>
        new(false, text, false, hints);
}

/// <summary>
/// Parsing the command line and the commands themselves.
///
/// <para>
/// One line instead of eight tools isn't a stylistic choice. On the previous deployment this was
/// measured directly: 46 narrow commands drowned the same model on the same quantization, while
/// about thirteen worked. Breadth is gained by consolidation, not by new registry entries, and a
/// shell is the most natural form of consolidation the model has ever had in its training.
/// </para>
/// <para>
/// There are no pipes, redirects, or substitutions here, and there won't be. The model will
/// happily write <c>grep pump /wiki_ru | head -5</c>, and pretending we understood that is worse
/// than refusing it: a partially executed pipeline returns a plausible but wrong answer. An
/// unknown command lists the supported ones.
/// </para>
/// </summary>
public sealed class Shell
{
    /// <summary>
    /// Output ceilings. Without them, a single <c>grep</c> over a megabyte and a half blows out the
    /// context window.
    /// </summary>
    /// <remarks>
    /// Truncation is reported OUT LOUD, always. Silently truncated output reads to the model as
    /// "there's nothing more" — that is, it turns lack of room into absence of fact, and does so
    /// quietly.
    /// </remarks>
    public const int MaxHits = 40;
    public const int MaxCat = 8000;
    public const int MaxList = 200;

    private static readonly string[] Known =
    {
        "ls", "tree", "cat", "grep", "find", "mkdir", "rm", "mv",
    };

    private readonly Vfs _vfs;

    public Shell(Vfs vfs)
    {
        _vfs = vfs;
    }

    public ShellResult Run(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ShellResult.No("пустая команда. Есть: " + string.Join(", ", Known));

        if (command.IndexOfAny(new[] { '|', '>', '<', ';', '&', '`' }) >= 0)
        {
            return ShellResult.No(
                "труб, редиректов и цепочек здесь нет — это не настоящий шелл, а восемь команд. " +
                "Вызови одну; если вывод длинный, сузь путь или слово. Есть: " + string.Join(", ", Known));
        }

        var argv = Tokenize(command);

        if (argv.Count == 0)
            return ShellResult.No("пустая команда. Есть: " + string.Join(", ", Known));

        var verb = argv[0].ToLowerInvariant();
        var args = argv.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        var flags = argv.Skip(1).Where(a => a.StartsWith('-')).Select(a => a.TrimStart('-')).ToList();

        return verb switch
        {
            "ls" or "dir" => Ls(Arg(args, 0), flags.Contains("l")),
            "tree" => Tree(Arg(args, 0)),
            "cat" or "read" or "less" or "head" => Cat(Arg(args, 0)),
            "grep" or "search" or "rg" => Grep(Arg(args, 0), Arg(args, 1)),
            "find" => Find(Arg(args, 0)),
            "mkdir" => Mkdir(Arg(args, 0)),
            "rm" or "del" => Rm(Arg(args, 0)),
            "mv" or "move" or "rename" => Mv(Arg(args, 0), Arg(args, 1)),
            "pwd" => ShellResult.Fine("/ — путь всегда полный, текущего каталога нет"),
            _ => ShellResult.No($"нет команды «{verb}». Есть: " + string.Join(", ", Known),
                Known.OrderBy(k => Tools.AiToolRegistry.Distance(k, verb)).Take(3).ToList()),
        };
    }

    private static string Arg(IReadOnlyList<string> args, int i) => i < args.Count ? args[i] : string.Empty;

    /// <summary>
    /// Split a line into words, respecting quotes.
    ///
    /// Cyrillic is normal here: only whitespace counts as a separator, not "unknown character" —
    /// otherwise half the paths in this tree would become unspeakable.
    /// </summary>
    public static List<string> Tokenize(string line)
    {
        var argv = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in line.Trim())
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    argv.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            argv.Add(current.ToString());

        return argv;
    }

    // ------------------------------------------------------------------- commands

    private ShellResult Ls(string raw, bool details)
    {
        if (raw.Length == 0 || raw == "/")
            return ShellResult.Fine(Format(_vfs.RootEntries(), "/", details));

        if (!VfsPath.TryParse(raw, out var path, out var parseError))
            return ShellResult.No(parseError);

        if (!_vfs.TryResolve(path, out var mount, out var relative, out var error))
            return ShellResult.No(error, _vfs.MountPoints());

        var entries = mount.List(relative, out var listError);

        if (listError.Length > 0)
            return ShellResult.No(listError);

        return ShellResult.Fine(Format(entries, path.ToString(), details));
    }

    private ShellResult Tree(string raw)
    {
        var root = raw.Length == 0 ? "/" : raw;
        var sb = new StringBuilder();
        var lines = 0;

        if (root == "/")
        {
            foreach (var entry in _vfs.RootEntries())
            {
                sb.Append('/').Append(entry.Name).Append('\n');
                lines++;
                lines += Descend("/" + entry.Name, sb, "  ", MaxList - lines);
            }
        }
        else
        {
            if (!VfsPath.TryParse(root, out var path, out var parseError))
                return ShellResult.No(parseError);

            sb.Append(path).Append('\n');
            lines = 1 + Descend(path.ToString(), sb, "  ", MaxList - 1);
        }

        if (lines >= MaxList)
            sb.Append($"… обрезано на {MaxList} строках — смотри ls по нужной ветке\n");

        return ShellResult.Fine(sb.ToString().TrimEnd());
    }

    /// <summary>One level deep. We don't go deeper: the whole tree is the same 16 KB index.</summary>
    private int Descend(string raw, StringBuilder sb, string indent, int budget)
    {
        if (budget <= 0 || !VfsPath.TryParse(raw, out var path, out _))
            return 0;

        if (!_vfs.TryResolve(path, out var mount, out var relative, out _))
            return 0;

        var entries = mount.List(relative, out var error);

        if (error.Length > 0)
            return 0;

        var written = 0;

        foreach (var entry in entries)
        {
            if (written >= budget)
                break;

            sb.Append(indent).Append(entry.Name).Append(entry.IsDir ? "/" : string.Empty);

            if (entry.Desc.Length > 0)
                sb.Append("  — ").Append(entry.Desc);

            sb.Append('\n');
            written++;
        }

        return written;
    }

    private ShellResult Cat(string raw)
    {
        if (raw.Length == 0)
            return ShellResult.No("нужен путь: cat /wiki_ru/атмосфера/насосы");

        // A line range is separated by a colon: "path:120-160". This is how you finish reading an
        // article that didn't fit under the ceiling, without forcing the shell to dump it whole.
        var (target, from, to) = SplitRange(raw);

        if (!VfsPath.TryParse(target, out var path, out var parseError))
            return ShellResult.No(parseError);

        if (!_vfs.TryResolve(path, out var mount, out var relative, out var error))
            return ShellResult.No(error, _vfs.MountPoints());

        if (!mount.TryRead(relative, out var content, out var readError))
            return ShellResult.No(readError);

        if (from > 0)
        {
            var lines = content.Split('\n');
            var start = Math.Max(0, from - 1);
            var count = Math.Max(0, Math.Min(lines.Length - start, to - from + 1));

            if (start >= lines.Length)
                return ShellResult.No($"в файле всего {lines.Length} строк");

            content = string.Join('\n', lines.Skip(start).Take(count));
            return ShellResult.Fine(content);
        }

        if (content.Length > MaxCat)
        {
            var head = content[..MaxCat];
            var shown = head.Count(c => c == '\n') + 1;
            var total = content.Count(c => c == '\n') + 1;

            return ShellResult.Fine(
                head + $"\n\n… показано {shown} строк из {total}. Дальше — «cat {target}:{shown}-{shown + 120}» " +
                "или grep по нужному слову.");
        }

        return ShellResult.Fine(content);
    }

    private static (string Path, int From, int To) SplitRange(string raw)
    {
        var colon = raw.LastIndexOf(':');

        if (colon <= 0 || colon == raw.Length - 1)
            return (raw, 0, 0);

        var tail = raw[(colon + 1)..];
        var dash = tail.IndexOf('-');

        if (dash <= 0)
            return int.TryParse(tail, out var single) ? (raw[..colon], single, single) : (raw, 0, 0);

        if (int.TryParse(tail[..dash], out var from) && int.TryParse(tail[(dash + 1)..], out var to) && to >= from)
            return (raw[..colon], from, to);

        return (raw, 0, 0);
    }

    private ShellResult Grep(string needle, string raw)
    {
        if (needle.Length == 0)
            return ShellResult.No("нужно слово: grep насос /wiki_ru");

        var hits = new List<VfsHit>();

        if (raw.Length == 0 || raw == "/")
        {
            foreach (var mount in _vfs.Mounts)
            {
                hits.AddRange(mount.Grep(needle, VfsPath.Root, MaxHits - hits.Count));

                if (hits.Count >= MaxHits)
                    break;
            }
        }
        else
        {
            if (!VfsPath.TryParse(raw, out var path, out var parseError))
                return ShellResult.No(parseError);

            if (!_vfs.TryResolve(path, out var mount, out var relative, out var error))
                return ShellResult.No(error, _vfs.MountPoints());

            hits.AddRange(mount.Grep(needle, relative, MaxHits));
        }

        if (hits.Count == 0)
        {
            // Empty is a success, not a failure: "that word isn't in the reference library" is a
            // complete answer, whereas a rejection would teach the model that searching was a mistake.
            return ShellResult.Fine($"«{needle}» не встречается{(raw.Length > 0 ? " в " + raw : string.Empty)}");
        }

        var sb = new StringBuilder();

        foreach (var hit in hits.Take(MaxHits))
            sb.Append(hit.Path).Append(':').Append(hit.Line).Append(": ").Append(Clip(hit.Text, 160)).Append('\n');

        if (hits.Count >= MaxHits)
            sb.Append($"… совпадений больше {MaxHits} — сузь слово или путь\n");

        return ShellResult.Fine(sb.ToString().TrimEnd());
    }

    private ShellResult Find(string needle)
    {
        if (needle.Length == 0)
            return ShellResult.No("нужен кусок имени: find насос");

        var found = new List<string>();

        foreach (var mount in _vfs.Mounts)
            Collect(mount, VfsPath.Root, "/" + mount.Point, needle, found);

        if (found.Count == 0)
            return ShellResult.Fine($"файлов с «{needle}» в имени нет");

        var sb = new StringBuilder();

        foreach (var path in found.Take(MaxList))
            sb.Append(path).Append('\n');

        if (found.Count > MaxList)
            sb.Append($"… ещё {found.Count - MaxList}\n");

        return ShellResult.Fine(sb.ToString().TrimEnd());
    }

    private void Collect(VfsMount mount, VfsPath relative, string prefix, string key, List<string> found)
    {
        if (found.Count > MaxList)
            return;

        var entries = mount.List(relative, out var error);

        if (error.Length > 0)
            return;

        foreach (var entry in entries)
        {
            var path = prefix + "/" + entry.Name;

            if (entry.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                found.Add(path);

            if (entry.IsDir)
                Collect(mount, relative.Child(entry.Name), path, key, found);
        }
    }

    private ShellResult Mkdir(string raw) => Mutate(raw, (m, p) => m.MakeDir(p));

    private ShellResult Rm(string raw) => Mutate(raw, (m, p) => m.Remove(p));

    private ShellResult Mv(string fromRaw, string toRaw)
    {
        if (fromRaw.Length == 0 || toRaw.Length == 0)
            return ShellResult.No("нужны оба пути: mv /skills/старое /skills/новое");

        if (!VfsPath.TryParse(fromRaw, out var from, out var fromError))
            return ShellResult.No(fromError);

        if (!VfsPath.TryParse(toRaw, out var to, out var toError))
            return ShellResult.No(toError);

        if (!_vfs.TryResolve(from, out var mount, out var relFrom, out var error))
            return ShellResult.No(error, _vfs.MountPoints());

        if (!string.Equals(from.Mount, to.Mount, StringComparison.Ordinal))
            return ShellResult.No("переносить между корневыми папками нельзя: у них разные права и разное устройство");

        var result = mount.Move(relFrom, to.WithoutMount());

        return result.Ok
            ? ShellResult.Fine(result.Message, mutated: true)
            : ShellResult.No(result.Message, result.Hints);
    }

    private ShellResult Mutate(string raw, Func<VfsMount, VfsPath, VfsWrite> act)
    {
        if (raw.Length == 0)
            return ShellResult.No("нужен путь");

        if (!VfsPath.TryParse(raw, out var path, out var parseError))
            return ShellResult.No(parseError);

        if (!_vfs.TryResolve(path, out var mount, out var relative, out var error))
            return ShellResult.No(error, _vfs.MountPoints());

        var result = act(mount, relative);

        return result.Ok
            ? ShellResult.Fine(result.Message, mutated: true)
            : ShellResult.No(result.Message, result.Hints);
    }

    // ---------------------------------------------------------------- formatting

    private static string Format(IReadOnlyList<VfsEntry> entries, string where, bool details)
    {
        if (entries.Count == 0)
            return $"{where} — пусто";

        var width = entries.Max(e => e.Name.Length + (e.IsDir ? 1 : 0));
        var sb = new StringBuilder();

        foreach (var entry in entries.Take(MaxList))
        {
            var name = entry.Name + (entry.IsDir ? "/" : string.Empty);
            sb.Append("  ").Append(name.PadRight(width));

            if (details)
            {
                sb.Append(entry.Modified is { } m ? m.ToString("  dd.MM.yy") : "          ");
                sb.Append(entry.Size.ToString().PadLeft(7));
            }

            if (entry.Desc.Length > 0)
                sb.Append("  ").Append(Clip(entry.Desc, 90));

            sb.Append('\n');
        }

        if (entries.Count > MaxList)
            sb.Append($"… ещё {entries.Count - MaxList} — сузь путь\n");

        return sb.ToString().TrimEnd();
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
