using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Tools;

/// <summary>Handler for one tool. Runs on the agent thread; anything touching the world marshals itself.</summary>
public delegate Task<ToolResult> ToolHandler(JsonElement args, CancellationToken ct);

/// <summary>
/// One callable tool: the wire schema plus the code behind it.
///
/// <see cref="SchemaJson"/> is a hand-written canonical JSON string parsed once into a
/// <see cref="JsonNode"/>. Hand-written rather than generated because it lands verbatim in the
/// frozen system prefix — a reflection-generated schema whose property order could shift between
/// runtimes would silently invalidate the whole KV cache.
/// </summary>
public sealed class AiTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string SchemaJson { get; init; }
    public required ToolHandler Handler { get; init; }

    /// <summary>Refused while the curator is reviewing — see the review_mode error code.</summary>
    public bool GameAction { get; init; }

    /// <summary>
    /// A successful call puts words in front of the crew, so the turn counts as having spoken.
    ///
    /// Declared here rather than as a name list inside the loop. The loop's own doc comment says it
    /// cannot so much as name <c>EntityManager</c> — and it knew the signatures of three game tools
    /// by heart, so adding a fourth way of speaking would have silently broken both repeat
    /// suppression and the untooled-prose nudge with nothing to point at.
    /// </summary>
    public bool Speech { get; init; }

    /// <summary>
    /// Pull the spoken text out of the parsed arguments, for repeat suppression.
    /// Only consulted when <see cref="Speech"/> is true. May be handed an undefined element when
    /// the arguments failed to parse, so implementations must tolerate that.
    /// </summary>
    public Func<JsonElement, string?>? SpokenText { get; init; }

    /// <summary>The common case: a required <c>text</c> property.</summary>
    public static string? TextArgument(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty("text", out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>
/// The agent's tool surface.
///
/// Held deliberately small. The mcbot deployment on this box measured it directly: 46 narrow
/// commands drowned this exact model on this exact quant, while ~13 worked. Breadth is achieved
/// by consolidation — one <c>inspect</c> instead of twenty readers, one <c>device_action</c>
/// instead of fifteen verbs — not by adding entries.
/// </summary>
public sealed class AiToolRegistry
{
    private readonly Dictionary<string, AiTool> _tools = new();
    private List<ToolDto>? _wire;
    private string? _wireJson;

    public IReadOnlyCollection<AiTool> Tools => _tools.Values;

    public void Register(AiTool tool)
    {
        _tools[tool.Name] = tool;
        _wire = null;
        _wireJson = null;
    }

    public bool TryGet(string name, out AiTool tool) => _tools.TryGetValue(name, out tool!);

    /// <summary>
    /// The tool array as sent on the wire, built once and cached.
    ///
    /// Sorted by name so the order does not depend on registration order, which could otherwise
    /// change with an innocuous code edit and move the cache divergence point into the prefix.
    /// </summary>
    public IReadOnlyList<ToolDto> WireSchemas()
    {
        if (_wire != null)
            return _wire;

        _wire = _tools.Values
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ToolDto
            {
                Type = "function",
                Function = new ToolFunctionDto
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = JsonNode.Parse(t.SchemaJson),
                },
            })
            .ToList();

        return _wire;
    }

    /// <summary>Serialized tool array, hashed together with the system prompt as the prefix canary.</summary>
    public string WireJson() => _wireJson ??= JsonSerializer.Serialize(WireSchemas(), LlmJson.Options);

    /// <summary>
    /// Names closest to <paramref name="name"/> by edit distance, for a helpful bad_args reply.
    /// Guessing wrong is normal; leaving the model to guess again blindly is what costs turns.
    /// </summary>
    public IReadOnlyList<string> Nearest(string name, int count = 3)
    {
        return _tools.Keys
            .OrderBy(k => Distance(k, name))
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    /// <summary>Plain Levenshtein.</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
