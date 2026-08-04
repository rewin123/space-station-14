using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Server.AiAgent.Llm;

namespace Content.Server.AiAgent.Context;

/// <summary>On-disk shape of a conversation snapshot.</summary>
public sealed class SessionSnapshot
{
    [JsonPropertyName("prefix_hash")]
    public string PrefixHash { get; set; } = string.Empty;

    [JsonPropertyName("turns")]
    public int Turns { get; set; }

    [JsonPropertyName("compactions")]
    public int Compactions { get; set; }

    [JsonPropertyName("chars_per_token")]
    public double CharsPerToken { get; set; } = 3.0;

    [JsonPropertyName("volatile_tail")]
    public string? VolatileTail { get; set; }

    [JsonPropertyName("body")]
    public List<ChatMessageDto> Body { get; set; } = new();
}

/// <summary>
/// Persists the conversation so a server restart does not amnesia the agent mid-round.
///
/// Deliberately <b>not</b> storing the system prompt: it is rebuilt from code and from the agent's
/// own files at startup, and restoring a stale copy would silently pin the agent to an old prompt
/// while everything else moved on. The stored hash is compared instead — a mismatch means the
/// prompt changed under us and the body is dropped rather than replayed against a prefix it was
/// never written for.
/// </summary>
public sealed class SessionStore
{
    private readonly string _dir;
    private readonly ISawmill _sawmill;

    public SessionStore(string dataDir, ISawmill sawmill)
    {
        _dir = Path.Combine(dataDir, "sessions");
        _sawmill = sawmill;
    }

    private string PathFor(string id) => Path.Combine(_dir, $"{id}.json");

    public void Save(string id, ConversationState conv, int compactions)
    {
        try
        {
            Directory.CreateDirectory(_dir);

            var snapshot = new SessionSnapshot
            {
                PrefixHash = conv.PrefixHash,
                Turns = conv.TurnCount,
                Compactions = compactions,
                CharsPerToken = conv.CharsPerToken,
                VolatileTail = conv.VolatileTail,
                Body = new List<ChatMessageDto>(conv.Body),
            };

            var json = JsonSerializer.Serialize(snapshot, LlmJson.Options);

            // Write-then-rename: a crash mid-write must not leave a half-file that fails to parse
            // and takes the agent's whole history with it.
            var tmp = PathFor(id) + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, PathFor(id), overwrite: true);
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не удалось сохранить снапшот сессии: {e.Message}");
        }
    }

    /// <summary>
    /// Restore a body, or null. Returns null — rather than throwing — for every failure mode:
    /// a missing file, a corrupt file, or a prefix that no longer matches.
    /// </summary>
    public SessionSnapshot? Load(string id, string currentPrefixHash)
    {
        var path = PathFor(id);

        try
        {
            if (!File.Exists(path))
                return null;

            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path), LlmJson.Options);
            if (snapshot == null)
                return null;

            if (snapshot.PrefixHash != currentPrefixHash)
            {
                _sawmill.Info(
                    $"снапшот сессии отброшен: префикс изменился ({snapshot.PrefixHash} -> {currentPrefixHash})");
                return null;
            }

            _sawmill.Info($"восстановлена сессия: {snapshot.Body.Count} сообщений, {snapshot.Turns} ходов");
            return snapshot;
        }
        catch (Exception e)
        {
            _sawmill.Warning($"снапшот сессии не читается, начинаю с чистого листа: {e.Message}");
            return null;
        }
    }

    public void Delete(string id)
    {
        try
        {
            if (File.Exists(PathFor(id)))
                File.Delete(PathFor(id));
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не удалось удалить снапшот: {e.Message}");
        }
    }
}
