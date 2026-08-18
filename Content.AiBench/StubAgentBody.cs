using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Core;
using Content.Server.AiAgent.Tools;
using Robust.Shared.GameObjects;

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
    public static AgentBody Make(EntityUid owner = default, string id = "test") => new()
    {
        Owner = owner,
        Id = id,
        Name = "Тест",
        SoulFile = "SOUL.md",
        Eye = () => null,
        Alive = () => true,
        BuildPrompt = () => "ПРОМПТ",
        SelfLine = _ => "SELF тест",
        RegisterTools = (_, _) => { },
        Announce = null,
        Speak = (_, _, _) => Task.FromResult(true),
        ChannelsFor = _ => new[] { "Common" },
    };
}
