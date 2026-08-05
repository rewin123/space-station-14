using System.Threading.Tasks;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Tools;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The one class of regression in this system that is invisible to a human reader: the frozen
/// prefix moving.
///
/// llama.cpp reuses its KV cache only up to the first divergent token, so a single shifted byte at
/// the top of the request costs a full prefill on every single turn. There is no exception, no
/// error and no wrong answer — the agent just becomes slow, and the cause is a property reordered
/// in a DTO or a separator someone tidied up six months earlier.
///
/// These tests are written to be maintainable rather than merely strict. Asserting the live prompt
/// against a historical hash would fail on every deliberate wording change, which teaches everyone
/// to update the constant reflexively and destroys the signal. So the live checks assert
/// <em>invariance</em> — the prefix must be the same each time it is built and must not move while
/// it is supposed to be frozen — which is exactly the property that a stray timestamp breaks and a
/// deliberate edit does not.
/// </summary>
[TestFixture]
public sealed class PrefixStabilityTests
{
    // ---------------------------------------------------------------- no server needed

    /// <summary>
    /// The prefix hash separates the system prompt from the tool array with a NUL byte, and the
    /// literal in <c>ConversationState.SetPrefix</c> is a raw 0x00 rather than the escape sequence
    /// <c>\0</c>. That single byte makes the whole file read as binary, so grep skips it silently —
    /// which makes it exactly the kind of thing an editor or a well-meaning cleanup replaces with a
    /// space. Doing so changes every prefix hash in existence and drops every session snapshot on
    /// disk, with no error anywhere.
    ///
    /// The constant is SHA256("a\0b") truncated the way <c>Hash</c> truncates it. It can never
    /// legitimately change.
    /// </summary>
    [Test]
    [Category("AiContext")]
    public void Hash_SeparatesPromptFromToolsWithNul()
    {
        var conv = new ConversationState();
        conv.SetPrefix("a", "b");

        Assert.Multiple(() =>
        {
            Assert.That(ConversationState.Hash("a\0b"), Is.EqualTo("59B271AE1BBCB1D3"),
                "SHA256(\"a\\0b\") — константа, она не может измениться законно");

            Assert.That(conv.PrefixHash, Is.EqualTo(ConversationState.Hash("a\0b")),
                "разделитель в SetPrefix обязан быть NUL-байтом, а не пробелом");

            Assert.That(conv.PrefixHash, Is.Not.EqualTo(ConversationState.Hash("a b")),
                "если это совпало — разделитель заменили на пробел, и все снапшоты на диске мертвы");
        });
    }

    /// <summary>
    /// The tool array as it goes on the wire, byte for byte, against a fixture registry.
    ///
    /// A private three-tool fixture rather than the live registry on purpose: this pins the
    /// <em>encoding</em> — property order, null omission, escaping, the ordinal sort by name — and
    /// stays valid when the game's own tools change. An exact string rather than a hash because the
    /// failure has to say what moved, not merely that something did.
    /// </summary>
    [Test]
    [Category("AiContext")]
    public void WireJson_HasTheExactWireShape()
    {
        var registry = FixtureRegistry();

        // Registered out of order below; the ordinal sort by name is what puts them back.
        const string expected =
            """
            [{"type":"function","function":{"name":"alpha","description":"первый","parameters":{"type":"object","properties":{}}}},{"type":"function","function":{"name":"beta","description":"второй","parameters":{"type":"object","properties":{"x":{"type":"integer"}},"required":["x"]}}},{"type":"function","function":{"name":"gamma","description":"третий","parameters":{"type":"object","properties":{}}}}]
            """;

        Assert.That(registry.WireJson(), Is.EqualTo(expected),
            "форма tool-массива на проводе сместилась — это полный префилл на каждом ходу");
    }

    /// <summary>
    /// Cyrillic must survive as UTF-8 rather than as <c>\uXXXX</c> escapes.
    ///
    /// Not cosmetic: escaped Russian is roughly six times the bytes and tokenises as punctuation
    /// soup instead of as words. Caught once already by a benchmark where a perfectly correct answer
    /// came back as <c>провод</c>.
    /// </summary>
    [Test]
    [Category("AiContext")]
    public void WireJson_KeepsCyrillicAsUtf8()
    {
        Assert.That(FixtureRegistry().WireJson(), Does.Contain("первый"),
            "кириллица уехала в \\uXXXX — сменился Encoder в LlmJson.Options");
    }

