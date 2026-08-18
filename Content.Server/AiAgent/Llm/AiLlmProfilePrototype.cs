using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Llm;

/// <summary>Как оплачивается обращение к профилю — это меняет реакцию роутера на отказ.</summary>
public enum LlmQuotaKind
{
    /// <summary>Своё железо. Исчерпать нельзя, платить не надо.</summary>
    Free,

    /// <summary>Оплата по токенам. Исчерпание означает пустой счёт, а не окно.</summary>
    Metered,

    /// <summary>
    /// Подписка. Исчерпание — <b>нормальное состояние</b>, а не ошибка.
    ///
    /// У Codex квота считается в обращениях за окно (250–2000 к Luna за пять часов; недельный
    /// потолок есть, чисел OpenAI не публикует), у Grok Build — один недельный пул на все продукты,
    /// и xAI не публикует вообще ничего. Планировать наперёд нельзя — можно только реагировать на
    /// 429 длинным сном до сброса и мерить расход самим.
    /// </summary>
    Subscription,
}

/// <summary>Через что идёт исходящий трафик этого профиля.</summary>
public enum LlmProxyMode
{
    /// <summary>Напрямую. Обязательно для loopback: локальный порт через немецкий выход не найдётся.</summary>
    None,

    /// <summary>Через SOCKS из <c>ai.llm_socks_proxy</c>.</summary>
    Socks,
}

/// <summary>Форма протокола. Заведено на вырост — см. комментарий на <see cref="AiLlmProfilePrototype.Transport"/>.</summary>
public enum LlmTransport
{
    /// <summary>Обычный <c>POST /chat/completions</c>, не-стрим.</summary>
    ChatCompletions,
}

/// <summary>Откуда узнавать реальный размер контекста.</summary>
public enum LlmCtxProbe
{
    /// <summary>Не спрашивать, брать <see cref="AiLlmProfilePrototype.CtxLimit"/>.</summary>
    None,

    /// <summary><c>GET /props?model=…</c> — умеет только llama-server (и llama-swap как прокси).</summary>
    Props,
}

/// <summary>
/// Один провайдер модели: куда стучаться, чем платить и какие поля он переживёт.
///
/// <para>
/// <b>Прототип, а не CVar-строка.</b> Профилей четыре и у каждого десяток полей; в TOML это стало бы
/// либо десятками плоских ключей вида <c>ai.deepseek_ctx_limit</c>, либо JSON в строке. Прототип
/// валидируется на старте, переживает правку без пересборки и повторяет уже узаконенный в форке
/// приём — <see cref="AiBackupPowerPrototype"/> с таблицей в
/// <c>Resources/Prototypes/_AiAgent/backup_power.yml</c>.
/// </para>
/// <para>
/// <b>Секретов здесь быть не может.</b> <c>Content.Server/Acz/ContentMagicAczProvider.cs</c> раздаёт
/// всю папку <c>Resources/</c> каждому подключившемуся игроку — ключ, положенный в этот YAML,
/// уехал бы к первому же зашедшему. Поэтому <see cref="KeyFile"/> хранит <b>имя файла</b> внутри
/// <c>ai_data/</c>, а не значение.
/// </para>
/// <para>
/// Порядок профилей задаётся не здесь, а в <c>ai.llm_chain</c>: цепочка — это операционное решение,
/// которое надо менять из консоли живого сервера, а не правкой данных с перезапуском.
/// </para>
/// </summary>
[Prototype]
public sealed partial class AiLlmProfilePrototype : IPrototype
{
    /// <summary>Короткое имя для <c>ai.llm_chain</c> и для <c>aiagent llm</c>: <c>local</c>, <c>codex</c>.</summary>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Базовый URL, уже включающий <c>/v1</c>. Например <c>http://127.0.0.1:9292/v1</c>.</summary>
    [DataField(required: true)]
    public string Endpoint = string.Empty;

    /// <summary>Имя модели в том виде, в каком его ждёт эндпоинт.</summary>
    [DataField(required: true)]
    public string Model = string.Empty;

    /// <summary>Какие поля запроса эндпоинт переживёт. См. <see cref="LlmDialect"/>.</summary>
    [DataField]
    public LlmDialect Dialect = LlmDialect.OpenAiCompat;

    /// <summary>
    /// Форма протокола. Сейчас у всех одна, и это сознательно.
    ///
    /// Поле заведено заранее, чтобы будущий свой адаптер Responses API встал как ещё одно значение,
    /// а не как переделка роутера. Пока подписки ходят через локальный мост, который сам переводит
    /// протокол, и с точки зрения игры они — обычный OpenAI-совместимый эндпоинт на loopback.
    /// </summary>
    [DataField]
    public LlmTransport Transport = LlmTransport.ChatCompletions;

    /// <summary>Чем платим. Меняет длину сна после 429 и то, считать ли деньги.</summary>
    [DataField]
    public LlmQuotaKind Quota = LlmQuotaKind.Metered;

    /// <summary>Через что идёт трафик. Для loopback обязательно <see cref="LlmProxyMode.None"/>.</summary>
    [DataField]
    public LlmProxyMode Proxy = LlmProxyMode.None;

