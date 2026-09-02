using Robust.Shared.Configuration;

namespace Content.Server.AiAgent;

/// <summary>
/// CVars for the LLM-driven Station AI.
///
/// Upstream <c>Content.Shared/CCVar/CCVars.cs</c> explicitly instructs forks to declare their
/// own CVars in a separate file with its own <see cref="CVarDefsAttribute"/>; RobustToolbox's
/// <c>ConfigurationManager</c> scans every loaded assembly for such types. That is why this
/// file exists instead of a patch to CCVars.cs — it keeps the upstream rebase surface at zero.
/// </summary>
[CVarDefs]
public sealed class AiCVars
{
    // ---------------------------------------------------------------- master

    /// <summary>Master switch. Off by default so a stock build never phones an LLM.</summary>
    public static readonly CVarDef<bool> Enabled =
        CVarDef.Create("ai.enabled", false, CVar.SERVERONLY);

    /// <summary>Claim an unoccupied AI core automatically when a round starts.</summary>
    public static readonly CVarDef<bool> AutoClaim =
        CVarDef.Create("ai.auto_claim", true, CVar.SERVERONLY);

    /// <summary>
    /// Tools validate arguments and run the full gate chain but never mutate the world.
    /// For experimenting on a live server without the AI actually bolting anything.
    /// </summary>
    public static readonly CVarDef<bool> DryRun =
        CVarDef.Create("ai.dry_run", false, CVar.SERVERONLY);

    /// <summary>
    /// Потолок на число одновременно живых агентов — ядро и борги вместе.
    ///
    /// <para>
    /// Проверяется в <c>StationAiAgentSystem.StartSession</c>, то есть в одном месте на оба тела.
    /// Раньше стоял только на захвате ядра и считал там же боргов — из-за чего робот, занявший
    /// тело первым, отбирал у мозга место в ядре.
    /// </para>
    /// <para>
    /// Восемь, и число не с потолка: ровно столько нужно открытому режиму злого ИИ — ядро плюс
    /// семь тел, шесть боевых и одно инженерное (<c>supportBorgs</c> в <c>rogue_ai.yml</c>).
    ///
    /// <para>
    /// Умолчание обязано вмещать тот режим, который форк везёт с собой. Стояло четыре, а отряд
    /// вырос до семи 01.09.2026, и на умолчаниях это давало тихую половинчатость: либо часть
    /// корпусов оставалась без агента и стояла столбом, либо тела разбирали весь лимит и мозг не
    /// садился в ЯДРО — режим злого ИИ без злого ИИ, без единой ошибки в журнале. Меняя
    /// <c>supportBorgs</c>, меняйте и это число.
    /// </para>
    /// <para>
    /// Прежнее умолчание в единицу защищало односотовый llama-server, у которого два агента
    /// вытесняют префикс-кэш друг друга, — но эта защита живёт не здесь, а в цепочке профилей
    /// (<c>AgentBody.LlmChain</c>): агенту с чужой моделью чужой слот не мешает.
    /// </para>
    /// </summary>
    public static readonly CVarDef<int> MaxAgents =
        CVarDef.Create("ai.max_agents", 8, CVar.SERVERONLY);

    // ------------------------------------------------------------------- llm

    /// <summary>
    /// Any OpenAI-compatible chat-completions endpoint.
    ///
    /// Defaults to DeepSeek rather than the local llama-swap it was built against. That is a
    /// deliberate move rather than a convenience: on the same two behavioural scenarios the local
    /// 27B quant promised to open a door and then did not, while this model refused, asked what for,
    /// and — handed an unverifiable claim about an unconscious crewman — put the question on the
    /// channel instead of guessing. Local is still one CVar away.
    /// </summary>
    public static readonly CVarDef<string> Endpoint =
        CVarDef.Create("ai.endpoint", "https://api.deepseek.com/v1", CVar.SERVERONLY);

    public static readonly CVarDef<string> Model =
        CVarDef.Create("ai.model", "deepseek-v4-flash", CVar.SERVERONLY);

