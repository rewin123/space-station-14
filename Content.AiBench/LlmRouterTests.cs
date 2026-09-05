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
/// The provider chain: traversal order, stickiness, sleep, and what actually goes over the wire.
///
/// <para>
/// The tests deliberately run without a live server. What's checked here is the failure policy and
/// serialization, and the cost of an integration test (a full server with prototypes) for this kind
/// of logic would mean it simply wouldn't get tested. That's why the router accepts an
/// <see cref="LlmProfileConfig"/> instead of a prototype, a clock via a delegate, and a client
/// factory as a parameter.
/// </para>
/// <para>
/// Where the <em>wire</em> itself is checked, the test spins up a real <see cref="HttpListener"/>
/// on loopback and inspects the body that arrives. Otherwise the test would have to repeat the same
/// request-building logic and check it against itself — and what needs to be caught is exactly what
/// used to leak through to every provider before profiles existed: <c>top_k</c>, <c>min_p</c>,
/// <c>cache_prompt</c>, and <c>id_slot</c> sent to a provider that responds to an unknown field with
/// a 400.
/// </para>
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class LlmRouterTests
{
    private static ISawmill Sawmill => new LogManager().GetSawmill("llm-router-test");

    // ----------------------------------------------------------------- fixtures

    /// <summary>A client that answers according to a scripted scenario and counts calls.</summary>
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

        /// <summary>Repeat the last scenario step indefinitely.</summary>
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
                // A temp folder — if it fails to delete, that shouldn't fail the test.
            }
        }
    }

    private static Task<LlmResponse> Ask(ILlmClient client) =>
        client.ChatAsync(new[] { ChatMessageDto.User("привет") }, null, CancellationToken.None);

    // -------------------------------------------------------------- chain traversal

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

        // Three turns later, but before ai.llm_recheck_seconds.
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

        // Later than both the recheck threshold and the failed profile's sleep.
        h.Now = h.Now.AddSeconds(400);
        var response = await Ask(router);

        Assert.That(response.Profile, Is.EqualTo("primary"), "после паузы главный профиль обязан быть проверен");
    }

    // --------------------------------------------------------------------- quota

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

        // An hour later — i.e. already past the default ai.llm_quota_cooldown_seconds, but before
        // the deadline named by the provider.
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

        // Exactly what ResetLlmClient does on every round restart: a new client, a new router, state
        // read back from disk. There are dozens of rounds per day, and without persistence each of
        // them would hammer the exhausted subscription all over again.
        var reread = h.NewState();
        using var second = h.Build(reread, "primary", "backup");
        var response = await Ask(second);

        Assert.That(response.Profile, Is.EqualTo("backup"));
        Assert.That(h.C("primary").Calls, Is.EqualTo(callsBefore),
            "после рестарта роутера спящий профиль не должен пробоваться вовсе");
    }

    // ------------------------------------------------------------------ relogin

    [Test]
    public async Task ReloginMarksTheProfileDeadAndStopsRetrying()
    {
        using var h = new Harness();

        // The exact text as it arrived on the live machine: another client had already used up
        // the one-time refresh token.
        h.C("primary").Http(401, "Codex refresh token was already consumed by another client");
        h.C("backup").Ok();

        var state = h.NewState();
        using var router = h.Build(state, "primary", "backup");

        await Ask(router);
        var calls = h.C("primary").Calls;

        Assert.That(state.IsAvailable("primary", out var why), Is.False);
        Assert.That(why, Does.Contain("401"));

        // A day later — "dead" doesn't clear on its own, unlike sleep.
        h.Now = h.Now.AddDays(1);
        await Ask(router);

        Assert.That(h.C("primary").Calls, Is.EqualTo(calls),
            "перелогин сам не случится, и повторы в пустоту не помогут — нужен человек");

        Assert.That(router.Revive("primary", out _), Is.True);
        Assert.That(state.IsAvailable("primary", out _), Is.True, "aiagent llm revive должен снимать метку");
    }

    // ----------------------------------------------------- what is NOT a reason to switch

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

        // An empty answer isn't a sign that the provider is down, so it shouldn't be put to sleep:
        // otherwise a single sampling failure would take the main model offline for five minutes.
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

        // An empty assistant message in the history is a real incident: after it, DeepSeek answered
        // HTTP 400 to every subsequent request for the rest of the round. Better a failure the
        // agent loop can survive than a response that poisons the conversation.
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

    // ------------------------------------------------------------ manual selection

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

    // --------------------------------------------------------------- spend accounting

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

        // 1000 uncached × 0.44 + 49000 from cache × 0.014 + 20 output × 1.32, all per million.
        var expected = 1000 / 1e6 * 0.44 + 49_000 / 1e6 * 0.014 + 20 / 1e6 * 1.32;
        Assert.That(snap.DaySpendUsd, Is.EqualTo(expected).Within(1e-9),
            "промах и попадание в кэш стоят по-разному в тридцать раз — считать их одинаково значит " +
            "ошибиться в счёте на порядок");
    }

    // --------------------------------------------------------- dialect on the wire

    /// <summary>A real HTTP receiver: the request body is checked exactly as the provider will see it.</summary>
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
                // The receiver may have already closed itself.
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

    // ------------------------------------------- a disposed client doesn't bury providers with it

    /// <summary>
    /// A round-restart race: ResetLlmClient has already disposed the clients, but a farewell
    /// compaction is still calling into the model. An ObjectDisposedException here is the death of
    /// the instance, not of the provider, and it must not land in the shared (round-surviving)
    /// counter: otherwise the next round's fresh chain gets every link in cooldown and answers
    /// "no provider responded in 0s" for three minutes (observed live on 2026-08-25, the round
    /// after 19:11).
    /// </summary>
    [Test]
    public void DisposedClientDoesNotPoisonSharedState()
    {
        using var h = new Harness();
        var state = h.NewState();

        h.C("a").Disposed();
        h.C("b").Ok();

        var oldRouter = h.Build(state, "a", "b");

        // A farewell call through the disposed client: the exception propagates up as-is...
        Assert.ThrowsAsync<ObjectDisposedException>(() =>
            oldRouter.ChatAsync(new[] { ChatMessageDto.User("проба") }, null, CancellationToken.None));

        // ...and both links stay alive in the shared counter: "b" wasn't even tried.
        Assert.Multiple(() =>
        {
            Assert.That(state.IsAvailable("a", out var whyA), Is.True, $"a усыплён: {whyA}");
            Assert.That(state.IsAvailable("b", out var whyB), Is.True, $"b усыплён: {whyB}");
            Assert.That(h.C("b").Calls, Is.Zero, "разобран весь экземпляр — идти по цепочке некуда");
        });

        // A new round: fresh clients, the same counter — the very first turn must go through "a".
        h.Clients["a"] = new FakeClient("a").Ok();
        var newRouter = h.Build(state, "a", "b");

        var response = newRouter.ChatAsync(new[] { ChatMessageDto.User("проба") }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.That(response.Profile, Is.EqualTo("a"), "свежая цепочка не должна наследовать чужую смерть");
    }

    // --------------------------------------------------- vLLM and its null fields in the response

    /// <summary>
    /// A real vLLM response captured live (2026-08-25, vllm-0.27.1); only the reasoning text has
    /// been shortened. vLLM sends unfilled protocol fields as <c>null</c> rather than omitting them
    /// — in every single response.
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
    /// Parsing must survive vLLM's null fields — a full day of a mute AI (2026-08-24 to 08-25)
    /// happened exactly here: <c>"prompt_tokens_details": null</c> made every turn throw from
    /// <c>TryGetProperty</c> on a Null element, even though the request itself came back with a 200.
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

            // The effort level goes INSIDE thinking, and it must not be duplicated as a top-level
            // field: two sources for one setting with an unpredictable winner. Checked by occurrence
            // count, because the substring `reasoning_effort` also appears inside the object itself.
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

            // llama.cpp's reasoning level is set by the --chat-template-kwargs launch flag, and it
            // accepts the body field and silently ignores it. A setting that looks like it works
            // and does nothing is worse than one that's simply missing.
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
        // `ai.thinking_effort` is one setting for all profiles, and "off" is only meaningful for
        // DeepSeek, where it's the object {"type":"disabled"}. OpenAI has no such value:
        // `reasoning_effort: "off"` is an HTTP 400 on every turn, and the router would honestly
        // consider the subscription profile incompatible.
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

        // Field order isn't cosmetic: llama.cpp reuses the KV cache up to the first diverging
        // token, and the live server holds a 97.9% reuse rate on that. The reference order is
        // pinned so that a change to declaration order in ChatRequestDto breaks the test, not
        // performance.
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

    /// <summary>Top-level field names in the order they appear.</summary>
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

    /// <summary>A receiver that responds with 429 and a given <c>Retry-After</c>.</summary>
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
                // Already closed.
            }
        }
    }

    // ------------------------------------------------------------- context

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

        // A real client, but with ctxProbe: None it must not make any call — otherwise the test
        // would hang on the unreachable 127.0.0.1:1.
        var ctx = await router.GetContextSizeAsync(CancellationToken.None);

        Assert.That(ctx, Is.EqualTo(128_000),
            "без этого EffectiveCompactHigh садится на печатное ai.compact_high и компактит облачную " +
            "модель так же часто, как локальную");
    }
}
