using System.Collections.Generic;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;

using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.OracleTrace;

/// <summary>Куда система складывает замеченные события. Реализует <see cref="TraceRecorder"/>.</summary>
public interface IOracleEventSink
{
    void Event(string name, EntityUid target, params object[] args);
}

/// <summary>
/// Наблюдатель шины событий. Ничего не меняет — только записывает факт и
/// порядок поднятия событий, потому что порядок в "ev" сравнивается EXACT.
///
/// ПОЧЕМУ это EntitySystem, а не подписка снаружи через IEventBus: направленные
/// подписки Robust регистрируются один раз на старте и не рассчитаны на
/// подключение-отключение по ходу игры. Система подписывается штатно, а
/// «включена ли запись» решает наличие приёмника — <see cref="Sink"/>.
///
/// ПОЧЕМУ подписки навешаны на MetaDataComponent, а не на DoorComponent, к
/// которому события относятся: в Robust пара (компонент, событие) может иметь
/// РОВНО ОДНУ направленную подписку на весь движок — см.
/// EntityEventBus.EntAddSubscription, «Duplicate Subscriptions». Пара
/// (DoorComponent, DoorStateChangedEvent) уже занята самой дверной системой, и
/// наблюдатель на том же компоненте просто не дал бы серверу стартовать.
/// MetaDataComponent висит на КАЖДОЙ сущности, поэтому подписка через него
/// ловит те же направленные события и никому не мешает.
///
/// ЧЕГО ЗДЕСЬ НАМЕРЕННО НЕТ: флага «событие отменено». Отмена ставится
/// обработчиками, а место моего обработчика в очереди подписок никак не
/// зафиксировано; записанный «cancelled» означал бы «отменено к моменту, когда
/// до меня дошла очередь» — величину, которая у двух движков совпадёт только
/// случайно. Пишутся аргументы, заданные при конструировании события.
/// </summary>

public sealed class OracleTraceSystem : EntitySystem
{
    /// <summary>Приёмник. null — запись выключена, подписки вхолостую.</summary>
    public IOracleEventSink Sink;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MetaDataComponent, DoorStateChangedEvent>(OnDoorState);
        SubscribeLocalEvent<MetaDataComponent, DoorBoltsChangedEvent>(OnDoorBolts);
        SubscribeLocalEvent<MetaDataComponent, BeforeDoorOpenedEvent>(OnBeforeOpened);
        SubscribeLocalEvent<MetaDataComponent, BeforeDoorClosedEvent>(OnBeforeClosed);
        SubscribeLocalEvent<MetaDataComponent, BeforeDoorDeniedEvent>(OnBeforeDenied);
        SubscribeLocalEvent<MetaDataComponent, BeforeDoorAutoCloseEvent>(OnBeforeAutoClose);
        SubscribeLocalEvent<MetaDataComponent, ActivateInWorldEvent>(OnActivate);

        SubscribeLocalEvent<MetaDataComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<MetaDataComponent, EntRemovedFromContainerMessage>(OnRemoved);
    }

    private void OnDoorState(EntityUid uid, MetaDataComponent comp, DoorStateChangedEvent args)
        => Sink?.Event(nameof(DoorStateChangedEvent), uid, args.State);

    private void OnDoorBolts(EntityUid uid, MetaDataComponent comp, DoorBoltsChangedEvent args)
        => Sink?.Event(nameof(DoorBoltsChangedEvent), uid, args.BoltsDown);

    private void OnBeforeOpened(EntityUid uid, MetaDataComponent comp, BeforeDoorOpenedEvent args)
        => Sink?.Event(nameof(BeforeDoorOpenedEvent), uid);

    private void OnBeforeClosed(EntityUid uid, MetaDataComponent comp, BeforeDoorClosedEvent args)
        => Sink?.Event(nameof(BeforeDoorClosedEvent), uid, args.Partial);

    private void OnBeforeDenied(EntityUid uid, MetaDataComponent comp, BeforeDoorDeniedEvent args)
        => Sink?.Event(nameof(BeforeDoorDeniedEvent), uid);

    private void OnBeforeAutoClose(EntityUid uid, MetaDataComponent comp, BeforeDoorAutoCloseEvent args)
        => Sink?.Event(nameof(BeforeDoorAutoCloseEvent), uid);

    private void OnActivate(EntityUid uid, MetaDataComponent comp, ActivateInWorldEvent args)
        => Sink?.Event(nameof(ActivateInWorldEvent), uid, args.User, args.Complex);

    private void OnInserted(EntityUid uid, MetaDataComponent comp, EntInsertedIntoContainerMessage args)
        => Sink?.Event(nameof(EntInsertedIntoContainerMessage), uid, args.Container.ID, args.Entity);

    private void OnRemoved(EntityUid uid, MetaDataComponent comp, EntRemovedFromContainerMessage args)
        => Sink?.Event(nameof(EntRemovedFromContainerMessage), uid, args.Container.ID, args.Entity);
}
