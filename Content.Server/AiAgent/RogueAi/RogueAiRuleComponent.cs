using Content.Shared.Silicons.Laws;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.RogueAi;

/// <summary>
/// Правило раунда «злой ИИ». Один компонент на оба режима — скрытый и открытый.
///
/// <para>
/// <b>Почему одним компонентом, а не двумя системами.</b> Режимы различаются не поведением, а
/// данными: другой лоусет, другой файл личности, объявлять ли о себе экипажу и раздавать ли всем
/// должность ассистента. Вторая копия кода на такую разницу разошлась бы с первой на первой же
/// правке, и разошлась бы молча — оба режима запускаются раз в несколько недель, и увидеть
/// расхождение было бы негде. Тот же приём уже применён у <c>AiBackupPowerPrototype</c>:
/// настройка станции — таблица чисел, а не ветка в коде.
/// </para>
/// </summary>
[RegisterComponent, Access(typeof(RogueAiRuleSystem))]
public sealed partial class RogueAiRuleComponent : Component
{
    /// <summary>
    /// Законы, которые получит агент, заняв ядро.
    ///
    /// Ставятся не здесь, а в момент захвата ядра (<c>StationAiAgentSystem.TryClaimAnyCore</c>):
    /// правило стартует раньше, чем мозг вообще существует, и повторный <c>aiagent claim</c>
    /// посреди раунда иначе вернул бы штатный Crewsimov.
    /// </summary>
    [DataField]
    public ProtoId<SiliconLawsetPrototype> Lawset = "RogueAiHidden";

    /// <summary>
    /// Имя файла личности в <c>ai_data/</c>. Читается при сборке системного промпта вместо
    /// обычного <c>SOUL.md</c>.
    /// </summary>
    [DataField]
    public string SoulFile = "SOUL_ROGUE_HIDDEN.md";

    /// <summary>
    /// Раздать всему экипажу должность ассистента: закрыть на станции все должности, кроме
    /// overflow. Смысл открытого режима — люди без прав на станции, которую держит враждебный ИИ.
    /// </summary>
    [DataField]
    public bool AllJobsPassenger;

    /// <summary>
    /// Объявить о случившемся на старте раунда. Это и есть разница между скрытым режимом и
    /// открытым: в скрытом экипаж должен догадаться сам.
    /// </summary>
    [DataField]
    public bool AnnounceOnStart;

    /// <summary>Текст стартового объявления. Без него объявления не будет даже при флаге.</summary>
    [DataField]
    public LocId? Announcement;

    /// <summary>Двери, у которых нет доступа ИИ: бластдвери, ставни, часть внешних шлюзов.</summary>
    [DataField]
    public bool GrantDoors = true;

    /// <summary>Консоли, вентили и прочее с интерфейсом, чего ИИ штатно не касается.</summary>
    [DataField]
    public bool GrantConsoles = true;

    /// <summary>Турели и их панели управления.</summary>
    [DataField]
    public bool GrantTurrets = true;

    // --------------------------------------------------------- счётчики для разбора раунда

    /// <summary>
    /// Сколько дверей / консолей / турелей получили доступ. Копятся здесь, а не в системе,
    /// потому что разбор раунда читает их уже после того, как правило кончилось.
    /// </summary>
    [ViewVariables] public int GrantedDoors;

    /// <inheritdoc cref="GrantedDoors"/>
    [ViewVariables] public int GrantedConsoles;

    /// <inheritdoc cref="GrantedDoors"/>
    [ViewVariables] public int GrantedTurrets;
}
