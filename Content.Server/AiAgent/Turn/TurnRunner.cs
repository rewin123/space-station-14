using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Perception;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent.Turn;

/// <summary>
/// One turn, as named nodes over a <see cref="TurnContext"/>.
///
/// <code>
///   Open ─► Step ─► Request ─► Classify ─┬─► Dispatch ─► Step
///                                        └─► Prose ─┬─► Nudge ─► Step
///                                                   └─► Settle ─┬─► Recover ─► Close
///                                                               └────────────► Close
/// </code>
///
/// Cancellation and a throwing model client are not nodes but exits: the caller's <c>finally</c>
/// closes the turn and the exception propagates with the outcome already recorded.
///
/// Deliberately knows nothing about entities, main-thread marshalling or compaction. That is what
/// makes it constructible in a line each and testable in milliseconds — the behaviours in here used
/// to need a pooled server and thirty seconds apiece to assert two booleans, which is why most of
/// them were never asserted at all.
/// </summary>
public sealed class TurnRunner
{
    private readonly ILlmClient _llm;
    private readonly AiToolRegistry _registry;
    private readonly ToolDispatcher _dispatcher;
    private readonly ObservationQueue _queue;
    private readonly AgentState _state;
    private readonly CacheMetrics _cache;
    private readonly Journal _journal;
    private readonly Func<string, string?, Task<bool>> _speak;
    private readonly ISawmill _sawmill;
    private readonly Locale.AgentLocale _locale;

    public TurnRunner(
        ILlmClient llm,
        AiToolRegistry registry,
        ToolDispatcher dispatcher,
        ObservationQueue queue,
        AgentState state,
        CacheMetrics cache,
        Journal journal,
        Func<string, string?, Task<bool>> speak,
        ISawmill sawmill,
        Locale.AgentLocale? locale = null)
    {
        _llm = llm;
        _registry = registry;
        _dispatcher = dispatcher;
        _queue = queue;
        _state = state;
        _cache = cache;
        _journal = journal;
        _speak = speak;
        _sawmill = sawmill;
        _locale = locale ?? Locale.AgentLocale.Ru;
    }

    private ConversationState Conv => _state.Conv;

    /// <summary>
    /// Run one turn. Throws only <see cref="OperationCanceledException"/> and whatever the model
    /// client throws; in both cases the conversation is left protocol-valid by the caller's
    /// <c>finally</c>.
    /// </summary>
    public async Task<TurnContext> RunAsync(TurnPerception perception, int maxSteps, CancellationToken ct)
    {
        var ctx = new TurnContext(_state.Turns, perception, maxSteps);

        // What the agent heard, recorded alongside what it then did.
        //
        // Observations were the one thing missing from the journal, which made a transcript read as
        // a series of actions with no stimulus. They are also what a fold needs most: after the
        // history is summarised away, "who said what to me" is the part the agent cannot reconstruct
        // from memory or from the station.
        _journal.Write("obs", new Dictionary<string, object?>
        {
            ["turn"] = ctx.Index,
            ["text"] = perception.Text,
        });

        try
        {
            await StepsAsync(ctx, ct).ConfigureAwait(false);
        }
        // Тот же фильтр, что в петле (`AgentSession`, catch по OperationCanceledException):
        // `HttpClient.Timeout` бросает `TaskCanceledException`, наследника OperationCanceledException.
        // Без `when` истёкший таймаут модели попадал бы в журнал как `Cancelled` — то есть как
        // штатное закарживание, — тогда как петля в ту же секунду пишет о нём как об отказе.
        // Разбирать потом два взаимоисключающих объяснения одного события невозможно.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ctx.Finish(TurnExit.Cancelled, TurnDelivery.Abandoned);
            throw;
        }
        catch
        {
            ctx.Finish(TurnExit.Failed, TurnDelivery.Abandoned);
            throw;
        }

