using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Content.Server.AiAgent.Perception;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Server.AiAgent;

/// <summary>
/// Зрение агента как поток событий, а не как опрос.
///
/// <para>
/// До этого файла агент не видел НИЧЕГО. Он слышал рацию, речь у ядра и объявления, а происходящее в
/// мире для него не существовало: инструмент <c>look</c> отвечает на «что стоит вокруг», но не на
/// «что сейчас произошло», и опрашивать его ради этого нельзя — он стоит десятки миллисекунд
/// главного потока.
/// </para>
/// <para>
/// Дырой это делает не пропущенная драка, а невыполнимая просьба. «Когда я вставлю плазму в
/// генератор аномалий — запусти его» упиралась в то, что узнать о вставленной плазме агенту нечем;
/// оставалось переспрашивать по рации, а это ровно то поведение, из-за которого с ним перестают
/// разговаривать. Реактивность здесь — не украшение зрения, а условие того, чтобы отложенная
/// просьба вообще могла быть выполнена.
/// </para>
/// <para>
/// <b>Семантики в этом файле нет и быть не должно.</b> Мы не решаем, что «важно»: список важного
/// заведомо не покроет просьбы экипажа, потому что они не ограничены дракой. Задача кода —
/// доставить событие с участниками и координатами; понять, что оно значит, — работа модели.
/// Поэтому ярлыки вроде «предметом» это подписи, а не классификация, и никакого разворота
/// «на самом деле стрелял вот этот» здесь тоже нет: апстрим отдаёт в <c>Origin</c> хитскана ствол
/// вместо человека — так и передаём, у модели есть <c>inspect</c>.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    // ------------------------------------------------------------------ ярлыки

    private const string LabelHand = "рукой";
    private const string LabelUsing = "предметом";
    private const string LabelRanged = "издали";
    private const string LabelActivate = "включил";
    private const string LabelInserted = "вложил";
    private const string LabelRemoved = "вынул";
    private const string LabelPullStart = "тащит";
    private const string LabelPullStop = "отпустил";
    private const string LabelEquipped = "надел";
    private const string LabelUnequipped = "снял";
    private const string LabelState = "состояние";
    private const string LabelDamage = "урон";
    private const string LabelShot = "выстрел";
    private const string LabelDoor = "дверь";

    // ------------------------------------------------------------------ подписки

    /// <summary>
    /// Повесить прослушку мира. Вызывается один раз из <c>Initialize</c>.
    /// </summary>
    /// <remarks>
    /// Половина подписок широковещательные, половина направленные, и разница здесь не стилистическая.
    /// <c>RaiseLocalEvent(uid, ev)</c> по умолчанию поднимает событие БЕЗ широковещания
    /// (<c>EntityEventBus.Directed.cs</c>), поэтому на такое можно подписаться только через
    /// конкретный компонент — а направленная пара «компонент + событие» в RobustToolbox глобально
    /// уникальна: второй претендент получает <c>Duplicate Subscriptions</c> при старте сервера.
    /// Отсюда <see cref="TryWitness{TComp,TEvent}"/> у направленных: занятая пара обязана стоить нам
    /// одной категории наблюдений, а не поднятия сервера.
    /// </remarks>
    private void SubscribeWitness()
    {
        // Широковещательные. Пару не занимают, отобрать их у нас нельзя.
        //
        // Все до одного несут своих участников ВНУТРИ объекта события, и это не совпадение, а отбор:
        // широковещательный обработчик получает только само событие и не знает, на какой сущности
        // оно поднято. Поэтому UseInHandEvent, DroppedEvent и LockToggledEvent сюда не попали — они
        // называют человека, но не называют предмет или замок, а строка «Иван что-то уронил» это
        // половина наблюдения. Направленная подписка вернула бы им недостающую сущность, но каждая
        // такая подписка занимает глобально уникальную пару, и тратить её на действие, которое и
        // так видно кликом (InteractUsing по тому же предмету), не стоит.
        SubscribeLocalEvent<InteractHandEvent>(OnWitnessHand);
        SubscribeLocalEvent<InteractUsingEvent>(OnWitnessUsing);
        SubscribeLocalEvent<RangedInteractEvent>(OnWitnessRanged);
        SubscribeLocalEvent<ActivateInWorldEvent>(OnWitnessActivate);
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnWitnessInserted);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnWitnessRemoved);
        SubscribeLocalEvent<PullStartedMessage>(OnWitnessPullStarted);
        SubscribeLocalEvent<PullStoppedMessage>(OnWitnessPullStopped);
        SubscribeLocalEvent<DidEquipEvent>(OnWitnessEquipped);
        SubscribeLocalEvent<DidUnequipEvent>(OnWitnessUnequipped);
        SubscribeLocalEvent<MobStateChangedEvent>(OnWitnessMobState);

        // Направленные. Каждая занимает пару, поэтому их всего три и каждая выбрана как единственная
        // точка на целый класс происходящего.
        //
        // DamageChangedEvent — вся боль в игре разом. Через
        // TryChangeDamage → ChangeDamage → DamageDealtEvent → InjurableComponent → OnEntityDamageChanged
        // проходят и мили, и пули, и хитскан, и огонь; шесть подписок на оружие заменяются одной.
        TryWitness<MobStateComponent, DamageChangedEvent>(OnWitnessDamage);
        TryWitness<GunComponent, GunShotEvent>(OnWitnessShot);
        TryWitness<DoorComponent, DoorStateChangedEvent>(OnWitnessDoor);
    }

    /// <summary>
    /// Подписаться на направленную пару, не убив сервер, если апстрим её уже занял.
    ///
    /// Отказ остаётся громким — он первой же строкой в журнале, — но публичный сервер поднимается и
    /// работает без одной категории наблюдений вместо того, чтобы не подниматься вовсе. Пары
    /// проверены на момент написания; проверка нужна на будущий ребейз, где чужая подписка появится
    /// без нашего ведома.
    /// </summary>
    private void TryWitness<TComp, TEvent>(EntityEventRefHandler<TComp, TEvent> handler)
        where TComp : IComponent
        where TEvent : notnull
    {
        try
        {
            SubscribeLocalEvent(handler);
        }
        catch (InvalidOperationException e)
        {
            _sawmill.Error(
                $"наблюдение: пара ({typeof(TComp).Name}, {typeof(TEvent).Name}) уже занята — " +
                $"эта категория событий агенту не придёт. {e.Message}");
        }
    }

    private void TryWitness<TComp, TEvent>(ComponentEventHandler<TComp, TEvent> handler)
        where TComp : IComponent
        where TEvent : EntityEventArgs
    {
        try
        {
            SubscribeLocalEvent(handler);
        }
        catch (InvalidOperationException e)
        {
            _sawmill.Error(
                $"наблюдение: пара ({typeof(TComp).Name}, {typeof(TEvent).Name}) уже занята — " +
                $"эта категория событий агенту не придёт. {e.Message}");
        }
    }

    // ------------------------------------------------------------------ обработчики

    // Каждый — одна строка. Всё, что они делают, это называют ярлык, говорят, ГДЕ произошло, и
    // перечисляют участников в порядке «кто, чем, над чем». Добавить новое событие — тоже одна
    // строка; ничего больше в этом файле для этого трогать не нужно.

    private void OnWitnessHand(InteractHandEvent args) =>
        Witness(LabelHand, args.Target, args.User, args.Target);

    private void OnWitnessUsing(InteractUsingEvent args) =>
        Witness(LabelUsing, args.Target, args.User, args.Used, args.Target);

    private void OnWitnessRanged(RangedInteractEvent args) =>
        Witness(LabelRanged, args.TargetUid, args.UserUid, args.UsedUid, args.TargetUid);

    private void OnWitnessActivate(ActivateInWorldEvent args) =>
        Witness(LabelActivate, args.Target, args.User, args.Target);

    // Имя контейнера едет отдельным параметром, а не вклеивается в ярлык здесь: приклеить его
    // строкой значило бы собирать строку на КАЖДОМ вложении на станции, включая те, что ворота
    // отвергнут следующей же проверкой, — а вложений на станции больше, чем любых других событий.
    // Само имя («left hand», «storagebase», «machine_parts») отдаётся как есть: положили в руку,
    // в сумку или внутрь машины — это разные вещи, и различать их модели, а не нам.
    private void OnWitnessInserted(EntInsertedIntoContainerMessage args) =>
        Witness(LabelInserted, args.Container.Owner, args.Entity, args.Container.Owner,
            detail: args.Container.ID);

    private void OnWitnessRemoved(EntRemovedFromContainerMessage args) =>
        Witness(LabelRemoved, args.Container.Owner, args.Entity, args.Container.Owner,
            detail: args.Container.ID);

    private void OnWitnessPullStarted(PullStartedMessage args) =>
        Witness(LabelPullStart, args.PulledUid, args.PullerUid, args.PulledUid);

    private void OnWitnessPullStopped(PullStoppedMessage args) =>
        Witness(LabelPullStop, args.PulledUid, args.PullerUid, args.PulledUid);

    private void OnWitnessEquipped(DidEquipEvent args) =>
        Witness(LabelEquipped, args.EquipTarget, args.EquipTarget, args.Equipment);

    private void OnWitnessUnequipped(DidUnequipEvent args) =>
        Witness(LabelUnequipped, args.EquipTarget, args.EquipTarget, args.Equipment);

    private void OnWitnessMobState(MobStateChangedEvent args) =>
        Witness($"{LabelState}: {StateRu(args.OldMobState)}→{StateRu(args.NewMobState)}",
            args.Target, args.Origin ?? args.Target, args.Target);

    private void OnWitnessDamage(Entity<MobStateComponent> ent, ref DamageChangedEvent args)
    {
        // Лечение — не событие «кому-то досталось», а его противоположность, и путать их в одной
        // строке значит заставлять модель разбирать знак числа, которого в строке нет.
        if (!args.DamageIncreased)
            return;

        // Урон без источника: упал, обжёгся, задохнулся. Виновника нет, и придумывать его нельзя —
        // строка «X ударил» с угаданным X хуже отсутствующей.
        Witness(LabelDamage, ent.Owner, args.Origin ?? ent.Owner, ent.Owner);
    }

    private void OnWitnessShot(Entity<GunComponent> ent, ref GunShotEvent args) =>
        Witness(LabelShot, ent.Owner, args.User, ent.Owner);

    // Ярлык двери берётся готовым, а не склеивается: двери на станции щёлкают десятками в секунду,
    // и собирать строку на каждый щелчок ради события, которое почти всегда за пределами кадра, —
    // это мусор в тике на ровном месте.
    private void OnWitnessDoor(Entity<DoorComponent> ent, ref DoorStateChangedEvent args)
    {
        var label = DoorLabel(args.State);
        if (label == null)
            return;

        Witness(label, ent.Owner, ent.Owner);
    }

    private static string StateRu(MobState state) => state switch
    {
        MobState.Alive => "жив",
        MobState.Critical => "крит",
        MobState.Dead => "мёртв",
        _ => "?",
    };

    /// <summary>
    /// Готовый ярлык на каждое состояние двери — ни одной склейки строк в горячем пути.
    /// <c>null</c> значит «не докладывать вовсе».
    ///
    /// <para>
    /// <b>Промежуточные состояния молчат, и это починка, а не экономия.</b> Дверь проходит
    /// <c>Closed → Opening → Open</c>, то есть <see cref="DoorStateChangedEvent"/> прилетает на
    /// один проход дважды. Раньше <c>Opening</c> и <c>Open</c> давали ОДИН И ТОТ ЖЕ ярлык, и агент
    /// получал две неотличимые строки подряд. В боевой сессии 16 августа это стоило семи ходов из
    /// сорока двух: агент честно отвечал «повторное событие, уже учтено» — семь запросов к модели,
    /// потраченных на пересказ самому себе.
    /// </para>
    /// <para>
    /// Оставлено конечное состояние, а не начальное, хотя начальное приходит на полсекунды раньше.
    /// Причина: дверь можно перевести в <c>Open</c> без анимации — вскрытие, обесточивание,
    /// принудительная установка состояния, — и тогда <c>Opening</c> не приходит вообще. Ставка на
    /// промежуточное состояние теряла бы ровно те события, ради которых зрение и заводилось.
    /// </para>
    /// </summary>
    private static string? DoorLabel(DoorState state) => state switch
    {
        DoorState.Open => LabelDoor + ": открылась",
        DoorState.Closed => LabelDoor + ": закрылась",
        DoorState.Denying => LabelDoor + ": отказ",
        DoorState.Emagging => LabelDoor + ": взлом",
        DoorState.Welded => LabelDoor + ": заварена",
        _ => null,
    };

    // ------------------------------------------------------------------ воронка

    /// <summary>
    /// Всё увиденное сходится сюда: ворота, опознание, формат.
    /// </summary>
    /// <param name="label">Что произошло. Подпись для модели и ключ для <c>ai.observe_kinds</c>.</param>
    /// <param name="where">
    /// По какой сущности мерить расстояние до глаза. Обычно это цель действия: событие происходит
    /// там, где стоит она, а не там, где стоит инициатор — стрелявший из-за угла в кадр не попал,
    /// а вот попадание попало.
    /// </param>
    /// <param name="first">Кто это сделал.</param>
    /// <param name="second">Чем — или над чем, если инструмента не было.</param>
    /// <param name="third">Над чем, если названы все трое.</param>
    /// <remarks>
    /// Три отдельных параметра, а не <c>params EntityUid[]</c>, и это не придирка к стилю: массив
    /// собирался бы на КАЖДОМ событии станции, включая те, что ворота отвергают следующей же
    /// строкой. На потоке кликов это мусор в тике на ровном месте, а тик в этом проекте только что
    /// вычищали от куда меньших поводов.
    /// </remarks>
    /// <param name="detail">
    /// Уточнение к ярлыку — приклеивается через двоеточие и только ПОСЛЕ ворот, чтобы событие,
    /// которого глаз не видел, не стоило ни одной склейки строк.
    /// </param>
    private void Witness(string label, EntityUid where, EntityUid first, EntityUid second = default,
        EntityUid third = default, string? detail = null)
    {
        // Первым делом — есть ли вообще кому смотреть. На станции без агента этот метод зовётся на
        // каждый клик каждого игрока, и он обязан стоить одного сравнения.
        if (_sessions.Count == 0 || !_cfg.GetCVar(AiCVars.Observe))
            return;

        if (!KindEnabled(label))
            return;

        var now = RoundTime();

        foreach (var session in _sessions.Values)
        {
            if (!NearTheEye(session, where, out var at, out var eyeAt))
                continue;

            var line = Describe(session, first, second, third, eyeAt, at);
            if (line == null)
                continue;

            session.Queue.Push(Observation.Observed(
                detail == null ? label : $"{label}: {detail}", line, now));

            _witnessed++;
        }
    }

    /// <summary>
    /// Включён ли этот ярлык. Пустой список — все.
    /// </summary>
    /// <remarks>
    /// Сравнение по префиксу до двоеточия: составные ярлыки вроде <c>состояние: жив→крит</c>
    /// настраиваются одним словом <c>состояние</c>, иначе выключить категорию можно было бы только
    /// перечислив все её значения.
    /// </remarks>
    private bool KindEnabled(string label)
    {
        var allowed = _cfg.GetCVar(AiCVars.ObserveKinds);
        if (string.IsNullOrWhiteSpace(allowed))
            return true;

        var colon = label.IndexOf(':');
        var head = colon < 0 ? label : label[..colon];

        foreach (var part in allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, head, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ ворота

    /// <summary>
    /// Попало ли это место в поле зрения глаза.
    ///
    /// <para>
    /// Две ступени вместо трёх, и это осознанный выбор, а не упрощение. Строгая проверка стен —
    /// <c>StationAiVisionSystem.IsAccessible</c>, а она разворачивает три сотни тайлов и делает
    /// broadphase-запрос на каждый. На редком вызове это незаметно; здесь вызовов поток, и полная
    /// проверка вернула бы тик ровно к тому состоянию, из-за которого <c>look</c> держал его
    /// секунду. Цена: в пределах <c>ai.observe_range</c> агент заметит происходящее за стеной,
    /// тогда как человек на его месте увидел бы стену. Третья ступень включается
    /// <c>ai.observe_occlusion</c>, см. <see cref="TileIsVisible"/>.
    /// </para>
    /// <para>
    /// Квадрат, а не круг (<c>max(|dx|,|dy|)</c>, а не длина): у человека на экране прямоугольный
    /// вьюпорт, и круг обрезал бы углы, которые он видит.
    /// </para>
    /// </summary>
    private bool NearTheEye(AgentSession session, EntityUid what, out Vector2 at, out Vector2 eyeAt)
    {
        at = default;
        eyeAt = default;

        if (!_stationAi.TryGetCore(session.Brain, out var core) || core.Comp?.RemoteEntity == null)
            return false;

        var eye = core.Comp.RemoteEntity.Value;

        // Без <TransformComponent>, и это не косметика. Негенерическая перегрузка ходит через
        // готовый TransformQuery, а не через общий словарь компонентов; на пути, который
        // срабатывает на каждое событие станции, разница считается. Аналитик RA0030 требует
        // ровно этого и в конфигурации Release считает генерическую форму ошибкой сборки.
        if (!TryComp(what, out TransformComponent? xform) || !TryComp(eye, out TransformComponent? eyeXform))
            return false;

        // Разные сетки — разные места, даже если координаты близки. Шаттл, пролетающий мимо станции,
        // не должен показываться агенту как происходящее в соседнем отсеке.
        if (xform.GridUid == null || xform.GridUid != eyeXform.GridUid)
            return false;

        at = _xform.GetWorldPosition(xform);
        eyeAt = _xform.GetWorldPosition(eyeXform);

        var range = _cfg.GetCVar(AiCVars.ObserveRange);
        var delta = at - eyeAt;

        if (MathF.Abs(delta.X) > range || MathF.Abs(delta.Y) > range)
            return false;

        return TileIsVisible(xform.GridUid.Value, xform);
    }

    /// <summary>
    /// Третья ступень: не за стеной ли. Выключена по умолчанию, см. <c>ai.observe_occlusion</c>.
    /// </summary>
    /// <remarks>
    /// Две защиты, обе внутри тика. Мемо по тайлу живёт РОВНО тик и сбрасывается в <c>Update</c> —
    /// это не кэш обзора: набор не переживает ни одного изменения мира, которое агент мог бы
    /// пропустить, он лишь схлопывает драку на одном тайле в одну проверку вместо десяти. Потолок
    /// проверок за тик — страховка от нагрузки, которую мемо не схлопывает; сверх него события
    /// пропускаются, и число пропущенных уходит в журнал, потому что молча терять наблюдения хуже,
    /// чем терять их громко.
    /// </remarks>
    private bool TileIsVisible(EntityUid gridUid, TransformComponent xform)
    {
        if (!_cfg.GetCVar(AiCVars.ObserveOcclusion))
            return true;

        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid) ||
            !TryComp<BroadphaseComponent>(gridUid, out var broadphase))
            return false;

        var tile = _mapSystem.LocalToTile(gridUid, mapGrid, xform.Coordinates);

        if (_seenTiles.TryGetValue(tile, out var known))
            return known;

        if (_visionChecks >= _cfg.GetCVar(AiCVars.ObserveMaxChecksPerTick))
        {
            _visionSkipped++;
            return false;
        }

        _visionChecks++;

        var visible = _vision.IsAccessible((gridUid, broadphase, mapGrid), tile, fastPath: false);
        _seenTiles[tile] = visible;
        return visible;
    }

    // ------------------------------------------------------------------ опознание

    /// <summary>
    /// Собрать участников в строку: <c>crew-7 Иван Петров | obj-412 лист плазмы | Δ(2,-1) (12,-34)</c>.
    ///
    /// <para>
    /// Хендл — то, ради чего вся затея работает. Увидев <c>device-3 генератор аномалий</c>, агент
    /// вызывает по нему инструмент немедленно, без промежуточного <c>look</c>; без хендла «запусти
    /// его» стоило бы трёх ходов вместо одного. Реестр тот же самый, что у <c>look</c>
    /// (<see cref="AgentSession.Handles"/>), и это требование, а не удобство: разойдись они — и одна
    /// вещь стала бы для агента двумя.
    /// </para>
    /// <para>
    /// Уговору «наблюдение не носит EntityUid» это не противоречит. Тот запрет — про голос по рации:
    /// связать голос с сущностью человек в этой роли не может. Увиденное — ровно наоборот: игрок,
    /// который смотрит, как в генератор кладут плазму, может по этому генератору кликнуть.
    /// </para>
    /// </summary>
    private string? Describe(AgentSession session, EntityUid first, EntityUid second, EntityUid third,
        Vector2 eyeAt, Vector2 at)
    {
        var sb = new StringBuilder();
        var wrote = 0;

        // Повторы отсеиваются сравнением с предыдущими, а не множеством: участников не больше трёх,
        // и половина событий называет цель дважды — она же и «где». Печатать её два раза значит
        // платить токенами за шум.
        AppendPart(session, sb, first, ref wrote);

        if (second != first)
            AppendPart(session, sb, second, ref wrote);

        if (third != first && third != second)
            AppendPart(session, sb, third, ref wrote);

        if (wrote == 0)
            return null;

        // Тот же формат, что у строк look: Δ отвечает «в какой стороне от меня», абсолютная пара
        // скармливается move_camera. Δ отсчитана от глаза В МОМЕНТ СОБЫТИЯ — агент мог увести
        // камеру до своего хода, и eye= в строке SELF будет уже о другом месте.
        sb.Append(" | ").Append(PositionFrom(eyeAt, at));

        return sb.ToString();
    }

    /// <summary>Дописать одного участника как «хендл имя», если он вообще есть и как-то зовётся.</summary>
    private void AppendPart(AgentSession session, StringBuilder sb, EntityUid uid, ref int wrote)
    {
        if (!uid.IsValid() || Deleted(uid))
            return;

        var name = Identity.Name(uid, EntityManager);
        if (string.IsNullOrWhiteSpace(name))
            return;

        // TryGetHandle ДО KindOf, а не GetOrCreate(uid, KindOf(uid)): аргумент вычисляется всегда,
        // даже когда хендл уже есть, а KindOf — цепочка из тринадцати HasComp. Ровно эта ошибка
        // стоила времени в look; здесь она сработала бы в десятки раз чаще.
        if (!session.Handles.TryGetHandle(uid, out var handle))
            handle = session.Handles.GetOrCreate(uid, KindOf(uid));

        if (wrote > 0)
            sb.Append(" | ");

        sb.Append(handle).Append(' ').Append(name);
        wrote++;
    }

    // ------------------------------------------------------------------ учёт

    /// <summary>Мемо видимости тайла на один тик. Не кэш обзора: живёт до конца тика и умирает.</summary>
    private readonly Dictionary<Vector2i, bool> _seenTiles = new();

    private int _visionChecks;
    private int _visionSkipped;

    /// <summary>Сколько строк наблюдения выпущено за жизнь процесса. Только для тестов и журнала.</summary>
    private int _witnessed;

    private float _sinceWitnessReport;

    /// <summary>Сбросить счётчики тика и, если что-то потерялось, сказать об этом вслух.</summary>
    private void ResetWitnessTick(float frameTime)
    {
        _seenTiles.Clear();
        _visionChecks = 0;

        if (_visionSkipped == 0)
            return;

        _sinceWitnessReport += frameTime;
        if (_sinceWitnessReport < WitnessReportSeconds)
            return;

        _sawmill.Warning(
            $"наблюдение: пропущено {_visionSkipped} событий — потолок проверок видимости " +
            $"({_cfg.GetCVar(AiCVars.ObserveMaxChecksPerTick)} за тик) выбран");

        _visionSkipped = 0;
        _sinceWitnessReport = 0f;
    }

    private const float WitnessReportSeconds = 30f;
}
