using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Tools;
using Content.Shared.AlertLevel;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The contract the model actually reads: schemas, error codes, and answers that must not lie.
///
/// The failures collected here share a shape. Nothing crashed, nothing was logged, every call
/// returned <c>ok:true</c> — and the AI went and told the crew something that was not so. That is
/// the expensive kind of bug in this system, because neither the code nor the model can notice it.
/// </summary>
[TestFixture]
public sealed class ContractTests
{
    // ------------------------------------------------------------- schema vs reality

    /// <summary>
    /// Every alert level the schema offers must be a prototype id that actually resolves.
    ///
    /// It offered green/blue/red; the prototypes are Green/Blue/Red; ProtoId resolution is ordinal;
    /// and AlertLevelSystem.SetLevel returns early on an unknown id without raising anything. So
    /// every level the model could ask for was a silent no-op that answered ok:true — and it
    /// announced the change to the station.
    /// </summary>
    [Test]
    [Category("AiTools")]
    public async Task Announce_AlertLevels_AreRealPrototypeIds()
    {
        await using var w = await AiWorld.Create();

        var levels = await w.Read(() =>
        {
            var protos = w.Pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>();
            return protos.EnumeratePrototypes<AlertLevelPrototype>().Select(p => p.ID).ToList();
        });

        foreach (var offered in new[] { "Green", "Blue", "Yellow", "Violet", "Red" })
        {
            Assert.That(levels, Does.Contain(offered),
                $"схема announce предлагает '{offered}', а такого прототипа нет — вызов будет тихим no-op");
        }
    }

    [Test]
    [Category("AiTools")]
    public async Task Announce_ReportsWhenTheLevelDidNotChange()
    {
        // The read-back existed already and nothing compared it, so the model saw
        // {"alert_level_requested":"red","alert_level_now":"Green"} and — being what it is —
        // reported success.
        await using var w = await AiWorld.Create();

        // Epsilon is a real prototype and deliberately not selectable, so the console refuses it
        // without the level ever changing — the same shape as a cooldown or a bad id, but without
        // the "invalid ProtoId" error the test harness would (rightly) treat as a failure.
        var result = await w.Invoke("announce", "{\"alert_level\":\"Epsilon\"}");

        Assert.That(result.ToJson(), Does.Contain("alert_level_отказано"),
            "несостоявшаяся смена уровня обязана быть названа вслух: " + result.ToJson());
    }

    [Test]
    [Category("AiTools")]
    public async Task DeviceAction_AirAlarmModes_AreRealEnumValues()
    {
        // The schema and the failure message both offered "replace", which is not a member of
        // AirAlarmMode — so a model that took the hint got refused for the same value it had just
        // been told to use. WideFiltering and Fill existed and were never advertised.
        await using var w = await AiWorld.Create();
        var alarm = await w.Spawn("AirAlarm");
        var handle = await w.Handle(alarm);

        foreach (var mode in new[] { "filtering", "wide_filtering", "fill", "panic", "none" })
        {
            var result = await w.Invoke("device_action",
                $$"""{"handle":"{{handle}}","action":"air_alarm_mode","value":"{{mode}}"}""");

            Assert.That(result.Error, Is.Not.EqualTo(ToolError.BadArgs),
                $"режим '{mode}' объявлен в схеме и обязан приниматься: " + result.ToJson());
        }
    }

