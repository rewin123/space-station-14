using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.OracleTrace;

/// <summary>
/// Канонический дампер. После каждого тика сервера пишет одну строку трассы в
/// формате tools/tracediff/types.ts.
///
/// СИНТЕТИЧЕСКИЕ ID. Сущности нумеруются с 1 в порядке срабатывания
/// EntityAdded, а не по EntityUid: номера сущностей в двух движках не совпадут
/// никогда, и диффить по ним нельзя. Нумеруются ТОЛЬКО сущности, родившиеся
/// после начала записи; карта, сетка и служебные сущности харнесса рождаются
/// до и в трассу не попадают вовсе — иначе трасса зависела бы от того, сколько
/// сущностей поднял пул, а это не свойство симуляции.
///
/// СНИМОК ПОЛНЫЙ, НЕ ДЕЛЬТА. Каждый тик выписываются все наблюдаемые
/// компоненты всех живых отслеживаемых сущностей. Дельта была бы короче, но
/// потребовала бы, чтобы оба движка одинаково определяли «поле изменилось»;
/// на дельте расхождение в детекторе изменений маскирует расхождение в
/// симуляции. Полный снимок — строгое надмножество: любая разница в значении
/// видна на том же тике, где возникла.
/// </summary>
public sealed class TraceRecorder : IOracleEventSink
{
    private readonly IEntityManager _entMan;
    private readonly IComponentFactory _factory;
    private readonly SharedTransformSystem _xform;
    private readonly OracleTraceSystem _observer;
    private readonly ObserveOp _observe;
    private readonly IGameTiming _timing;

    /// <summary>
    /// Игровое время в момент включения записи. Все АБСОЛЮТНЫЕ отметки времени
    /// (поля, помеченные в "observe" префиксом "@") пишутся относительно него.
    ///
    /// ПОЧЕМУ: CurTime у оригинала отсчитывается от старта сервера и к началу
    /// сценария уже накопил сотни тиков, потраченных пулом на поднятие пары.
    /// У порта такой предыстории нет вовсе. Абсолютные отметки разошлись бы на
    /// эту предысторию — на величину, которая к симуляции отношения не имеет,
    /// и допуск в 0.033 с (один тик) её не покрыл бы никогда.
    /// </summary>
    private TimeSpan _timeOrigin;

    /// <summary>Имя C#-класса компонента -> тип. Ключи "observe" — имена классов, не YAML-имена.</summary>
    private readonly Dictionary<string, Type> _compTypes = new();

    /// <summary>Кэш «camelCase-имя поля -> член типа». Рефлексия по каждому тику на каждое поле — дорого.</summary>
    private readonly Dictionary<Type, Dictionary<string, MemberInfo>> _members = new();

    private readonly Dictionary<EntityUid, int> _ids = new();
    private readonly List<EntityUid> _alive = new();
    private readonly List<Entity<MetaDataComponent>> _spawnedThisTick = new();
    private readonly List<int> _despawnedThisTick = new();
    private readonly List<JsonArray> _eventsThisTick = new();
    private readonly List<string> _lines = new();

    /// <summary>
    /// Кодировщик без экранирования безобидных символов. По умолчанию
    /// System.Text.Json пишет ключ "+" как "\u002B" — JSON.parse это, конечно,
    /// разберёт, но глазами трассу читать становится нечем, а читают её как раз
    /// тогда, когда что-то разошлось.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private int _nextId = 1;
    private int _tick;
    private bool _armed;

    /// <summary>Имена компонентов из "observe", отсортированные — обход множеств только по отсортированному ключу.</summary>
    private readonly string[] _observedComps;

    public TraceRecorder(IEntityManager entMan, IComponentFactory factory, IGameTiming timing, ObserveOp observe)
    {
        _entMan = entMan;
        _factory = factory;
        _observe = observe;
        _timing = timing;
        _xform = entMan.System<SharedTransformSystem>();
        _observer = entMan.System<OracleTraceSystem>();
        _observedComps = observe.Components.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        foreach (var type in factory.AllRegisteredTypes)
            _compTypes[type.Name] = type;

        foreach (var name in _observedComps)
        {
            if (!_compTypes.ContainsKey(name))
                throw new InvalidOperationException($"observe: неизвестный компонент \"{name}\"");
        }
    }

    public IReadOnlyList<string> Lines => _lines;
    public int Ticks => _tick;

    /// <summary>Синтетический номер сущности; -1 для нетронутых записью и невалидных.</summary>
    public int SyntheticOf(EntityUid uid) => _ids.TryGetValue(uid, out var id) ? id : -1;

    /// <summary>Найти сущность по синтетическому номеру. Бросает, если номера нет — сценарий врёт.</summary>
    public EntityUid Resolve(int synthetic)
    {
        foreach (var (uid, id) in _ids)
        {
            if (id == synthetic)
                return uid;
        }

        throw new InvalidOperationException($"в сценарии упомянута сущность {synthetic}, которой нет");
    }

