namespace Content.Server.AiAgent.Llm;

/// <summary>
/// Какие поля запроса провайдер вообще способен принять.
///
/// <para>
/// Один <see cref="ChatRequestDto"/> обслуживает всех, и исторически он посылал объединение
/// расширений llama.cpp и DeepSeek сразу: <c>top_k</c> и <c>min_p</c> (не параметры OpenAI),
/// <c>cache_prompt</c> и <c>id_slot</c> (чисто llama-server), <c>thinking</c> (чисто DeepSeek).
/// Пока эндпоинт был один, это работало: llama-server игнорирует незнакомые поля молча. Строгая
/// API так себя не ведёт — она отвечает 400, и до этой таблицы отличить «провайдер лежит» от
/// «провайдер не понял четвёртое поле» было нечем.
/// </para>
/// <para>
/// Таблица описывает <b>что можно послать</b>, а не что умеет модель. Умеет ли конкретная модель
/// думать — решает её собственный конфиг; здесь только вопрос, переживёт ли эндпоинт поле в теле.
/// </para>
/// </summary>
public enum LlmDialect
{
    /// <summary>llama.cpp / llama-server напрямую или через llama-swap.</summary>
    LlamaCpp,

    /// <summary>api.deepseek.com — OpenAI плюс собственный объект <c>thinking</c>.</summary>
    DeepSeek,

    /// <summary>Строгий OpenAI-совместимый эндпоинт: только то, что описано у OpenAI.</summary>
    OpenAiCompat,
}

/// <summary>
/// Правила из <see cref="LlmDialect"/> в исполняемом виде.
///
/// Отдельным типом, а не свойствами на прототипе, ровно по одной причине: тест
/// <c>LlmRouterTests</c> сверяет их с сериализованным телом запроса, и правило должно быть в одном
/// месте, а не продублировано в прототипе и в клиенте.
/// </summary>
public static class LlmDialectRules
{
    /// <summary><c>top_k</c> и <c>min_p</c> — сэмплеры llama.cpp, у OpenAI таких параметров нет.</summary>
    public static bool AllowsSamplerExtras(LlmDialect dialect) => dialect == LlmDialect.LlamaCpp;

    /// <summary><c>cache_prompt</c> — расширение llama.cpp.</summary>
    public static bool AllowsCachePrompt(LlmDialect dialect) => dialect == LlmDialect.LlamaCpp;

    /// <summary><c>id_slot</c> — закрепление за слотом llama-server.</summary>
    public static bool AllowsIdSlot(LlmDialect dialect) => dialect == LlmDialect.LlamaCpp;

    /// <summary>Объект <c>thinking</c> — расширение DeepSeek; их же SDK прячет его в <c>extra_body</c>.</summary>
    public static bool AllowsThinking(LlmDialect dialect) => dialect == LlmDialect.DeepSeek;

    /// <summary>
    /// <c>reasoning_effort</c> верхнего уровня — форма OpenAI.
    ///
    /// Локальному llama.cpp его посылать бессмысленно и вредно: llama-server принимает поле в теле
    /// и молча его игнорирует, потому что уровень усилия там задаётся флагом запуска
    /// <c>--chat-template-kwargs</c>. То есть выставленное здесь значение выглядело бы рабочим и не
    /// делало ничего — худший вид настройки.
    /// </summary>
    public static bool AllowsReasoningEffort(LlmDialect dialect) => dialect == LlmDialect.OpenAiCompat;
}
