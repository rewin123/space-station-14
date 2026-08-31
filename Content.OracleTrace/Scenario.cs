using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Content.OracleTrace;

/// <summary>
/// Разбор in.jsonl — ВХОДА, который читают ОБА движка.
///
/// В файл попадает только то, что переносимо: спавн прототипа по координатам,
/// запись поля компонента, игровое взаимодействие, операция контейнера,
/// удаление сущности и продвижение времени. Вызовов методов C# в сценарии
/// нет и быть не может: у порта нет ни `DoorSystem.StartOpening`, ни его
/// сигнатуры, а если бы и были — трасса тогда проверяла бы метод, а не
/// симуляцию.
///
/// Строка — один JSON-объект. Ключ "//" в любом объекте игнорируется и служит
/// комментарием: JSONL не знает комментариев-строк, а сценарий без пояснений
/// нечитаем.
/// </summary>
public abstract record ScenarioOp
{
    public int Line { get; init; }
}

/// <summary>
/// Что вообще писать в трассу. ОБЯЗАН быть первой операцией.
///
/// ПОЧЕМУ это лежит во входном файле, а не в коде дампера: если списки
/// наблюдаемого разъедутся между движками, tracediff увидит структурное
/// расхождение и обвинит порт в том, чего тот не делал. Один список на оба
/// движка — единственный способ этого избежать.
/// </summary>
public sealed record ObserveOp(
    Dictionary<string, string[]> Components,
    HashSet<string> Events,
    bool Containers) : ScenarioOp;

/// <summary>
/// Спавн прототипа. Координаты — на тестовой карте сценария, в тайлах.
/// <paramref name="Id"/> — синтетический номер, который ОБЯЗАН совпасть с
/// порядковым номером спавна; сверяется при исполнении.
/// </summary>
public sealed record SpawnOp(int Id, string Proto, float X, float Y) : ScenarioOp;

/// <summary>Явная запись поля компонента.</summary>
public sealed record SetOp(int Entity, string Comp, string Field, JsonElement Value) : ScenarioOp;

/// <summary>
/// Игровое взаимодействие. Точка входа — SharedInteractionSystem, она есть в
/// обоих движках под теми же именами (activate -> InteractionActivate,
/// hand -> InteractHand, using -> InteractUsing, use-in-hand ->
/// UseInHandInteraction).
///
/// ПОЧЕМУ не сырое нажатие клавиши: раскладка и клиентское предсказание в
/// порт не переносятся (AGENTS.md), так что «нажатие» пришлось бы
/// эмулировать по-разному в двух движках — то есть сравнивать разное.
/// Граница «пользователь применил действие к цели» одинакова у обоих.
/// </summary>
public sealed record InteractOp(
    string Kind,
    int User,
    int Target,
    int? Used,
    bool Complex,
    bool CheckCanInteract,
    bool CheckAccess) : ScenarioOp;

/// <summary>
/// Телепорт тела: МИРОВЫЕ позиция и поворот разом.
///
/// ПОЧЕМУ ОТДЕЛЬНАЯ ОПЕРАЦИЯ, А НЕ <see cref="SetOp"/> ПО ПОЛЯМ ТРАНСФОРМА.
/// Мировая позиция в Robust — не поле компонента, а величина, считаемая по
/// цепочке родителей; запись в LocalPosition рефлексией не перевесила бы
/// сущность между сеткой и картой и не подняла бы событие движения. Обоим
/// движкам доступен ровно один общий вход —
/// SharedTransformSystem.SetWorldPositionRotation, — в него операция и бьёт.
///
/// Позиция и поворот задаются ВМЕСТЕ, потому что вместе их ставит и оригинал:
/// два раздельных вызова подняли бы два события движения вместо одного.
/// </summary>
public sealed record MoveOp(int Entity, float X, float Y, double Rot) : ScenarioOp;

/// <summary>Операция контейнера: положить или достать.</summary>
public sealed record ContainerOp(string Action, int Owner, string Key, int Entity) : ScenarioOp;

/// <summary>Удаление сущности.</summary>
public sealed record DeleteOp(int Entity) : ScenarioOp;

/// <summary>Продвижение времени на N тиков сервера.</summary>
public sealed record TickOp(int N) : ScenarioOp;

public sealed class Scenario
{
    public string Name { get; }
    public ObserveOp Observe { get; }
    public IReadOnlyList<ScenarioOp> Ops { get; }
    /// <summary>Сколько тиков будет в трассе. Известно до запуска — сумма всех tick.</summary>
    public int TotalTicks { get; }

    private Scenario(string name, ObserveOp observe, IReadOnlyList<ScenarioOp> ops)
    {
        Name = name;
        Observe = observe;
        Ops = ops;
        TotalTicks = ops.OfType<TickOp>().Sum(t => t.N);
    }

