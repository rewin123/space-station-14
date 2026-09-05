using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.AiBench;

/// <summary>
/// "AI, do I have access to Engineering?"
///
/// The most common request a Station AI gets is to open a door, and the honest answer is often
/// "your own card already opens it". Guessing from the job title is not good enough: access is
/// edited at the ID console and drifts away from the job within minutes of a shift starting.
///
/// The verdict comes from the same <c>AccessReaderSystem.IsAllowed</c> the game runs when that
/// person touches that door, so it is a simulation of their own attempt rather than a private
/// oracle — and it is gated on the AI actually seeing them, because reading a card over the radio
/// is not something a player can do.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class AccessTests
{
    private static async Task<string> Name(AiWorld w, EntityUid uid) =>
        await w.Read(() => Content.Shared.IdentityManagement.Identity.Name(uid, w.Ent));

    /// <summary>
    /// Give a door a specific access requirement, through the same system the ID console uses.
    ///
    /// Going through <c>TrySetAccesses</c> rather than poking the component is not politeness: the
    /// component guards its lists with an access attribute, and the system is where the original
    /// list gets snapshotted for the "access has been modified" examine text.
    /// </summary>
    private static async Task Require(AiWorld w, EntityUid door, string access)
    {
        var ent = w.Ent;
        await w.Post(() =>
        {
            ent.EnsureComponent<AccessReaderComponent>(door);

            var access0 = w.Pair.Server.System<Content.Shared.Access.Systems.AccessReaderSystem>();

            // An airlock's own reader is a shell pointing at the door electronics board; setting
            // access on the shell changes nothing the game reads. GetMainAccessReader resolves to
            // whichever entity actually holds the requirement.
            if (!access0.GetMainAccessReader(door, out var main))
                throw new global::System.InvalidOperationException("у двери нет читателя доступа");

            access0.TrySetAccesses(main.Value,
                new List<Robust.Shared.Prototypes.ProtoId<AccessLevelPrototype>> { access });
        });
    }

    /// <summary>Put access tags on a person, as the ID card in their pocket would.</summary>
    private static async Task Grant(AiWorld w, EntityUid person, params string[] access)
    {
        var ent = w.Ent;
        await w.Post(() =>
        {
            ent.EnsureComponent<AccessComponent>(person);
            w.Pair.Server.System<Content.Shared.Access.Systems.SharedAccessSystem>()
                .TrySetTags(person, access.Select(a => new Robust.Shared.Prototypes.ProtoId<AccessLevelPrototype>(a)));
        });
    }

    [Test]
    public async Task Inspect_ReportsWhatTheLockRequires()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        await Require(w, door, "Command");
        var handle = await w.Handle(door);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("Command"),
                "требование замка должно быть видно: " + result.ToJson());
        });
    }

    [Test]
    public async Task Inspect_By_SaysYesWhenTheCardOpensIt()
    {
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand", dx: 3);
        await Require(w, door, "Command");

        var crew = await w.Spawn("MobHuman", dx: 2);
        await Grant(w, crew, "Command");

        // Look first, exactly as the agent must: a person the AI has never seen has no handle and
        // no verdict, which is the parity rule this tool is built on.
        await w.Invoke("look");

        var handle = await w.Handle(door);
        var name = await Name(w, crew);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}","by":"{{name}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("\"access_allowed\":true"),
                "карта с нужным доступом должна открывать: " + result.ToJson());
        });
    }

    [Test]
    public async Task Inspect_By_SaysNo_AndShowsWhatTheyDoHold()
    {
        // The refusal has to be actionable: "нет доступа" alone tells the crew nothing, while the
        // list of what the card does carry lets the AI say which head to ask.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand", dx: 3);
        await Require(w, door, "Command");

        var crew = await w.Spawn("MobHuman", dx: 2);
        await Grant(w, crew, "Maintenance");

        // Look first, exactly as the agent must: a person the AI has never seen has no handle and
        // no verdict, which is the parity rule this tool is built on.
        await w.Invoke("look");

        var handle = await w.Handle(door);
        var name = await Name(w, crew);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}","by":"{{name}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("\"access_allowed\":false"), result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("Maintenance"),
                "что у него на карте — часть полезного отказа: " + result.ToJson());
        });
    }

    [Test]
    public async Task Inspect_By_RefusesForSomeoneOffCamera()
    {
        // Parity: a voice on the radio is not a card the AI can read. The tool must say so instead
        // of quietly answering about someone it cannot see.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand");
        var handle = await w.Handle(door);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}","by":"Кого Тут Нет"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, "сам осмотр двери должен пройти: " + result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("look near"),
                "в ответе должно быть сказано, как найти человека: " + result.ToJson());
            Assert.That(result.ToJson(), Does.Not.Contain("access_allowed"),
                "вердикта о человеке, которого не видно, быть не должно: " + result.ToJson());
        });
    }

    [Test]
    public async Task Inspect_By_TrustsAccessNotJobTitle()
    {
        // The whole point. A Passenger whose card was upgraded at the ID console gets in; the agent
        // must report that rather than reasoning from the job it saw in the records.
        await using var w = await AiWorld.Create();
        var door = await w.Spawn("AirlockCommand", dx: 3);
        await Require(w, door, "Command");

        var passenger = await w.Spawn("MobHuman", dx: 2);
        await Grant(w, passenger, "Command");

        await w.Invoke("look");

        var handle = await w.Handle(door);
        var name = await Name(w, passenger);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}","by":"{{name}}"}""");

        Assert.That(result.ToJson(), Does.Contain("\"access_allowed\":true"),
            "доступ решает карта, а не должность: " + result.ToJson());
    }
}