    [Test]
    [Category("AiTools")]
    public async Task DeviceAction_RefusesAModeThatDoesNotExist()
    {
        await using var w = await AiWorld.Create();
        var alarm = await w.Spawn("AirAlarm");
        var handle = await w.Handle(alarm);

        var result = await w.Invoke("device_action",
            $$"""{"handle":"{{handle}}","action":"air_alarm_mode","value":"replace"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False, "'replace' — не режим воздушной тревоги");
            Assert.That(result.Detail, Does.Not.Contain("replace"),
                "и текст отказа не должен предлагать то же значение обратно: " + result.ToJson());
        });
    }

    // ------------------------------------------------------------------- honest answers

    [Test]
    [Category("AiTools")]
    public async Task Inspect_OutOfSight_MarksTheAnswerStale_AndDropsLiveState()
    {
        // Handles live for the whole shift, so this used to hand over live bolt and breaker state
        // for anything the AI had ever seen — from the far side of the station, through walls.
        // identify already refused on the same grounds; only this half was exempt.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand", dx: 2);
        var handle = await w.Handle(door);

        var near = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");
        Assert.That(near.ToJson(), Does.Contain("door_state"), "вблизи состояние читается: " + near.ToJson());

        // Move it out of camera coverage rather than moving the eye: visibility is computed from
        // cameras near the TARGET, so walking the eye away would change nothing.
        await w.Post(() =>
        {
            var xform = w.Pair.Server.System<Robust.Shared.GameObjects.SharedTransformSystem>();
            xform.SetWorldPosition(door, new System.Numerics.Vector2(400f, 400f));
        });

        await w.Pair.Server.WaitRunTicks(5);

        var far = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(far.Ok, Is.True, "отказывать не надо — что это за объект, знать не вредно");
            Assert.That(far.ToJson(), Does.Contain("устарело"),
                "но ответ обязан быть помечен как устаревший: " + far.ToJson());
            Assert.That(far.ToJson(), Does.Not.Contain("door_state"),
                "и живого состояния в нём быть не должно: " + far.ToJson());
        });
    }

    [Test]
    [Category("AiTools")]
    public async Task Look_KindFilter_NarrowsTheList()
    {
        // The only truncation remedy that works. "expand поменьше" was the advice, and expand
        // defaults to 0 — its minimum — so in the common case the first suggestion was impossible.
        await using var w = await AiWorld.Create();
        await w.Spawn("AirlockCommand", dx: 2);
        await w.Spawn("SMESBasic", dx: 3, dy: 3);

        var all = await w.Invoke("look");
        var doors = await w.Invoke("look", "{\"kind\":\"door\"}");

        Assert.Multiple(() =>
        {
            Assert.That(all.ToJson(), Does.Contain("SMES").IgnoreCase);
            Assert.That(doors.ToJson(), Does.Not.Contain("SMES").IgnoreCase,
                "фильтр по виду обязан отсечь всё, кроме дверей: " + doors.ToJson());
            Assert.That(doors.ToJson(), Does.Contain("door-"),
                "но сами двери — оставить: " + doors.ToJson());
        });
    }

    [Test]
    [Category("AiTools")]
    public async Task NotVisible_DoesNotAdviseMovingTheEye()
    {
        // Visibility is computed from cameras around the TARGET tile; the eye's own position never
        // enters the calculation. "Наведи глаз ближе" was therefore a no-op, and it is precisely
        // the advice that burned the turn budget on move_camera → device_action → not_visible.
        await using var w = await AiWorld.Create();

        var result = await w.Invoke("move_camera", "{\"x\":400,\"y\":400}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Detail, Does.Not.Contain("ближе"),
                "совет «переместись ближе» невыполним и жжёт ход: " + result.ToJson());
        });
    }

    // ------------------------------------------------------------------ error vocabulary

    /// <summary>
    /// Every error code the tools can produce must be named in the prompt.
    ///
    /// <c>stale_handle</c> is the agent's most common failure and the prompt did not mention it, nor
    /// <c>timeout</c>, <c>review_mode</c>, <c>unknown_tool</c> or <c>turn_budget</c>. A code the
    /// model was never taught is a dead end it has to guess its way out of.
    /// </summary>
    [Test]
    [Category("AiTools")]
    public async Task EveryErrorCode_IsExplainedInThePrompt()
    {
        await using var w = await AiWorld.Create();
        var prompt = await w.Read(() => w.System.BuildSystemPromptForTest());

        var codes = new[]
        {
            ToolError.BadArgs, ToolError.StaleHandle, ToolError.NotVisible, ToolError.NotControllable,
            ToolError.NoAccess, ToolError.Unpowered, ToolError.WireCut, ToolError.Carded,
            ToolError.Dead, ToolError.Timeout, ToolError.ReviewMode, ToolError.TurnBudget,
            ToolError.Internal, ToolError.UnknownTool,
        };

        Assert.Multiple(() =>
        {
            foreach (var code in codes)
            {
                Assert.That(prompt, Does.Contain(code),
                    $"код '{code}' инструменты возвращают, а промпт про него молчит");
            }
        });
    }

    [Test]
    [Category("AiTools")]
    public async Task Prompt_ExplainsHandlesAndRetry()
    {
        // Two things the model needs on every single turn and that the prompt never said: what a
        // handle is and where it comes from, and what the three retry values mean.
        await using var w = await AiWorld.Create();
        var prompt = await w.Read(() => w.System.BuildSystemPromptForTest());

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("ХЕНДЛЫ"), "модель обязана знать, откуда берутся хендлы");
            Assert.That(prompt, Does.Contain("other_target"), "и что означает retry");
            Assert.That(prompt, Does.Contain("ПРИМЕР ПОЛНОГО ХОДА"),
                "один проработанный пример учит цепочке лучше, чем пять абзацев прозы");
        });
    }

    /// <summary>
    /// The dispatch table, the schema enum and what <c>inspect</c> advertises must agree.
    ///
    /// They did not: <c>light_on</c>/<c>light_off</c> were in the enum and in the table, never in
    /// <c>inspect</c>'s list, had the only branch with no component check, and on this fork were
    /// unreachable anyway — no prototype carries both ItemTogglePointLight and StationAiWhitelist.
    /// </summary>
    [Test]
    [Category("AiContext")]
    public void DeviceAction_SchemaAndDispatchTable_Agree()
    {
        var schema = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "Content.Server/AiAgent/StationAiAgentSystem.Tools.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(schema, Does.Not.Contain("light_on"),
                "light_on недостижим ни на одном прототипе — рекламировать его значит дарить модели " +
                "лишний ход на каждую попытку");
            Assert.That(schema, Does.Not.Contain("\"replace\""),
                "'replace' не является членом AirAlarmMode");
        });
    }

    /// <summary>
    /// Walk up to the repository root. Anchored on the solution file rather than on a directory
    /// name: the tests run from <c>bin/</c>, which contains a <c>Content.Server</c> folder of its
    /// own and would stop the walk one level too early.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = System.IO.Directory.GetCurrentDirectory();
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "SpaceStation14.slnx")))
            dir = System.IO.Directory.GetParent(dir)?.FullName;

        return dir ?? throw new System.InvalidOperationException("не нашёл корень репозитория");
    }
}
