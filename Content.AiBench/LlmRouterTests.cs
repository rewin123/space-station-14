using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// Цепочка провайдеров: порядок обхода, липкость, сны и то, что уходит в провод.
///
/// <para>
/// Тесты намеренно без поднятого сервера. Проверяемое здесь — политика отказов и сериализация, а
/// цена интеграционного теста (полный сервер с прототипами) для такой логики означала бы, что её
/// просто не будут проверять. Ради этого роутер и принимает <see cref="LlmProfileConfig"/> вместо
/// прототипа, часы через делегат, а фабрику клиентов — параметром.
/// </para>
/// <para>
/// Там, где проверяется <em>провод</em>, тест поднимает настоящий <see cref="HttpListener"/> на
/// loopback и смотрит на пришедшее тело. Иначе пришлось бы повторить в тесте ту же логику сборки
/// запроса и проверить её саму на себе — а сломать надо ровно то, что до профилей уходило всегда:
/// <c>top_k</c>, <c>min_p</c>, <c>cache_prompt</c> и <c>id_slot</c> в адрес провайдера, который
/// отвечает на неизвестное поле кодом 400.
/// </para>
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class LlmRouterTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("llm-router-test");

    // ----------------------------------------------------------------- заготовки

    /// <summary>Клиент, отвечающий по сценарию и считающий обращения.</summary>
    private sealed class FakeClient : ILlmClient
    {
        private readonly Queue<Func<LlmResponse>> _script = new();
        private readonly string _id;

        public FakeClient(string id) => _id = id;

        public int Calls { get; private set; }

        public FakeClient Then(Func<LlmResponse> step)
        {
            _script.Enqueue(step);
            return this;
        }

        public FakeClient Ok(int prompt = 1000, int cached = 900, string content = "ок")
            => Then(() => new LlmResponse(content, Array.Empty<ToolCallDto>(), prompt, cached, 20, 0.1,
                Profile: _id));

        public FakeClient Empty()
            => Then(() => new LlmResponse(null, Array.Empty<ToolCallDto>(), 1000, 900, 0, 0.1, Profile: _id));

        public FakeClient Http(int code, string body = "", DateTime? retryAfter = null)
            => Then(() => throw new LlmHttpException(code, body, retryAfter, $"HTTP {code}"));

        public FakeClient Timeout()
            => Then(() => throw new OperationCanceledException("таймаут"));

        public FakeClient Disposed()
            => Then(() => throw new ObjectDisposedException("HttpClient"));

        /// <summary>Повторять последний шаг сценария бесконечно.</summary>
        public bool Repeat { get; set; } = true;

        private Func<LlmResponse>? _last;

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessageDto> messages,
            IReadOnlyList<ToolDto>? tools,
            CancellationToken ct)
        {
            Calls++;

            if (_script.Count > 0)
                _last = _script.Dequeue();
            else if (!Repeat)
                throw new InvalidOperationException($"{_id}: сценарий кончился");

            return Task.FromResult(_last!());
        }

        public Task<int?> GetContextSizeAsync(CancellationToken ct) => Task.FromResult<int?>(131072);
    }

    private sealed class Harness : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "ai-router-" + Guid.NewGuid().ToString("N"));
        public DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        public readonly Dictionary<string, FakeClient> Clients = new();

        public Harness() => Directory.CreateDirectory(Dir);

        public LlmQuotaState NewState() => new(Dir, Sawmill, () => Now);

        public RoutingLlmClient Build(LlmQuotaState state, params string[] ids)
        {
            var chain = new List<(LlmProfileConfig, LlmEndpoint, LlmSampling)>();

            foreach (var id in ids)
            {
                Clients.TryAdd(id, new FakeClient(id));

                chain.Add((
                    new LlmProfileConfig(id, "model-" + id, LlmQuotaKind.Subscription, QuotaWindowHours: 5f),
                    new LlmEndpoint(id, "http://127.0.0.1:1/v1", "model-" + id, "", LlmDialect.OpenAiCompat,
                        TimeSpan.FromSeconds(30)),
                    new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, null)));
            }

            return new RoutingLlmClient(
                chain,
                state,
                new LlmRouterOptions(CooldownSeconds: 300, QuotaCooldownSeconds: 3600, RecheckSeconds: 300,
                    TotalTimeoutSeconds: 240),
                Sawmill,
                () => Now,
                (endpoint, _) => Clients[endpoint.Id]);
        }

        public FakeClient C(string id)
        {
            Clients.TryAdd(id, new FakeClient(id));
            return Clients[id];
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch
            {
                // Временная папка — если не удалилась, тест это не должен ронять.
            }
        }
    }

    private static Task<LlmResponse> Ask(ILlmClient client) =>
        client.ChatAsync(new[] { ChatMessageDto.User("привет") }, null, CancellationToken.None);

    // -------------------------------------------------------------- обход цепочки

    [Test]
    public async Task WalksTheChainAndSleepsWhatFailed()
    {
        using var h = new Harness();
        h.C("primary").Http(503, "upstream down");
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        var response = await Ask(router);

        Assert.That(response.Profile, Is.EqualTo("backup"), "ответить должен был второй в цепочке");
        Assert.That(router.CurrentProfile, Is.EqualTo("backup"));
        Assert.That(state.IsAvailable("primary", out var why), Is.False, "упавший профиль должен спать");
        Assert.That(why, Does.Contain("503"));
    }

    [Test]
    public async Task StaysOnTheFallbackInsteadOfDriftingBack()
    {
        using var h = new Harness();
        h.C("primary").Http(503);
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        await Ask(router);
        var primaryCalls = h.C("primary").Calls;

        // Три хода спустя, но раньше ai.llm_recheck_seconds.
        h.Now = h.Now.AddSeconds(30);
        await Ask(router);
        await Ask(router);
        await Ask(router);

        Assert.That(h.C("primary").Calls, Is.EqualTo(primaryCalls),
            "главный профиль не должен пробоваться заново до истечения ai.llm_recheck_seconds: " +
            "каждое переключение стоит полного prefill на новой стороне");
        Assert.That(h.C("backup").Calls, Is.EqualTo(4));
    }

    [Test]
    public async Task ComesBackToTheMainProfileAfterTheRecheckInterval()
    {
        using var h = new Harness();
        h.C("primary").Http(503).Ok();
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        await Ask(router);
        Assert.That(router.CurrentProfile, Is.EqualTo("backup"));

        // Позже и порога проверки, и сна упавшего профиля.
        h.Now = h.Now.AddSeconds(400);
        var response = await Ask(router);

        Assert.That(response.Profile, Is.EqualTo("primary"), "после паузы главный профиль обязан быть проверен");
    }

    // --------------------------------------------------------------------- квота

    [Test]
    public async Task QuotaSleepsUntilTheResetTheProviderNamed()
    {
        using var h = new Harness();
        var reset = h.Now.AddMinutes(90);
        h.C("primary").Http(429, "rate limit", reset);
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        await Ask(router);

        // Час спустя — то есть уже после дефолтного ai.llm_quota_cooldown_seconds, но раньше
        // названного провайдером срока.
        h.Now = h.Now.AddMinutes(61);
        Assert.That(state.IsAvailable("primary", out _), Is.False,
            "срок из Retry-After должен побеждать дефолтный час: у подписки каждая проба — обращение, " +
            "то есть трата ровно того, чего уже нет");

        h.Now = reset.AddSeconds(1);
        Assert.That(state.IsAvailable("primary", out _), Is.True);
    }

    [Test]
    public async Task QuotaSleepSurvivesTheClientBeingRebuilt()
    {
        using var h = new Harness();
        h.C("primary").Http(429, "quota exhausted");
        h.C("backup").Ok();

        var state = h.NewState();
        using (var router = h.Build(state, "primary", "backup"))
        {
            await Ask(router);
        }

        var callsBefore = h.C("primary").Calls;

        // Ровно то, что делает ResetLlmClient на каждом рестарте раунда: новый клиент, новый роутер,
        // прочитанное с диска состояние. Раундов за сутки десятки, и без персиста каждый из них
        // заново лез бы в исчерпанную подписку.
        var reread = h.NewState();
        using var second = h.Build(reread, "primary", "backup");
        var response = await Ask(second);

        Assert.That(response.Profile, Is.EqualTo("backup"));
        Assert.That(h.C("primary").Calls, Is.EqualTo(callsBefore),
            "после рестарта роутера спящий профиль не должен пробоваться вовсе");
    }

    // ------------------------------------------------------------------ перелогин

    [Test]
    public async Task ReloginMarksTheProfileDeadAndStopsRetrying()
    {
        using var h = new Harness();

        // Ровно тот текст, каким это пришло на живой машине: одноразовый refresh-токен успел
        // использовать другой клиент.
        h.C("primary").Http(401, "Codex refresh token was already consumed by another client");
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        await Ask(router);
        var calls = h.C("primary").Calls;

        Assert.That(state.IsAvailable("primary", out var why), Is.False);
        Assert.That(why, Does.Contain("401"));

        // Сутки спустя — «мёртв» само не проходит, в отличие от сна.
        h.Now = h.Now.AddDays(1);
        await Ask(router);

        Assert.That(h.C("primary").Calls, Is.EqualTo(calls),
            "перелогин сам не случится, и повторы в пустоту не помогут — нужен человек");

        Assert.That(router.Revive("primary", out _), Is.True);
        Assert.That(state.IsAvailable("primary", out _), Is.True, "aiagent llm revive должен снимать метку");
    }

    // ----------------------------------------------------- что НЕ повод для смены

    [Test]
    public async Task TruncationIsNotAReasonToSwitch()
    {
        using var h = new Harness();
        h.C("primary").Then(() => new LlmResponse(
            "не дописал", Array.Empty<ToolCallDto>(), 1000, 900, 4096, 1.0,
            FinishReason: "length", Profile: "primary"));
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        var response = await Ask(router);

        Assert.That(response.Truncated, Is.True);
        Assert.That(response.Profile, Is.EqualTo("primary"),
            "обрезка по max_tokens — наша проблема бюджета, у другого провайдера она воспроизведётся так же");
        Assert.That(h.C("backup").Calls, Is.Zero);
        Assert.That(state.IsAvailable("primary", out _), Is.True);
    }

    [Test]
    public async Task EmptyAnswerIsRetriedOnceThenHandedOver()
    {
        using var h = new Harness();
        h.C("primary").Empty().Empty();
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        var response = await Ask(router);

        Assert.That(h.C("primary").Calls, Is.EqualTo(2), "один повтор на месте, не больше и не меньше");
        Assert.That(response.Profile, Is.EqualTo("backup"));

        // Пустой ответ — не признак того, что провайдер лежит, так что спать он не должен: иначе
        // одна неудача семплирования выключала бы главную модель на пять минут.
        Assert.That(state.IsAvailable("primary", out _), Is.True);
    }

    [Test]
    public void EmptyAnswerNeverBecomesAResponse()
    {
        using var h = new Harness();
        h.C("only").Empty();
        h.C("only").Repeat = true;

        var state = h.NewState();
        using var router = h.Build(state, "only");

        // Пустое assistant-сообщение в истории — реальный инцидент: после него DeepSeek отвечал
        // HTTP 400 на все последующие запросы до конца раунда. Лучше отказ, который цикл агента
        // умеет пережить, чем ответ, который отравит диалог.
        Assert.ThrowsAsync<LlmException>(async () => await Ask(router));
    }

    [Test]
    public void WholeChainDownReportsEveryReasonAtOnce()
    {
        using var h = new Harness();
        h.C("a").Http(503, "boom");
        h.C("b").Timeout();
        h.C("c").Http(429, "no quota");

        var state = h.NewState();
        using var router = h.Build(state, "a", "b", "c");

        var e = Assert.ThrowsAsync<LlmException>(async () => await Ask(router))!;

        Assert.That(e.Message, Does.Contain("a:").And.Contain("b:").And.Contain("c:"),
            "четыре отдельных ERROR'а в журнале читаются как четыре инцидента — причины должны быть рядом");
    }

    // ------------------------------------------------------------ ручной выбор

    [Test]
    public async Task ManualPinIsTriedEvenWhileSleeping()
    {
        using var h = new Harness();
        h.C("primary").Http(503).Ok();
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        await Ask(router);
        Assert.That(state.IsAvailable("primary", out _), Is.False);

        Assert.That(router.TryUse("primary", out _), Is.True);
        var response = await Ask(router);

        Assert.That(response.Profile, Is.EqualTo("primary"), "смысл ручного закрепления — настоять");
        Assert.That(router.TryUse("нетакого", out var why), Is.False);
        Assert.That(why, Does.Contain("primary"));
    }

    // --------------------------------------------------------------- учёт расхода

    [Test]
    public async Task CountsCallsAndMoneySeparately()
    {
        using var h = new Harness();
        h.C("paid").Ok(prompt: 50_000, cached: 49_000);

        var state = h.NewState();
        var chain = new List<(LlmProfileConfig, LlmEndpoint, LlmSampling)>
        {
            (new LlmProfileConfig("paid", "deepseek-v4-flash", LlmQuotaKind.Metered,
                    PriceInPer1M: 0.44f, PriceCachedInPer1M: 0.014f, PriceOutPer1M: 1.32f),
                new LlmEndpoint("paid", "http://127.0.0.1:1/v1", "deepseek-v4-flash", "", LlmDialect.DeepSeek,
                    TimeSpan.FromSeconds(30)),
                new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, null)),
        };

        using var router = new RoutingLlmClient(
            chain, state,
            new LlmRouterOptions(300, 3600, 300, 240),
            Sawmill, () => h.Now, (e, _) => h.Clients[e.Id]);

        await Ask(router);

        var snap = state.Snapshot("paid");
        Assert.That(snap.WindowCalls, Is.EqualTo(1));
        Assert.That(snap.WindowTokens, Is.EqualTo(50_020));

        // 1000 промахнувшихся × 0.44 + 49000 из кэша × 0.014 + 20 выданных × 1.32, всё за миллион.
        var expected = 1000 / 1e6 * 0.44 + 49_000 / 1e6 * 0.014 + 20 / 1e6 * 1.32;
        Assert.That(snap.DaySpendUsd, Is.EqualTo(expected).Within(1e-9),
            "промах и попадание в кэш стоят по-разному в тридцать раз — считать их одинаково значит " +
            "ошибиться в счёте на порядок");
    }

    // --------------------------------------------------------- диалект в проводе

    /// <summary>Настоящий HTTP-приёмник: тело запроса проверяется таким, каким его увидит провайдер.</summary>
    private sealed class BodyCatcher : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public string Body { get; private set; } = string.Empty;
        public string Prefix { get; }

        public BodyCatcher(string response)
        {
            var port = FreePort();
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();

            _loop = Task.Run(async () =>
            {
                var ctx = await _listener.GetContextAsync();

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                Body = await reader.ReadToEndAsync();

                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });
        }

        public Task Done => _loop;

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            try
            {
                _listener.Close();
            }
            catch
            {
                // Приёмник мог уже закрыться сам.
            }
        }
    }

    private const string MinimalCompletion =
        """{"choices":[{"finish_reason":"stop","message":{"content":"ок"}}],"usage":{"prompt_tokens":10}}""";

    private static async Task<string> CapturedBody(LlmDialect dialect, string effort)
    {
        using var catcher = new BodyCatcher(MinimalCompletion);

        using var client = new LlamaClient(
            new LlmEndpoint("probe", catcher.Prefix.TrimEnd('/') + "/v1", "m", "", dialect,
                TimeSpan.FromSeconds(15)),
            new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, IdSlot: 0, ThinkingEffort: effort),
            Sawmill);

        await client.ChatAsync(new[] { ChatMessageDto.User("привет") }, null, CancellationToken.None);
        await catcher.Done;

        return catcher.Body;
    }

    // ------------------------------------------- разобранный клиент не хоронит провайдеров

    /// <summary>
    /// Гонка рестарта раунда: ResetLlmClient уже разобрал клиентов, а прощальная компакция ещё
    /// ходит в модель. ObjectDisposedException при этом — смерть экземпляра, а не провайдера, и в
    /// общий (переживающий раунды) счётчик она попадать не должна: иначе свежая цепочка
    /// следующего раунда получает все звенья в кулдауне и три минуты отвечает
    /// «ни один провайдер не ответил за 0с» (наблюдалось живьём 25.08.2026, раунд после 19:11).
    /// </summary>
    [Test]
    public void DisposedClientDoesNotPoisonSharedState()
    {
        using var h = new Harness();
        var state = h.NewState();

        h.C("a").Disposed();
        h.C("b").Ok();

        var oldRouter = h.Build(state, "a", "b");

        // Прощальный вызов через разобранный клиент: исключение уходит наверх как есть...
        Assert.ThrowsAsync<ObjectDisposedException>(() =>
            oldRouter.ChatAsync(new[] { ChatMessageDto.User("проба") }, null, CancellationToken.None));

        // ...и оба звена остаются живыми в общем счётчике: «b» даже не пробовали.
        Assert.Multiple(() =>
        {
            Assert.That(state.IsAvailable("a", out var whyA), Is.True, $"a усыплён: {whyA}");
            Assert.That(state.IsAvailable("b", out var whyB), Is.True, $"b усыплён: {whyB}");
            Assert.That(h.C("b").Calls, Is.Zero, "разобран весь экземпляр — идти по цепочке некуда");
        });

        // Новый раунд: свежие клиенты, тот же счётчик — первый же ход обязан пройти по «a».
        h.Clients["a"] = new FakeClient("a").Ok();
        var newRouter = h.Build(state, "a", "b");

        var response = newRouter.ChatAsync(new[] { ChatMessageDto.User("проба") }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.That(response.Profile, Is.EqualTo("a"), "свежая цепочка не должна наследовать чужую смерть");
    }

    // --------------------------------------------------- vLLM и его null-поля в ответе

    /// <summary>
    /// Ответ vLLM с натуры (25.08.2026, vllm-0.27.1), сокращён только текст рассуждения.
    /// Незаполненные поля протокола vLLM шлёт как <c>null</c>, а не опускает — в каждом ответе.
    /// </summary>
    private const string VllmCompletion =
        """
        {"id":"chatcmpl-93d8b171bd54cb1d","object":"chat.completion","created":1787650614,
         "model":"qwen3.8-27b-awq",
         "choices":[{"index":0,"message":{"role":"assistant","content":null,"refusal":null,
           "annotations":null,"audio":null,"function_call":null,
           "tool_calls":[{"id":"chatcmpl-tool-83644ae4b07001b4","type":"function",
             "function":{"name":"say","arguments":"{\"text\": \"привет\"}"}}],
           "reasoning":"…"},
           "logprobs":null,"finish_reason":"tool_calls","stop_reason":null,
           "token_ids":null,"routed_experts":null}],
         "service_tier":null,"system_fingerprint":"vllm-0.27.1-tp2-ff481821",
         "usage":{"prompt_tokens":311,"total_tokens":411,"completion_tokens":100,
           "prompt_tokens_details":null},
         "prompt_logprobs":null,"prompt_token_ids":null,"prompt_text":null,
         "kv_transfer_params":null,"ec_transfer_params":null,"metrics":null}
        """;

    /// <summary>
    /// Разбор обязан пережить null-поля vLLM — сутки немого ИИ (24–25.08.2026) случились ровно
    /// здесь: <c>"prompt_tokens_details": null</c> ронял каждый ход исключением из
    /// <c>TryGetProperty</c> по Null-элементу, при том что сам запрос проходил с кодом 200.
    /// </summary>
    [Test]
    public async Task VllmNullFieldsDoNotBreakParsing()
    {
        using var catcher = new BodyCatcher(VllmCompletion);

        using var client = new LlamaClient(
            new LlmEndpoint("probe", catcher.Prefix.TrimEnd('/') + "/v1", "m", "", LlmDialect.OpenAiCompat,
                TimeSpan.FromSeconds(15)),
            new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, IdSlot: 0, ThinkingEffort: "low"),
            Sawmill);

        var response = await client.ChatAsync(new[] { ChatMessageDto.User("привет") }, null, CancellationToken.None);
        await catcher.Done;

        Assert.Multiple(() =>
        {
            Assert.That(response.ToolCalls, Has.Count.EqualTo(1), "вызов инструмента должен пережить content:null рядом с собой");
            Assert.That(response.ToolCalls[0].Function.Name, Is.EqualTo("say"));
            Assert.That(response.Content, Is.Null, "content:null — это отсутствие текста, а не ошибка");
            Assert.That(response.PromptTokens, Is.EqualTo(311));
            Assert.That(response.CompletionTokens, Is.EqualTo(100));
            Assert.That(response.CachedTokens, Is.EqualTo(0), "prompt_tokens_details:null означает «неизвестно», то есть ноль");
            Assert.That(response.FinishReason, Is.EqualTo("tool_calls"));
        });
    }

    [Test]
    public async Task DeepSeekNeverSeesLlamaOnlyFields()
    {
        var body = await CapturedBody(LlmDialect.DeepSeek, "low");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Not.Contain("top_k"));
            Assert.That(body, Does.Not.Contain("min_p"));
            Assert.That(body, Does.Not.Contain("cache_prompt"));
            Assert.That(body, Does.Not.Contain("id_slot"));
            Assert.That(body, Does.Contain("\"thinking\""), "объект thinking — единственное, что DeepSeek ждёт сверх OpenAI");

            // Усилие идёт ВНУТРИ thinking, и полем верхнего уровня его дублировать нельзя: два
            // источника одной настройки с непредсказуемым победителем. Проверяется по числу
            // вхождений, потому что подстрока `reasoning_effort` есть и внутри объекта тоже.
            Assert.That(Occurrences(body, "reasoning_effort"), Is.EqualTo(1));
            Assert.That(OrderOfKeys(body), Does.Not.Contain("reasoning_effort"));
        });
    }

    [Test]
    public async Task LlamaCppNeverSeesThinkingOrReasoningEffort()
    {
        var body = await CapturedBody(LlmDialect.LlamaCpp, "low");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("\"top_k\":20"));
            Assert.That(body, Does.Contain("\"min_p\":0.05"));
            Assert.That(body, Does.Contain("\"cache_prompt\":true"));
            Assert.That(body, Does.Contain("\"id_slot\":0"));

            // Уровень размышления у llama.cpp задаётся флагом запуска --chat-template-kwargs, а
            // поле в теле он принимает и молча игнорирует. Настройка, которая выглядит рабочей и
            // ничего не делает, хуже отсутствующей.
            Assert.That(body, Does.Not.Contain("\"thinking\""));
            Assert.That(body, Does.Not.Contain("reasoning_effort"));
        });
    }

    [Test]
    public async Task StrictOpenAiGetsReasoningEffortAtTheTopLevel()
    {
        var body = await CapturedBody(LlmDialect.OpenAiCompat, "low");

        Assert.That(body, Does.Contain("\"reasoning_effort\":\"low\""));
        Assert.That(body, Does.Not.Contain("\"thinking\""));
        Assert.That(body, Does.Not.Contain("top_k"));
    }

    [Test]
    public async Task StrictOpenAiNeverGetsAnEffortLevelItDoesNotKnow()
    {
        // `ai.thinking_effort` один на всех профилей, и «off» осмысленно только у DeepSeek, где это
        // объект {"type":"disabled"}. У OpenAI такого значения нет: `reasoning_effort: "off"` — это
        // HTTP 400 на каждом ходу, и роутер честно счёл бы подписочный профиль несовместимым.
        foreach (var effort in new[] { "off", "none", "xhigh", "чтотопопало" })
        {
            var body = await CapturedBody(LlmDialect.OpenAiCompat, effort);
            Assert.That(body, Does.Not.Contain("reasoning_effort"),
                $"«{effort}» не должен уходить строгому OpenAI — лучше его собственный умолчательный уровень");
        }
    }

    [Test]
    public async Task FieldOrderOnTheWireIsUnchanged()
    {
        var body = await CapturedBody(LlmDialect.LlamaCpp, "");

        // Порядок полей — не косметика: llama.cpp переиспользует KV-кэш до первого разошедшегося
        // токена, и живой сервер держит на этом реюз 97.9%. Эталон зафиксирован, чтобы правка
        // порядка объявления в ChatRequestDto роняла тест, а не производительность.
        var keys = OrderOfKeys(body);

        Assert.That(keys, Is.EqualTo(new[]
        {
            "model", "messages", "parallel_tool_calls", "stream", "temperature", "top_p",
            "top_k", "min_p", "cache_prompt", "id_slot",
        }));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;

        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    /// <summary>Имена полей верхнего уровня в порядке появления.</summary>
    private static List<string> OrderOfKeys(string json)
    {
        var keys = new List<string>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var depth = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.PropertyName when depth == 1:
                    keys.Add(reader.GetString()!);
                    break;
            }
        }

        return keys;
    }

    // ------------------------------------------------------------ Retry-After

    [Test]
    public async Task RetryAfterInSecondsIsUnderstood()
    {
        using var catcher = new BodyCatcher429("120");

        using var client = new LlamaClient(
            new LlmEndpoint("probe", catcher.Prefix.TrimEnd('/') + "/v1", "m", "", LlmDialect.OpenAiCompat,
                TimeSpan.FromSeconds(15)),
            new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, null),
            Sawmill);

        var before = DateTime.UtcNow;
        var e = Assert.ThrowsAsync<LlmHttpException>(async () =>
            await client.ChatAsync(new[] { ChatMessageDto.User("привет") }, null, CancellationToken.None))!;

        Assert.That(e.StatusCode, Is.EqualTo(429));
        Assert.That(e.RetryAfterUtc, Is.Not.Null, "без разбора Retry-After сон после квоты — угадывание");
        Assert.That(e.RetryAfterUtc!.Value, Is.EqualTo(before.AddSeconds(120)).Within(TimeSpan.FromSeconds(10)));

        await catcher.Done;
    }

    /// <summary>Приёмник, отвечающий 429 с заданным <c>Retry-After</c>.</summary>
    private sealed class BodyCatcher429 : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public string Prefix { get; }
        public Task Done => _loop;

        public BodyCatcher429(string retryAfter)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();

            _loop = Task.Run(async () =>
            {
                var ctx = await _listener.GetContextAsync();
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                await reader.ReadToEndAsync();

                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"] = retryAfter;
                var bytes = Encoding.UTF8.GetBytes("""{"error":"rate limited"}""");
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });
        }

        public void Dispose()
        {
            try
            {
                _listener.Close();
            }
            catch
            {
                // Уже закрыт.
            }
        }
    }

    // ------------------------------------------------------------- контекст

    [Test]
    public async Task ContextWindowFollowsTheServingProfile()
    {
        using var h = new Harness();
        h.C("wide").Http(503);
        h.C("narrow").Ok();

        var state = h.NewState();

        var chain = new List<(LlmProfileConfig, LlmEndpoint, LlmSampling)>
        {
            (new LlmProfileConfig("wide", "m", LlmQuotaKind.Free),
                new LlmEndpoint("wide", "http://127.0.0.1:1/v1", "m", "", LlmDialect.LlamaCpp,
                    TimeSpan.FromSeconds(5), CtxLimit: 262_144),
                new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, 0)),
            (new LlmProfileConfig("narrow", "m", LlmQuotaKind.Subscription),
                new LlmEndpoint("narrow", "http://127.0.0.1:1/v1", "m", "", LlmDialect.OpenAiCompat,
                    TimeSpan.FromSeconds(5), CtxLimit: 128_000),
                new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, null)),
        };

        using var router = new RoutingLlmClient(chain, state, new LlmRouterOptions(300, 3600, 300, 240),
            Sawmill, () => h.Now, (e, _) => h.Clients[e.Id]);

        await Ask(router);

        Assert.That(router.CurrentCtxLimit, Is.EqualTo(128_000),
            "размер контекста снимается один раз при старте сессии, а профиль за раунд может " +
            "смениться на модель с окном вдвое меньше — порог компакции обязан следовать за тем, " +
            "кто отвечает сейчас");
    }

    [Test]
    public async Task ProfileContextLimitIsUsedWhenPropsCannotBeAsked()
    {
        using var h = new Harness();
        var state = h.NewState();

        var chain = new List<(LlmProfileConfig, LlmEndpoint, LlmSampling)>
        {
            (new LlmProfileConfig("cloud", "m", LlmQuotaKind.Subscription, CompactHigh: 100_000),
                new LlmEndpoint("cloud", "http://127.0.0.1:1/v1", "m", "", LlmDialect.OpenAiCompat,
                    TimeSpan.FromSeconds(5), CtxProbe: LlmCtxProbe.None, CtxLimit: 128_000),
                new LlmSampling(0.3f, 0.85f, 20, 0.05f, 0, null)),
        };

        using var router = new RoutingLlmClient(chain, state, new LlmRouterOptions(300, 3600, 300, 240),
            Sawmill, () => h.Now);

        // Настоящий клиент, но с ctxProbe: None он не должен никуда ходить — иначе тест бы завис на
        // недостижимом 127.0.0.1:1.
        var ctx = await router.GetContextSizeAsync(CancellationToken.None);

        Assert.That(ctx, Is.EqualTo(128_000),
            "без этого EffectiveCompactHigh садится на печатное ai.compact_high и компактит облачную " +
            "модель так же часто, как локальную");
    }
}
