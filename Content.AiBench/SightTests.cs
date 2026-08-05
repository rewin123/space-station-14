using System.Threading.Tasks;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// What the agent is allowed to see, as opposed to what it is allowed to touch.
///
/// look used to list only things the AI could operate — doors, APCs, cameras — and silently drop
/// everything else. On a live round that produced a flat lie: an engineer standing at the SMES bank
/// asked about it and was told no such device was in view. A player at that camera sees the whole
/// room, so hiding the uncontrollable half was a handicap rather than parity.
///
/// The opposite mistake is just as real, which is why the second half of this fixture exists:
/// "everything in the broadphase" would include the cable under the plating and the pen inside
/// somebody's backpack, neither of which is on anyone's screen.
/// </summary>
[TestFixture]
[Category("AiTools")]
public sealed class SightTests
{
    [Test]
    public async Task Look_ListsMachineryItCannotOperate()
    {
        await using var w = await AiWorld.Create();
        await w.Spawn("SMESBasic", dx: 3);

        var result = await w.Invoke("look");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("SMES").IgnoreCase,
                "SMES стоит в трёх тайлах и обязан быть в списке: " + result.ToJson());
        });
    }

    [Test]
    public async Task Inspect_ReadsChargeOffSomethingItCannotControl()
    {
        // Looking and controlling are different rights. The charge lamps are readable across the
        // room; refusing to report them because the AI has no wire to the thing is a handicap.
        await using var w = await AiWorld.Create();
        var smes = await w.Spawn("SMESBasic", dx: 3);
        var handle = await w.Handle(smes);

        var result = await w.Invoke("inspect", $$"""{"handle":"{{handle}}"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("заряд"),
                "заряд читается по индикатору: " + result.ToJson());
            Assert.That(result.ToJson(), Does.Contain("по индикатору"),
                "и он обязан быть помечен как показание лампы, а не телеметрия: " + result.ToJson());
        });
    }

    [Test]
    public async Task Look_HidesWhatIsUnderTheFloor()
    {
        // Cables and pipes are SubFloorHide: invisible to players, and pure noise in a listing that
        // the crew reads back as "what is in this room".
        await using var w = await AiWorld.Create();
        await w.Spawn("CableApcExtension", dx: 2);

        var result = await w.Invoke("look");

        Assert.That(result.ToJson(), Does.Not.Contain("cable").IgnoreCase,
            "кабель под полом не виден никому: " + result.ToJson());
    }
}
