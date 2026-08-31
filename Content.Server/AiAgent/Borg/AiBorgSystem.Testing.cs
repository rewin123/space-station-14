using Robust.Shared.GameObjects;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Окна во внутренности робота — только для стенда.
/// </summary>
/// <remarks>
/// Отдельным файлом по той же причине, что и у <c>StationAiAgentSystem.Testing</c>: тестовые входы
/// не должны стоять вперемешку с боевым кодом, иначе через месяц их начинают звать из боевого.
/// </remarks>
public sealed partial class AiBorgSystem
{
    /// <summary>
    /// Сколько кадров подряд робот не может уйти с точки отсчёта. −1 — за ним никто не следит.
    /// </summary>
    /// <remarks>
    /// Единственный способ проверить главное свойство счётчика заторов: идущий робот заторов не
    /// набирает. Снаружи это не видно ничем — ни позицией, ни маршрутом, ни журналом: сломанный
    /// счётчик выглядел как исправный ровно до того момента, когда набегала тридцатка и робот
    /// объявлял непроходимым коридор, по которому шёл.
    /// </remarks>
    public int StallsForTest(EntityUid borg) =>
        _progress.TryGetValue(borg, out var p) ? p.Stalls : -1;

    /// <summary>Тайлы, которые робот на текущем маршруте счёл непроходимыми.</summary>
    public int BlockedTilesForTest(EntityUid borg) =>
        _blocked.TryGetValue(borg, out var set) ? set.Count : 0;

    /// <summary>Та же строка, что видит скрипт через <c>walk_status</c>.</summary>
    public string WalkStatusForTest(EntityUid borg) => WalkStatus(borg);

    /// <summary>
    /// Спрятать или вернуть поддерево тела, не заводя агента.
    /// </summary>
    /// <remarks>
    /// Стенду с настоящим подключённым клиентом нужен ровно этот шаг, а не весь захват: захват
    /// требует включённой <c>ai.enabled</c> и живой модели, то есть тащит в тест про состав пакета
    /// PVS ещё и петлю хода вместе с её журналом. Само сокрытие при захвате проверяется отдельно,
    /// по маске видимости на сервере.
    /// </remarks>
    public void SetSubtreeHiddenForTest(EntityUid borg, bool hidden)
    {
        if (hidden)
            HideSubtree(borg);
        else
            ShowSubtree(borg);
    }

    /// <summary>
    /// Полный обход поля зрения: сколько сущностей отдал радиус и сколько прошло проверку лучом.
    /// </summary>
    /// <remarks>
    /// Стенд меряет им цену <c>BeforeObservation</c>. Именно эти два числа объясняют цену: радиус
    /// стоит один запрос в broadphase, а вот луч <c>InRangeUnOccluded</c> платится за КАЖДОГО
    /// кандидата отдельно.
    /// </remarks>
    public (int Visible, int Candidates) SightDeltaCostForTest(EntityUid borg)
    {
        var candidates = _lookup.GetEntitiesInRange(_xform.GetMapCoordinates(borg), 8.5f,
            LookupFlags.Uncontained | LookupFlags.Approximate);

        return (VisibleFrom(borg).Count, candidates.Count);
    }
}
