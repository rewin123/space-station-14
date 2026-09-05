using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Content.Server.AiAgent.Vfs;

/// <summary>
/// Parsing and normalizing a path. The one place where a string from the model becomes a path.
///
/// <para>
/// For notes about people, directory traversal is inexpressible by construction: <c>PlayerNoteStore</c>
/// assembles the file name from a character whitelist, so no matter what the model sends, dots and
/// slashes never reach the disk. That trick doesn't work for the filesystem — here the path IS the
/// argument — so the check lives as a separate type instead of being scattered across commands: a
/// check missed in one command looks exactly like a working one.
/// </para>
/// <para>
/// A path here is ordinary, as in any filesystem: letter for letter, with the extension, case
/// sensitive. No lowercasing, no space substitution — the model copies names from <c>ls</c> rather
/// than making them up, and "smart" normalization would only diverge from what's actually on disk.
/// The one concession doesn't live here but in search: a name without <c>.md</c> is searched for
/// with it too.
/// </para>
/// </summary>
public sealed class VfsPath
{
    /// <summary>File name carrying a folder's metadata: the description in the "when:" line, the body a section overview.</summary>
    public const string IndexFile = "_index";

    /// <summary>Extension on disk. Optional in paths: "pumps" and "pumps.md" are the same thing.</summary>
    public const string Extension = ".md";

    private readonly string[] _segments;

    private VfsPath(string[] segments)
    {
        _segments = segments;
    }

    public static VfsPath Root { get; } = new(System.Array.Empty<string>());

    public int Count => _segments.Length;
    public bool IsRoot => _segments.Length == 0;

    /// <summary>The first segment is the mount point, or an empty string for the root.</summary>
    public string Mount => _segments.Length == 0 ? string.Empty : _segments[0];

    public IReadOnlyList<string> Segments => _segments;

    /// <summary>The last segment — the file or folder name.</summary>
    public string Name => _segments.Length == 0 ? "/" : _segments[^1];

    /// <summary>The path within the mount, without the first segment.</summary>
    public string Relative => string.Join('/', _segments.Skip(1));

    public override string ToString() => "/" + string.Join('/', _segments);

    /// <summary>
    /// Parse a path. <paramref name="error"/> explains a rejection in words the model sees.
    ///
    /// Relative paths are deliberately unsupported: the agent has no current directory, so "pumps"
    /// without a leading slash isn't a path — it's a hope. Rejecting it is clearer than guessing.
    /// </summary>
    public static bool TryParse(string? raw, out VfsPath path, out string error)
    {
        path = Root;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "пустой путь";
            return false;
        }

        var text = raw.Trim().Replace('\\', '/');

        if (text[0] != '/')
        {
            error = $"путь должен начинаться со слэша: «/{text.TrimStart('/')}», а не «{text}»";
            return false;
        }

        var parts = new List<string>();

        foreach (var rawSegment in text.Split('/'))
        {
            var segment = rawSegment.Trim();

            if (segment.Length == 0)
                continue;

            // "." and ".." are cut off here and only here. Downstream, the path is already
            // assembled from validated segments, and there's nothing left to glue into an escape
            // above the root.
            if (segment == "." || segment == "..")
            {
                error = "«.» и «..» в путях не бывает: путь всегда полный, от корня";
                return false;
            }

            if (segment.Any(c => c is '\0' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            {
                error = $"недопустимые символы в «{segment}»";
                return false;
            }

            parts.Add(segment);
        }

        if (parts.Count == 0)
        {
            path = Root;
            return true;
        }

        path = new VfsPath(parts.ToArray());
        return true;
    }

    /// <summary>
    /// Name with the extension: <c>"pumps"</c> → <c>"pumps.md"</c>; one that's already complete is left as is.
    ///
    /// <para>
    /// The one concession in all of addressing. The extension is a storage detail, not part of an
    /// article's name, and forcing the model to append it to every path would mean catching
    /// mistakes where there aren't any. The reverse operation (stripping the extension) doesn't
    /// exist on purpose: the file name on disk and the name in the path must match, or else
    /// <c>ls</c> shows one thing while a different one works.
    /// </para>
    /// </summary>
    public static string WithExtension(string name) =>
        name.EndsWith(Extension, System.StringComparison.Ordinal) ? name : name + Extension;

    /// <summary>Path without the first segment — what the mount itself sees.</summary>
    public VfsPath WithoutMount() =>
        _segments.Length <= 1 ? Root : new VfsPath(_segments[1..]);

    public VfsPath Child(string name) =>
        new(_segments.Append(name.Trim()).ToArray());

    public VfsPath Parent() =>
        _segments.Length == 0 ? Root : new VfsPath(_segments[..^1]);
}