        await SettleAsync(ctx).ConfigureAwait(false);
        ctx.Enter(TurnPhase.Done);
        return ctx;
    }

    private async Task StepsAsync(TurnContext ctx, CancellationToken ct)
    {
        while (true)
        {
            ctx.Enter(TurnPhase.Step);
            ct.ThrowIfCancellationRequested();

            ctx.Enter(TurnPhase.Request);
            var response = await _llm.ChatAsync(Conv.Build(), _registry.WireSchemas(), ct).ConfigureAwait(false);

            ctx.Enter(TurnPhase.Classify);
            Classify(ctx, response);

            if (response.ToolCalls.Count == 0)
            {
                ctx.Enter(TurnPhase.Prose);

                // Told the crew it would do something, then stopped without doing it. Same failure
                // as inaudible prose — the model believes the saying was the doing — so it gets the
                // same one reminder. Checked before the prose nudge; the two cannot both apply,
                // because promising requires having spoken and the prose nudge requires not having.
                if (TryNudgePromise(ctx))
                {
                    if (ctx.TryAdvanceStep())
                        continue;

                    ctx.Finish(TurnExit.BudgetExhausted, TurnDelivery.NothingOwed);
                    return;
                }

                if (TryNudge(ctx, response.Content?.Trim()))
                {
                    if (ctx.TryAdvanceStep())
                        continue;

                    ctx.Finish(TurnExit.BudgetExhausted, TurnDelivery.NothingOwed);
                    return;
                }

                ctx.Finish(TurnExit.ModelStopped, TurnDelivery.NothingOwed);
                return;
            }

            ctx.Enter(TurnPhase.Dispatch);
            await DispatchAsync(ctx, response, ct).ConfigureAwait(false);

            // Модель сказала «делать нечего» — значит ход окончен, и следующий запрос не нужен.
            //
            // Это и есть весь смысл noop. Раньше единственным способом закрыть ход было перестать
            // звать инструменты, то есть ответить прозой, а проза при любом радиотрафике поднимает
            // owed и тянет за собой напоминание и лишний шаг. Модель, которой нечего сказать,
            // получала подталкивание высказаться — ровно там, где правильный ответ молчание.
            if (ctx.Idled)
            {
                // Обещание проверяется и здесь. Иначе noop стал бы способом сказать экипажу
                // «сейчас открою» и молча закрыть ход: люди стоят у двери, а агент уже спит.
                if (TryNudgePromise(ctx))
                {
                    if (ctx.TryAdvanceStep())
                        continue;

                    ctx.Finish(TurnExit.BudgetExhausted, TurnDelivery.NothingOwed);
                    return;
                }

                _state.IdleTurns++;
                ctx.Finish(TurnExit.Idled, TurnDelivery.NothingOwed);
                return;
            }

            if (ctx.TryAdvanceStep())
                continue;

            ctx.Finish(TurnExit.BudgetExhausted, TurnDelivery.NothingOwed);
            return;
        }
    }

    private void Classify(TurnContext ctx, LlmResponse response)
    {
        Conv.LastPromptTokens = response.PromptTokens;
        Conv.Calibrate(response.PromptTokens);
        ctx.RecordResponse(response.CacheRatio, response.ToolCalls.Count);

        _cache.NoteProvider(response.Profile, response.ReportsCache);
        _cache.Record(response.PromptTokens, response.CachedTokens, Conv.PrefixHash, Conv.SystemPrompt);
        Conv.AppendAssistant(response);

        _sawmill.Info($"turn {ctx.Index} step {ctx.Step}  " +
                      _cache.Format(response.PromptTokens, response.CachedTokens,
                          response.CompletionTokens, response.DurationSeconds,
                          response.ToolCalls.Count, _state.Mode.ToString()));

        _journal.Write("step", new Dictionary<string, object?>
        {
            ["turn"] = ctx.Index,
            ["step"] = ctx.Step,
            ["prompt_tokens"] = response.PromptTokens,
            ["cached_tokens"] = response.CachedTokens,
            ["completion_tokens"] = response.CompletionTokens,
            ["seconds"] = Math.Round(response.DurationSeconds, 2),
            ["tools"] = response.ToolCalls.Count,
            ["mode"] = _state.Mode.ToString(),

            // Кто ответил. С цепочкой фаллбеков без этого поля разобрать постфактум, чья была
            // выдача, невозможно — а спрашивают об этом именно на плохих ходах.
            ["profile"] = response.Profile,
        });

        if (!string.IsNullOrWhiteSpace(response.Content))
            _sawmill.Debug($"thought: {response.Content!.Trim()}");
    }

    /// <summary>
    /// Remind it, once, that it promised the crew something and then stopped.
    ///
    /// Declining is a legitimate answer — "открою, когда подтвердит инженер" is a real thing to
    /// mean — so the reminder asks rather than insists. What it must not be is silent: from the
    /// crew's side an unkept promise is worse than a refusal, because they are standing at a door
    /// they were told would open.
    /// </summary>
    private bool TryNudgePromise(TurnContext ctx)
    {
        if (!ctx.HasUnkeptPromise || ctx.NudgedPromise)
            return false;

        ctx.Enter(TurnPhase.Nudge);
        ctx.MarkPromiseNudged();

        Conv.AppendUser(
            "NOTIFY Ты сказал экипажу, что сейчас что-то сделаешь, но ни одного действия не " +
            "вызвал. Сделай это сейчас — либо скажи вслух, что передумал или чего ждёшь. " +
            "Обещание, о котором забыли, для экипажа хуже отказа.");

        _sawmill.Warning($"обещание без действия: {Trim(ctx.Promised, 200)}");
        _state.BrokenPromises++;

        _journal.Write("promise", new Dictionary<string, object?>
        {
            ["turn"] = ctx.Index,
            ["said"] = Trim(ctx.Promised, 300),
        });

        return true;
    }

    /// <summary>
    /// The model answered in prose. Decide whether to remind it once, and hold what it said.
    ///
    /// The failure this guards against: the model composes a perfectly good reply as plain text and
    /// stops, believing it has answered. Nothing reaches the station and the crew sees a dead AI.
    /// Prompting alone does not fix it reliably, so the loop says so out loud and gives it one more
    /// step to say it properly.
    /// </summary>
    private bool TryNudge(TurnContext ctx, string? prose)
    {
        var owed = ctx.Perception.Addressed && !ctx.Spoke && !string.IsNullOrEmpty(prose);

        if (!ctx.Nudged && owed)
        {
            ctx.Enter(TurnPhase.Nudge);
            ctx.MarkNudged(prose!);
            Conv.AppendUser(
                "NOTIFY Этого никто не услышал: обычный текст не доходит до экипажа. " +
                "Если хочешь ответить — вызови инструмент say или radio.");
            return true;
        }

        ctx.HoldProse(owed ? prose : null);
        return false;
    }

    private async Task DispatchAsync(TurnContext ctx, LlmResponse response, CancellationToken ct)
    {
        ctx.HoldProse(null);
        ctx.ClearIdle();

        foreach (var call in response.ToolCalls)
        {
            ct.ThrowIfCancellationRequested();

            var gate = _state.Mode == AgentMode.Review ? DispatchGate.NoGameActions : DispatchGate.None;
            var invocation = await _dispatcher.InvokeAsync(call, gate, ct).ConfigureAwait(false);
            var result = invocation.Result;

            Conv.AppendToolResult(call.Id, result.ToJson());

            // Without this the log shows "tools=1" and nothing else: which tool ran, with what
            // arguments, and which gate refused are all invisible. That turns any behavioural
            // question — why did it not move the eye, why did it give up — into guesswork.
            _sawmill.Debug(
                $"  {call.Function.Name}({Trim(call.Function.Arguments)}) -> " +
                (result.Ok ? "ok " + Trim(result.EffectJson(), 1200) : $"{result.Error}: {result.Detail}"));

            _journal.Write("tool", new Dictionary<string, object?>
            {
                ["turn"] = ctx.Index,
                ["name"] = call.Function.Name,
                ["args"] = Trim(call.Function.Arguments, 400),
                ["ok"] = result.Ok,
                ["error"] = result.Error,
                ["detail"] = result.Ok ? null : Trim(result.Detail, 200),
            });

            if (result.Ok && invocation.Tool is { Speech: true } speech)
            {
                ctx.MarkSpoke();

                var spoken = speech.SpokenText?.Invoke(invocation.Args);
                _state.RememberSpeech(spoken);
                ctx.MarkPromised(spoken);
            }

            // Any successful action on the station settles whatever was promised. Deliberately not
            // matched against the promise: the model may well have said "открою" and then bolted
            // instead after looking, and second-guessing which action it meant would be a worse
            // reading of intent than simply noticing that it acted.
            else if (result.Ok && invocation.Tool is { GameAction: true, Speech: false })
            {
                ctx.MarkActed();
            }

            // Отдельным условием, а не веткой цепочки выше: «закрывает ход» ортогонально и речи, и
            // действию. Модель вправе ответить по рации и тем же ответом закрыть ход.
            if (result.Ok && invocation.Tool is { EndsTurn: true })
                ctx.MarkIdled();
        }

        SteerWithNewEvents(ctx);
    }

    /// <summary>
    /// Подмешать события, пришедшие пока модель работала, отдельным сообщением пользователя —
    /// сразу за результатами инструментов и перед следующим обращением к модели.
    ///
    /// <para>
    /// <b>Раньше это ехало внутри каждого результата инструмента полем <c>unread</c>, и это было
    /// двойным дублированием.</b> Во-первых, строки подставлялись в КАЖДЫЙ результат батча: на
    /// трёх параллельных вызовах одна реплика экипажа приезжала модели трижды. Во-вторых, окно
    /// только подсматривало в очередь, не забирая, — поэтому те же строки приходили ещё раз, уже
    /// как обычное наблюдение в начале следующего хода. На длинном ходу это раздувало контекст
    /// повторами одного и того же и учило модель, что события можно читать по диагонали.
    /// </para>
    /// <para>
    /// Теперь события ИЗЫМАЮТСЯ из очереди — доставлено значит удалено, — и вставляются ровно
    /// одним сообщением после ВСЕГО батча, а не после каждого вызова. Тот же приём и в том же
    /// порядке использует агент pi (<c>agent-loop.ts</c>, <c>getSteeringMessages</c> после
    /// <c>turn_end</c>): там это называется steering, дедупликация тоже сделана извлечением из
    /// очереди, без курсоров и флагов, и там же отдельно оговорено, что вставка ждёт окончания
    /// всего батча вызовов, а не встревает в его середину.
    /// </para>
    /// <para>
    /// Строки SELF здесь нет намеренно: состояние мира модель только что прочитала из
    /// <c>effect</c> каждого инструмента, и повторять его на каждом шаге двадцатипятишагового
    /// хода — платить за одно и то же двадцать пять раз. SELF принадлежит началу хода.
    /// </para>
    /// </summary>
    private void SteerWithNewEvents(TurnContext ctx)
    {
        var (items, dropped) = _queue.Drain();
        if (items.Count == 0 && dropped == 0)
            return;

        // Время берём у самого свежего события, а не у начала хода и не заводим ради него ещё одну
        // зависимость: событие само знает, когда случилось, а ход на grok46 идёт по двадцать пять
        // шагов и его начальная отметка к середине уже врёт на минуты.
        var when = items.Count > 0 ? items.Max(i => i.RoundTime) : TimeSpan.Zero;

        var text = Perception.ObservationFormatter.FormatSteering(items, dropped, when, _locale);
        if (text == null)
            return;

        Conv.AppendUser(text);

        _sawmill.Debug($"  steering: подмешано событий {items.Count}" +
                       (dropped > 0 ? $" (+{dropped} не поместилось)" : string.Empty));
    }

    /// <summary>Deliver what the crew was owed and never heard, or explain why we did not.</summary>
    private async Task SettleAsync(TurnContext ctx)
    {
        ctx.Enter(TurnPhase.Settle);

        var prose = ctx.UnheardProse;

        if (prose == null)
        {
            ctx.Finish(ctx.Exit, ctx.Spoke ? TurnDelivery.SpokeByTool : TurnDelivery.NothingOwed);
            return;
        }

        // Repeating itself on the radio is worse than saying nothing: the crew reads it as a stuck
        // machine, and it is the failure this model reaches for whenever it has nothing to add.
        if (_state.AlreadySaid(prose))
        {
            _sawmill.Debug($"проза повторяет уже сказанное, не доставляю: {prose}");
            ctx.HoldProse(null);
            ctx.Finish(ctx.Exit, TurnDelivery.SuppressedRepeat);
            return;
        }

        ctx.Enter(TurnPhase.Recover);
        _state.UntooledReplies++;
        _state.RememberSpeech(prose);

        // Log after the attempt, not before: the delivery can decline (ai.speak_untooled_text off,
        // dry run, AI no longer in play), and a line claiming a broadcast that never happened is
        // worse than no line at all.
        var delivered = await _speak(prose, ctx.Perception.RadioChannel).ConfigureAwait(false);

        _sawmill.Warning(delivered
            ? $"модель ответила текстом без say/radio даже после напоминания — доставлено вручную " +
              $"({(ctx.Perception.RadioChannel is { } ch ? "radio " + ch : "say")}): {prose}"
            : $"модель ответила текстом без say/radio даже после напоминания; доставка выключена, " +
              $"экипаж этого не услышал: {prose}");

        _journal.Write("untooled", new Dictionary<string, object?>
        {
            ["turn"] = ctx.Index,
            ["channel"] = ctx.Perception.RadioChannel,
            ["delivered"] = delivered,
            ["text"] = Trim(prose, 400),
        });

        ctx.Finish(ctx.Exit, delivered ? TurnDelivery.Delivered : TurnDelivery.DeliveryDeclined);
        ctx.Enter(TurnPhase.Close);
    }

    /// <summary>Keep a log line to one line — device_ui payloads are long and the point is the shape.</summary>
    private static string Trim(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

}
