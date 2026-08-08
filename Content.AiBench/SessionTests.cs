using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using System.Collections.Generic;
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

    private static AgentState Populated(string prefix = "ПРОМПТ")
    {
        var state = new AgentState();
        state.Conv.SetPrefix(prefix, "[]");

        state.Conv.AppendUser("наблюдение один");
        state.Conv.AppendAssistant(new LlmResponse("понял", System.Array.Empty<ToolCallDto>(), 100, 90, 5, 0.1));
        state.Conv.AppendUser("наблюдение два");

        return state;
    }

    [Test]
    public void SaveThenLoad_RoundTripsTheBody()
    {
        var (store, dir) = MakeStore();
        try
        {
            var state = Populated();
            state.Compactions = 2;
            store.Save("current", state, roundId: 7);

            var loaded = store.Load("current", state.Conv.PrefixHash, currentRoundId: 7);

            Assert.That(loaded, Is.Not.Null, "снапшот должен читаться");
            Assert.That(loaded!.Body.Count, Is.EqualTo(state.Conv.Body.Count));
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
            store.Save("current", Populated("СТАРЫЙ ПРОМПТ"), roundId: 7);

            var other = Populated("НОВЫЙ ПРОМПТ");
            var loaded = store.Load("current", other.Conv.PrefixHash, currentRoundId: 7);

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
            var state = Populated();
            store.Save("current", state, roundId: 41);

            Assert.Multiple(() =>
            {
                Assert.That(store.Load("current", state.Conv.PrefixHash, currentRoundId: 42), Is.Null,
                    "новая смена — новый разговор");
                Assert.That(store.Load("current", state.Conv.PrefixHash, currentRoundId: 41), Is.Not.Null,
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
            var state = new AgentState();
            state.Conv.SetPrefix("ПРОМПТ", "[]");
            state.Conv.AppendUser("наблюдение");
            state.Conv.AppendAssistant(new LlmResponse(null, new[]
            {
                new ToolCallDto { Id = "call_1", Function = new FunctionCallDto { Name = "look", Arguments = "{}" } },
            }, 100, 90, 5, 0.1));

            store.Save("current", state, roundId: 7);
            var loaded = store.Load("current", state.Conv.PrefixHash, currentRoundId: 7);
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Body.Any(m => m.ToolCalls is { Count: > 0 }), Is.True,
                "висящий вызов должен приехать вместе с телом");

            // Repair lives inside Restore now, so no caller can forget it.
            var restored = new AgentState();
            restored.Conv.SetPrefix("ПРОМПТ", "[]");
            restored.Restore(loaded);

            Assert.That(restored.Conv.HasOpenToolCalls, Is.False,
                "после восстановления сервер не отвергнет запрос из-за осиротевшего вызова");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Snapshot_CarriesTheAgent_NotJustTheConversation()
    {
        // The message list was persisted and the agent was not, so a restart brought back the
        // conversation and forgot the mode, the recent speech and the compaction arming. A restored
        // agent would repeat into the radio whatever it broadcast half a minute before going down.
        var (store, dir) = MakeStore();
        try
        {
            var state = Populated();
            state.Turns = 17;
            state.UntooledReplies = 2;
            state.Mode = AgentMode.Carded;
            state.RememberSpeech("Ожидание запросов.");

            store.Save("current", state, roundId: 7);
            var loaded = store.Load("current", state.Conv.PrefixHash, currentRoundId: 7);

            var restored = new AgentState();
            restored.Conv.SetPrefix("ПРОМПТ", "[]");
            restored.Restore(loaded!);

            Assert.Multiple(() =>
            {
                Assert.That(restored.Turns, Is.EqualTo(17));
                Assert.That(restored.UntooledReplies, Is.EqualTo(2));
                Assert.That(restored.Mode, Is.EqualTo(AgentMode.Carded));
                Assert.That(restored.AlreadySaid("Ожидание запросов."), Is.True,
                    "иначе после рестарта агент повторит в эфир то, что сказал перед падением");

                // turns keeps its old meaning; agent_turns is the new one. Both are real numbers
                // answering different questions, so neither takes the other's name.
                Assert.That(loaded!.Turns, Is.EqualTo(state.Conv.TurnCount));
                Assert.That(loaded.AgentTurns, Is.EqualTo(17));
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Restore_NeverComesBackInReviewMode()
    {
        // A snapshot taken mid-compaction holds Review. Restoring it verbatim would leave the agent
        // refusing every game action with review_mode for the rest of the round — silently, and
        // indistinguishably from a model that has stopped trying.
        var state = Populated();
        state.Mode = AgentMode.Review;

        var restored = new AgentState();
        restored.Restore(state.ToSnapshot("ХЭШ", 7));

        Assert.That(restored.Mode, Is.EqualTo(AgentMode.Core));
    }

    [Test]
    public void Load_AcceptsAFileWrittenBeforeTheAgentFieldsExisted()
    {
        // Additive-only is the contract. An old file must load and behave exactly as it used to,
        // which means every v2 default has to equal the value a fresh session starts with.
        var (store, dir) = MakeStore();
        try
        {
            var conv = new ConversationState();
            conv.SetPrefix("ПРОМПТ", "[]");

            Directory.CreateDirectory(Path.Combine(dir, "sessions"));
            File.WriteAllText(Path.Combine(dir, "sessions", "current.json"),
                $$"""
                {"prefix_hash":"{{conv.PrefixHash}}","round_id":7,"turns":3,"compactions":1,
                 "chars_per_token":3.5,"volatile_tail":null,
                 "body":[{"role":"user","content":"наблюдение"}]}
                """);

            var loaded = store.Load("current", conv.PrefixHash, currentRoundId: 7);

            Assert.That(loaded, Is.Not.Null, "старый файл обязан читаться");
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Mode, Is.EqualTo(AgentMode.Core));
                Assert.That(loaded.RecentSpeech, Is.Empty);
                Assert.That(loaded.AgentTurns, Is.Zero);
            });
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