    public void Arm()
    {
        if (_armed)
            throw new InvalidOperationException("запись уже включена");

        _armed = true;
        _timeOrigin = _timing.CurTime;
        _entMan.EntityAdded += OnEntityAdded;
        _entMan.EntityDeleted += OnEntityDeleted;
        _observer.Sink = this;
    }

    public void Disarm()
    {
        if (!_armed)
            return;

        _armed = false;
        _entMan.EntityAdded -= OnEntityAdded;
        _entMan.EntityDeleted -= OnEntityDeleted;
        _observer.Sink = null;
    }

    private void OnEntityAdded(Entity<MetaDataComponent> ent)
    {
        _ids[ent.Owner] = _nextId++;
        _alive.Add(ent.Owner);
        _spawnedThisTick.Add(ent);
    }

    private void OnEntityDeleted(Entity<MetaDataComponent> ent)
    {
        if (!_ids.TryGetValue(ent.Owner, out var id))
            return; // родилась до записи — в трассе её нет, и смерти её тоже нет.

        _despawnedThisTick.Add(id);
        _retired[ent.Owner] = id;
        _alive.Remove(ent.Owner);
        _ids.Remove(ent.Owner);
    }

    void IOracleEventSink.Event(string name, EntityUid target, params object[] args)
    {
        if (!_observe.Events.Contains(name))
            return;

        var arr = new JsonArray { JsonValue.Create(name), JsonValue.Create(SyntheticOf(target)) };
        foreach (var a in args)
            arr.Add(Render(a));

        _eventsThisTick.Add(arr);
    }

    /// <summary>Снять строку трассы за только что отработавший тик.</summary>
    public void Capture()
    {
        var line = new JsonObject { ["t"] = _tick };

        var life = new JsonObject();
        if (_spawnedThisTick.Count > 0)
        {
            var born = new JsonArray();
            foreach (var ent in _spawnedThisTick)
            {
                var proto = ent.Comp?.EntityPrototype?.ID;
                born.Add(new JsonArray { JsonValue.Create(_ids.TryGetValue(ent.Owner, out var id) ? id : IdOfDead(ent.Owner)), JsonValue.Create(proto) });
            }

            life["+"] = born;
        }

        if (_despawnedThisTick.Count > 0)
        {
            var dead = new JsonArray();
            foreach (var id in _despawnedThisTick)
                dead.Add(JsonValue.Create(id));
            life["-"] = dead;
        }

        if (life.Count > 0)
            line["life"] = life;

        var ents = SnapshotEntities();
        if (ents.Count > 0)
            line["e"] = ents;

        if (_eventsThisTick.Count > 0)
        {
            var evs = new JsonArray();
            foreach (var e in _eventsThisTick)
                evs.Add(e);
            line["ev"] = evs;
        }

        if (_observe.Containers)
        {
            var ctr = SnapshotContainers();
            if (ctr.Count > 0)
                line["ctr"] = ctr;
        }

        _lines.Add(line.ToJsonString(JsonOpts));
        _tick++;
        _spawnedThisTick.Clear();
        _despawnedThisTick.Clear();
        _eventsThisTick.Clear();
        _retired.Clear();
    }

    /// <summary>
    /// Номер сущности, которая родилась и умерла внутри одного тика: к моменту
    /// снимка её уже нет в _ids, но в "life" она обязана быть под своим номером.
    /// </summary>
    private readonly Dictionary<EntityUid, int> _retired = new();

    private int IdOfDead(EntityUid uid) => _retired.TryGetValue(uid, out var id) ? id : -1;

    private JsonArray SnapshotEntities()
    {
        var result = new JsonArray();

        // Обход строго по синтетическому номеру: порядок вставки в словарь
        // между движками совпадёт только случайно.
        foreach (var uid in _alive.OrderBy(SyntheticOf))
        {
            var id = SyntheticOf(uid);
            foreach (var compName in _observedComps)
            {
                var type = _compTypes[compName];
                if (!_entMan.TryGetComponent(uid, type, out var comp))
                    continue;

                var fields = new JsonObject();
                foreach (var field in _observe.Components[compName].OrderBy(Plain, StringComparer.Ordinal))
                    fields[Plain(field)] = RenderField(uid, comp, type, compName, field);

                result.Add(new JsonArray { JsonValue.Create(id), JsonValue.Create(compName), fields });
            }
        }

        return result;
    }

    private JsonArray SnapshotContainers()
    {
        var result = new JsonArray();

        foreach (var uid in _alive.OrderBy(SyntheticOf))
        {
            if (!_entMan.TryGetComponent<ContainerManagerComponent>(uid, out var manager))
                continue;

            foreach (var key in manager.Containers.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var contents = new JsonArray();
                // Порядок внутри контейнера — свойство симуляции, его НЕ сортируем.
                foreach (var contained in manager.Containers[key].ContainedEntities)
                    contents.Add(JsonValue.Create(SyntheticOf(contained)));

                result.Add(new JsonArray { JsonValue.Create(SyntheticOf(uid)), JsonValue.Create(key), contents });
            }
        }

        return result;
    }

