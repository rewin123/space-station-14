using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vfs;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// A stub body for tests that spin up an <see cref="AgentSession"/> directly, with no world.
///
/// <para>
/// Such tests exercise the loop, the operator inbox, and the journal — exactly the part of the
/// agent that does not depend on the body. The constructor's first argument used to be an
/// <c>EntityUid</c>, and they passed <c>default</c>; now it takes an <see cref="AgentBody"/>, and
/// <c>default</c> there would mean <c>null</c>.
/// </para>
/// <para>
/// <b>This is not a formality.</b> Exactly three tests broke this way during the migration:
/// <c>Body</c> turned out to be <c>null</c>, the loop threw on its very first access to it, the
/// exception was caught by its own general handler, and it fell back into a degraded mode — so the
/// failure looked like "the turn just didn't happen", with no word about why. This stub exists so
/// that never happens again.
/// </para>
/// </summary>
public static class StubAgentBody
{
    public static AgentBody Make(EntityUid owner = default, string id = "test", Vfs? vfs = null) => new()
    {
        Owner = owner,
        Id = id,
        Name = "Тест",
        SoulFile = "SOUL.md",
        Vfs = vfs ?? Scratch(id),
        Eye = () => null,
        Alive = () => true,
        BuildPrompt = () => "ПРОМПТ",
        SelfLine = _ => "SELF тест",
        RegisterTools = (_, _) => { },
        Announce = null,
        Speak = (_, _, _) => Task.FromResult(true),
        ChannelsFor = _ => new[] { "Common" },
    };

    /// <summary>
    /// A filesystem in a temp directory — so the stub body is fully functional.
    ///
    /// <para>
    /// An empty stub in its place would return <c>null</c> from <c>Vfs.Memory</c>, and loop tests
    /// would fail somewhere other than where they actually broke — exactly the ailment this class
    /// exists to prevent.
    /// </para>
    /// </summary>
    private static Vfs Scratch(string id)
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ss14ai-stub", id, System.IO.Path.GetRandomFileName());

        var sawmill = new LogManager().GetSawmill("stub");

        return new VfsBuilder(sawmill)
            .AddFolder(System.IO.Path.Combine(dir, "skills"), "skills", VfsAccess.Write, "что ты понял сам")
            .AddNotes(dir, "players", VfsAccess.Write, "заметки о людях", () => "[раунд 1 · 01.01]")
            .AddMemory(dir, "memory.md", VfsAccess.Write, "факты о станции")
            .Build();
    }
}
