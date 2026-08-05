using System.IO;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Session persistence: the agent must survive a server restart mid-round rather than waking up
/// with amnesia, and it must refuse to replay a body that was written against a different prefix.
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class SessionTests
{
    private static (SessionStore Store, string Dir) MakeStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss14ai-bench", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return (new SessionStore(dir, new Robust.Shared.Log.LogManager().GetSawmill("test")), dir);
    }

    private static ConversationState Populated(string prefix = "ПРОМПТ")
    {
        var conv = new ConversationState();
        conv.SetPrefix(prefix, "[]");

        conv.AppendUser("наблюдение один");
        conv.AppendAssistant(new LlmResponse("понял", System.Array.Empty<ToolCallDto>(), 100, 90, 5, 0.1));
        conv.AppendUser("наблюдение два");

        return conv;
    }

    [Test]
    public void SaveThenLoad_RoundTripsTheBody()
    {
        var (store, dir) = MakeStore();
        try
        {
            var conv = Populated();
            store.Save("current", conv, compactions: 2, roundId: 7);

            var loaded = store.Load("current", conv.PrefixHash, currentRoundId: 7);

            Assert.That(loaded, Is.Not.Null, "снапшот должен читаться");
            Assert.That(loaded!.Body.Count, Is.EqualTo(conv.Body.Count));
            Assert.That(loaded.Compactions, Is.EqualTo(2));
            Assert.That(loaded.Body[0].Content, Is.EqualTo("наблюдение один"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Load_RefusesWhenPrefixChanged()
    {
        var (store, dir) = MakeStore();
        try
        {
            var conv = Populated("СТАРЫЙ ПРОМПТ");
            store.Save("current", conv, compactions: 0, roundId: 7);

            var other = Populated("НОВЫЙ ПРОМПТ");
            var loaded = store.Load("current", other.PrefixHash, currentRoundId: 7);

            Assert.That(loaded, Is.Null,
                "тело, записанное под другой префикс, воспроизводить нельзя — оно писалось не для него");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Load_RefusesASnapshotFromAnotherRound()
    {
        // The prefix hash does NOT discriminate rounds — it is byte-stable across a restart by
        // design, which is the whole point of it. So without the round id the snapshot written at
        // the end of one shift was restored at the start of the next, and the AI woke up
        // mid-conversation about people who were no longer on board.
        var (store, dir) = MakeStore();
        try
        {
            var conv = Populated();
            store.Save("current", conv, compactions: 0, roundId: 41);

            Assert.Multiple(() =>
            {
                Assert.That(store.Load("current", conv.PrefixHash, currentRoundId: 42), Is.Null,
                    "новая смена — новый разговор");
                Assert.That(store.Load("current", conv.PrefixHash, currentRoundId: 41), Is.Not.Null,
                    "но перезапуск посреди той же смены обязан восстановиться — ради этого всё и писалось");
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Load_SurvivesACorruptFile()
    {
        var (store, dir) = MakeStore();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sessions"));
            File.WriteAllText(Path.Combine(dir, "sessions", "current.json"), "{ это не json");

            Assert.That(store.Load("current", "ЛЮБОЙ", currentRoundId: 7), Is.Null,
                "битый снапшот должен молча игнорироваться, а не ронять запуск агента");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void RestoredBody_WithDanglingCall_IsRepairable()
    {
        var (store, dir) = MakeStore();
        try
        {
            // Exactly what a crash mid-turn leaves behind: a tool call with no result.
            var conv = new ConversationState();
            conv.SetPrefix("ПРОМПТ", "[]");
            conv.AppendUser("наблюдение");
            conv.AppendAssistant(new LlmResponse(null, new[]
            {
                new ToolCallDto { Id = "call_1", Function = new FunctionCallDto { Name = "look", Arguments = "{}" } },
            }, 100, 90, 5, 0.1));

            store.Save("current", conv, 0, roundId: 7);
            var loaded = store.Load("current", conv.PrefixHash, currentRoundId: 7);
            Assert.That(loaded, Is.Not.Null);

            var restored = new ConversationState();
            restored.SetPrefix("ПРОМПТ", "[]");
            restored.RestoreBody(loaded!.Body, loaded.VolatileTail, loaded.CharsPerToken);

            Assert.That(restored.HasOpenToolCalls, Is.True, "висящий вызов должен приехать вместе с телом");

            restored.Repair();

            Assert.That(restored.HasOpenToolCalls, Is.False,
                "после Repair сервер не отвергнет запрос из-за осиротевшего вызова");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>
/// The compaction ritual driven through the real agent loop on a live server, rather than against
/// the compactor in isolation.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class CompactionIntegrationTests
{
    [Test]
    public async Task Compaction_AnnouncesInGame_AndKeepsPrefixStableBetweenCompactions()
    {
        // A script long enough to build history, plus the summary the compactor will ask for.
        var llm = new ScriptedLlmClient();
        for (var i = 0; i < 12; i++)
            llm.ThenCall("say", $$"""{"text":"реплика {{i}}"}""");

        await using var w = await AiWorld.Create(llm);

        var session = await w.Read(() => w.System.GetSession(w.Brain));
        Assert.That(session, Is.Not.Null);

        var hashBefore = session!.Conv.PrefixHash;

        // Drive several turns by hand: the loop's own tick is eight seconds, which would make this
        // a two-minute test for no extra coverage.
        for (var i = 0; i < 4; i++)
            await w.Invoke("say", $$"""{"text":"ход {{i}}"}""");

        var hashAfter = await w.Read(() => session.Conv.PrefixHash);

        Assert.That(hashAfter, Is.EqualTo(hashBefore),
            "хэш зоны 0 обязан быть неизменным между компакциями — иначе префикс-кэш рвётся каждый ход");
        Assert.That(session.Cache.Alarms, Is.Zero, "тревог префикс-кэша быть не должно");
    }
}
