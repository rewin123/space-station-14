namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Шасси, которое ведёт языковая модель.
///
/// <para>
/// Маркер плюс настройки конкретного робота. Отдельный компонент, а не флаг в
/// <c>BorgChassisComponent</c>: тот апстримовый, и править его нельзя, а кроме того «этим боргом
/// управляет ИИ» — свойство нашего форка, а не игры.
/// </para>
/// </summary>
[RegisterComponent, Access(typeof(AiBorgSystem))]
public sealed partial class AiBorgComponent : Component
{
    /// <summary>
    /// Идентификатор агента: им названы каталог памяти, файл сессии и саймилл.
    ///
    /// <para>
    /// Обязан быть уникальным на сервере. Совпадение с чужим означало бы, что два робота пишут
    /// диалог в один файл и после рестарта восстанавливают чужую память как свою — ровно та
    /// поломка, из-за которой идентификатор перестал быть константой.
    /// </para>
    /// </summary>
    [DataField]
    public string AgentId = "borg-1";

    /// <summary>Как робота зовут в эфире. Должно совпадать с тем, как его зовёт SOUL-файл.</summary>
    [DataField]
    public string AgentName = "Сегмент";

    /// <summary>Файл личности внутри каталога агента.</summary>
    [DataField]
    public string SoulFile = "SOUL.md";

    /// <summary>Занимать тело автоматически на старте раунда.</summary>
    [DataField]
    public bool AutoClaim = true;

    /// <summary>
    /// Своя цепочка профилей модели, пусто — общая <c>ai.llm_chain</c>.
    ///
    /// <para>
    /// Существует не ради гибкости, а ради физики: два агента на одном слоте llama-server
    /// вытесняют префиксы друг друга и платят полный prefill каждый ход.
    /// </para>
    /// </summary>
    [DataField]
    public string LlmChain = string.Empty;

    /// <summary>Радиоканалы шасси. Должны совпадать с его <c>IntrinsicRadioTransmitter</c>.</summary>
    [DataField]
    public string[] Channels = { "Binary", "Common", "Science", "Engineering" };

    /// <summary>Разум, созданный ради активации шасси. Удаляется вместе с сессией.</summary>
    [ViewVariables]
    public EntityUid? Mind;
}