    /// <summary>
    /// Значение одного поля. TransformComponent разобран вручную: мировые
    /// координаты в Robust живут не в полях компонента, а считаются системой
    /// по цепочке родителей, и рефлексия по полям дала бы локальные координаты
    /// относительно сетки — величину, зависящую от того, какую сетку поднял
    /// харнесс.
    /// </summary>
    /// <summary>Имя поля без пометки "@" — именно оно попадает в трассу и в tolerance.yml.</summary>
    private static string Plain(string field) => field.StartsWith('@') ? field[1..] : field;

    private JsonNode RenderField(EntityUid uid, IComponent comp, Type type, string compName, string field)
    {
        var absoluteTime = field.StartsWith('@');
        field = Plain(field);

        if (compName == nameof(TransformComponent))
        {
            var xform = (TransformComponent)comp;
            switch (field)
            {
                case "x": return JsonValue.Create((double)_xform.GetWorldPosition(uid).X);
                case "y": return JsonValue.Create((double)_xform.GetWorldPosition(uid).Y);
                case "rot": return JsonValue.Create(_xform.GetWorldRotation(uid).Theta);
                case "parent": return JsonValue.Create(SyntheticOf(xform.ParentUid));
                case "anchored": return JsonValue.Create(xform.Anchored);
            }
        }

        var member = Member(type, field);
        var value = member switch
        {
            FieldInfo f => f.GetValue(comp),
            PropertyInfo p => p.GetValue(comp),
            _ => throw new InvalidOperationException($"observe: у {compName} нет поля \"{field}\""),
        };

        if (absoluteTime)
        {
            if (value == null)
                return null;

            if (value is not TimeSpan stamp)
            {
                throw new InvalidOperationException(
                    $"observe: пометка \"@\" у {compName}.{field} значит «абсолютное игровое время», " +
                    $"а поле имеет тип {value.GetType().Name}");
            }

            return JsonValue.Create((stamp - _timeOrigin).TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        return Render(value);
    }

    private MemberInfo Member(Type type, string camelField)
    {
        if (!_members.TryGetValue(type, out var map))
        {
            map = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var f in type.GetFields(flags))
                map[Camel(f.Name)] = f;
            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length == 0)
                    map[Camel(p.Name)] = p;
            }

            _members[type] = map;
        }

        return map.GetValueOrDefault(camelField);
    }

    /// <summary>
    /// C# пишет поля с большой буквы, порт — с маленькой. Каноническое имя в
    /// трассе — как в порте: допуски в tolerance.yml перечислены в camelCase
    /// ("posX", "nextStateChange"), и переименовывать их ради C# нельзя.
    /// </summary>
    public static string Camel(string name)
        => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private JsonNode Render(object value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return JsonValue.Create(b);
            case string s:
                return JsonValue.Create(s);
            case Enum e:
                // Перечисление — строкой, а не числом: числовые значения enum
                // в порте могут быть переставлены, имя же обязано совпадать.
                return JsonValue.Create(e.ToString());
            case TimeSpan ts:
                // Игровое время — строкой с тремя знаками, ровно как ждёт
                // правило "time" в tolerance.yml (parseAsFloat).
                return JsonValue.Create(ts.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
            case EntityUid uid:
                return JsonValue.Create(SyntheticOf(uid));
            case float f:
                return JsonValue.Create((double)f);
            case double d:
                return JsonValue.Create(d);
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value));
            case Angle a:
                return JsonValue.Create(a.Theta);
            case Vector2 v:
                return new JsonObject { ["x"] = JsonValue.Create((double)v.X), ["y"] = JsonValue.Create((double)v.Y) };
            case EntProtoId proto:
                return JsonValue.Create(proto.Id);
            case IEnumerable<EntityUid> ents:
            {
                // Множества сущностей упорядочиваются по синтетическому номеру:
                // порядок обхода HashSet в C# и Set в JS разный, а состав — EXACT.
                var arr = new JsonArray();
                foreach (var id in ents.Select(SyntheticOf).OrderBy(x => x))
                    arr.Add(JsonValue.Create(id));
                return arr;
            }
            case IEnumerable seq and not IEnumerable<char>:
            {
                var arr = new JsonArray();
                foreach (var item in seq)
                    arr.Add(Render(item));
                return arr;
            }
            default:
                throw new InvalidOperationException(
                    $"в трассу нечем записать значение типа {value.GetType().Name}; " +
                    "либо убери поле из \"observe\", либо научи TraceRecorder.Render его переносить");
        }
    }
}
