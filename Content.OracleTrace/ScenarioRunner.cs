using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Shared.Interaction;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.OracleTrace;

/// <summary>
/// Исполнитель сценария поверх харнесса. Держит соответствие «синтетический
/// номер -> EntityUid» через <see cref="TraceRecorder"/> и умеет применить
/// каждую операцию in.jsonl к живому серверу.
///
/// ВСЕ операции применяются на потоке сервера (WaitPost) и ТОЛЬКО на границе
/// тика, перед его прогоном. Иначе момент применения зависел бы от того, где
/// именно в такте оказался тестовый поток, и трасса перестала бы быть
/// воспроизводимой сама с собой, не говоря уже про второй движок.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly IEntityManager _entMan;
    private readonly TraceRecorder _recorder;
    private readonly MapId _map;
    private readonly Dictionary<string, Type> _compTypes = new();

    private readonly SharedInteractionSystem _interaction;
    private readonly SharedContainerSystem _containers;
    private readonly SharedTransformSystem _transform;

    public ScenarioRunner(IEntityManager entMan, IComponentFactory factory, TraceRecorder recorder, MapId map)
    {
        _entMan = entMan;
        _recorder = recorder;
        _map = map;
        _interaction = entMan.System<SharedInteractionSystem>();
        _containers = entMan.System<SharedContainerSystem>();
        _transform = entMan.System<SharedTransformSystem>();

        foreach (var type in factory.AllRegisteredTypes)
            _compTypes[type.Name] = type;
    }

    public void Apply(ScenarioOp op)
    {
        switch (op)
        {
            case SpawnOp spawn:
            {
                var uid = _entMan.SpawnEntity(spawn.Proto, new MapCoordinates(new Vector2(spawn.X, spawn.Y), _map));
                var actual = _recorder.SyntheticOf(uid);
                if (actual != spawn.Id)
                {
                    // Номер назначается порядком спавна. Если он разошёлся с
                    // объявленным, значит порядок в сценарии уже не тот, что
                    // на самом деле, и ВСЕ последующие ссылки указывают не туда.
                    throw new InvalidOperationException(
                        $"строка {op.Line}: спавн {spawn.Proto} получил синтетический номер {actual}, " +
                        $"а сценарий объявил {spawn.Id}");
                }

                break;
            }

            case SetOp set:
            {
                var uid = _recorder.Resolve(set.Entity);
                if (!_compTypes.TryGetValue(set.Comp, out var type))
                    throw new InvalidOperationException($"строка {op.Line}: неизвестный компонент {set.Comp}");

                if (!_entMan.TryGetComponent(uid, type, out var comp))
                    throw new InvalidOperationException($"строка {op.Line}: у сущности {set.Entity} нет {set.Comp}");

                AssignMember(type, comp, set.Field, set.Value, op.Line);
                _entMan.Dirty(uid, comp);
                break;
            }

            case MoveOp move:
            {
                // Мировые позиция и поворот разом: локальные координаты в двух
                // движках значат разное (здесь сущность висит на сетке, у порта
                // сеток нет вовсе), а мировые — одно и то же.
                var uid = _recorder.Resolve(move.Entity);
                _transform.SetWorldPositionRotation(uid, new Vector2(move.X, move.Y), new Angle(move.Rot));
                break;
            }

            case InteractOp interact:
            {
                var user = _recorder.Resolve(interact.User);
                var target = _recorder.Resolve(interact.Target);

                switch (interact.Kind)
                {
                    case "activate":
                        _interaction.InteractionActivate(
                            user,
                            target,
                            checkCanInteract: interact.CheckCanInteract,
                            checkUseDelay: true,
                            checkAccess: interact.CheckAccess,
                            complexInteractions: interact.Complex);
                        break;
                    case "hand":
                        _interaction.InteractHand(user, target);
                        break;
                    case "using":
                    {
                        if (interact.Used is not { } usedId)
                            throw new InvalidOperationException($"строка {op.Line}: kind=using требует \"used\"");

                        var used = _recorder.Resolve(usedId);
                        _interaction.InteractUsing(
                            user,
                            used,
                            target,
                            _entMan.GetComponent<TransformComponent>(target).Coordinates,
                            checkCanInteract: interact.CheckCanInteract,
                            checkCanUse: interact.CheckCanInteract);
                        break;
                    }
                    case "use-in-hand":
                        _interaction.UseInHandInteraction(
                            user,
                            target,
                            checkCanUse: interact.CheckCanInteract,
                            checkCanInteract: interact.CheckCanInteract);
                        break;
                    default:
                        throw new InvalidOperationException($"строка {op.Line}: неизвестный kind \"{interact.Kind}\"");
                }

                break;
            }

            case ContainerOp container:
            {
                var owner = _recorder.Resolve(container.Owner);
                var entity = _recorder.Resolve(container.Entity);

                switch (container.Action)
                {
                    case "insert":
                    {
                        var cont = _containers.EnsureContainer<Container>(owner, container.Key);
                        if (!_containers.Insert((entity, null, null, null), cont))
                        {
                            // Отказ вставки — не «ничего не произошло»: дальше
                            // сценарий будет доставать предмет, которого там нет,
                            // и трасса запишет пустой контейнер как норму.
                            throw new InvalidOperationException(
                                $"строка {op.Line}: не удалось положить {container.Entity} в {container.Key}");
                        }

                        break;
                    }
                    case "remove":
                    {
                        if (!_containers.TryGetContainingContainer((entity, null, null), out var cont)
                            || cont.ID != container.Key)
                        {
                            throw new InvalidOperationException(
                                $"строка {op.Line}: {container.Entity} не лежит в контейнере \"{container.Key}\"");
                        }

                        if (!_containers.Remove((entity, null, null), cont))
                            throw new InvalidOperationException($"строка {op.Line}: не удалось достать {container.Entity}");

                        break;
                    }
                    default:
                        throw new InvalidOperationException($"строка {op.Line}: неизвестное action \"{container.Action}\"");
                }

                break;
            }

            case DeleteOp del:
                _entMan.DeleteEntity(_recorder.Resolve(del.Entity));
                break;

            case TickOp:
                throw new InvalidOperationException("tick исполняется снаружи, а не через Apply");

            case ObserveOp:
                break;

            default:
                throw new InvalidOperationException($"строка {op.Line}: нечем исполнить {op.GetType().Name}");
        }
    }

    /// <summary>
    /// Запись поля компонента из JSON. Типы разбираются явно и по одному:
    /// «универсальный» JsonSerializer.Deserialize сюда не годится, потому что
    /// TimeSpan он ждёт строкой ISO-8601, а сценарий пишет секунды числом —
    /// как их видит порт.
    /// </summary>
    private static void AssignMember(Type type, IComponent comp, string camelField, JsonElement value, int line)
    {
        MemberInfo member = null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        foreach (var f in type.GetFields(flags))
        {
            if (TraceRecorder.Camel(f.Name) == camelField)
                member = f;
        }

        if (member == null)
        {
            foreach (var p in type.GetProperties(flags))
            {
                if (TraceRecorder.Camel(p.Name) == camelField && p.GetIndexParameters().Length == 0 && p.CanWrite)
                    member = p;
            }
        }

        if (member == null)
            throw new InvalidOperationException($"строка {line}: у {type.Name} нет записываемого поля \"{camelField}\"");

        var target = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;
        var converted = Convert(target, value, line);

        if (member is FieldInfo field)
            field.SetValue(comp, converted);
        else
            ((PropertyInfo)member).SetValue(comp, converted);
    }

    private static object Convert(Type target, JsonElement value, int line)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying != null)
        {
            if (value.ValueKind == JsonValueKind.Null)
                return null;
            target = underlying;
        }

        if (target == typeof(bool))
            return value.GetBoolean();
        if (target == typeof(string))
            return value.GetString();
        if (target.IsEnum)
            return Enum.Parse(target, value.GetString(), ignoreCase: false);
        if (target == typeof(TimeSpan))
            return TimeSpan.FromSeconds(value.GetDouble());
        if (target == typeof(float))
            return value.GetSingle();
        if (target == typeof(double))
            return value.GetDouble();
        if (target == typeof(int))
            return value.GetInt32();
        if (target == typeof(long))
            return value.GetInt64();

        throw new InvalidOperationException(
            $"строка {line}: поле типа {target.Name} через \"set\" не переносится; " +
            "добавь разбор в ScenarioRunner.Convert, если это действительно переносимая величина");
    }
}
