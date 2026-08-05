using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Context;

/// <summary>
/// One JSON line per notable thing the agent did, in <c>ai_data/logs/events-YYYY-MM-DD.jsonl</c>.
///
/// The sawmill already narrates all of this, but a day of server log is prose interleaved with
/// everything else on the station. Answering "how often did it compact", "which tool refuses most",
/// "did the cache ever drop" over a 24-hour run means grepping and eyeballing, and the answers come
/// out as impressions rather than numbers. A machine-readable line per turn makes them a one-liner
/// in jq — which is the whole point of the acceptance run this file exists for.
///
/// Written from the agent thread, never the main thread, and appended rather than held open: a
/// line every few seconds does not justify a file handle that would have to be closed correctly on
/// a shutdown path that (see <c>AutoSaveSessions</c>) does not reliably run.
/// </summary>
public sealed class Journal
{
    private readonly string? _dir;
    private readonly ISawmill? _sawmill;

    /// <summary>Journalling turned off — every Write is a no-op.</summary>
    public static Journal Disabled { get; } = new(null, null);

    public Journal(string? logDir, ISawmill? sawmill)
    {
        _dir = logDir;
        _sawmill = sawmill;
    }

    public bool Enabled => _dir != null;

    public void Write(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        if (_dir == null)
            return;

        try
        {
            Directory.CreateDirectory(_dir);

            var line = new Dictionary<string, object?>(fields.Count + 2)
            {
                ["ts"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = kind,
            };

            foreach (var (key, value) in fields)
                line[key] = value;

            var path = Path.Combine(_dir, $"events-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(line, LlmJson.Options) + "\n");
        }
        catch (Exception e)
        {
            // A journal that cannot write must never take the turn down with it.
            _sawmill?.Warning($"журнал не пишется: {e.GetType().Name}: {e.Message}");
        }
    }
}