    public static Scenario Load(string name, string path)
    {
        var ops = new List<ScenarioOp>();
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0)
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"{path}:{i + 1}: не JSON — {e.Message}");
            }

            using (doc)
            {
                ops.Add(ParseOp(doc.RootElement, path, i + 1));
            }
        }

        if (ops.Count == 0)
            throw new InvalidDataException($"{path}: пустой сценарий");

        if (ops[0] is not ObserveOp observe)
            throw new InvalidDataException($"{path}:1: первой операцией обязана быть \"observe\"");

        for (var i = 1; i < ops.Count; i++)
        {
            if (ops[i] is ObserveOp)
                throw new InvalidDataException($"{path}:{ops[i].Line}: \"observe\" может быть только одна");
        }

        // Операции, не завершённые тиком, никогда не попадут в трассу: снимок
        // делается ПОСЛЕ прогона тика. Это ровно тот молчаливый отказ, который
        // выглядит как успех, поэтому — ошибка разбора.
        if (ops[^1] is not TickOp)
            throw new InvalidDataException($"{path}: сценарий обязан заканчиваться операцией \"tick\"");

        if (ops.OfType<TickOp>().Sum(t => t.N) == 0)
            throw new InvalidDataException($"{path}: в сценарии нет ни одного тика");

        return new Scenario(name, observe, ops);
    }

    private static ScenarioOp ParseOp(JsonElement el, string path, int line)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}:{line}: строка сценария обязана быть объектом");

        var op = Req(el, "op", path, line).GetString();

        switch (op)
        {
            case "observe":
            {
                var comps = new Dictionary<string, string[]>();
                foreach (var prop in Req(el, "components", path, line).EnumerateObject())
                {
                    comps[prop.Name] = prop.Value.EnumerateArray().Select(x => x.GetString()).ToArray();
                }

                var events = new HashSet<string>();
                if (el.TryGetProperty("events", out var evs))
                {
                    foreach (var e in evs.EnumerateArray())
                        events.Add(e.GetString());
                }

                var containers = el.TryGetProperty("containers", out var c) && c.GetBoolean();
                return new ObserveOp(comps, events, containers) { Line = line };
            }
            case "spawn":
            {
                var at = Req(el, "at", path, line);
                var coords = at.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (coords.Length != 2)
                    throw new InvalidDataException($"{path}:{line}: \"at\" — ровно два числа [x, y]");

                return new SpawnOp(
                    Req(el, "id", path, line).GetInt32(),
                    Req(el, "proto", path, line).GetString(),
                    coords[0],
                    coords[1]) { Line = line };
            }
            case "set":
                return new SetOp(
                    Req(el, "entity", path, line).GetInt32(),
                    Req(el, "comp", path, line).GetString(),
                    Req(el, "field", path, line).GetString(),
                    Req(el, "value", path, line).Clone()) { Line = line };
            case "move":
            {
                var at = Req(el, "at", path, line);
                var coords = at.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (coords.Length != 2)
                    throw new InvalidDataException($"{path}:{line}: \"at\" — ровно два числа [x, y]");

                // Поворот обязателен, а не «оставить нынешний»: умолчание
                // пришлось бы читать из мира, и два движка прочли бы его в
                // разный момент.
                return new MoveOp(
                    Req(el, "entity", path, line).GetInt32(),
                    coords[0],
                    coords[1],
                    Req(el, "rot", path, line).GetDouble()) { Line = line };
            }
            case "interact":
                return new InteractOp(
                    Req(el, "kind", path, line).GetString(),
                    Req(el, "user", path, line).GetInt32(),
                    Req(el, "target", path, line).GetInt32(),
                    el.TryGetProperty("used", out var used) ? used.GetInt32() : null,
                    !el.TryGetProperty("complex", out var cx) || cx.GetBoolean(),
                    el.TryGetProperty("checkCanInteract", out var cci) && cci.GetBoolean(),
                    el.TryGetProperty("checkAccess", out var ca) && ca.GetBoolean()) { Line = line };
            case "container":
                return new ContainerOp(
                    Req(el, "action", path, line).GetString(),
                    Req(el, "owner", path, line).GetInt32(),
                    Req(el, "key", path, line).GetString(),
                    Req(el, "entity", path, line).GetInt32()) { Line = line };
            case "delete":
                return new DeleteOp(Req(el, "entity", path, line).GetInt32()) { Line = line };
            case "tick":
            {
                var n = Req(el, "n", path, line).GetInt32();
                if (n <= 0)
                    throw new InvalidDataException($"{path}:{line}: \"n\" обязано быть положительным");
                return new TickOp(n) { Line = line };
            }
            default:
                throw new InvalidDataException($"{path}:{line}: неизвестная операция \"{op}\"");
        }
    }

    private static JsonElement Req(JsonElement el, string name, string path, int line)
    {
        if (!el.TryGetProperty(name, out var value))
            throw new InvalidDataException($"{path}:{line}: нет обязательного поля \"{name}\"");
        return value;
    }
}