    /// <summary>
    /// Имя файла с ключом внутри <c>ai.data_dir</c> — например <c>deepseek.key</c>. Не значение.
    ///
    /// Пусто — берётся <c>ai.api_key</c>, чтобы одиночная настройка «эндпоинт из TOML» продолжала
    /// работать без всякого YAML.
    /// </summary>
    [DataField]
    public string KeyFile = string.Empty;

    /// <summary>Откуда узнавать размер контекста.</summary>
    [DataField]
    public LlmCtxProbe CtxProbe = LlmCtxProbe.None;

    /// <summary>
    /// Размер контекста, когда спросить некого. 0 — неизвестно.
    ///
    /// Указывать обязательно для всего, кроме llama-server: без него
    /// <c>EffectiveCompactHigh</c> молча садится на печатное <c>ai.compact_high</c>, и на модели с
    /// контекстом в четыреста тысяч токенов агент компактится так же часто, как на локальной.
    /// </summary>
    [DataField]
    public int CtxLimit;

    /// <summary>Свой порог компакции. 0 — брать <c>ai.compact_high</c>.</summary>
    [DataField]
    public int CompactHigh;

    /// <summary>Свой таймаут запроса, секунды. 0 — брать <c>ai.request_timeout</c>.</summary>
    [DataField]
    public float TimeoutSeconds;

    /// <summary>
    /// Сообщает ли провайдер, сколько промпта пришло из кэша.
    ///
    /// Врать здесь дорого в обе стороны. Если поставить <c>true</c> провайдеру, который ничего не
    /// сообщает, <see cref="Content.Server.AiAgent.Context.CacheMetrics"/> начнёт непрерывно писать
    /// ERROR «префикс-кэш сломан» — и обесценит алярм, который заведён ловить настоящую поломку.
    /// </summary>
    [DataField]
    public bool ReportsCache = true;

    /// <summary>
    /// Уровень усилия для <c>thinking.reasoning_effort</c> (DeepSeek) или для
    /// <c>reasoning_effort</c> верхнего уровня (строгий OpenAI). Пусто — брать
    /// <c>ai.thinking_effort</c>.
    ///
    /// Что именно из этого уйдёт в провод, решает диалект: посылать оба поля сразу нельзя.
    /// </summary>
    [DataField]
    public string ReasoningEffort = string.Empty;

    // ------------------------------------------------------------------ деньги

    /// <summary>Цена миллиона входных токенов при промахе кэша, USD. 0 — не считать.</summary>
    [DataField]
    public float PriceInPer1M;

    /// <summary>Цена миллиона входных токенов при попадании в кэш, USD.</summary>
    [DataField]
    public float PriceCachedInPer1M;

    /// <summary>Цена миллиона выходных токенов, USD.</summary>
    [DataField]
    public float PriceOutPer1M;

    // ------------------------------------------------------------------- квота

    /// <summary>
    /// Длина окна квоты в часах — только для учёта, не для ограничения.
    ///
    /// Пять часов, потому что окно Codex такое. Мы не умеем узнать потолок у вендора, зато умеем
    /// посчитать свой расход в том же окне и наконец увидеть, укладываются ли наши ~148 обращений
    /// в заявленные 250–2000.
    /// </summary>
    [DataField]
    public float QuotaWindowHours = 5f;

    /// <summary>
    /// Сколько спать после 429, если провайдер не сказал, когда сброс. Секунды. 0 — брать
    /// <c>ai.llm_quota_cooldown_seconds</c>.
    /// </summary>
    [DataField]
    public float QuotaCooldownSeconds;
}

/// <summary>
/// То же, что <see cref="AiLlmProfilePrototype"/>, но без прототипной обвязки.
///
/// Существует ради проверяемости, и причина конкретная: <c>ID</c> у прототипа имеет приватный
/// сеттер, потому что его заполняет сериализатор, — а значит собрать профиль из теста нельзя, и
/// проверить цепочку падений можно было бы только поднимая сервер с полным набором прототипов. Для
/// логики, где важны сны, квоты и порядок обхода, это неподъёмная цена: такие тесты медленные, и
/// именно поэтому их не пишут.
///
/// Заодно это правильная граница: роутеру нужно десять полей, а не тип из слоя данных.
/// </summary>
public sealed record LlmProfileConfig(
    string Id,
    string Model,
    LlmQuotaKind Quota,
    int CompactHigh = 0,
    float QuotaWindowHours = 5f,
    float QuotaCooldownSeconds = 0f,
    float PriceInPer1M = 0f,
    float PriceCachedInPer1M = 0f,
    float PriceOutPer1M = 0f)
{
    public static LlmProfileConfig From(AiLlmProfilePrototype p) => new(
        p.ID,
        p.Model,
        p.Quota,
        p.CompactHigh,
        p.QuotaWindowHours,
        p.QuotaCooldownSeconds,
        p.PriceInPer1M,
        p.PriceCachedInPer1M,
        p.PriceOutPer1M);
}