    /// <summary>
    /// Two registries built from identical declarations must serialise identically.
    ///
    /// Registration order is deliberately reversed in the second one: the ordinal sort exists so an
    /// innocuous code edit that moves a Register call cannot move the cache divergence point into
    /// the prefix.
    /// </summary>
    [Test]
    [Category("AiContext")]
    public void WireJson_DoesNotDependOnRegistrationOrder()
    {
        var forward = new AiToolRegistry();
        forward.Register(Tool("alpha", "первый", "{\"type\":\"object\",\"properties\":{}}"));
        forward.Register(Tool("gamma", "третий", "{\"type\":\"object\",\"properties\":{}}"));

        var reversed = new AiToolRegistry();
        reversed.Register(Tool("gamma", "третий", "{\"type\":\"object\",\"properties\":{}}"));
        reversed.Register(Tool("alpha", "первый", "{\"type\":\"object\",\"properties\":{}}"));

        Assert.That(reversed.WireJson(), Is.EqualTo(forward.WireJson()));
    }

    private static AiToolRegistry FixtureRegistry()
    {
        var registry = new AiToolRegistry();

        registry.Register(Tool("gamma", "третий", "{\"type\":\"object\",\"properties\":{}}"));
        registry.Register(Tool("alpha", "первый", "{\"type\":\"object\",\"properties\":{}}"));
        registry.Register(Tool("beta", "второй",
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\"}},\"required\":[\"x\"]}"));

        return registry;
    }

    private static AiTool Tool(string name, string description, string schema) => new()
    {
        Name = name,
        Description = description,
        SchemaJson = schema,
        Handler = (_, _) => Task.FromResult(ToolResult.Success()),
    };

    // ------------------------------------------------------------------ needs a server

    /// <summary>
    /// Zone 0 built twice must be the same string.
    ///
    /// This is the test that catches an interpolated <c>DateTime.Now</c>, a turn counter, a GUID or
    /// a "currently N skills" line finding its way into the frozen prompt. Ticks pass between the
    /// two builds so a clock has a chance to move.
    /// </summary>
    [Test]
    [Category("AiTools")]
    public async Task SystemPrompt_IsIdenticalWhenBuiltTwice()
    {
        await using var w = await AiWorld.Create();

        var first = await w.Read(() => w.System.BuildSystemPromptForTest());
        await w.Pair.Server.WaitRunTicks(30);
        var second = await w.Read(() => w.System.BuildSystemPromptForTest());

        Assert.That(second, Is.EqualTo(first),
            "зона 0 изменилась между двумя сборками — где-то в промпте часы, счётчик или GUID");
    }

    /// <summary>
    /// The prefix must not move while it is frozen — that is, anywhere except a compaction.
    ///
    /// The watchdog in <c>CacheMetrics</c> raises an alarm on exactly this, so asserting on its
    /// alarm count proves both that the prefix held and that the watchdog is still wired up.
    /// </summary>
    [Test]
    [Category("AiTools")]
    public async Task LivePrefix_DoesNotMoveWhileTheAgentPlays()
    {
        var llm = new ScriptedLlmClient()
            .Then("думаю")
            .Then("всё ещё думаю")
            .Then("и ещё");

        await using var w = await AiWorld.Create(llm);

        var session = await w.Read(() => w.System.GetSession(w.Brain));
        Assert.That(session, Is.Not.Null);

        var before = session!.Conv.PrefixHash;

        // Let the loop actually take turns rather than merely waiting: a prefix that only drifts
        // when something is appended would survive an idle wait.
        await w.Post(() => w.System.InjectRadio("Binary", "ИИ, приём", out _));
        await w.Pair.Server.WaitRunTicks(120);

        Assert.Multiple(() =>
        {
            Assert.That(session.Conv.PrefixHash, Is.EqualTo(before),
                "хэш зоны 0 сменился вне компакции — это баг по определению");
            Assert.That(session.Cache.Alarms, Is.Zero,
                "сторож префикса поднял тревогу — смотри ERROR-строки в логе 'ai'");
        });
    }
}
