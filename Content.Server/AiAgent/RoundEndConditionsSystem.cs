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
/// Два условия конца раунда, которых нет в апстриме: пустой сервер и убитый злой ИИ.
/// </summary>
/// <remarks>
/// <para>
/// Третье условие — улетевший эвакуационный шаттл — уже работает и заводить его заново не нужно:
/// <c>EmergencyShuttleSystem.Console.cs</c> ставит таймер на <c>_roundEnd.EndRound()</c>. Здесь
/// добавлены ровно те два случая, в которых апстрим оставляет раунд висеть бесконечно.
/// </para>
/// <para>
/// <b>Почему отдельным файлом, а не внутри правила режима.</b> Условия разной природы: гибель ИИ
/// имеет смысл только в режиме злого ИИ, а пустой сервер — в любом раунде, и в
/// <see cref="RogueAiRuleSystem"/> оно было бы мёртвым кодом в обычную смену. Общее у них одно —
/// оба наши, и оба обязаны жить вне апстримовых файлов.
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
    /// Сколько ждать перед рестартом после того, как ушёл последний игрок.
    /// </summary>
    /// <remarks>
    /// Десять секунд, а не штатные тридцать, и это не спешка. Смотреть итоги раунда некому — на
    /// сервере никого нет по условию, — а всё это время сервер стоит в PostRound, куда зашедший
    /// игрок попадёт вместо лобби и будет ждать неизвестно чего.
    /// </remarks>
    private static readonly TimeSpan EmptyRestartDelay = TimeSpan.FromSeconds(10);

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai.roundend");

        // Подписка на C#-событие, а не на шину сущностей, и по-другому нельзя: отключение игрока —
        // это событие менеджера игроков, сущности у него может уже не быть. Тем же приёмом
        // пользуются GhostRoleSystem и апстримовый PathfindingSystem.
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    /// <summary>
    /// Ушёл последний игрок — раунд закончен.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему по событию, а не проверкой в Update.</b> Из-за <c>game.auto_pause_empty</c>. При
    /// включённом (а это умолчание движка) на пустом сервере тик пропускается целиком:
    /// <c>GameLoop.cs</c> делает <c>if (_timing.Paused) continue;</c> ДО симуляции, то есть
    /// <c>Update</c> у систем не вызывается вовсе. Опрос «сколько игроков» не сработал бы ни разу —
    /// ровно тогда, когда он нужен. Событие отключения приходит независимо от паузы, поэтому
    /// работает при любом значении cvar'а.
    /// </para>
    /// <para>
    /// <b>Два разных исхода, оба верные.</b> При включённой паузе отсчёт до рестарта идёт через
    /// <c>Timer.Spawn</c>, а таймеры на паузе стоят: рестарт случится не сразу, а когда кто-нибудь
    /// подключится и снимет паузу. Это не изъян — перезапускать карту в пустоту незачем, а
    /// зашедший получит новую смену через <see cref="EmptyRestartDelay"/> вместо чужого
    /// доигранного раунда. При выключенной (как сейчас на боевом инстансе, где паузу сняли под
    /// разработку борга) рестарт пройдёт сразу — и это тем более нужно: без паузы агенты
    /// продолжают ходить по мёртвой станции и платить за это токенами.
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

        // Считаем ПОСЛЕ отключения: обработчик зовётся уже с обновлённым списком, но полагаться на
        // это вслепую нельзя — сессия уходящего может ещё числиться. Поэтому исключаем её явно.
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
    /// Злой ИИ убит — играть больше не во что. Зовётся из <see cref="StationAiAgentSystem"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Вызовом, а не своей подпиской, и обойти это нельзя.</b> Таблица направленных подписок в
    /// движке глобальна по паре «компонент + событие»:
    /// <c>EntityEventBus.Directed.cs:418</c> делает <c>TryAdd</c> и бросает
    /// <c>Duplicate Subscriptions for comp=…, event=…</c>, если пара уже занята — независимо от
    /// того, какая система подписывается. А на <c>LlmStationAiComponent</c> +
    /// <c>MobStateChangedEvent</c> уже подписан <see cref="StationAiAgentSystem"/>
    /// (<c>StationAiAgentSystem.cs:171</c>), который на смерть ИИ освобождает тело. Вторая подписка
    /// роняет сервер на старте — проверено 20.08.2026, сервер не поднялся вовсе.
    /// </para>
    /// <para>
    /// Проверка на режим обязательна. В обычную смену гибель ИИ — это происшествие, после которого
    /// экипаж живёт дальше; в режиме злого ИИ он единственный антагонист, и без него экипаж без
    /// допусков просто ждёт шаттла на мёртвой станции. Это ровно та же логика, по которой
    /// апстримовый режим ядерной операции заканчивается вместе с оперативниками.
    /// </para>
    /// </remarks>
    public void OnStationAiDied(EntityUid ai)
    {
        if (!_cfg.GetCVar(AiCVars.EndRoundOnAiDeath))
            return;

        if (_rogue.ActiveRule == null)
            return;

        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        _sawmill.Info($"станционный ИИ {ToPrettyString(ai)} убит — раунд завершается");
        _roundEnd.EndRound();
    }
}
