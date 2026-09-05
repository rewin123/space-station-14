using Content.Server.AiAgent.Components;
using Content.Server.AiAgent.RogueAi;
using Content.Server.GameTicking;
using Content.Server.RoundEnd;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;

namespace Content.Server.AiAgent;

/// <summary>
/// Two round-end conditions that don't exist upstream: an empty server and a killed rogue AI.
/// </summary>
/// <remarks>
/// <para>
/// The third condition — the evacuation shuttle having left — already works and does not need to
/// be added again: <c>EmergencyShuttleSystem.Console.cs</c> sets a timer to
/// <c>_roundEnd.EndRound()</c>. Added here are exactly the two cases where upstream leaves the
/// round hanging forever.
/// </para>
/// <para>
/// <b>Why a separate file rather than inside the mode's rule.</b> The conditions are of different
/// natures: the AI dying only makes sense in rogue AI mode, while an empty server applies to any
/// round, and inside <see cref="RogueAiRuleSystem"/> it would be dead code in an ordinary shift.
/// What they have in common is one thing — both are ours, and both must live outside upstream
/// files.
/// </para>
/// </remarks>
public sealed partial class RoundEndConditionsSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private RoundEndSystem _roundEnd = default!;
    [Dependency] private RogueAiRuleSystem _rogue = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    /// <summary>
    /// How long to wait before restarting after the last player has left.
    /// </summary>
    /// <remarks>
    /// Ten seconds, not the standard thirty, and this is not haste. There is nobody to watch the
    /// round-end summary — by definition there is nobody on the server — and for that whole time the
    /// server sits in PostRound, which is where a joining player would land instead of the lobby, and
    /// wait for who knows what.
    /// </remarks>
    private static readonly TimeSpan EmptyRestartDelay = TimeSpan.FromSeconds(10);

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai.roundend");

        // A subscription to a C# event rather than the entity bus, and it can't be done otherwise:
        // a player disconnecting is an event of the player manager, and the entity may already be
        // gone. GhostRoleSystem and the upstream PathfindingSystem use the same trick.
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    /// <summary>
    /// The last player has left — the round is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why by event rather than a check in Update.</b> Because of
    /// <c>game.auto_pause_empty</c>. When it's on (the engine's default) the tick is skipped
    /// entirely on an empty server: <c>GameLoop.cs</c> does <c>if (_timing.Paused) continue;</c>
    /// BEFORE simulation, meaning systems' <c>Update</c> is never called at all. Polling "how many
    /// players" would never fire — exactly when it's needed. The disconnect event arrives
    /// regardless of pause, so it works for any value of the cvar.
    /// </para>
    /// <para>
    /// <b>Two different outcomes, both correct.</b> With pause enabled, the countdown to restart
    /// runs through <c>Timer.Spawn</c>, and timers stand still while paused: the restart won't
    /// happen right away, but when someone connects and lifts the pause. That is not a flaw — there
    /// is no need to restart the map into emptiness, and a joining player gets a fresh shift via
    /// <see cref="EmptyRestartDelay"/> instead of someone else's finished round. With pause disabled
    /// (as it currently is on the live instance, where pause was lifted for borg development) the
    /// restart happens immediately — and that is needed all the more: without pause, agents keep
    /// walking around a dead station and paying tokens for it.
    /// </para>
    /// </remarks>
    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Disconnected)
            return;

        if (!_cfg.GetCVar(AiCVars.EndRoundWhenEmpty))
            return;

        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        // Counting AFTER the disconnect: the handler is already called with the updated list, but
        // that can't be relied on blindly — the departing session might still be listed. So we
        // exclude it explicitly.
        var left = 0;
        foreach (var session in _players.Sessions)
        {
            if (session != args.Session && session.Status != SessionStatus.Disconnected)
                left++;
        }

        if (left > 0)
            return;

        _sawmill.Info("на сервере не осталось игроков — раунд завершается");
        _roundEnd.EndRound(EmptyRestartDelay);
    }

    /// <summary>
    /// The rogue AI is dead — there's nothing left to play for. Called from
    /// <see cref="StationAiAgentSystem"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>By call rather than its own subscription, and this can't be worked around.</b> The
    /// engine's directed subscription table is global per "component + event" pair:
    /// <c>EntityEventBus.Directed.cs:418</c> does a <c>TryAdd</c> and throws
    /// <c>Duplicate Subscriptions for comp=…, event=…</c> if the pair is already taken — regardless
    /// of which system is subscribing. And <see cref="StationAiAgentSystem"/> is already subscribed
    /// to <c>LlmStationAiComponent</c> + <c>MobStateChangedEvent</c> (<c>StationAiAgentSystem.cs:171</c>),
    /// which releases the body on the AI's death. A second subscription crashes the server at
    /// startup — verified on 20.08.2026, the server did not come up at all.
    /// </para>
    /// <para>
    /// The mode check is mandatory. In an ordinary shift, the AI dying is an incident after which
    /// the crew carries on; in rogue AI mode it's the sole antagonist, and without it a crew with no
    /// clearances simply waits for the shuttle on a dead station. This is exactly the same logic by
    /// which the upstream nuclear operative mode ends together with the operatives.
    /// </para>
    /// </remarks>
    public void OnStationAiDied(EntityUid ai)
    {
        if (!_cfg.GetCVar(AiCVars.EndRoundOnAiDeath))
            return;

        // The rule isn't exclusive to rogue modes: the peaceful one (AiPeacefulRule) uses the same
        // component for the borg and the personality, but the round doesn't end with its death —
        // otherwise anyone who reached the core would get an "end shift" button.
        if (_rogue.ActiveRule is not { EndsRoundOnAiDeath: true })
            return;

        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        _sawmill.Info($"станционный ИИ {ToPrettyString(ai)} убит — раунд завершается");
        _roundEnd.EndRound();
    }
}
