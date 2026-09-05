using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Config;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.RogueAi;
using Content.Server.GameTicking.Presets;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>
/// Reconfiguration without a rebuild: the prototype overlay, endpoint probing, and a mode overview.
///
/// <para>
/// Kept in a separate file because this is an operational layer, not a gameplay one: nothing here
/// gets called from the turn loop. Everything in it exists for one question — "why is the server
/// behaving differently from what's in the config" — and answers it BEFORE the round, rather than
/// from the log a day later.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private AiConfigOverlay.OverlayReport? _overlay;

    /// <summary>What the overlay did last time. Null means it has never been read.</summary>
    public AiConfigOverlay.OverlayReport? Overlay => _overlay;

    /// <summary>
    /// Read <c>ai_data/config.d/*.yml</c> on top of the prototypes from <c>Resources/</c>.
    /// </summary>
    /// <param name="live">
    /// A live reload (from the console) versus a startup one. The difference is in how the change
    /// gets reported to systems; see <see cref="AiConfigOverlay.Load"/> for details.
    /// </param>
    /// <remarks>
    /// A live reload changes DATA, not entities that already exist. A round rule set up at start
    /// keeps living with its old fields until the end of the shift — the new values go to the next
    /// round. Model profiles switch over sooner: the client is built on the agent's first turn, and
    /// <c>aiagent release</c> forces it to be built again.
    /// </remarks>
    public AiConfigOverlay.OverlayReport LoadOverlay(bool live)
    {
        _overlay = AiConfigOverlay.Load(DataDir(), _protoMan, live, _sawmill);
        return _overlay;
    }

    // ------------------------------------------------------------------ profiles

    /// <summary>
    /// Gather everything needed to reach a profile: the prototype, the address, and the parameters.
    /// </summary>
    /// <remarks>
    /// Uses the same <c>EndpointFor</c>/<c>SamplingFor</c> as a live turn. Building a separate address
    /// just for the probe would mean testing something other than what's actually in play: half of
    /// config breakage is the proxy, the key, and the dialect — exactly the fields computed here.
    /// </remarks>
    public bool TryProfile(
        string id,
        out AiLlmProfilePrototype profile,
        out LlmEndpoint endpoint,
        out LlmSampling sampling)
    {
        if (!_protoMan.TryIndex(id, out AiLlmProfilePrototype? found))
        {
            profile = default!;
            endpoint = default!;
            sampling = default!;
            return false;
        }

        profile = found;
        endpoint = EndpointFor(found);
        sampling = SamplingFor(found);
        return true;
    }

    /// <summary>All known profiles, alphabetically.</summary>
    public IEnumerable<AiLlmProfilePrototype> Profiles() =>
        _protoMan.EnumeratePrototypes<AiLlmProfilePrototype>().OrderBy(p => p.ID, StringComparer.Ordinal);

    /// <summary>
    /// The chain a new core agent would take right now: the mode's own if it has one, else the shared one.
    /// </summary>
    public IReadOnlyList<string> ActiveChain()
    {
        var raw = StationLlmChain();

        if (string.IsNullOrWhiteSpace(raw))
            raw = _cfg.GetCVar(AiCVars.LlmChain);

        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Where the profile's key came from, in printable form. The value itself is never shown.</summary>
    public string KeyNote(AiLlmProfilePrototype profile)
    {
        if (string.IsNullOrWhiteSpace(profile.KeyFile))
        {
            var inline = _cfg.GetCVar(AiCVars.ApiKey);
            return string.IsNullOrWhiteSpace(inline)
                ? "не задан (ни keyFile, ни ai.api_key) — запросы уйдут без авторизации"
                : $"ai.api_key, {inline.Length} символов";
        }

        var path = System.IO.Path.Combine(DataDir(), profile.KeyFile);

        if (!System.IO.File.Exists(path))
            return $"ФАЙЛА НЕТ: {path}";

        try
        {
            var length = System.IO.File.ReadAllText(path).Trim().Length;
            return length == 0
                ? $"{profile.KeyFile} — файл пустой"
                : $"{profile.KeyFile}, {length} символов";
        }
        catch (Exception e)
        {
            return $"{profile.KeyFile} — не прочитать: {e.Message}";
        }
    }

    // -------------------------------------------------------------------- probe

    private int _probing;

    /// <summary>
    /// Actually reach every profile and report the discrepancies.
    /// </summary>
    /// <param name="write">
    /// Where to write. ALWAYS called on the main thread — the console shell is bound to the admin's
    /// session and cannot be written to from a foreign thread.
    /// </param>
    /// <remarks>
    /// <para>
    /// Doesn't block the main thread, and that's not decoration: a cloud profile responds in seconds,
    /// but its timeout is in minutes. A synchronous check on a server with real players would mean a
    /// frozen tick at exactly the moment an admin is trying to figure out why the AI is silent.
    /// </para>
    /// <para>
    /// Only one probe runs at a time. A second call doesn't queue up — it's refused: these are paid
    /// requests, and two reports interleaved in the same console are unreadable anyway.
    /// </para>
    /// </remarks>
    public bool StartProbe(IReadOnlyList<string> ids, Action<string> write)
    {
        if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0)
            return false;

        var jobs = new List<(AiLlmProfilePrototype, LlmEndpoint, LlmSampling, string)>();
        var missing = new List<string>();

        foreach (var id in ids)
        {
            if (TryProfile(id, out var profile, out var endpoint, out var sampling))
                jobs.Add((profile, endpoint, sampling, KeyNote(profile)));
            else
                missing.Add(id);
        }

        var compactHigh = _cfg.GetCVar(AiCVars.CompactHigh);
        var total = _cfg.GetCVar(AiCVars.LlmTotalTimeout);
        var sawmill = _sawmill;

        // Captured by value before going to the background: cvars cannot be read from a foreign thread.
        Task.Run(async () =>
        {
            try
            {
                foreach (var id in missing)
                    Post(write, $"{id}: такого профиля нет (aiLlmProfile)");

                foreach (var (profile, endpoint, sampling, keyNote) in jobs)
                {
                    List<string> lines;

                    try
                    {
                        lines = await LlmProbe
                            .RunAsync(profile, endpoint, sampling, keyNote, compactHigh, total, sawmill,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        lines = new List<string> { $"{profile.ID}: проверка сорвалась — {e.Message}" };
                    }

                    foreach (var line in lines)
                        Post(write, line);
                }

                Post(write, "проверка закончена");
            }
            finally
            {
                Volatile.Write(ref _probing, 0);
            }
        });

        return true;
    }

    /// <summary>
    /// A report line — dispatched to the main thread, and silently dropped if there's no one left to write to.
    /// </summary>
    /// <remarks>
    /// An admin who closed the console or dropped off the network is a normal outcome of a long
    /// probe, not a reason to flood the log with stack traces. The line isn't lost either way:
    /// <see cref="LlmProbe"/> writes its own log, and the full report stays in the server log.
    /// </remarks>
    private void Post(Action<string> write, string line)
    {
        _sawmill.Info($"проверка эндпоинта: {line}");

        _taskManager.RunOnMainThread(() =>
        {
            try
            {
                write(line);
            }
            catch (Exception)
            {
                // The console dropped off. See the comment above.
            }
        });
    }

    // --------------------------------------------------------------------- modes

    /// <summary>The preset, its AI rule, and what that rule resolved to.</summary>
    public sealed record ModeView(
        string Preset,
        string Rule,
        string Lawset,
        string SoulFile,
        string Chain,
        bool Announces,
        bool EndsRoundOnAiDeath,
        bool GrantDoors,
        bool GrantConsoles,
        bool GrantTurrets,
        IReadOnlyList<string> Borgs,
        IReadOnlyList<string> Beacons);

    /// <summary>
    /// Every preset that has a rule with <see cref="RogueAiRuleComponent"/>.
    /// </summary>
    /// <remarks>
    /// Read from PROTOTYPES, not from a live round, so it answers the question "what would happen if
    /// this mode came up" — the very question that would otherwise mean reading three YAML files in a
    /// row and keeping in your head whatever the overlay might have rewritten in them.
    /// </remarks>
    public List<ModeView> Modes()
    {
        var result = new List<ModeView>();

        foreach (var preset in _protoMan.EnumeratePrototypes<GamePresetPrototype>()
                     .OrderBy(p => p.ID, StringComparer.Ordinal))
        {
            foreach (var ruleId in preset.Rules)
            {
                if (!_protoMan.TryIndex<EntityPrototype>(ruleId, out var rule))
                    continue;

                if (!rule.TryComp<RogueAiRuleComponent>(out var comp, Factory))
                    continue;

                result.Add(new ModeView(
                    preset.ID,
                    ruleId,
                    comp.Lawset.Id,
                    comp.SoulFile,
                    string.IsNullOrWhiteSpace(comp.LlmChain) ? "(общая ai.llm_chain)" : comp.LlmChain,
                    comp.AnnounceOnStart,
                    comp.EndsRoundOnAiDeath,
                    comp.GrantDoors,
                    comp.GrantConsoles,
                    comp.GrantTurrets,
                    comp.SupportBorgs.Select(b => b.Id).ToList(),
                    comp.SupportBorgBeacons.ToList()));
            }
        }

        return result;
    }
}
