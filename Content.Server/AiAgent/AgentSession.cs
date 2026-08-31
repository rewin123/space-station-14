using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Threading;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Turn;

namespace Content.Server.AiAgent;

public enum AgentMode : byte
{
    /// <summary>In the core: full tool surface.</summary>
    Core,

    /// <summary>Ejected into an intellicard: still hears and speaks on Binary, but cannot touch devices.</summary>
    Carded,

    /// <summary>The curator is reviewing; game-acting tools refuse with review_mode.</summary>
    Review,
}

/// <summary>Knobs the loop reads each turn, so a live <c>cvar</c> change takes effect without a restart.</summary>
public sealed class AgentLoopOptions
{
    public required Func<float> TickSeconds { get; init; }
    public required Func<float> TickSecondsIdle { get; init; }
    public required Func<int> MaxToolCallsPerTurn { get; init; }
    public required Func<int> MaxConsecutiveFailures { get; init; }
}

/// <summary>
/// One LLM-driven Station AI: its conversation, its perception queue, and the background loop
/// that ties them together.
///
/// The loop runs on the thread pool via <see cref="Task.Run(Func{Task})"/> rather than
/// <c>Task.Factory.StartNew</c>. That matters: <c>TaskManager.Initialize</c> installs a
/// <c>RobustSynchronizationContext</c> on the game thread, and starting the loop there would make
/// every <c>await</c> resume on the game thread. On the pool, <c>SynchronizationContext.Current</c>
/// is null, so continuations stay off the tick — and every await carries
/// <c>ConfigureAwait(false)</c> as belt and braces.
///
/// Nothing in this class dereferences the entity world. Its only door to the game is the set of
/// delegates handed in by <see cref="StationAiAgentSystem"/>, each of which marshals itself onto
/// the main thread. If you cannot name <c>EntityManager</c>, you cannot touch it off-thread.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly ILlmClient _llm;
    private readonly AiToolRegistry _registry;

    private readonly ISawmill _sawmill;
    private readonly AgentLoopOptions _options;
    private readonly Func<bool, CancellationToken, Task<TurnPerception?>> _buildObservation;
    private readonly Func<string, Task> _announce;
    private readonly Func<string, string?, Task<bool>> _speak;
    /// <summary>
    /// Разбор отрезка. Возвращает короткий отчёт, если что-то записал, и <c>null</c>, если нет.
    ///
    /// Возвращаемое значение появилось вместе с отчётом в диалоге: раньше вердикт уходил только в
    /// лог, и агент не знал, что вообще что-то записал, — разбор шёл на копии и исчезал вместе с ней.
    /// </summary>
    private readonly Func<Task<string?>>? _curate;
    private readonly Func<(string SystemPrompt, string ToolsJson)> _rebuildPrefix;

    private readonly TurnRunner _turn;

    /// <summary>The debug bus, or null when it is off. The loop uses it only for the stats sample.</summary>
    private readonly IAgentEventSink? _sink;

    /// <summary>Как часто пробовать после того, как отказы перешли порог.</summary>
    private const int DegradedRetryMs = 60_000;

    /// <summary>Сказать про разреженный режим один раз, а не на каждом отказе.</summary>
    private bool _notedDegraded;

    /// <summary>How the last turn ended, for diagnostics and for tests that assert on the shape.</summary>
    public TurnContext? LastTurn { get; private set; }

    /// <summary>Context compaction, wired in phase 3.</summary>
    public Compactor Compactor { get; }

    /// <summary>Prefix-cache watchdog. A broken cache is silent; this is what makes it loud.</summary>
    public CacheMetrics Cache { get; }

    /// <summary>
    /// Machine-readable event log for the acceptance run. <see cref="Journal.Disabled"/> when off.
    ///
    /// A constructor parameter, deliberately, and not an <c>init</c> property: the constructor hands
    /// this to <see cref="TurnRunner"/>, and an object initializer runs <em>after</em> the constructor.
    /// As an <c>init</c> property it read <see cref="Journal.Disabled"/> every time, so the four
    /// per-turn event kinds never reached disk while the compaction event — the only one written
    /// through this property at call time — did. A day of acceptance log said "1 compaction" and
    /// nothing else, and nothing anywhere reported an error.
    /// </summary>
    public Journal Journal { get; }

    /// <summary>
    /// The model server's real context window, asked for once when the loop starts.
    ///
    /// Zero until it answers, or if it cannot. Compaction thresholds are clamped against it, so a
    /// server reconfigured to a smaller window does not let the agent sail past it into bare HTTP
    /// errors with nothing to say why.
    /// </summary>
    public int ContextLimit { get; private set; }

    /// <summary>Тело, в котором живёт агент, — единственная дверь ядра к игровому миру.</summary>
    public AgentBody Body { get; }

    /// <summary>Сущность тела. Оставлено свойством, чтобы не переписывать полсотни мест обращения.</summary>
    public EntityUid Brain => Body.Owner;

    /// <summary>Everything mutable about this agent. See <see cref="AgentState"/> for why.</summary>
    public AgentState State { get; } = new();

    // Forwarders. The console command, the SELF line, the speech tools and the benchmarks all read
    // these, and keeping them here is what lets the state move without touching any of that.
    public ConversationState Conv => State.Conv;
    /// <summary>The live tool registry — benchmarks invoke through it, never around it.</summary>
    public AiToolRegistry Registry => _registry;

    /// <summary>
    /// The one door every tool call goes through: the loop's, the curator's and the test harness's.
    /// </summary>
    public ToolDispatcher Dispatcher { get; }

    public ObservationQueue Queue { get; }

    /// <summary>
    /// Фоновые скрипты этого агента — <c>null</c>, пока режим скрипта не включён.
    ///
    /// Живёт на сессии, а не в системе, ровно по той же причине, что и всё остальное здесь:
    /// процессы принадлежат агенту и обязаны умереть вместе с ним. Мира эта таблица не касается —
    /// её процессы ходят в мир тем же диспетчером, что и обычный ход.
    /// </summary>
    public Core.Scripting.ScriptProcessTable? Scripts { get; set; }

    /// <summary>Handle registry — per session, so names never leak between rounds.</summary>
    public Handles.EntityHandleRegistry Handles { get; } = new();

    /// <summary>
    /// Кому за эту смену уже напоминали, что на него есть заметка.
    ///
    /// Живёт на сессии, а не в <see cref="AgentState"/>, намеренно: <c>AgentState</c> уезжает в
    /// снапшот и восстанавливается, и поле здесь поменяло бы схему снапшота ради того, чтобы после
    /// рестарта посреди раунда не подсказать во второй раз. Одна лишняя строка дешевле схемы.
    ///
    /// Сброса не требуется: сессия умирает вместе с раундом (<c>OnRoundCleanup</c> зовёт
    /// <c>ReleaseAll</c>), а вместе с ней и это множество. Комментарий здесь именно затем, чтобы
    /// никто не добавил «забытую» очистку.
    ///
    /// Читается и пишется с главного потока, из обработчиков речи; лок — на случай, если однажды
    /// это перестанет быть правдой.
    /// </summary>
    private readonly HashSet<string> _notedPeople = new(StringComparer.Ordinal);

    /// <summary>
    /// Первая ли это реплика этого человека за смену. Дальше вызывающий решает, есть ли о чём
    /// напоминать.
    ///
    /// Имя запоминается и тогда, когда заметки нет: иначе безымянный болтун стоил бы обращения к
    /// локу хранилища на каждую свою реплику, а так — одно за смену.
    /// </summary>
    public bool FirstUtteranceOf(string speaker)
    {
        lock (_notedPeople)
            return _notedPeople.Add(speaker);
    }

    public string? LastLawsDigest
    {
        get => State.LastLawsDigest;
        set => State.LastLawsDigest = value;
    }

    /// <summary>Turns that ended in prose and had to be delivered mechanically. Should stay near zero.</summary>
    public int UntooledReplies => State.UntooledReplies;

    /// <summary>
    /// Somebody asked for a review out of band (the <c>aiagent curate</c> console command).
    ///
    /// A flag the loop picks up at a turn boundary, never a second thread. The curator walks the
    /// same message list the loop appends to, so there can only ever be one owner of it, and that
    /// owner is the loop.
    /// </summary>
    public volatile bool CurateRequested;

    /// <summary>
    /// Text an operator injected through the debug API, for the next turn. Same rule as
    /// <see cref="CurateRequested"/>: asked for from outside, applied by the loop.
    /// </summary>
    public AgentInbox Inbox { get; } = new();

    /// <summary>
    /// Released whenever something the agent should look at arrives — a radio line, speech, an
    /// announcement, an operator's message.
    ///
    /// Capacity one on purpose. A burst of chatter should start exactly one turn, and that turn's
    /// observation carries every line of it; releasing per line would queue up turns describing a
    /// conversation that has already moved on. The count also survives a turn: something that lands
    /// while the model is working is waited on for zero milliseconds afterwards, not slept past.
    /// </summary>
    public SemaphoreSlim Woken { get; } = new(0, 1);

    /// <summary>
    /// Wake the loop. Safe to call from any thread and as often as anything likes — a signal that
    /// is already pending is simply left pending.
    /// </summary>
    public void Wake()
    {
        try
        {
            if (Woken.CurrentCount == 0)
                Woken.Release();
        }
        catch (SemaphoreFullException)
        {
            // Two perception handlers released at once. The loop is awake either way, which is the
            // entire point of the call.
        }
        catch (ObjectDisposedException)
        {
            // The session is going away mid-round. Nothing left to wake.
        }
    }

    public CancellationTokenSource Cts { get; } = new();
    public Task Loop { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Записать снимок диалога на диск. Вешается после конструктора — как <c>Arrived</c> у очереди
    /// наблюдений, и по той же причине: делегат замыкается на систему, которой в момент постройки
    /// сессии ещё нечего о ней сказать.
    ///
    /// Зовётся ТОЛЬКО из петли, то есть с потока агента. Всё, что ему нужно, — хранилище (свои
    /// файлы), идентификатор (константа) и номер раунда (снятый на главном потоке
    /// <c>volatile int</c>). Ни одного обращения к миру, иначе это пришлось бы маршалить и вся
    /// затея потеряла бы смысл.
    /// </summary>
    public Action? Persist { get; set; }

    /// <summary>Когда снимок последний раз лёг на диск. Читает <c>Release</c>, чтобы решить, нужен ли аварийный.</summary>
    public DateTime LastPersistedUtc { get; private set; } = DateTime.MinValue;

    private void SaveSnapshot()
    {
        if (Persist == null)
            return;

        try
        {
            Persist();
            LastPersistedUtc = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            // Не роняем ход из-за диска. Молча тоже нельзя: «агент забыл смену» — это то, что
            // замечают через сутки и объясняют чем угодно, кроме несохранённого файла.
            _sawmill.Warning($"снапшот не сохранён: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Bumped by the owning system on every lifecycle change; marshalled calls check it.</summary>
    public int Generation;

    public AgentMode Mode
    {
        get => State.Mode;
        set => State.Mode = value;
    }

    // Diagnostics surfaced by `aiagent status`.
    public int Turns => State.Turns;
    public int ConsecutiveFailures => State.ConsecutiveFailures;
    public double LastCacheRatio { get; private set; }
    public string? LastError { get; private set; }

    public AgentSession(
        AgentBody body,
        ILlmClient llm,
        AiToolRegistry registry,
        ObservationQueue queue,
        AgentLoopOptions options,
        Func<bool, CancellationToken, Task<TurnPerception?>> buildObservation,
        Func<string, Task> announce,
        Func<string, string?, Task<bool>> speak,
        Func<Task<string?>>? curate,
        Func<(string SystemPrompt, string ToolsJson)> rebuildPrefix,
        CompactionOptions compaction,
        Journal journal,
        IAgentEventSink? sink,
        ISawmill sawmill)
    {
        Body = body;
        Journal = journal;
        _llm = llm;
        _registry = registry;
        Queue = queue;
        _options = options;
        _buildObservation = buildObservation;
        _announce = announce;
        _speak = speak;
        _curate = curate;
        _rebuildPrefix = rebuildPrefix;
        _sawmill = sawmill;

        Cache = new CacheMetrics(sawmill);
        Compactor = new Compactor(llm, compaction, Cache, sawmill, Journal);
        Dispatcher = new ToolDispatcher(registry, sawmill);
        _turn = new TurnRunner(llm, registry, Dispatcher, queue, State, Cache, Journal, speak, sawmill);

        // Here rather than in the field initializer: the conversation is built before this
        // constructor body runs (AgentState's field initializer builds it), so it cannot take the
        // sink as a constructor parameter the way every other collaborator here does. Attaching
        // before Start() means the loop's very first append is already reported.
        _sink = sink;
        if (sink != null)
            Conv.AttachSink(sink);
    }

    public void Start()
    {
        Loop = Task.Run(() => RunAsync(Cts.Token), Cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _sawmill.Info($"agent loop started for brain {Brain}");

        await DiscoverContextLimitAsync(ct).ConfigureAwait(false);

        var idleStreak = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var idle = idleStreak > 2;
                var wait = idle ? _options.TickSecondsIdle() : _options.TickSeconds();

                // A ceiling on the sleep, not a period.
                //
                // Anything pushed into the observation queue releases this, so being spoken to
                // starts a turn now rather than whenever the timer happened to land. Polling alone
                // made response time a coin flip across the whole interval, and the crew feels that
                // precisely when waiting is least acceptable: on a shout about a fire.
                //
                // A signal that arrived while the previous turn was still running is still sitting
                // in the semaphore, so this returns immediately and nothing is missed. Several lines
                // in the same instant collapse into one wake, and the observation carries them all —
                // which is the batching the old delay provided, without the latency it charged for
                // it.
                await Woken.WaitAsync(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

                // Claimed HERE, at the top of the body, and not at the end of the turn where
                // CurateRequested is picked up.
                //
                // On an idle station _buildObservation returns null and the loop `continue`s
                // without running a turn at all; force only kicks in after six such ticks, which
                // at tick_seconds_idle = 25 is up to 150 seconds. An operator's message sitting
                // there for two and a half minutes is not a debugger. Claiming here — and forcing
                // on it — means the very next tick carries it.
                //
                // It also has to be here for correctness: the previous turn's
                // finally { Conv.CloseTurn(); } has already run by this point, so nothing can land
                // between an assistant's tool_calls and their results.
                // Peeked, not claimed.
                //
                // Claiming here and building the observation afterwards lost the message outright
                // whenever the build returned null, and two of the three ways it can do that —
                // ai.enabled switched off, and a world paused because the last player left — do not
                // look at `force` at all. The text was already out of the inbox by then, so an
                // operator's message sent in either of those windows went nowhere and nothing
                // anywhere reported it. Observed live: a message typed into the debugger never
                // reached the agent.
                //
                // Only this loop ever claims, so nothing can take it in between; and a message that
                // arrives during the build is simply picked up by the Claim below.
                // A turn cut off by the step budget is unfinished business, not a decision.
                //
                // The model was mid-plan — the observed case was move_camera as the last allowed
                // call, so the eye was aimed and nothing was ever looked at or said — and the loop
                // then went back to waiting for a new observation. On a quiet station there is no
                // new observation, so the agent simply stopped, with the crew watching it do
                // nothing after being asked something. Forcing the next tick lets it carry on from
                // where it was cut, which is what a player would do.
                var unfinished = LastTurn?.Exit == TurnExit.BudgetExhausted;

                var force = idleStreak >= 6 || Inbox.HasPending || unfinished;
                var perception = await _buildObservation(force, ct).ConfigureAwait(false);

                if (perception == null)
                {
                    idleStreak++;
                    continue;
                }

                var pending = Inbox.Claim();

                // Merged into the one observation rather than appended as a second user message:
                // two adjacent user messages fabricate a turn boundary that TurnBoundaries() will
                // happily cut at, and strict providers reject the alternation outright.
                if (pending != null)
                {
                    perception = perception with
                    {
                        Text = pending + "\n\n" + perception.Text,
                        Forced = true,
                    };
                }

                idleStreak = 0;
                await RunTurnAsync(perception, ct).ConfigureAwait(false);

                // Ход, закрытый noop, — тоже простой.
                //
                // Модель прямо сказала, что вмешиваться не нужно; продолжать опрашивать её в полном
                // темпе значит платить за тот же ответ каждые несколько секунд. Считаем такой ход
                // наравне с тиком, на котором вообще нечего было наблюдать, — после трёх подряд
                // петля сама переходит на tick_seconds_idle.
                //
                // На force это не влияет вредно: пока экипаж говорит, наблюдение непустое и
                // строится независимо от idleStreak.
                if (LastTurn?.Exit == TurnExit.Idled)
                    idleStreak++;

                if (_notedDegraded)
                {
                    _notedDegraded = false;
                    _sawmill.Info($"агент вернулся в строй после {ConsecutiveFailures} отказов");
                }

                State.ConsecutiveFailures = 0;
                LastError = null;

                // Снимок пишется ЗДЕСЬ, в потоке агента, а не раз в минуту из Update.
                //
                // Раньше это делал главный поток: `Conv.Snapshot()` под локом, который в тот же
                // момент держал агент, затем сериализация тела (при 83к токенов это сотни
                // килобайт JSON) и блокирующая запись файла — всё внутри тика. Здесь ничего этого
                // в кадре нет: тело принадлежит петле, сериализация в этом же потоке уже
                // происходит на каждый запрос к модели, а лок никем не оспаривается.
                //
                // После каждого хода, а не раз в минуту: цена упала настолько, что экономить
                // больше не на чем, а потеря при аварии сократилась с минуты до одного хода.
                SaveSnapshot();
            }
            // ТОЛЬКО наша отмена, и это `when` — не украшение.
            //
            // `HttpClient.Timeout` бросает `TaskCanceledException`, а он НАСЛЕДУЕТ
            // `OperationCanceledException`. Без фильтра один запрос, упёршийся в
            // `ai.request_timeout` (180с), выходил сюда и убивал петлю до конца раунда — мимо
            // всего разреженного режима ниже, написанного ровно на этот случай. В логе при этом
            // не оставалось ни одной ошибки: `LastError` = "cancelled", как при штатном
            // закарживании. Экипаж читал это как «ИИ закардили» и шёл искать его к ядру.
            //
            // Насколько близко к краю: замеренный максимум хода в бою 16.08 — 163.0с при
            // потолке 180. Запас был семнадцать секунд, и в раунде 72 его не хватило.
            //
            // Теперь таймаут модели — обычный отказ: он идёт в общий обработчик, наращивает
            // ConsecutiveFailures и уходит на backoff, как любая другая недоступность провайдера.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LastError = "cancelled";
                break;
            }
            catch (StaleGenerationException e)
            {
                // The AI was carded, killed, or the round restarted mid-call. Not an error — but
                // it must still be visible, because a generation mismatch caused by a bug looks
                // exactly like a legitimate one: the loop just stops, quietly, doing nothing.
                LastError = e.Message;
                _sawmill.Warning($"agent loop exiting: {e.Message}");
                break;
            }
            catch (Exception e)
            {
                State.ConsecutiveFailures++;
                LastError = $"{e.GetType().Name}: {e.Message}";
                _sawmill.Error($"agent turn failed ({ConsecutiveFailures}): {LastError}");

                // Порог отказов больше НЕ убивает петлю.
                //
                // Раньше здесь стоял `break`, и это означало, что три-пять минут недоступности
                // модели выключают ИИ до конца раунда. Вернуть его было нечем: watchdog'а нет, а
                // ядро оставалось занято. В игре при этом не появлялось ни одного признака —
                // экипаж читал молчание как «ИИ закардили» и шёл искать его к ядру.
                //
                // Теперь порог лишь переводит агента в разреженный режим: он продолжает
                // пробовать раз в минуту и возвращается сам, когда провайдер оживёт. Спиннинга
                // ядра, ради которого стоял `break`, при таком интервале нет.
                var degraded = ConsecutiveFailures >= _options.MaxConsecutiveFailures();

                if (degraded && !_notedDegraded)
                {
                    _notedDegraded = true;
                    _sawmill.Error(
                        $"агент в разреженном режиме после {ConsecutiveFailures} отказов подряд, " +
                        $"продолжит пробовать раз в {DegradedRetryMs / 1000}с; последняя ошибка: {LastError}");
                }

                // Exponential back-off, capped. A dead endpoint must not spin a core all round.
                var backoff = degraded
                    ? DegradedRetryMs
                    : Math.Min(30_000, 1000 * (int)Math.Pow(2, Math.Min(ConsecutiveFailures, 5)));
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _sawmill.Info($"agent loop ended for brain {Brain} after {Turns} turns (reason: {LastError ?? "cancelled"})");
    }

    /// <summary>
    /// Ask the model server how big its context actually is.
    ///
    /// Until this ran, <c>ai.compact_high</c> was a guessed constant checked against nothing:
    /// reconfigure llama-server to a smaller window and the agent would grow happily past it and
    /// start collecting bare HTTP errors with no hint of the cause anywhere.
    /// </summary>
    private async Task DiscoverContextLimitAsync(CancellationToken ct)
    {
        try
        {
            ContextLimit = await _llm.GetContextSizeAsync(ct).ConfigureAwait(false) ?? 0;

            if (ContextLimit > 0)
                _sawmill.Info($"окно контекста модели: {ContextLimit}т");
            else
                _sawmill.Warning("сервер модели не сообщил n_ctx — пороги компакции сверять не с чем");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _sawmill.Warning($"не удалось прочитать n_ctx: {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task RunTurnAsync(TurnPerception perception, CancellationToken ct)
    {
        Conv.AppendUser(perception.Text);
        State.Turns++;

        // The turn's input, verbatim. It carries the SELF line — where the eye is, whether the core
        // has power — which is the first thing anyone asks when the agent behaves oddly, and until
        // now the only copy of it lived inside the request nobody could see.
        _sawmill.Debug($"turn {Turns} <- {Trim(perception.Text, 400)}");

        // The turn closes on the way out, whatever the way out is.
        //
        // CloseTurn used to sit on the happy path only, so a cancellation inside the tool-result
        // loop — shutdown, carding, death, any of which arrive mid-turn — left the body ending in
        // `assistant{tool_calls:[1,2,3]}, tool(1)`. That is a protocol error the server rejects
        // wholesale, not per message. It survived only because Release → Save → Repair-on-load
        // happened to paper over it on the one path that was taken; nothing made it true.
        try
        {
            var outcome = await _turn.RunAsync(perception, _options.MaxToolCallsPerTurn(), ct)
                .ConfigureAwait(false);

            LastCacheRatio = outcome.LastCacheRatio;
            LastTurn = outcome;
        }
        finally
        {
            // Any call left dangling — by the step budget or by an exception — gets a synthetic
            // result, or the next request is rejected for having an assistant tool_calls with no
            // matching tool message.
            Conv.CloseTurn();

            // One statistics sample per turn, from the one place a turn always passes through
            // however it ended. Counters are not diffed individually: they are `++` on
            // auto-properties across four files, and six publishing setters would be six new
            // chances to forget, feeding a stream nobody reads as a delta.
            _sink?.Stats(AgentDebugState.Stats(this));
        }

        // Zone 2 is consumed by the turn that sent it.
        //
        // The compaction note was set and never cleared, so it rode every subsequent request for the
        // rest of the round. Because it always sits LAST, after the body, each new observation
        // pushes it along — which means the prompt diverges from the previous one at the note's
        // position and the server re-computes it, plus everything after, every single turn. A
        // permanent cache tax of the note's own length, silently paid by the one field designed to
        // be temporary.
        Conv.VolatileTail = null;

        // Compaction sits here, at a turn boundary, precisely because that is the only place the
        // body may be cut without orphaning a tool result from its parent call.
        var compacted = false;

        if (Compactor.ShouldCompact(State))
        {
            Mode = AgentMode.Review;
            try
            {
                var hooks = new CompactionHooks
                {
                    Announce = _announce,
                    RebuildPrefix = _rebuildPrefix,
                    Curate = _curate,
                };

                compacted = await Compactor
                    .CompactAsync(State, _registry.WireSchemas(), hooks, perception.RoundStamp, ct)
                    .ConfigureAwait(false);

                if (compacted)
                {
                    Journal.Write("compaction", new Dictionary<string, object?>
                    {
                        ["turn"] = Turns,
                        ["n"] = State.Compactions,
                        ["messages_after"] = Conv.Body.Count,
                        ["prefix_hash"] = Conv.PrefixHash,
                        ["summary_chars"] = State.LastSummary?.Length ?? 0,
                    });
                }
            }
            finally
            {
                Mode = State.ModeBeforeReview;
            }
        }

        // A review asked for from the console, honoured here rather than on its own thread. Skipped
        // when the ritual just ran one anyway — step 1 of a compaction IS the review.
        if (CurateRequested)
        {
            CurateRequested = false;

            if (!compacted)
                await RunReviewAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run the curator over the live conversation, on the loop's own thread.
    ///
    /// The mode is restored to <see cref="AgentState.ModeBeforeReview"/> rather than to
    /// <see cref="AgentMode.Core"/>: an AI carded while the review was running must come back
    /// carded, or the device gate silently hands the station's equipment to an agent sitting in
    /// somebody's pocket.
    /// </summary>
    private async Task RunReviewAsync(CancellationToken ct)
    {
        if (_curate == null)
            return;

        Mode = AgentMode.Review;
        try
        {
            var report = await _curate().ConfigureAwait(false);

            // Здесь, в отличие от ритуала, свёртки не было: тело кончается результатом инструмента
            // или репликой модели, и отдельное user-сообщение законно. Зона 0 при этом остаётся
            // прежней до следующей перестройки префикса — ровно как после любой другой записи.
            if (!string.IsNullOrWhiteSpace(report))
                Conv.AppendUserOrMerge(report!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _sawmill.Error($"ревью по запросу не отработало: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Mode = State.ModeBeforeReview;
        }
    }

    /// <summary>Keep a log line to one line — device_ui payloads are long and the point is the shape.</summary>
    private static string Trim(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private void RememberSpeech(string? text) => State.RememberSpeech(text);

    /// <summary>Has this exact line gone out in the last few turns? Public so the speech tools can refuse it.</summary>
    public bool AlreadySaid(string text) => State.AlreadySaid(text);

    /// <summary>One string argument out of a raw tool-call payload, or null if it is not there.</summary>
    private static string? ArgumentValue(string argsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Cts.Cancel();
        Cts.Dispose();
    }
}
