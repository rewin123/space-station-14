using Content.Server.AiAgent.Core;

namespace Content.Server.AiAgent;

/// <summary>
/// Station AI как <b>одно из тел</b>, а не как единственно возможный агент.
///
/// <para>
/// Файл маленький намеренно: это весь станционно-специфичный интерфейс к ядру агента. Всё, что
/// раньше делало <c>StartSession</c> непереносимым — промпт, набор инструментов, речь, каналы,
/// точка зрения, — собрано здесь в один объект. Второе тело (борг) собирает такой же объект из
/// своих методов и ничего в общем коде не трогает.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    /// <summary>
    /// Описание тела «мозг в ядре».
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AgentBody.Eye"/> отдаёт <c>RemoteEntity</c>, а не сам мозг, и это не мелочь:
    /// у Station AI глаз ездит отдельно от тела, и вся геометрия — обзор, поток <c>OBSERVED</c>,
    /// пеленги — считается от камеры. Слух при этом идёт от ядра, но слух телу и не принадлежит:
    /// он подписан отдельно и остаётся станционным.
    /// </para>
    /// <para>
    /// <see cref="AgentBody.Announce"/> заполнен: у мозга в ядре есть встроенная
    /// <c>CommunicationsConsoleComponent</c>. У борга это поле останется <c>null</c> — не как
    /// недоделка, а как отсутствие органа.
    /// </para>
    /// </remarks>
    private AgentBody BuildStationBody(EntityUid brain)
    {
        // Как и у борга: режим фиксируется при сборке тела, чтобы промпт и провод не разъехались
        // при переключении cvar посреди раунда.
        var scripted = _cfg.GetCVar(AiCVars.ScriptMode);

        // Своя файловая система ядра. Сохраняется полем, потому что BuildSystemPrompt — Func<string>
        // без аргументов: и блок ПАМЯТЬ, и корень дерева обязаны описывать одно и то же тело.
        var vfs = BuildVfs(CoreAgentId);
        CoreVfs = vfs;

        return new AgentBody
        {
            Owner = brain,
            Id = CoreAgentId,
            Name = AgentName,
            SoulFile = StationSoulFile(),
            Vfs = vfs,
            LlmChain = StationLlmChain(),
            Eye = () => _stationAi.TryGetCore(brain, out var core) ? core.Comp?.RemoteEntity : null,
            Alive = () => IsPlayable(brain),
            ScriptMode = scripted,
            BuildPrompt = () => BuildSystemPrompt(scripted),
            SelfLine = SelfLine,
            RegisterTools = RegisterTools,
            Announce = AnnounceInGameAsync,
            Speak = SpeakUntooledAsync,
            ChannelsFor = ChannelsFor,
        };
    }
}
