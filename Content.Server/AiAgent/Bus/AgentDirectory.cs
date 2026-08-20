using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// Один агент, каким его видит HTTP-поток.
/// </summary>
/// <remarks>
/// <para>
/// Хендл несёт <b>делегаты</b>, а не ссылку на <see cref="AgentSession"/>, и это тот же довод, по
/// которому делегаты несёт <c>AgentBody</c>: маршрутизатор не должен знать о сессии ничего, а тест
/// не должен уметь её собирать, чтобы проверить маршрут. Заодно это единственное, что физически
/// мешает HTTP-потоку дотянуться до словаря <c>_sessions</c>, который мутирует главный поток.
/// </para>
/// <para>
/// <see cref="Alive"/> — единственное изменяемое поле, и это не небрежность. Живость тела у ядра
/// считается через <c>IsPlayable</c>, то есть обращением к <c>EntityManager</c>, а туда с чужого
/// потока ходить нельзя. Поэтому значение снимает главный поток раз в секунду, а HTTP-поток только
/// читает. Отсюда же ограничение: <b>годится для индикатора и ни для чего больше</b> — в первую
/// секунду после смерти оно врёт.
/// </para>
/// </remarks>
public sealed class AgentHandle
{
    /// <summary>Идентификатор тела: <c>core</c>, <c>borg-1</c>, <c>combat-2</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Как агента зовут в игре.</summary>
    public required string Name { get; init; }

    /// <summary>Сущность мозга — тем же числом, каким она приходит в кадре <c>session.started</c>.</summary>
    public required int Brain { get; init; }

    /// <summary>Раунд, в котором сессия началась.</summary>
    public required int Round { get; init; }

    /// <summary>Номер кадра на момент старта сессии: им клиент отличает переклейм от того же агента.</summary>
    public required long StartedSeq { get; init; }

    /// <summary>Полный снимок сессии. Зовётся с HTTP-потока и ходит только по своим замкам.</summary>
    public required Func<AgentSessionDto> Capture { get; init; }

    /// <summary>Дешёвая строка ростера: без системного промпта и без истории.</summary>
    public required Func<AgentRosterEntryDto> Roster { get; init; }

    /// <summary>Положить сообщение оператора в ящик агента. Любой поток: у ящика свой замок.</summary>
    public required Func<string, (bool Ok, string Reason)> Send { get; init; }

    /// <inheritdoc cref="AgentHandle"/>
    public volatile bool Alive;

    /// <summary>Строка ростера с актуальной живостью.</summary>
    public AgentRosterEntryDto RosterEntry() => Roster() with { Alive = Alive };
}

/// <summary>
/// Витрина живых агентов: пишет главный поток, читает HTTP-поток.
/// </summary>
/// <remarks>
/// <para>
/// Заменяет собой единственное <c>volatile AgentSession?</c>, из-за которого отладчик показывал
/// того, кто занял тело последним.
/// </para>
/// <para>
/// <b>Словарь И опубликованный массив, а не что-то одно.</b> Словарь нужен ради <see cref="Find"/>
/// — его зовут на каждый запрос снимка и на каждую команду — и ради того, чтобы <see cref="Add"/>
/// мог ОБНАРУЖИТЬ занятый идентификатор, а не затереть чужой хендл. Массив нужен ради ростера:
/// перечисление <c>ConcurrentDictionary</c> безопасно, но порядок у него произвольный, а порядок
/// вкладок в интерфейсе не должен плясать между запросами.
/// </para>
/// </remarks>
public sealed class AgentDirectory
{
    private readonly ConcurrentDictionary<string, AgentHandle> _byId = new(StringComparer.Ordinal);

    /// <summary>Опубликованный упорядоченный массив. HTTP-поток НИКОГДА не обходит словарь.</summary>
    private volatile AgentHandle[] _ordered = Array.Empty<AgentHandle>();

    /// <summary>Все агенты в порядке показа. Любой поток.</summary>
    public AgentHandle[] All => _ordered;

    /// <summary>Сколько агентов живо. Любой поток.</summary>
    public int Count => _ordered.Length;

    /// <summary>Агент по идентификатору, либо null. Любой поток.</summary>
    public AgentHandle? Find(string? id) =>
        id != null && _byId.TryGetValue(id, out var handle) ? handle : null;

    /// <summary>Ростер целиком. Любой поток.</summary>
    public List<AgentRosterEntryDto> Roster() => _ordered.Select(h => h.RosterEntry()).ToList();

    /// <summary>Добавить агента. Главный поток. False — идентификатор уже занят.</summary>
    public bool Add(AgentHandle handle)
    {
        if (!_byId.TryAdd(handle.Id, handle))
            return false;

        Republish();
        return true;
    }

    /// <summary>
    /// Снять агента. Главный поток. Снимает ТОЛЬКО тот хендл, который передан.
    /// </summary>
    /// <remarks>
    /// Сравнение по ссылке обязательно: борга можно переклеймить в том же тике, и безусловное
    /// удаление по идентификатору снесло бы с витрины хендл НОВОГО агента при отпускании старого.
    /// Тот же класс ошибки уже был закрыт в <c>DetachDebugSession</c> проверкой <c>ReferenceEquals</c>.
    /// </remarks>
    public bool Remove(string id, AgentHandle expected)
    {
        if (!_byId.TryGetValue(id, out var current) || !ReferenceEquals(current, expected))
            return false;

        if (!_byId.TryRemove(id, out _))
            return false;

        Republish();
        return true;
    }

    /// <summary>
    /// Оставить только перечисленных. Главный поток.
    /// </summary>
    /// <remarks>
    /// Страховка от утечки. Появись когда-нибудь путь, убирающий сессию мимо <c>Release</c>, хендл
    /// остался бы жить со ссылкой на закрытую сессию — и запрос снимка уткнулся бы в отменённый
    /// токен. Три строки закрывают целый класс.
    /// </remarks>
    public void RetainOnly(IReadOnlyCollection<string> ids)
    {
        var live = new HashSet<string>(ids, StringComparer.Ordinal);
        var stale = _byId.Keys.Where(id => !live.Contains(id)).ToList();

        if (stale.Count == 0)
            return;

        foreach (var id in stale)
            _byId.TryRemove(id, out _);

        Republish();
    }

    /// <summary>
    /// Пересобрать опубликованный порядок: ядро первым, дальше по алфавиту.
    /// </summary>
    /// <remarks>
    /// Порядок задаётся здесь, а не на клиенте, чтобы обе стороны не разошлись. Чисто
    /// алфавитный поставил бы <c>borg-1</c> раньше <c>core</c>, и вкладка по умолчанию прыгала бы
    /// в зависимости от того, кто в этом раунде вообще есть.
    /// </remarks>
    private void Republish() =>
        _ordered = _byId.Values
            .OrderByDescending(h => h.Id == StationAiAgentSystem.CoreAgentId)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .ToArray();
}
