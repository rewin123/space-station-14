using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Content.Server.Power.Components;
using Content.Server.Power.SMES;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.NodeContainer;
using Content.Shared.NodeContainer;
using Content.Shared.NodeContainer.NodeGroups;
using Content.Shared.Physics;
using Content.Shared.Power;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>
/// Резервное питание на смену, где нет инженеров — <c>ai.backup_power</c>.
///
/// <para>
/// <b>Зачем.</b> На этом сервере онлайн 0–2 человека, и это норма, а не авария. Инженерную смену
/// при таком онлайне не набирает никто, и станция остаётся на том, что накопили батареи: SMES
/// держит 8 МДж (<c>smes.yml</c>), апстримовый гайдбук прямо пишет «this will at most last 5-10
/// minutes». Дальше раунд идёт в темноте, и ИИ, чьё ядро тоже питается от этой сети, замолкает.
/// </para>
/// <para>
/// <b>Почему не солнечные панели, хотя они бесплатны и вечны.</b> Они не подключены. Обход графа
/// HV-кабеля по всем тринадцати картам ротации: массивы стоят на собственных кабельных островах,
/// без SMES и без пути к главной сети — на <b>одиннадцати картах из тринадцати</b> солнечной
/// мощности на главной сети РОВНО НОЛЬ. Исключения два и оба частичные: Oasis (70 панелей из 230)
/// и Tram2 (20 из 244). Апстрим этого не скрывает: «At the start of the shift solar panels are
/// misaligned and disconnected from the grid» (<c>Guidebook/Engineering/SolarPanels.xml</c>). Чтобы
/// они начали давать ток, на карте Packed нужно проложить около полутора сотен тайлов кабеля — то
/// есть переписать мир кодом, что куда наглее, чем поставить одну машину. Вдобавок панели никто не
/// наводит: <c>PowerSolarSystem</c> принудительно ставит всем панелям один поворот, и пишет его
/// только человек за консолью, поэтому ненаведённый массив половину каждого солнечного цикла даёт
/// нуль.
/// </para>
/// <para>
/// <b>Почему не игровое правило.</b> Список правил старта раунда лежит в
/// <c>Resources/Prototypes/game_presets.yml</c> — апстримовый файл, и добавить туда своё правило
/// аддитивно нельзя. Поэтому обычная система с подпиской, по образцу
/// <see cref="StationNameOverrideSystem"/>, заведённого здесь по той же причине: ни одного
/// изменённого файла апстрима.
/// </para>
/// <para>
/// <b>Почему на раздаче должностей, а не на <c>StationPostInitEvent</c>.</b> Условие «есть ли на
/// смене инженеры» до спавна игроков неизвестно. <c>StationPostInitEvent</c> срабатывает раньше и
/// ответил бы «инженеров нет» всегда.
/// </para>
/// </summary>
public sealed partial class BackupPowerSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private Content.Server.Maps.IGameMapManager _maps = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private Content.Server.NodeContainer.EntitySystems.NodeContainerSystem _nodes = default!;
    [Dependency] private Content.Shared.Construction.EntitySystems.AnchorableSystem _anchorable = default!;

    private const string GeneratorProto = "AiAgentBackupGenerator";

    /// <summary>Мощность одной машины из прототипа. Больше — просто ставим несколько.</summary>
    private const int WattsPerUnit = 60000;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("ai.power");

        // Момент, когда должности уже разданы и игроки заспавнены. Образец разбора —
        // Content.Server/Access/Systems/PresetIdCardSystem.cs.
        //
        // Событие поднимается ВСЕГДА, даже когда готовых игроков ноль: в
        // GameTicker.Spawning.SpawnPlayers нет раннего выхода по пустому списку. Это то, что нужно —
        // ядру ИИ питание нужно и на смене без экипажа.
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnJobsAssigned);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
    }

    /// <summary>
    /// Станции, которым генераторы на этот раунд уже поставлены.
    ///
    /// Событие раздачи должностей в принципе может подняться не один раз (его поднимают и правила,
    /// доигрывающие спавн), а <see cref="Deploy"/> сам по себе не идемпотентен — он поставит вторую
    /// партию поверх первой. Молча удвоенная мощность выглядит как «работает» и обнаружится только
    /// тем, что питание стало неубиваемым.
    /// </summary>
    private readonly HashSet<EntityUid> _served = new();

    private void OnRoundCleanup(RoundRestartCleanupEvent ev) => _served.Clear();

    private void OnJobsAssigned(RulePlayerJobsAssignedEvent ev)
    {
        if (!_cfg.GetCVar(AiCVars.BackupPower))
            return;

        // Своя настройка для этой станции, если она известна; иначе общий путь.
        //
        // Записи может не быть, и это нормальный путь, а не отказ: апстрим добавляет карты, и новая
        // попадёт в ротацию раньше, чем ей заведут строку. Тогда берём мощность из CVar'а и ищем
        // место так же, как на любой незнакомой станции.
        var tuning = SelectedMapTuning();

        var baseWatts = tuning?.Watts ?? _cfg.GetCVar(AiCVars.BackupPowerWatts);

        // Множитель поверх любого источника — единственный способ подкрутить мощность на живом
        // сервере: таблица станций лежит в прототипе, а прототипы читаются при старте процесса.
        var scale = Math.Max(0f, _cfg.GetCVar(AiCVars.BackupPowerScale));
        var watts = (int) MathF.Round(baseWatts * scale);

        if (watts <= 0)
            return;

        foreach (var station in _station.GetStations())
        {
            if (_served.Contains(station))
                continue;

            if (EngineeringOnDuty(station))
                continue;

            _served.Add(station);

            // НЕ ставим сразу. Энергосети на этот момент ещё не существует.
            //
            // Группы узлов собирает NodeGroupSystem в своём Update, отложенной очередью
            // (QueueReflood -> _toReflood -> FloodFillNode). На раздаче должностей, то есть в тот же
            // тик, что и старт раунда, Node.NodeGroup у кабелей ещё null, и поиск по главной сети
            // честно находит ноль тайлов. Первая версия этой правки ровно так и сломалась: тесты
            // показали ноль генераторов при живой логике поиска.
            _pending.Add(new Pending(station, watts, tuning?.Anchors));
        }
    }

    /// <summary>Станции, ждущие размещения, пока движок не соберёт энергосети.</summary>
    private readonly List<Pending> _pending = new();

    private sealed record Pending(EntityUid Station, int Watts, List<string>? Anchors)
    {
        public float Waited;
    }

    /// <summary>
    /// Сколько ждать появления энергосети, прежде чем признать отказ.
    ///
    /// Десять секунд — заведомо больше, чем нужно движку на первый флуд, и заведомо меньше, чем
    /// время, за которое кто-то заметит темноту. Истёк — пишем ошибку: молчаливое «сеть так и не
    /// появилась» выглядело бы точно как «генераторы не нужны».
    /// </summary>
    private const float DeployTimeoutSeconds = 10f;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pending.Count == 0)
            return;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];

            if (Deploy(pending.Station, pending.Watts, pending.Anchors))
            {
                _pending.RemoveAt(i);
                continue;
            }

            pending.Waited += frameTime;

            if (pending.Waited < DeployTimeoutSeconds)
                continue;

            _pending.RemoveAt(i);
            _sawmill.Error(
                $"резервное питание не поставлено на {ToPrettyString(pending.Station)}: за " +
                $"{DeployTimeoutSeconds:F0}с не нашлось ни одной энергосети с SMES");
        }
    }

    /// <summary>
    /// Настройка для выбранной на этот раунд карты, если она заведена.
    /// </summary>
    private AiBackupPowerPrototype? SelectedMapTuning()
    {
        var mapId = _maps.GetSelectedMap()?.ID;

        if (mapId == null)
            return null;

        return _protoMan.TryIndex<AiBackupPowerPrototype>(mapId, out var tuning) ? tuning : null;
    }

    /// <summary>
    /// Есть ли на этой станции хоть один игрок с должностью из инженерного департамента.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Состав отдела берётся из прототипа департамента, а не списком роль-за-ролью в коде: форк,
    /// добавивший свою инженерную должность, учтётся сам. Для апстрима это
    /// <c>AtmosphericTechnician</c>, <c>ChiefEngineer</c>, <c>StationEngineer</c>,
    /// <c>TechnicalAssistant</c> (<c>Roles/Jobs/departments.yml</c>).
    /// </para>
    /// <para>
    /// <b>Публичный ради теста, и это осознанно.</b> Проверить условие, поднимая
    /// <c>RulePlayerJobsAssignedEvent</c> вручную, нельзя: на него подписан
    /// <c>AntagSelectionSystem</c>, и вне последовательности старта раунда он валится с
    /// «_postSpawnRules was null». То есть единственный способ проверить решение — спросить о нём
    /// напрямую.
    /// </para>
    /// </remarks>
    public bool EngineeringOnDuty(EntityUid station)
    {
        var departmentId = _cfg.GetCVar(AiCVars.BackupPowerDepartment);

        if (!_protoMan.TryIndex<DepartmentPrototype>(departmentId, out var department))
        {
            // Отказ громкий. Молча считать, что инженеров нет, — значит ставить генератор каждую
            // смену, включая полностью укомплектованные, и узнать об этом от игроков.
            _sawmill.Error(
                $"департамент '{departmentId}' не найден — резервное питание не ставится. " +
                "Проверь ai.backup_power_department");
            return true;
        }

        // Нет компонента должностей — нечего и считать. Такое бывает у не-станций (аутпост
        // ядерщиков, ЦК): резервное питание им не нужно.
        if (!TryComp<StationJobsComponent>(station, out var stationJobs))
            return true;

        var roles = department.Roles;

        foreach (var assigned in stationJobs.PlayerJobs.Values)
        {
            foreach (var job in assigned)
            {
                if (roles.Contains(job))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Поставить генераторы на станцию.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Место — только тайл, на котором УЖЕ лежит высоковольтный кабель, и это не удобство, а
    /// требование. <c>PowerSupplier</c> подключается через <c>CableDeviceNode</c>, а тот
    /// (<c>Content.Server/Power/Nodes/CableDeviceNode.cs</c>) соединяется исключительно с
    /// <c>CableNode</c> в том же тайле и только у заякоренной сущности. Генератор, поставленный не
    /// на кабель, — мёртвый металл, который гудит и не даёт ничего, и никакой ошибки при этом не
    /// будет.
    /// </para>
    /// <para>
    /// Тайлы берутся вокруг SMES, а не случайные. Во-первых, SMES по определению стоит на ГЛАВНОЙ
    /// сети — попасть на изолированный солнечный остров невозможно. Во-вторых, это инженерное
    /// помещение: генератор оказывается там, где ему место, рядом с тем, что он питает. Случайный
    /// тайл (<c>TryFindRandomTileOnStation</c>, которым пользуются variation-пассы) почти никогда
    /// не окажется кабельным.
    /// </para>
    /// </remarks>
    private bool Deploy(EntityUid station, int watts, List<string>? anchors)
    {
        var wanted = Math.Max(1, (int) Math.Ceiling(watts / (double) WattsPerUnit));
        var perUnit = watts / (float) wanted;

        var placed = 0;

        foreach (var coords in PlacementTiles(station, anchors))
        {
            if (placed >= wanted)
                break;

            var uid = Spawn(GeneratorProto, coords);

            // Мощность ставится здесь, а не только в прототипе: иначе ai.backup_power_watts нельзя
            // было бы подкрутить на живом сервере, а пересборка кикает всех играющих.
            if (TryComp<PowerSupplierComponent>(uid, out var supplier))
                supplier.MaxSupply = perUnit;

            placed++;
        }

        // Ноль здесь чаще всего значит «сеть ещё не собрана», а не «места нет»: вызывающий
        // повторит попытку в следующем тике и признает отказ только по таймауту.
        if (placed == 0)
            return false;

        // Недобор называется вслух. Молчаливый недобор читается как «работает», хотя мощности
        // вдвое меньше заказанной, и разбираться с этим будут по жалобам на свет. Разбор карт
        // показал, что это не теория: на нескольких станциях подходящих тайлов ровно столько,
        // сколько нужно, и один катwalk от маппера превращает два генератора в один.
        if (placed < wanted)
        {
            _sawmill.Warning(
                $"резервное питание: поставлено {placed} из {wanted} машин — " +
                $"{placed * perUnit:F0}Вт вместо {watts}Вт, не хватило подходящих тайлов");
        }
        else
        {
            _sawmill.Info(
                $"резервное питание: {placed}×{perUnit:F0}Вт на станции {ToPrettyString(station)} " +
                "(инженерной смены нет)");
        }

        return true;
    }

    /// <summary>
    /// Свободные тайлы с высоковольтным кабелем, соседние с SMES этой станции.
    /// </summary>
    /// <summary>
    /// Тайлы главной энергосети станции, куда можно поставить генератор, в порядке предпочтения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Первая версия искала только рядом с SMES, и это было ошибкой.</b> Разбор всех тринадцати
    /// карт ротации показал, что банки SMES обставлены кабельными терминалами, катwalk'ами и
    /// подстанциями почти вплотную: на Sushi подходящих тайлов вышло <b>ноль</b> — то есть система
    /// молча не поставила бы ничего, — на Oasis один, на Marathon два, а на Bagel четыре из пяти
    /// оказались в дальних солнечных отсеках вместо инженерного. Соседство с SMES — не то место,
    /// где есть свободные тайлы.
    /// </para>
    /// <para>
    /// Ищем поэтому по <b>всей главной сети</b>. Главная — та, в которой больше всего SMES; это
    /// снимает вторую ошибку той же версии: она не различала сети и могла поставить генератор на
    /// изолированный солнечный остров, где нет ни одного потребителя (на Packed такой SMES есть,
    /// с тридцатью одной панелью и без APC).
    /// </para>
    /// <para>
    /// Сеть берётся из рантайма, а не обходом графа кабелей: у каждого узла есть
    /// <c>Node.NodeGroup</c>, и движок уже посчитал связность за нас.
    /// </para>
    /// </remarks>
    private List<EntityCoordinates> PlacementTiles(EntityUid station, List<string>? anchors)
    {
        var result = new List<EntityCoordinates>();

        if (MainNet(station) is not { } net)
            return result;

        var targets = anchors is { Count: > 0 } ? AnchorPositions(station, anchors) : new List<Vector2>();
        if (targets.Count == 0)
            targets = SmesPositions(station);

        var seen = new HashSet<(EntityUid Grid, Vector2i Tile)>();
        var candidates = new List<(EntityCoordinates Coords, float Score)>();

        var query = EntityQueryEnumerator<CableComponent, NodeContainerComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var cable, out var container, out var xform))
        {
            if (cable.CableType != CableType.HighVoltage)
                continue;

            if (!_nodes.TryGetNode<Node>(container, "power", out var node) || node.NodeGroup != net)
                continue;

            if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
                continue;

            var tile = _map.CoordinatesToTile(gridUid, grid, xform.Coordinates);

            if (!seen.Add((gridUid, tile)))
                continue;

            // Штатная проверка апстрима, а не своя. Прошлая версия считала занятым любой
            // заякоренный сосед, включая катwalk и трубу, — по физике генератор рядом с ними
            // прекрасно стоит, а вот стена или машина действительно мешают.
            if (!_anchorable.TileFree((gridUid, grid), tile,
                    (int) CollisionGroup.MachineLayer, (int) CollisionGroup.MachineMask))
            {
                continue;
            }

            var coords = _map.ToCoordinates(gridUid, tile, grid);
            candidates.Add((coords, Score(_xform.ToMapCoordinates(coords).Position)));
        }

        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));

        foreach (var (coords, _) in candidates)
            result.Add(coords);

        return result;

        float Score(Vector2 at)
        {
            if (targets.Count == 0)
                return 0f;

            var best = float.MaxValue;

            foreach (var target in targets)
                best = MathF.Min(best, (target - at).LengthSquared());

            return best;
        }
    }

    /// <summary>
    /// Главная энергосеть станции — та, в которой больше всего SMES.
    /// </summary>
    /// <remarks>
    /// «Больше всего SMES», а не «первая найденная»: на каждой карте есть солнечные острова со
    /// своим SMES, и попасть генератором туда значит питать сеть без потребителей.
    /// </remarks>
    private INodeGroup? MainNet(EntityUid station)
    {
        var votes = new Dictionary<INodeGroup, int>();
        var query = EntityQueryEnumerator<SmesComponent, NodeContainerComponent>();

        while (query.MoveNext(out var smes, out _, out var container))
        {
            if (_station.GetOwningStation(smes) != station)
                continue;

            if (!_nodes.TryGetNode<Node>(container, "output", out var node) || node.NodeGroup == null)
                continue;

            votes[node.NodeGroup] = votes.GetValueOrDefault(node.NodeGroup) + 1;
        }

        INodeGroup? best = null;
        var bestCount = 0;

        foreach (var (group, count) in votes)
        {
            if (count <= bestCount)
                continue;

            best = group;
            bestCount = count;
        }

        return best;
    }

    /// <summary>
    /// Мировые позиции маяков станции, чьё название совпадает с одним из названных.
    /// </summary>
    /// <remarks>
    /// Сравнение по вхождению подстроки без учёта регистра: в списке стоит «Engineering», а на карте
    /// маяк может называться «Engineering Storage». Требовать точного совпадения значило бы, что
    /// предпочтение молча не работает на половине карт.
    /// </remarks>
    private List<Vector2> AnchorPositions(EntityUid station, List<string> anchors)
    {
        var result = new List<Vector2>();
        var query = EntityQueryEnumerator<Content.Shared.Pinpointer.NavMapComponent, TransformComponent>();

        while (query.MoveNext(out var gridUid, out var navMap, out _))
        {
            if (_station.GetOwningStation(gridUid) != station)
                continue;

            foreach (var beacon in navMap.Beacons.Values)
            {
                foreach (var wanted in anchors)
                {
                    if (string.IsNullOrWhiteSpace(wanted))
                        continue;

                    if (beacon.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(_xform.ToMapCoordinates(new EntityCoordinates(gridUid, beacon.Position)).Position);
                        break;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Мировые позиции SMES станции — запасное предпочтение, когда маяков нет.</summary>
    private List<Vector2> SmesPositions(EntityUid station)
    {
        var result = new List<Vector2>();
        var query = EntityQueryEnumerator<SmesComponent, TransformComponent>();

        while (query.MoveNext(out var smes, out _, out var xform))
        {
            if (_station.GetOwningStation(smes) == station)
                result.Add(_xform.GetWorldPosition(xform));
        }

        return result;
    }
}
