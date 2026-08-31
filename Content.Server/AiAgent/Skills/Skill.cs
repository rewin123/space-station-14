using System.Collections.Generic;

namespace Content.Server.AiAgent.Skills;

/// <summary>
/// Одна запись библиотеки в том виде, в каком её видит отладочная шина.
///
/// <para>
/// Раньше это была единица хранения: <c>SkillStore</c> держал такие записи плоским словарём и
/// печатал из них индекс в системный промпт. Хранение переехало в
/// <see cref="Vfs.DocTree"/> — с вложенностью, правами и поиском, — а тип остался, потому что на
/// нём висит формат событий <c>skill.updated</c> и <c>skills.reloaded</c> и вкладка отладчика.
/// Менять проводной формат заодно с хранением значило бы чинить две вещи сразу и не знать, какая
/// из них сломалась.
/// </para>
/// <para>
/// <see cref="Name"/> теперь путь внутри монтирования («питание/смес»), а не плоское имя.
/// </para>
/// </summary>
public sealed record Skill(string Name, string When, string Body);

/// <summary>Итог правки библиотеки снаружи агента — с HTTP-эндпоинта отладчика.</summary>
public sealed record SkillResult(bool Ok, string Message, IReadOnlyList<string>? Names = null);
