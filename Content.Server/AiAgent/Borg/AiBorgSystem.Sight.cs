using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Perception;
using Content.Shared.Examine;
using Content.Shared.IdentityManagement;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Глаза робота — и разность мира.
///
/// <para>
/// <b>Почему не апстримовое зрение Station AI.</b> Соблазн переиспользовать
/// <c>StationAiVisionSystem.GetView</c> велик и ошибочен: тот собирает <em>объединение по всем
/// сидам</em> <c>StationAiVisionComponent</c> в радиусе, то есть по всем камерам вокруг. Робот с
/// таким зрением видел бы станцию глазами сети наблюдения, стоя в тёмном коридоре, — то есть
/// получил бы ровно ту способность, ради отсутствия которой его и делают телом. Вдобавок он дорог
/// (30–100 мс на вызов) и его быстрый путь сломан для повёрнутых сеток.
/// </para>
/// <para>
/// Робот смотрит так же, как апстримовые NPC: выборка по радиусу плюс один луч на кандидата
/// (<c>InRangeUnOccluded</c>) против дерева окклюдеров. Один рейкаст вместо сотен тайловых
/// запросов.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ExamineSystemShared _examine = default!;

    /// <summary>Радиус зрения робота в тайлах. Столько же, сколько полурамка экрана у человека.</summary>
    private const float SightRange = 8.5f;

    /// <summary>
    /// Что робот видел в конце прошлого хода: хендл → короткое состояние.
    ///
    /// Это и есть основание для разности мира. Живёт по телу, а не по сессии, потому что
    /// пересчитывается на главном потоке при сборке наблюдения.
    /// </summary>
    private readonly Dictionary<EntityUid, Dictionary<string, string>> _lastSeen = new();

    private void InitializeSight()
    {
    }

    private void ForgetSight(EntityUid borg) => _lastSeen.Remove(borg);

    /// <summary>
    /// Всё, что робот сейчас видит: рядом и не за стеной.
    /// </summary>
    /// <remarks>
    /// Флаги <c>Uncontained | Approximate</c> обязательны. По умолчанию
    /// <c>EntityLookupSystem</c> тянет ещё и содержимое контейнеров — то есть начинку каждого
    /// рюкзака и шкафа в радиусе, — и это ровно та статья расходов, которая когда-то стоила
    /// секунды на обзоре Station AI.
    /// </remarks>
    private List<EntityUid> VisibleFrom(EntityUid borg)
    {
        var result = new List<EntityUid>();

        if (!Exists(borg))
            return result;

        var origin = _xform.GetMapCoordinates(borg);
        var candidates = _lookup.GetEntitiesInRange(origin, SightRange,
            LookupFlags.Uncontained | LookupFlags.Approximate);

        foreach (var uid in candidates)
        {
            if (uid == borg || TerminatingOrDeleted(uid))
                continue;

            // Безымянное — это стены, полы и прочая геометрия: называть их поштучно нечем и незачем.
            if (string.IsNullOrWhiteSpace(Name(uid)))
                continue;

            if (!_examine.InRangeUnOccluded(borg, uid, SightRange))
                continue;

            result.Add(uid);
        }

        return result;
    }

    /// <summary>
    /// Разность поля зрения со времени прошлого хода.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>На ходу не считается НИЧЕГО — ни строки, ни само поле зрения.</b> У неподвижного глаза
    /// «появилось/исчезло» — чистый сигнал. У идущего робота смена десяти тайлов означает, что
    /// появилась и исчезла половина станции: строки вытеснили бы из очереди обращение по рации —
    /// то есть ровно ту реплику, ради которой очередь и существует. Поэтому пока робот идёт, база
    /// сравнения сбрасывается, а сводка «что вокруг» выдаётся по прибытии.
    /// </para>
    /// <para>
    /// Считается один раз за ход, при сборке наблюдения, а не каждый тик: цена — один обход
    /// радиуса, и платить её тридцать раз в секунду не за что.
    /// </para>
    /// </remarks>
    private List<(string Label, string Text)> SightDelta(EntityUid borg, AgentSession session)
    {
        var lines = new List<(string, string)>();

        // ПРОВЕРКА ХОДЬБЫ — ПЕРВОЙ СТРОКОЙ, И ЭТО ПОЧИНКА, А НЕ ПЕРЕСТАНОВКА (20.08.2026).
        //
        // Она стояла ПОСЛЕ обхода, и комментарий выше — «на ходу разность не считается вовсе» —
        // описывал намерение, а не код: не считалась только печать строк, а платилось всё. Обход
        // радиуса, `InRangeUnOccluded` на каждого кандидата и `ShortState`, который для
        // большинства сущностей сваливается в опрос энергосети, — всё это исполнялось каждый ход
        // идущего робота. В живом раунде это дало 117 перерасходов бюджета главного потока,
        // худший 45 мс при кадре 33, и снаружи выглядело как «стоит роботу пойти, и fps умирает».
        //
        // База сравнения на ходу не обновляется, а СБРАСЫВАЕТСЯ. Обновлять её значило бы платить
        // ровно ту цену, от которой мы уходим; а сброс переводит первый ход после прибытия в уже
        // существующую ветку «первый ход в этом теле» — она молча запомнит новое окружение. Ничего
        // не теряется: разность против последнего шага ходьбы всё равно бессмысленна, робот только
        // что проехал полстанции, а «что вокруг» он узнаёт из ARRIVED и своего же look.
        if (IsWalking(borg))
        {
            _lastSeen.Remove(borg);
            return lines;
        }

        var now = new Dictionary<string, string>();
        foreach (var uid in VisibleFrom(borg))
        {
            var handle = session.Handles.GetOrCreate(uid, _host.KindOf(uid));
            now[handle] = _host.ShortState(uid);
        }

        if (!_lastSeen.TryGetValue(borg, out var before))
        {
            // Первый ход в этом теле: сравнивать не с чем, и «появилось 40 предметов» —
            // не наблюдение, а шум. Просто запоминаем.
            _lastSeen[borg] = now;
            return lines;
        }

        foreach (var (handle, state) in now)
        {
            if (!before.TryGetValue(handle, out var was))
                lines.Add(("появилось", $"{handle} {NameFor(session, handle)} | {state}"));
            else if (was != state)
                lines.Add(("изменилось", $"{handle} {NameFor(session, handle)} | {was} → {state}"));
        }

        foreach (var (handle, _) in before)
        {
            if (!now.ContainsKey(handle))
                lines.Add(("исчезло", $"{handle} {NameFor(session, handle)}"));
        }

        _lastSeen[borg] = now;
        return lines;
    }

    /// <summary>Посчитать разность и положить её в очередь наблюдений. Главный поток.</summary>
    private void PushSightDelta(AgentSession session, EntityUid borg)
    {
        // Кладётся как Observed, а не отдельной категорией, и это осознанно: разность поля зрения
        // — такой же поток, как чужие действия в кадре, и ей нужен ровно тот же отдельный потолок
        // в очереди, чтобы возня вокруг не вытеснила обращение по рации. Ярлык («появилось») ложится
        // в тот же слот грамматики OBSERVED, который уже знает модель.
        var now = _host.RoundTime();
        foreach (var (label, text) in SightDelta(borg, session))
            session.Queue.Push(Observation.Observed(label, text, now));
    }

    private string NameFor(AgentSession session, string handle) =>
        session.Handles.TryResolve(handle, out var uid) && Exists(uid)
            ? Identity.Name(uid, EntityManager)
            : "?";
}
