using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Tools;
using Content.Server.AiAgent.Vfs;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// Тело-заглушка для тестов, которые заводят <see cref="AgentSession"/> напрямую, без мира.
///
/// <para>
/// Такие тесты проверяют петлю, ящик оператора и журнал — то есть ровно ту часть агента, которая
/// от тела не зависит. Раньше первым аргументом конструктора шёл <c>EntityUid</c>, и они передавали
/// <c>default</c>; теперь там <see cref="AgentBody"/>, и <c>default</c> означал бы <c>null</c>.
/// </para>
/// <para>
/// <b>Это не формальность.</b> Ровно так три теста и сломались при переходе: <c>Body</c> оказался
/// <c>null</c>, петля падала на первом же обращении к нему, ловила исключение своим общим
/// обработчиком и уходила в разреженный режим — то есть отказ выглядел как «ход просто не
/// случился», без единого слова о причине. Заглушка существует, чтобы этого больше не повторилось.
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
    /// Файловая система во временном каталоге — чтобы тело-заглушка было полноценным.
    ///
    /// <para>
    /// Пустая заглушка вместо неё вернула бы <c>null</c> из <c>Vfs.Memory</c>, и тесты петли
    /// падали бы не там, где сломались, — ровно та болезнь, против которой этот класс и заведён.
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
