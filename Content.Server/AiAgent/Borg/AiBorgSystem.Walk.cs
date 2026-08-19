using System.Collections.Generic;
using System.Numerics;
using Content.Shared.Movement.Components;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Робот идёт по СВОЕМУ пути сам, без апстримового рулевого.
///
/// <para>
/// <b>Почему пришлось.</b> Сначала маршрут строил наш поиск, а вести по нему должен был
/// <c>NPCSteeringSystem</c> — по коротким ногам, каждая заведомо в его пределах. На карте ротации
/// это упёрлось в стену: робот проходил 27 тайлов из 47 и вставал на (28, −25) у входа в атмос,
/// отвечая «дороги нет». Наш путь там был построен и проверен ЕГО ЖЕ правилом проходимости —
/// полигон навмеша есть, коллизия не конфликтует, — а он всё равно отказывался, одинаково при
/// ногах в шесть тайлов и в три.
/// </para>
/// <para>
/// Ставка на «глобальный маршрут наш, локальное движение чужое» себя не оправдала, и держаться за
/// неё дальше значило бы подпирать чужой поиск всё новыми обходами. Поэтому движение тоже наше:
/// раз путь уже есть и он корректен, вести по нему — задача на десяток строк.
/// </para>
/// <para>
/// <b>Что при этом НЕ потеряно.</b> Робот двигается тем же способом, что и всякий моб игры —
/// через <c>InputMoverComponent.CurTickSprintMovement</c>, то есть ровно тем полем, куда клиент
/// живого игрока кладёт нажатые стрелки. Физика, столкновения, скорость, невесомость и открывание
/// дверей телом (<c>DoorBumpOpener</c>) остаются апстримовыми. Мы задаём только направление.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private IGameTiming _timing = default!;

    /// <summary>Тайлы, которые роботу осталось пройти, и куда он в итоге идёт.</summary>
    private readonly Dictionary<EntityUid, Queue<Vector2i>> _trail = new();

    /// <summary>Насколько близко надо подойти к тайлу, чтобы считать его пройденным.</summary>
    private const float TileReached = 0.35f;

    /// <summary>Скорость подхода к последнему тайлу: у цели идём шагом, чтобы не проскочить.</summary>
    // Строго меньше половины клетки, и это не подстройка «на глазок».
    //
    // Половина клетки — 0.5. Порог в 0.6 означал «дошёл», когда робот стоял уже на СОСЕДНЕМ
    // тайле: для «подойди к двери» это незаметно, а для стройки смертельно — упаковка ложится не
    // туда, и квадрат экранирования не сходится. Поймано тестом сборки: девять клеток заказано,
    // все девять раз робот встал мимо, ровно на одну клетку.
    private const float ArriveDistance = 0.3f;

    private void SetTrail(EntityUid borg, List<Vector2i> tiles)
    {
        var queue = new Queue<Vector2i>();

        foreach (var t in tiles)
            queue.Enqueue(t);

        _trail[borg] = queue;
    }

    private void ClearTrail(EntityUid borg)
    {
        _trail.Remove(borg);
        Halt(borg);
    }

    /// <summary>Остановить ноги. Без этого робот продолжает ехать по последнему заданию.</summary>
    private void Halt(EntityUid borg)
    {
        if (!TryComp<InputMoverComponent>(borg, out var mover))
            return;

        mover.CurTickSprintMovement = Vector2.Zero;
        mover.LastInputTick = _timing.CurTick;
        mover.LastInputSubTick = ushort.MaxValue;
    }

    /// <summary>
    /// Один шаг ведения: подвинуть робота к следующему тайлу пути.
    /// </summary>
    /// <returns><c>true</c>, пока идём; <c>false</c> — путь пройден.</returns>
    private bool StepAlongTrail(EntityUid borg)
    {
        if (!_trail.TryGetValue(borg, out var trail) || !TryComp<InputMoverComponent>(borg, out var mover))
            return false;

        var xform = Transform(borg);

        if (xform.GridUid is not { } grid)
            return false;

        var here = xform.LocalPosition;

        // Съесть все тайлы, до которых уже дошли: на скорости за тик можно перекрыть больше одного.
        while (trail.Count > 0)
        {
            var target = Center(trail.Peek());
            var last = trail.Count == 1;

            if ((target - here).Length() > (last ? ArriveDistance : TileReached))
                break;

            trail.Dequeue();
        }

        if (trail.Count == 0)
        {
            _trail.Remove(borg);
            Halt(borg);
            return false;
        }

        var next = Center(trail.Peek());
        var delta = next - here;

        if (delta.LengthSquared() < 0.0001f)
            return true;

        // То же поле и те же метки, что ставит апстримовый рулевой в SetDirection: движок не
        // отличает наш ввод от клавиш живого игрока, и вся физика достаётся нам даром.
        mover.CurTickSprintMovement = Vector2.Normalize(delta);
        mover.LastInputTick = _timing.CurTick;
        mover.LastInputSubTick = ushort.MaxValue;

        return true;
    }

    /// <summary>Следующий тайл пути, если робот куда-то идёт.</summary>
    private Vector2i? NextTile(EntityUid borg) =>
        _trail.TryGetValue(borg, out var trail) && trail.Count > 0 ? trail.Peek() : null;

    private static Vector2 Center(Vector2i tile) => new(tile.X + 0.5f, tile.Y + 0.5f);
}
