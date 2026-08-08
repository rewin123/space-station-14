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

    /// <summary>
    /// Which round this conversation belongs to.
    ///
    /// Zero on a file written before this field existed; such a snapshot is dropped rather than
    /// guessed at. The id comes from the database, so it survives a server restart and increments
    /// on a new round.
    /// </summary>
    [JsonPropertyName("round_id")]
    public int RoundId { get; set; }

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

    // --- v2: the agent, not just the conversation --------------------------------------------
    //
    // Additive only, and every field's initializer is what the agent starts a session with, so a
    // file written before these existed loads and behaves exactly as it used to. That is why there
    // is no version gate: the snapshot is already dropped whenever the prefix hash or the round
    // changes, which is most restarts, and a second rejection would cost real history for nothing.
    //
    // The rule for whoever comes next: a field may be ADDED with a default. Renaming, retyping or
    // removing one needs a version number and a refusal to load anything below it.

    /// <summary>Turns the loop ran. Distinct from <c>turns</c>, which counts appended user messages.</summary>
    [JsonPropertyName("agent_turns")]
    public int AgentTurns { get; set; }

    [JsonPropertyName("mode")]
    public AgentMode Mode { get; set; } = AgentMode.Core;

    [JsonPropertyName("untooled_replies")]
    public int UntooledReplies { get; set; }

    /// <summary>
    /// What the agent said just before the restart.
    ///
    /// Without it a restored agent repeats into the radio whatever it broadcast thirty seconds
    /// before going down — and repeat suppression exists precisely because this model fills silence.
    /// </summary>
    [JsonPropertyName("recent_speech")]
    public List<string> RecentSpeech { get; set; } = new();
}

/// <summary>
/// Persists the conversation so a server restart does not amnesia the agent mid-round.
///
/// Deliberately <b>not</b> storing the system prompt: it is rebuilt from code and from the agent's
/// own files at startup, and restoring a stale copy would silently pin the agent to an old prompt
/// while everything else moved on. The stored hash is compared instead — a mismatch means the
/// prompt changed under us and the body is dropped rather than replayed against a prefix it was
/// never written for.
///
/// <b>Mid-round</b> is the whole point, and it used to be the one thing this did not do. The hash
/// alone does not discriminate rounds — it is byte-stable across a restart by design — so a
/// snapshot written at the end of one round was restored at the start of the next, and the AI woke
/// up mid-conversation about a shift that no longer existed, naming crew who were not on board.
/// The round id is what makes the guard mean what the paragraph above claims.
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

    public void Save(string id, AgentState state, int roundId)
    {
        try
        {
            Directory.CreateDirectory(_dir);

            var snapshot = state.ToSnapshot(state.Conv.PrefixHash, roundId);
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
    public SessionSnapshot? Load(string id, string currentPrefixHash, int currentRoundId)
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

            if (snapshot.RoundId != currentRoundId)
            {
                _sawmill.Info(
                    $"снапшот сессии отброшен: это другой раунд ({snapshot.RoundId} -> {currentRoundId})");
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
}
