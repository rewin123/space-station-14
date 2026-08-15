using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Context;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.Skills;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The event stream is a faithful log of the conversation, proved by replaying it.
///
/// This is the test that makes "a mutation could forget to publish" a caught bug rather than a
/// rule nobody enforces. Structurally the body is already sealed off — <c>_body</c> is private and
/// <c>Body</c> is an <c>IReadOnlyList</c>, so the appenders are the only doors — but structure
/// cannot say whether a door reports what went through it.
///
/// So: drive a real <see cref="ConversationState"/> through every mutation it has, rebuild the body
/// from nothing but the recorded events, and compare. A forgotten publish makes the replay diverge
/// and this goes red. It asserts the property, not the implementation, which is the same shape as
/// the NUL-byte assertion in <c>PrefixStabilityTests</c>.
/// </summary>
[TestFixture]
[Category("AiBus")]
public sealed class BusReplayTests
{
    /// <summary>Rebuilds a body from events alone, exactly as a browser client would.</summary>
    private sealed class Replay
    {
        private readonly List<AgentMessageDto> _messages = new();

        public int BodyEpoch { get; private set; }
        public string PrefixHash { get; private set; } = "";
        public string SystemPrompt { get; private set; } = "";
        public IReadOnlyList<AgentMessageDto> Messages => _messages;

        public void Apply(AgentEvent e)
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            var root = doc.RootElement;