    /// <summary>
    /// Empty here on purpose — a remote endpoint needs a key and a key does not belong in source.
    /// Set it in <c>server_config.toml</c> (gitignored under bin/) or, for benchmarks, in
    /// <c>AI_API_KEY</c>.
    /// </summary>
    public static readonly CVarDef<string> ApiKey =
        CVarDef.Create("ai.api_key", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>
    /// 0.3 rather than a default 0.7.
    ///
    /// Originally because this box measured higher temperatures corrupting Cyrillic on a Q4 quant —
    /// a constraint a hosted model does not have. Kept anyway, for a different reason: the agent is
    /// operating station equipment, and creative variance in "which door did I mean" is not a
    /// quality anyone wants from it.
    /// </summary>
    public static readonly CVarDef<float> Temperature =
        CVarDef.Create("ai.temperature", 0.3f, CVar.SERVERONLY);

    public static readonly CVarDef<float> TopP =
        CVarDef.Create("ai.top_p", 0.85f, CVar.SERVERONLY);

    public static readonly CVarDef<int> TopK =
        CVarDef.Create("ai.top_k", 20, CVar.SERVERONLY);

    public static readonly CVarDef<float> MinP =
        CVarDef.Create("ai.min_p", 0.05f, CVar.SERVERONLY);

    /// <summary>
    /// How hard the model deliberates before answering: <c>off</c>, <c>low</c>, <c>high</c>,
    /// <c>max</c>, or empty to send nothing and leave the model on its own default.
    ///
    /// <c>low</c> rather than either extreme, and both extremes were measured. On
    /// <c>deepseek-v4-flash</c> thinking is on at <c>high</c> unless the request says otherwise,
    /// and that dominated the delay between a crewman asking something and hearing a reply — p90
    /// of six seconds, worst case fifteen. Turning it off outright halved that and visibly cost
    /// answer quality. The work per turn is picking a tool and filling a few arguments; it needs
    /// some thought, not a lot.
    ///
    /// Only DeepSeek is known to honour the field. Set it empty for a local endpoint, which would
    /// otherwise receive a parameter it does not recognise.
    /// </summary>
    public static readonly CVarDef<string> ThinkingEffort =
        CVarDef.Create("ai.thinking_effort", "low", CVar.SERVERONLY);

    /// <summary>
    /// Ceiling on one completion. Zero — the default — sends no limit at all.
    ///
    /// A ceiling is the wrong tool on a reasoning model. It cannot distinguish thinking from
    /// answering, so it cuts wherever the budget runs out, and a completion cut before it emitted
    /// either text or a tool call comes back empty. On a live shift one such response ended the
    /// round for the agent: 3000 tokens in, 3000 of them reasoning, nothing out — and every request
    /// afterwards was rejected for carrying an empty assistant message.
    ///
    /// What the model spends is now governed by <c>ai.thinking_effort</c>, which limits the part
    /// that actually runs long. Set this above zero only against an endpoint that needs it.
    /// </summary>
    public static readonly CVarDef<int> MaxTokens =
        CVarDef.Create("ai.max_tokens", 0, CVar.SERVERONLY);

    /// <summary>A remote API can be slow; llama-swap may need to load the model from disk.</summary>
    public static readonly CVarDef<float> RequestTimeout =
        CVarDef.Create("ai.request_timeout", 180f, CVar.SERVERONLY);

    // ------------------------------------------------------------ цепочка моделей

    /// <summary>
    /// Главная модель и порядок фаллбеков: id прототипов <c>aiLlmProfile</c> через запятую,
    /// например <c>codex,grok,deepseek,local</c>. Первый — главный.
    ///
    /// <para>
    /// Пусто — вести себя как раньше: один эндпоинт из <c>ai.endpoint</c> / <c>ai.model</c>. Это не
    /// временная мера, а рабочий режим: когда профили ещё не разложены или один из них сломал
    /// раунд, откат должен быть одной строкой в консоли, а не пересборкой с киком всех игроков.
    /// </para>
    /// <para>
    /// Порядок живёт здесь, а не в YAML профилей, ровно потому же: сам набор провайдеров — это
    /// данные, а вот кто из них сейчас главный — операционное решение, и менять его надо на живом
    /// сервере. Локальный профиль стоит держать последним: цепочка, целиком уехавшая в интернет,
    /// кончается вместе с интернетом.
    /// </para>
    /// </summary>
    public static readonly CVarDef<string> LlmChain =
        CVarDef.Create("ai.llm_chain", "", CVar.SERVERONLY);

    /// <summary>
    /// SOCKS-прокси для профилей с <c>proxy: Socks</c>. Пусто — такие профили пойдут напрямую.
    ///
    /// <c>SocketsHttpHandler</c> понимает <c>socks4://</c>, <c>socks4a://</c> и <c>socks5://</c> с
    /// .NET 6, так что своей библиотеки не нужно. Локальные профили обязаны стоять на
    /// <c>proxy: None</c>: запрос на loopback, ушедший в удалённый выход, просто зависает.
    /// </summary>
    public static readonly CVarDef<string> LlmSocksProxy =
        CVarDef.Create("ai.llm_socks_proxy", "socks5://127.0.0.1:10808", CVar.SERVERONLY);

    /// <summary>Сколько профиль спит после обычного отказа — сеть, 5xx, таймаут. Секунды.</summary>
    public static readonly CVarDef<float> LlmCooldownSeconds =
        CVarDef.Create("ai.llm_cooldown_seconds", 300f, CVar.SERVERONLY);

    /// <summary>
    /// Сколько спать после исчерпания квоты, когда провайдер не сказал, когда сброс. Секунды.
    ///
    /// Час, а не пять минут, и это не осторожность. Подписочная квота — окно с известным концом:
    /// у Codex пятичасовое, у Grok Build недельный пул. Пробы в исчерпанное окно ничего не
    /// возвращают, но каждая из них — обращение, то есть они тратят ровно то, чего уже нет.
    /// Если провайдер прислал <c>Retry-After</c>, это значение не используется вовсе — берётся
    /// названный им срок.
    /// </summary>
    public static readonly CVarDef<float> LlmQuotaCooldownSeconds =
        CVarDef.Create("ai.llm_quota_cooldown_seconds", 3600f, CVar.SERVERONLY);

    /// <summary>
    /// Как часто пробовать вернуться на главный профиль после ухода на фаллбек. Секунды.
    ///
    /// Проба не бесплатна: смена провайдера обесценивает префиксный кэш на обеих сторонах, а живой
    /// сервер держит реюз 97.9% и экономит этим десятки тысяч токенов на каждом ходу. Пять минут —
    /// компромисс между «сидим на резерве всю смену» и «дёргаемся каждый ход».
    /// </summary>
    public static readonly CVarDef<float> LlmRecheckSeconds =
        CVarDef.Create("ai.llm_recheck_seconds", 300f, CVar.SERVERONLY);

    /// <summary>
    /// Общий потолок на один ход поверх таймаутов отдельных профилей. Секунды.
    ///
    /// Четыре профиля по 150–180 с складываются в десять минут на одном ходу, и агент, который
    /// «просто думает», для экипажа неотличим от сломанного. Роутер не начинает новую попытку, если
    /// бюджет вышел, и обрезает текущую по остатку.
    /// </summary>
    public static readonly CVarDef<float> LlmTotalTimeout =
        CVarDef.Create("ai.llm_total_timeout", 240f, CVar.SERVERONLY);

    // ------------------------------------------------------------------ loop

    /// <summary>
    /// Ceiling on how long the agent sleeps before looking at what accumulated.
    ///
    /// A ceiling rather than a period: anything landing in the observation queue wakes the loop
    /// immediately, so this only governs how often it checks on a station that has said nothing.
    /// Before the wake existed this was the whole latency budget, and being addressed just after a
    /// tick began meant waiting out the full interval — worst on exactly the shouted, urgent
    /// message that should have been answered fastest.
    /// </summary>
    public static readonly CVarDef<float> TickSeconds =
        CVarDef.Create("ai.tick_seconds", 5f, CVar.SERVERONLY);

    /// <summary>Back-off when nothing at all happened, so an empty station costs nothing.</summary>
    public static readonly CVarDef<float> TickSecondsIdle =
        CVarDef.Create("ai.tick_seconds_idle", 25f, CVar.SERVERONLY);

    /// <summary>
    /// Steps in one turn, where a step is one round trip to the model.
    ///
    /// Six was too few for the work a single request actually takes. A question like "what is the
    /// pressure in the bar" is map, move_camera, look, inspect, radio — five before anything is
    /// said, and any correction along the way ran the turn out. What that looked like from the
    /// station was the AI aiming its camera at something and then going quiet, because a turn cut
    /// off by the budget says nothing on its way out.
    ///
    /// The ceiling is not the cost control here; the model stopping when it is done is. Turns end
    /// on their own at two or three steps when nothing is needed, and <c>noop</c> exists precisely
    /// so that a quiet observation costs one call. What this number does is stop a loop, and for
    /// that it only has to be lower than "forever".
    /// </summary>
    public static readonly CVarDef<int> MaxToolCallsPerTurn =
        CVarDef.Create("ai.max_tool_calls_per_turn", 90, CVar.SERVERONLY);

    /// <summary>
    /// Observations buffered before the oldest are dropped (and the drop is reported).
    ///
    /// Поднято с 200 под поток строк OBSERVED: наблюдение видит каждое действие каждого человека в
    /// кадре, и при двухстах строках на всё про всё разговорчивый отсек за один ход агента выбирал
    /// бы очередь целиком. Контекст модели — 256k, сотня строк в сообщении наблюдения ничего не
    /// ломает; ломала бы потеря реплики, а от неё защищает <see cref="ObserveBuffer"/>.
    /// </summary>
    public static readonly CVarDef<int> ObsBuffer =
        CVarDef.Create("ai.obs_buffer", 600, CVar.SERVERONLY);

    /// <summary>
    /// Сколько будильников агент может держать одновременно.
    ///
    /// Потолок нужен не ради памяти — восемь записей ничего не стоят, — а ради самой петли: каждый
    /// сработавший таймер это ход, то есть запрос к модели. Агент, поставивший напоминалку на каждое
    /// обещание за смену, разбудил бы себя чаще, чем его будит экипаж, и перестал бы отличать
    /// собственный фон от станции. Восемь — это примерно предел того, что он способен внятно
    /// перечислить в строке SELF.
    /// </summary>
    public static readonly CVarDef<int> MaxTimers =
        CVarDef.Create("ai.max_timers", 8, CVar.SERVERONLY);

    /// <summary>
    /// Нижняя граница срока таймера, в секундах. Она же минимальный интервал повтора.
    ///
    /// Тридцать секунд — это не «достаточно точно», а «дешевле, чем тик простоя». Повтор с
    /// интервалом в секунду превратил бы петлю в генератор ходов и счётчик расходов на модель,
    /// причём с самым безобидным следом в логах: агент просто всё время о чём-то думает.
    /// Бенчмарки опускают это значение, чтобы не ждать полминуты на каждый тест.
    /// </summary>
    public static readonly CVarDef<int> TimerMinSeconds =
        CVarDef.Create("ai.timer_min_seconds", 30, CVar.SERVERONLY);

    /// <summary>Vanilla parity: the AI hears local speech only near its physical core.</summary>
    public static readonly CVarDef<float> HearRange =
        CVarDef.Create("ai.hear_range", 10f, CVar.SERVERONLY);

    /// <summary>A broken endpoint must not spin a core for the rest of the round.</summary>
    public static readonly CVarDef<int> MaxConsecutiveFailures =
        CVarDef.Create("ai.max_consecutive_failures", 10, CVar.SERVERONLY);

    // --------------------------------------------------------------- context

    /// <summary>
    /// 0 means: read the real n_ctx from llama-server's /props at startup.
    ///
    /// A hosted API has no such endpoint, so on one of those this stays 0 and the compaction
    /// thresholds below stand on their own — set it by hand if the provider's window is smaller
    /// than they assume.
    /// </summary>
    public static readonly CVarDef<int> CtxLimit =
        CVarDef.Create("ai.ctx_limit", 0, CVar.SERVERONLY);

    /// <summary>
    /// Event lines the fold keeps as the whole of the retained history.
    ///
    /// Replaces a character budget over retained <em>messages</em>. Messages are where the weight
    /// is — one <c>look</c> of a busy room is thousands of tokens of crates — and keeping the last
    /// few of them carried that weight forward for the rest of the round. Forty lines of "heard
    /// this, called that, it refused" is a page, and it is the part the agent cannot reconstruct
    /// from its memory files or by looking again.
    /// </summary>
    public static readonly CVarDef<int> CompactEvents =
        CVarDef.Create("ai.compact_events", 40, CVar.SERVERONLY);

    public static readonly CVarDef<int> CompactHigh =
        CVarDef.Create("ai.compact_high", 90000, CVar.SERVERONLY);



    // ----------------------------------------------------------- diagnostics

    /// <summary>
    /// How many rows a single look may return.
    ///
    /// Generous on purpose: the model's context is large, and blindness costs far more than
    /// verbosity — an agent that lists sixty of four hundred things tells the crew "there is no
    /// SMES here" with complete confidence. Rows come out nearest-first, so a cut removes the far
    /// end of the room, and the answer says out loud that it was cut.
    ///
    /// It stays generous now that a fold no longer retains tool results: a big look is expensive
    /// once, in the turn that asks for it, and stops being expensive the moment the history is
    /// compacted. Paying for the answer is the point; paying for it forever was the bug.
    /// </summary>
    public static readonly CVarDef<int> LookLimit =
        CVarDef.Create("ai.look_limit", 300, CVar.SERVERONLY);

    /// <summary>
    /// Порог предупреждения для ОДНОГО среза работы в мире. Ничего не ограничивает — только пишет
    /// в журнал и в <c>aiagent cost</c>. Ограничивает <see cref="FrameBudgetMs"/>.
    /// </summary>
    public static readonly CVarDef<float> MainThreadBudgetMs =
        CVarDef.Create("ai.mainthread_budget_ms", 5f, CVar.SERVERONLY);

    /// <summary>
    /// Сколько миллисекунд кадра шина мира может занять под запросы агента.
    ///
    /// Три при тикрейте 30 — это 9% кадра. Здоровый тик на этом сервере тратит 21–26 мс из 33.3
    /// на <c>EntitySystems</c>, так что три миллисекунды берутся из запаса, а не из чужой работы.
    ///
    /// Ноль не выключает шину: один срез исполняется до первой проверки дедлайна, иначе
    /// перегруженный сервер заморозил бы агента навсегда, и тот тихо умер бы посреди раунда.
    /// </summary>
    public static readonly CVarDef<float> FrameBudgetMs =
        CVarDef.Create("ai.frame_budget_ms", 3f, CVar.SERVERONLY);

    /// <summary>
    /// Возраст, после которого обычная заявка обслуживается вперёд срочных, мс.
    ///
    /// Сторож от голодания: без него поток срочных мог бы держать обзор в очереди неограниченно.
    /// При нынешней глубине очереди (инструменты вызываются строго последовательно, в полёте одна
    /// заявка) не срабатывает, и это нормально — страховка на будущее.
    /// </summary>
    public static readonly CVarDef<float> WorldPromoteMs =
        CVarDef.Create("ai.world_promote_ms", 500f, CVar.SERVERONLY);

    /// <summary>
    /// Потолок глубины очереди к миру. Обязан не срабатывать никогда — сработал, значит завёлся
    /// параллелизм, которого в модуле нет. Отказ громкий: заявка возвращает ошибку, а не теряется.
    /// </summary>
    public static readonly CVarDef<int> WorldQueueMax =
        CVarDef.Create("ai.world_queue_max", 256, CVar.SERVERONLY);

    /// <summary>
    /// Рубильник шины. <c>false</c> — запросы уходят прямо в очередь движка, как было до неё.
    ///
    /// Тот же приём, что у <see cref="LookFast"/>, и по той же причине: сервер публичный, а
    /// пересборка выкидывает всех, кто на нём играет. Откат обязан быть командой, а не выкаткой.
    /// </summary>
    public static readonly CVarDef<bool> WorldBusEnabled =
        CVarDef.Create("ai.world_bus", true, CVar.SERVERONLY);

    /// <summary>
    /// Собирать видимые сущности одним обходом дерева вместо запроса на каждый тайл.
    ///
    /// Рубильник, а не эксперимент. Медленный путь оставлен в дереве по двум причинам, и обе
    /// стоят пятнадцати строк.
    ///
    /// Первая — доказательство: тест эквивалентности гоняет оба пути по одной станции и требует,
    /// чтобы быстрый не потерял ничего из увиденного медленным. Утверждение «мы ничего не
    /// сломали» либо проверяемо, либо это обещание.
    ///
    /// Вторая — откат. Сервер публичный, и пересборка с перезапуском выкидывает всех, кто на
    /// нём играет. <c>cvar ai.look_fast false</c> из админ-консоли стоит секунды и ноль киков,
    /// а разбираться можно потом.
    /// </summary>
    public static readonly CVarDef<bool> LookFast =
        CVarDef.Create("ai.look_fast", true, CVar.SERVERONLY);

    // ------------------------------------------------------------- наблюдение

    /// <summary>
    /// Видит ли агент, что происходит рядом с его глазом.
    ///
    /// Общий рубильник над всеми подписками наблюдения. Сервер публичный, и если поток строк
    /// окажется вреднее пользы, <c>cvar ai.observe false</c> из админ-консоли стоит секунды и ноль
    /// киков — в отличие от пересборки с перезапуском.
    /// </summary>
    public static readonly CVarDef<bool> Observe =
        CVarDef.Create("ai.observe", true, CVar.SERVERONLY);

    /// <summary>
    /// Полурамка поля зрения глаза, в тайлах.
    ///
    /// Совпадает с <c>look {"expand":0}</c> намеренно: это одно и то же поле, и расхождение значило
    /// бы, что агент видит событие там, где обзор ничего не покажет, — или наоборот.
    /// </summary>
    public static readonly CVarDef<float> ObserveRange =
        CVarDef.Create("ai.observe_range", 8.5f, CVar.SERVERONLY);

    /// <summary>
    /// Какие ярлыки наблюдений включены. Пусто — все.
    ///
    /// Список через запятую (<c>урон,выстрел,вложил</c>), ручка на случай, если в бою окажется, что
    /// какой-то вид событий даёт поток без пользы. Сужается командой из консоли, а не выкаткой:
    /// решать это по журналу живого сервера правильнее, чем угадывать заранее, а угадывать заранее
    /// и вырезать кодом — значит подменять модель таблицей глаголов.
    /// </summary>
    public static readonly CVarDef<string> ObserveKinds =
        CVarDef.Create("ai.observe_kinds", string.Empty, CVar.SERVERONLY);

    /// <summary>
    /// Учитывать ли стены при наблюдении.
    ///
    /// Выключено, и это осознанная уступка. Строгая проверка — <c>StationAiVisionSystem.IsAccessible</c>,
    /// а она разворачивает три сотни тайлов и делает broadphase-запрос на каждый. На редком вызове
    /// (одна дверь, один клик) это незаметно; на потоке событий это возврат к тому, из-за чего
    /// <c>look</c> держал тик секунду.
    ///
    /// Цена выключенного состояния названа прямо: в пределах <see cref="ObserveRange"/> агент
    /// заметит происходящее за стеной, тогда как человек на его месте увидел бы стену. Включённое
    /// добавляет третью ступень ворот с мемо по тайлу на один тик и потолком проверок за тик.
    /// </summary>
    public static readonly CVarDef<bool> ObserveOcclusion =
        CVarDef.Create("ai.observe_occlusion", false, CVar.SERVERONLY);

    /// <summary>
    /// Сколько строк наблюдения очередь держит одновременно.
    ///
    /// Не про экономию, а про порядок вытеснения: общий потолок очереди выбрасывает старейшее
    /// безотносительно вида, и поток OBSERVED вытолкнул бы из неё обращение по рации. Этот потолок
    /// подрезает старейшую OBSERVED и только её, поэтому заглушить агента вознёй в кадре нельзя.
    /// </summary>
    public static readonly CVarDef<int> ObserveBuffer =
        CVarDef.Create("ai.observe_buffer", 400, CVar.SERVERONLY);

    /// <summary>
    /// Потолок строгих проверок видимости за тик; работает только при <see cref="ObserveOcclusion"/>.
    ///
    /// Страховка, а не настройка. Мемо по тайлу уже схлопывает драку на одном тайле в одну проверку,
    /// но выдумать нагрузку, где событий в кадре десятки за тик, ничего не стоит — а каждая
    /// проверка это сотни broadphase-запросов. Сверх потолка события пропускаются, и число
    /// пропущенных уходит в журнал: молча терять наблюдения хуже, чем терять их громко.
    /// </summary>
    public static readonly CVarDef<int> ObserveMaxChecksPerTick =
        CVarDef.Create("ai.observe_max_checks_per_tick", 4, CVar.SERVERONLY);

    /// <summary>Self-evolution: the review that writes skills and memory. Step 1 of compaction.</summary>
    public static readonly CVarDef<bool> CuratorEnabled =
        CVarDef.Create("ai.curator_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Deliver a reply the model wrote as prose instead of calling say/radio, after one reminder.
    /// Off means a model that forgets its tools is simply mute — honest, but useless to the crew.
    /// </summary>
    public static readonly CVarDef<bool> SpeakUntooledText =
        CVarDef.Create("ai.speak_untooled_text", true, CVar.SERVERONLY);

    public static readonly CVarDef<bool> LogTranscript =
        CVarDef.Create("ai.log_transcript", true, CVar.SERVERONLY);

    /// <summary>Empty resolves to &lt;server exe dir&gt;/../../ai_data. Benchmarks point this at a temp dir.</summary>
    public static readonly CVarDef<string> DataDir =
        CVarDef.Create("ai.data_dir", "", CVar.SERVERONLY);

    // ------------------------------------------------- резервное питание без инженеров
    //
    // Здесь же, по той же причине, что и ai.station_name ниже: форк владеет ровно одним классом
    // [CVarDefs], и завести второй ради трёх строк — значит положить новый файл в чужое дерево.

    /// <summary>
    /// Ставить ли резервный генератор, когда на смене нет инженеров.
    ///
    /// Рубильник по той же причине, что у <see cref="LookFast"/>: сервер публичный, пересборка
    /// кикает всех, поэтому откат обязан быть командой из консоли, а не выкаткой.
    /// </summary>
    public static readonly CVarDef<bool> BackupPower =
        CVarDef.Create("ai.backup_power", true, CVar.SERVERONLY);

    /// <summary>
    /// Мощность резервного контура, ватты.
    ///
    /// <para>
    /// Шестьдесят киловатт — цифра ДО замера, а не после, и это важно знать. Все опорные числа в
    /// апстримовом гайдбуке («батарей хватит на 5–10 минут», отсюда 340–670 кВт потребления)
    /// написаны для полного экипажа. Сколько тянет пустая станция, неизвестно: лампа берёт 5 Вт,
    /// и на смене из одного человека почти всё оборудование спит. Мерить надо консольной командой
    /// <c>powerstat</c> на живом раунде и ставить с запасом ×1.5.
    /// </para>
    /// <para>
    /// Для сравнения: солнечный массив на карте Packed — 98 кВт номинала (и ноль на главной сети,
    /// потому что он к ней не подключён), PACMAN — 30 кВт, SuperPACMAN — 50, апстримовый
    /// DebugGenerator — 300.
    /// </para>
    /// </summary>
    public static readonly CVarDef<int> BackupPowerWatts =
        CVarDef.Create("ai.backup_power_watts", 60000, CVar.SERVERONLY);

    /// <summary>
    /// Множитель поверх мощности резервного контура.
    ///
    /// <para>
    /// Заведён потому, что первый же боевой раунд вскрыл дырку: мощность известных станций лежит в
    /// прототипе (<c>backup_power.yml</c>), а прототипы читаются при старте процесса — то есть
    /// подкрутить число на живом сервере было нечем, и пришлось бы пересобирать и кикать
    /// играющих. Множитель действует на ЛЮБОЙ источник — и таблицу, и
    /// <see cref="BackupPowerWatts"/>, — и читается на раздаче должностей, поэтому вступает в силу
    /// со следующего раунда.
    /// </para>
    /// <para>
    /// Пропорции между станциями при этом сохраняются: таблица остаётся относительной шкалой, а
    /// множитель сдвигает её целиком. Если окажется, что прокси «APC × 1200» занижает везде
    /// одинаково, лечится одной командой вместо тринадцати правок.
    /// </para>
    /// </summary>
    public static readonly CVarDef<float> BackupPowerScale =
        CVarDef.Create("ai.backup_power_scale", 1f, CVar.SERVERONLY);

    /// <summary>
    /// Какой департамент считается «инженерной сменой».
    ///
    /// Читается из прототипа департамента, а не списком роль-за-ролью в коде: форк, добавивший
    /// свою инженерную должность, учтётся сам. Это же и задел на перенос аддона на чужой форк —
    /// там состав отдела почти наверняка другой.
    /// </summary>
    public static readonly CVarDef<string> BackupPowerDepartment =
        CVarDef.Create("ai.backup_power_department", "Engineering", CVar.SERVERONLY);

    /// <summary>
    /// Имя станции. Пустое — как в ваниле: генератор карты, «TG Box Station 14-Alpha».
    ///
    /// Живёт в пространстве <c>ai.</c>, хотя про агента не про него. Форк владеет ровно одним
    /// классом <c>[CVarDefs]</c>, и заводить второй ради одной строки — хуже, чем поставить её
    /// сюда с этим объяснением: любой другой вариант означал бы новый файл в чужом дереве.
    /// </summary>
    public static readonly CVarDef<string> StationName =
        CVarDef.Create("ai.station_name", "", CVar.SERVERONLY);

    // ----------------------------------------------------------------- отладка

    /// <summary>
    /// The agent event bus, for an external debugger.
    ///
    /// Off by default, and off is free: with no bus the conversation, the memory and the skill
    /// library each do one null check and never build an event. On, the agent broadcasts every
    /// change to its history, memory, skills and statistics.
    /// </summary>
    public static readonly CVarDef<bool> DebugEnabled =
        CVarDef.Create("ai.debug_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// How many events the ring keeps for clients that fell behind.
    ///
    /// A turn produces on the order of ten frames every eight seconds, so the default is minutes of
    /// tolerance for a debugger that blinked. Past the end of the ring a client is told to resync
    /// rather than handed a partial history — which is the whole reason the ring is bounded and
    /// nobody is registered as a subscriber.
    ///
    /// <para>
    /// 2048, а не прежние 512, потому что агентов теперь до четырёх: кадров вчетверо больше, а
    /// всплеск компакции — это четыре <c>history.replaced</c> с полными телами, который выбивает
    /// кольцо за один заход. Симптом переполнения обманчив: клиент не теряет данные, он их
    /// перекачивает, и выглядит это как «отладчик постоянно моргает без причины».
    /// </para>
    /// </summary>
    public static readonly CVarDef<int> DebugRing =
        CVarDef.Create("ai.debug_ring", 2048, CVar.SERVERONLY);

    /// <summary>
    /// Where the debug HTTP server listens. Its own port, not the engine's status host.
    ///
    /// Loopback by default and meant to stay there: put a reverse proxy in front if it has to be
    /// reachable, because there is no TLS here. Separate from <see cref="DebugEnabled"/> on purpose
    /// — one switch doubling as "off" and "address" is how somebody testing from another machine
    /// ends up publishing <c>0.0.0.0</c> and forgetting.
    /// </summary>
    public static readonly CVarDef<string> DebugBind =
        CVarDef.Create("ai.debug_bind", "127.0.0.1:9080", CVar.SERVERONLY);

    /// <summary>
    /// Bearer token for the debug endpoint. Empty means the server refuses to bind at all.
    ///
    /// Not optional, and the refusal is deliberate: <c>/state</c> returns the whole conversation,
    /// the agent's memory and its soul, and <c>/command</c> can put words in its mouth. An
    /// unauthenticated version of this endpoint is a metagame oracle for any player who finds it.
    ///
    /// ASCII only. It travels in an <c>Authorization</c> header, and header values are ASCII — a
    /// client handed a Cyrillic token throws before the request is even sent, which presents as an
    /// endpoint answering 401 to a token that looks right. The server refuses to bind on one.
    /// </summary>
    public static readonly CVarDef<string> DebugToken =
        CVarDef.Create("ai.debug_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    // ------------------------------------------------------------- режим «злой ИИ»

    /// <summary>
    /// Раздавать ли ИИ двери, которых он штатно не касается: бластдвери, ставни, часть внешних
    /// шлюзов.
    ///
    /// Все три ручки ниже <b>перекрывают прототип правила</b>, а не дополняют его: включённое в
    /// прототипе можно выключить отсюда, но не наоборот. Так и задумано — это аварийный тормоз на
    /// живом сервере, а не второй набор настроек режима. Читаются на раздаче должностей, то есть
    /// вступают в силу со следующего раунда.
    /// </summary>
    public static readonly CVarDef<bool> RogueGrantDoors =
        CVarDef.Create("ai.rogue_grant_doors", true, CVar.SERVERONLY);

    /// <summary>Раздавать ли доступ ко всему, у чего есть интерфейс: консоли, вентили, панели.</summary>
    public static readonly CVarDef<bool> RogueGrantConsoles =
        CVarDef.Create("ai.rogue_grant_consoles", true, CVar.SERVERONLY);

    /// <summary>Раздавать ли турели и их панели управления.</summary>
    public static readonly CVarDef<bool> RogueGrantTurrets =
        CVarDef.Create("ai.rogue_grant_turrets", true, CVar.SERVERONLY);

    /// <summary>
    /// Ставить ли на станцию киборгов поддержки, перечисленных в прототипе правила.
    ///
    /// Тот же аварийный тормоз: выключить перечисленное в прототипе отсюда можно, включить
    /// невыключенное — нет. Отдельная ручка от раздачи доступа потому, что и отказывают они по
    /// разному: доступ можно оставить, а роботов убрать, если вечер идёт слишком тяжело для
    /// экипажа.
    /// </summary>
    public static readonly CVarDef<bool> RogueSupportBorgs =
        CVarDef.Create("ai.rogue_support_borgs", true, CVar.SERVERONLY);

    /// <summary>
    /// В открытом режиме выдавать ассистента и тем, кто просил «оставить в лобби, если должность
    /// занята».
    ///
    /// Без этого закрытие должностей избирательно не пускает на сервер часть игроков — с их точки
    /// зрения без всякой причины. Выключается, если окажется, что лечение хуже болезни: тогда
    /// такие игроки просто останутся в лобби, как и просили.
    /// </summary>
    public static readonly CVarDef<bool> RogueForceOverflow =
        CVarDef.Create("ai.rogue_force_overflow", true, CVar.SERVERONLY);

    // ------------------------------------------------------------------ режим скрипта

    /// <summary>
    /// Инструменты идут через обёртку на Lua, а не отдельными вызовами модели.
    ///
    /// <para>
    /// Зачем. Замер боевого прогона борга: 661 обращение к модели на 680 вызовов инструментов —
    /// ровно один круг через LLM на каждое элементарное действие, по 14 секунд и 41k промпт-токенов
    /// за «шагни на тайл». Сборка АМЭ в такой арифметике не помещается в раунд. В режиме скрипта
    /// модель пишет программу, и цикл «дойти, взять, донести, распаковать» стоит одного обращения.
    /// </para>
    /// <para>
    /// Режим — свойство агента, и он ровно один: набор инструментов либо классический, либо
    /// скриптовый. Отдельному телу переключается полем <c>AgentBody.ScriptMode</c>, чтобы можно
    /// было держать ядро на классическом наборе, а борга — на скриптах, и сравнивать.
    /// </para>
    /// <para>
    /// <b>С 20.08.2026 умолчание — включено.</b> Решение владельца. Прежнее <c>false</c> было
    /// осторожностью новой фичи, а не выводом из замеров: замеры как раз против него — один круг
    /// через модель на каждый шаг по тайлу стоит 14 секунд и 41k токенов, и на четырёх агентах
    /// разом это уже не «дороже», а «не помещается в раунд». Откат — <c>cvar ai.script_mode
    /// false</c> на живом сервере, без пересборки; действует со следующей занятой сессии, потому
    /// что режим решается один раз при сборке тела (см. <c>AiBorgSystem.Prompt</c>).
    /// </para>
    /// </summary>
    public static readonly CVarDef<bool> ScriptMode =
        CVarDef.Create("ai.script_mode", true, CVar.SERVERONLY);

    /// <summary>
    /// Сколько ждать скрипт, прежде чем отпустить его в фон.
    ///
    /// Короткий скрипт обязан ответить в том же вызове — иначе модель платит лишний круг за
    /// «посмотри вокруг». Длинный уходит в фон и досылает итог наблюдением.
    /// </summary>
    public static readonly CVarDef<int> ScriptForegroundMs =
        CVarDef.Create("ai.script_foreground_ms", 1000, CVar.SERVERONLY);

    /// <summary>
    /// Сколько скриптов может идти одновременно.
    ///
    /// Тело у агента одно, и два скрипта, оба двигающие его, — это не параллелизм, а драка за
    /// ноги. Двойка оставляет место наблюдающему скрипту рядом с работающим.
    /// </summary>
    public static readonly CVarDef<int> ScriptMaxProcesses =
        CVarDef.Create("ai.script_max_processes", 2, CVar.SERVERONLY);

    /// <summary>
    /// Потолок жизни скрипта в реальных секундах — в отличие от будильников, которые живут в
    /// раундовом времени. На паузе раундовые часы стоят, и раундовый потолок не наступил бы
    /// никогда как раз тогда, когда он нужен.
    /// </summary>
    public static readonly CVarDef<int> ScriptMaxSeconds =
        CVarDef.Create("ai.script_max_seconds", 300, CVar.SERVERONLY);

    /// <summary>Сколько инструментов один скрипт может позвать. Предохранитель, а не регулятор темпа.</summary>
    public static readonly CVarDef<int> ScriptMaxCalls =
        CVarDef.Create("ai.script_max_calls", 400, CVar.SERVERONLY);

    /// <summary>Потолок инструкций Lua: единственная защита от <c>while true do end</c>.</summary>
    public static readonly CVarDef<int> ScriptMaxSteps =
        CVarDef.Create("ai.script_max_steps", 5_000_000, CVar.SERVERONLY);

    /// <summary>Сколько строк вывода держать на процесс; старые вытесняются с отметкой о потере.</summary>
    public static readonly CVarDef<int> ScriptOutputLines =
        CVarDef.Create("ai.script_output_lines", 200, CVar.SERVERONLY);

    /// <summary>
    /// Завершать раунд, когда с сервера ушёл последний игрок.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без этого пустой сервер остаётся в том же раунде навсегда, и первый зашедший через сутки
    /// попадает не на новую смену, а в остывший труп предыдущей: станция разгерметизирована,
    /// экипаж мёртв, злой ИИ уже победил. Формально раунд идёт, играть в нём нечего.
    /// </para>
    /// <para>
    /// Токены при этом не горят и без нас: <c>game.auto_pause_empty</c> останавливает симуляцию, а
    /// сборка наблюдения на паузе возвращает «нечего делать» (см. <c>BuildObservationAsync</c>).
    /// То есть эта настройка не про расход, а про то, чтобы следующий игрок получил чистую смену.
    /// </para>
    /// </remarks>
    public static readonly CVarDef<bool> EndRoundWhenEmpty =
        CVarDef.Create("ai.end_round_when_empty", true, CVar.SERVERONLY);

    /// <summary>
    /// Завершать раунд, когда станционный ИИ убит. Работает только в режимах злого ИИ.
    /// </summary>
    /// <remarks>
    /// Смысл ровно тот же, что у ядерной бомбы в апстримовом режиме операции: антагонист один, и
    /// когда его не стало, играть больше не во что. Экипаж без допусков, с мёртвым ИИ и тремя
    /// вставшими роботами будет просто ждать шаттла. Вне режимов злого ИИ проверка не действует —
    /// в обычной смене гибель ИИ это происшествие, а не конец истории.
    /// </remarks>
    public static readonly CVarDef<bool> EndRoundOnAiDeath =
        CVarDef.Create("ai.end_round_on_ai_death", true, CVar.SERVERONLY);

    /// <summary>
    /// Держать тела роботов вне обычных ограничений дальности репликации (PVS).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ВЫКЛЮЧЕНО, И ЭТО ЗАМЕР, А НЕ ОСТОРОЖНОСТЬ.</b> Правка родилась 20.08.2026 как лечение
    /// петли полных ресинков и сделала ровно обратное. Один и тот же игрок, два раунда подряд:
    /// 11 ресинков за 10 100 тиков без неё (1.1 на тысячу) против 89 за 5 100 с ней (17.4 на
    /// тысячу) — <b>в шестнадцать раз хуже</b>. Медианное отставание клиента выросло с 10 тиков до
    /// 36. В журнале раунда 162 первый ресинк стоит через восемнадцать строк после захвата тела и
    /// дальше не прекращается, при том что роботы за весь раунд не сделали ни шага.
    /// </para>
    /// <para>
    /// <b>Почему стало хуже.</b> Постоянная репликация не убирает вход сущности в зону видимости,
    /// а делает его вечным. Разбор <c>PvsSystem.Overrides.cs</c>: список постоянных сущностей
    /// раскрывается вместе со ВСЕМ поддеревом (у инженерного тела это 26 сущностей — модули и
    /// предметы в их руках), перебирается для каждой сессии, обрабатывается ДО обычных чанков
    /// (<c>PvsSystem.cs:321</c> против <c>:324</c>) и — в отличие от <c>AddPvsChunk</c> — не имеет
    /// ни проверки корня, ни выхода по исчерпании бюджета. То есть поддеревья роботов первыми
    /// съедают бюджет входа, вытесняя из состояния остальную станцию.
    /// </para>
    /// <para>
    /// Поле оставлено намеренно: стенду нужен отрицательный контроль. Воспроизведение, которое не
    /// умеет показать ухудшение от заведомо вредной правки, ничего не доказывает и про полезную.
    /// </para>
    /// </remarks>
    public static readonly CVarDef<bool> BorgPvsOverride =
        CVarDef.Create("ai.borg_pvs_override", false, CVar.SERVERONLY);

    /// <summary>
    /// Печатать в журнал шаги робота строками <c>NET TRACE kind=borg_move</c>. Ноль — молчать.
    /// </summary>
    /// <remarks>
    /// Прежде читался движковый <c>net.pvs_trace</c> из форкового патча PVS. Патчи движка сняты
    /// 30.08.2026 ради замера на чистом апстриме, и вместе с ними исчез тот cvar — а трасса
    /// движения нужна независимо от того, пропатчен движок или нет.
    /// </remarks>
    public static readonly CVarDef<int> BorgMoveTrace =
        CVarDef.Create("ai.borg_move_trace", 0, CVar.SERVERONLY);
}
