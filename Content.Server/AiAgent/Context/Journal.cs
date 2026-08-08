using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Journalling turned off: nothing reaches disk, but the in-memory ring still fills.
    ///
    /// A fresh instance per call rather than a shared singleton. The ring is per-conversation — it
    /// is what a fold rebuilds the history from — and one static instance would have let two
    /// sessions fold each other's events into their own past.
    /// </summary>
    public static Journal Disabled => new(null, null);

    public Journal(string? logDir, ISawmill? sawmill)
    {
        _dir = logDir;
        _sawmill = sawmill;
    }

    public bool Enabled => _dir != null;

    /// <summary>
    /// The same events, kept in memory as one short line each, for the compaction to fold the
    /// conversation down onto.
    ///
    /// A fold used to retain the last few <em>messages</em>, and messages are where the expensive
    /// things live: one <c>look</c> can be several thousand tokens of crates and debris, and
    /// retaining it carried that weight forward forever. What the agent actually needs after a fold
    /// is what recently happened — heard this, called that, it failed — and that is a line, not a
    /// payload. Held here rather than read back off disk because the fold runs on the agent thread
    /// and must not go near the filesystem, and because journalling can be switched off entirely
    /// while compaction cannot.
    /// </summary>
    private readonly Queue<string> _recent = new();

    private readonly object _recentGate = new();

    /// <summary>
    /// Twice the largest fold anyone would configure. Bounded because this lives for the whole
    /// round and a busy shift writes an event every few seconds.
    /// </summary>
    private const int RecentCapacity = 200;

    /// <summary>The last <paramref name="count"/> events, oldest first.</summary>
    public IReadOnlyList<string> Recent(int count)
    {
        lock (_recentGate)
            return _recent.Skip(Math.Max(0, _recent.Count - count)).ToList();
    }

    /// <summary>
    /// One line per event, or null for the ones not worth carrying.
    ///
    /// Token counts and cache ratios are for the operator reading the log, not for the agent
    /// reading its own past; including them would spend the fold's budget on numbers it cannot act
    /// on. Tool arguments are kept short and tool <em>results</em> are reduced to whether they
    /// worked — the result payload is exactly the weight this whole change exists to shed.
    /// </summary>
    private static string? Line(string kind, IReadOnlyDictionary<string, object?> f)
    {
        object? Get(string k) => f.TryGetValue(k, out var v) ? v : null;

        switch (kind)
        {
            case "obs":
                return Get("text") as string;

            case "tool":
            {
                var call = $"{Get("name")} {Trim(Get("args") as string, 120)}".TrimEnd();
                return Get("ok") is true
                    ? $"→ {call}"
                    : $"→ {call} — отказ {Get("error")}: {Trim(Get("detail") as string, 80)}";
            }

            case "untooled":
                return $"(ответил текстом, не инструментом: {Trim(Get("text") as string, 120)})";

            case "compaction":
                return "— здесь память была свёрнута —";

            default:
                return null;
        }
    }

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public void Write(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        // Before the disk guard, not after: the ring is what compaction depends on, and it has to
        // keep filling on a server with journalling switched off.
        if (Line(kind, fields) is { Length: > 0 } recent)
        {
            lock (_recentGate)
            {
                _recent.Enqueue(recent);
                while (_recent.Count > RecentCapacity)
                    _recent.Dequeue();
            }
        }

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