            switch (e.Kind)
            {
                case AgentEventKind.MessageAppended:
                {
                    var epoch = root.GetProperty("body_epoch").GetInt32();
                    var index = root.GetProperty("index").GetInt32();

                    Assert.That(epoch, Is.EqualTo(BodyEpoch),
                        "событие пришло из другой эпохи тела — клиент бы пропатчил не ту историю");
                    Assert.That(index, Is.EqualTo(_messages.Count),
                        "индексы разъехались: сообщение приписано не туда, куда оно легло");

                    _messages.Add(root.GetProperty("message").Deserialize<AgentMessageDto>(LlmJson.Options)!);
                    break;
                }

                case AgentEventKind.HistoryReplaced:
                {
                    BodyEpoch = root.GetProperty("body_epoch").GetInt32();
                    _messages.Clear();
                    _messages.AddRange(
                        root.GetProperty("messages").Deserialize<List<AgentMessageDto>>(LlmJson.Options)!);
                    break;
                }

                case AgentEventKind.PrefixReplaced:
                {
                    PrefixHash = root.GetProperty("prefix_hash").GetString() ?? "";
                    SystemPrompt = root.GetProperty("system_prompt").GetString() ?? "";
                    break;
                }
            }
        }
    }

    [Test]
    public void ConversationEventsReplayToTheLiveBody()
    {
        var bus = new AgentEventBus(4096);
        var conv = new ConversationState();
        conv.AttachSink(bus.ForSession("current"));

        // Every mutation the type has, in an order that also exercises the awkward combinations:
        // a dangling tool call closed by the budget, a fold, and a restore over the top of it.
        conv.SetPrefix("СИСТЕМНЫЙ ПРОМПТ", "[]");

        conv.AppendUser("НАБЛЮДЕНИЕ 1");
        conv.AppendAssistant(new LlmResponse("думаю вслух", Array.Empty<ToolCallDto>(), 10, 5, 5, 0.1));

        conv.AppendUser("НАБЛЮДЕНИЕ 2");
        var callId = conv.NextCallId();
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto
            {
                Id = callId,
                Type = "function",
                Function = new FunctionCallDto { Name = "look", Arguments = "{\"kind\":\"дверь\"}" },
            },
        }, 20, 15, 5, 0.1));
        conv.AppendToolResult(callId, "{\"ok\":true}");

        // A turn that ran out of budget with a call still open: CloseTurn must both repair and report.
        conv.AppendAssistant(new LlmResponse(null, new[]
        {
            new ToolCallDto
            {
                Id = "call_dangling",
                Type = "function",
                Function = new FunctionCallDto { Name = "radio", Arguments = "{}" },
            },
        }, 30, 25, 5, 0.1));
        conv.CloseTurn();

        conv.VolatileTail = "зона 2";
        conv.AppendUser("НАБЛЮДЕНИЕ 3");

        // A compaction folds the body...
        var cut = conv.SafeCutIndex(10);
        conv.ReplaceBody("СВОДКА", conv.BodyFrom(Math.Max(cut, 0)));
        conv.SetPrefix("СИСТЕМНЫЙ ПРОМПТ ПОСЛЕ КОМПАКЦИИ", "[]");
        conv.AppendUser("НАБЛЮДЕНИЕ 4");

        // ...and a restart restores over the top of it.
        conv.RestoreBody(new[]
        {
            ChatMessageDto.User("ИЗ СНАПШОТА"),
            ChatMessageDto.System("тоже из снапшота"),
        }, "хвост из снапшота", 3.5);
        conv.AppendUser("НАБЛЮДЕНИЕ 5");

        conv.ClearBody();
        conv.AppendUser("НАБЛЮДЕНИЕ 6");

        // Replay from nothing but the wire.
        var replay = new Replay();
        var read = bus.Read(bus.Instance, 0);
        Assert.That(read.Resync, Is.False, "кольцо не должно было переполниться на этом сценарии");

        foreach (var e in read.Events)
            replay.Apply(e);

        Assert.Multiple(() =>
        {
            Assert.That(replay.Messages.Count, Is.EqualTo(conv.Body.Count),
                "воспроизведение разъехалось по числу сообщений — какая-то мутация не публикуется");
            Assert.That(replay.BodyEpoch, Is.EqualTo(conv.BodyEpoch));
            Assert.That(replay.PrefixHash, Is.EqualTo(conv.PrefixHash));
            Assert.That(replay.SystemPrompt, Is.EqualTo(conv.SystemPrompt));
        });

        for (var i = 0; i < conv.Body.Count; i++)
        {
            var live = conv.Body[i];
            var seen = replay.Messages[i];

            Assert.Multiple(() =>
            {
                Assert.That(seen.Role, Is.EqualTo(live.Role), $"роль разъехалась на {i}");
                Assert.That(seen.Content, Is.EqualTo(live.Content), $"текст разъехался на {i}");
                Assert.That(seen.ToolCallId, Is.EqualTo(live.ToolCallId), $"tool_call_id разъехался на {i}");
                Assert.That(seen.ToolCalls?.Count ?? 0, Is.EqualTo(live.ToolCalls?.Count ?? 0),
                    $"число вызовов тулов разъехалось на {i}");
            });

            if (live.ToolCalls == null)
                continue;

            for (var c = 0; c < live.ToolCalls.Count; c++)
            {
                Assert.That(seen.ToolCalls![c].Id, Is.EqualTo(live.ToolCalls[c].Id));
                Assert.That(seen.ToolCalls[c].Name, Is.EqualTo(live.ToolCalls[c].Function.Name));
                Assert.That(seen.ToolCalls[c].Arguments, Is.EqualTo(live.ToolCalls[c].Function.Arguments),
                    "аргументы обязаны доезжать сырой строкой, как их выдала модель");
            }
        }
    }

    [Test]
    public void EveryPublicMutatorIsAccountedFor()
    {
        // The replay test above proves the mutators it knows about report themselves. This one
        // catches the next mutator somebody adds: an unlisted public method fails here, and whoever
        // added it has to say which list it belongs in.
        var publishing = new HashSet<string>
        {
            nameof(ConversationState.SetPrefix),
            nameof(ConversationState.AppendUser),
            nameof(ConversationState.AppendAssistant),
            nameof(ConversationState.AppendToolResult),
            nameof(ConversationState.CloseTurn),
            nameof(ConversationState.Repair),
            nameof(ConversationState.ReplaceBody),
            nameof(ConversationState.RestoreBody),
            nameof(ConversationState.ClearBody),
        };

        var silent = new HashSet<string>
        {
            // Readers and derived values — they change nothing a client could be told about.
            nameof(ConversationState.Build),
            nameof(ConversationState.Snapshot),
            nameof(ConversationState.BodyChars),
            nameof(ConversationState.TurnBoundaries),
            nameof(ConversationState.SafeCutIndex),
            nameof(ConversationState.BodyFrom),
            nameof(ConversationState.AttachSink),

            // Counters folded into the stats sample rather than diffed. See AgentEventBus.
            nameof(ConversationState.NextCallId),
            nameof(ConversationState.Calibrate),
        };

        var actual = typeof(ConversationState)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType == typeof(ConversationState))
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        var unaccounted = actual.Where(n => !publishing.Contains(n) && !silent.Contains(n)).ToList();

        Assert.That(unaccounted, Is.Empty,
            $"новые публичные методы ConversationState не отнесены ни к публикующим, ни к молчащим: " +
            $"{string.Join(", ", unaccounted)}");
    }

    [Test]
    public void AttachingASinkTwiceThrows()
    {
        var bus = new AgentEventBus(64);
        var conv = new ConversationState();

        conv.AttachSink(bus.ForSession("current"));

        Assert.Throws<InvalidOperationException>(() => conv.AttachSink(bus.ForSession("current")),
            "второй сток удвоил бы каждое событие, и заметить это можно было бы только по счётчику");
    }

    [Test]
    public void ConversationWithNoSinkPublishesNothing()
    {
        var bus = new AgentEventBus(64);
        var conv = new ConversationState();

        conv.SetPrefix("промпт", "[]");
        conv.AppendUser("наблюдение");
        conv.ReplaceBody("сводка", Array.Empty<ChatMessageDto>());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Seq, Is.Zero, "выключенная шина обязана стоить ровно ноль");
            Assert.That(conv.Body, Has.Count.EqualTo(1), "поведение разговора не должно зависеть от шины");
        });
    }

    [Test]
    public void MemoryAndSkillEventsCarryTheWholeNewValue()
    {
        // "memory update {new memory}" and "skill updated {new skill}" — the payload is the new
        // state, not a delta, so a client that missed an earlier frame still converges.
        var bus = new AgentEventBus(64);
        var sink = bus.ForProcess();

        sink.MemoryUpdated(new[] { "первая запись", "вторая запись" });
        sink.SkillUpdated(new Skill("restore-core-power", "когда ядро обесточено", "тело скилла"));

        var events = bus.Read(bus.Instance, 0).Events;

        using var memory = JsonDocument.Parse(events[0].PayloadJson);
        using var skill = JsonDocument.Parse(events[1].PayloadJson);

        Assert.Multiple(() =>
        {
            Assert.That(memory.RootElement.GetProperty("entries").GetArrayLength(), Is.EqualTo(2));

            Assert.That(skill.RootElement.GetProperty("name").GetString(), Is.EqualTo("restore-core-power"));
            Assert.That(skill.RootElement.GetProperty("when").GetString(), Is.EqualTo("когда ядро обесточено"));
            Assert.That(skill.RootElement.GetProperty("body").GetString(), Is.EqualTo("тело скилла"));
        });
    }
}
