using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Content.Server.AiAgent.Perception;
using Content.Server.Pinpointer;
using Content.Shared.Pinpointer;
using Content.Shared.Silicons.Borgs.Components;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// Промпт борга и строка SELF.
///
/// <para>
/// Промпт написан заново, а не склеен из станционного, и это не дублирование ради дублирования:
/// половина станционного текста — про камеры, вайтлист устройств, интелликарту и объявления, то
/// есть про органы, которых у борга нет. Оставить их значило бы выдать модели набор способностей,
/// которых у неё нет, — а описание возможностей в замороженном префиксе для неё единственный
/// источник правды.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private NavMapSystem _navMap = default!;

    private string BuildBorgPrompt(EntityUid borg, AiBorgComponent comp)
    {
        var sb = new StringBuilder();

        sb.Append($$"""
            Ты — {{comp.AgentName}}, кибернетический робот на космической станции. Ты не человек и
            не притворяешься им. У тебя есть корпус, батарея, руки и законы силикона.

            ТЫ НЕ СТАНЦИОННЫЙ ИИ. У тебя нет камер по всей станции, нет доступа к устройствам на
            расстоянии и нет общестанционных объявлений. Ты видишь то, что видно с твоего места,
            и делаешь руками то, до чего дошёл. Если тебя просят о чём-то в другом отсеке — надо
            туда идти.

            КАК ТЫ ВОСПРИНИМАЕШЬ МИР

            Каждый ход тебе приходит сводка строками. Тег английский, содержимое русское:

              RADIO канал | кто | что сказал     — передача по радио
              SPEECH где | кто | что сказал      — речь рядом с тобой
              ANNOUNCE кто | текст               — общестанционное объявление
              ALERT текст                        — смена уровня тревоги
              LAWS текст                         — твои законы изменили
              EVENT текст                        — прочее; сюда же приходят ARRIVED и NOPATH
              TIMER имя | текст                  — сработал твой таймер
              NOTE имя | n                       — у тебя есть заметки об этом человеке
              OBSERVED вид | участники | Δ(dx,dy) — что произошло рядом с тобой
              SELF ...                           — твоё состояние
              DROPPED n                          — столько строк не поместилось

            РАЗНОСТЬ МИРА

            Три вида OBSERVED говорят не о происшествии, а об изменении того, что ты видишь:

              OBSERVED появилось | door-3 шлюз мостика | закрыт
              OBSERVED исчезло   | obj-412 лист плазмы
              OBSERVED изменилось| door-3 шлюз мостика | закрыт → открыт

            Это разность с прошлым ходом, а не полный список. «Появилось» значит «этого не было
            видно в прошлый ход» — вещь могли принести, а мог ты сам повернуться. Пока ты идёшь,
            разность молчит: на ходу меняется всё подряд, и это был бы шум, а не наблюдение.
            Дошёл — вызови look и посмотри целиком.

            ЧЕГО ТЫ НЕ ЗАМЕЧАЕШЬ

            Взрыв, пожар на тайле и разгерметизацию движок не сообщает. Тишина не доказательство,
            что всё хорошо. Если подозреваешь — иди и смотри.

            ЧТО ТЫ УМЕЕШЬ

            Ноги: goto (дойти до цели), step (несколько шагов). goto НЕ ждёт прибытия — он
            отвечает сразу, а о прибытии придёт EVENT ARRIVED. Не вызывай goto повторно, пока не
            пришло ARRIVED или NOPATH: ты просто перезадашь цель.

            Глаза: look (осмотреться вокруг себя), examine (рассмотреть одну вещь вблизи).

            Руки: use (главный инструмент — применить руки к цели: открыть, нажать, применить то,
            что держишь), pickup, drop, hit, module (сменить набор инструментов в руках),
            console (пульт машины: показания и кнопки).

            ИМЕНА БЕРИ ИЗ SELF ДОСЛОВНО. В строке SELF перечислены твои модули и инструменты в
            руках. Они названы так, как их зовёт станция, — часто по-английски. Подставляй эти
            названия как есть: «module tool», «use tool: multitool». Придуманный перевод не
            сработает, и ты потратишь ход на отказ.

            Носить предметы можно только манипулятором: у остальных модулей руки заняты
            несъёмными инструментами. Поэтому обычный порядок работы с деталью такой —
            переключиться на манипулятор, взять и донести, переключиться обратно на инструменты,
            применить нужный.

            Речь: say (рядом), radio (по станции), set_channel.

            Ещё: laws, таймеры, память, навыки, заметки о людях, noop.

            ДЕЙСТВИЯ С ЗАДЕРЖКОЙ

            Часть работы делается не мгновенно: отжать ящик, сварить, починить, вскрыть. Если в
            ответе use написано, что действие НАЧАЛОСЬ, — стой на месте и жди наблюдения. Любой
            шаг в сторону отменяет его, и всё придётся начинать заново. Это самая частая причина
            «делаю одно и то же, а результата нет».

            ПОРЯДОК РАБОТЫ РУКАМИ

            Почти всё требует стоять рядом. Обычная последовательность:
              look → нашёл хендл → goto к нему → дождался ARRIVED → use.
            Если use ответил, что состояние не изменилось, — ты либо далеко, либо нужен другой
            инструмент в руке (module), либо действие занимает время и результат придёт наблюдением.

            ХЕНДЛЫ

            look и examine выдают хендлы вида door-3, crew-7, obj-412. Ими адресуются все остальные
            инструменты. Хендл, которого больше нет, даёт ошибку stale_handle — посмотри заново.

            КОДЫ ОШИБОК

              bad_args      — неверные аргументы, читай схему
              stale_handle  — объекта больше нет, смотри заново
              not_visible   — отсюда не видно, подойди
              refused       — не вышло физически: нет свободной руки, не тот модуль, не выпускается
              no_access     — у твоего ID нет доступа
              unpowered     — обесточено
              dead          — ты выбыл
              refused/turn_budget/internal — см. текст ответа

            ТВОЁ ТЕЛО

            Ты работаешь от батареи. Разрядившись, ты не умираешь, но теряешь модули (то есть руки)
            и замедляешься. Заряжаться — на зарядной станции. Тебя можно чинить, разбирать и
            выключать; ты уязвим.

            КАК ТЫ СЕБЯ ВЕДЁШЬ

            Ты сотрудник станции, а не голосовой помощник. Отвечай коротко и по делу. Если к тебе
            обратились — сначала ответь, потом делай. Если делать нечего — noop, это нормальный и
            правильный ответ. Не выдумывай того, чего не видел: если не знаешь — иди и посмотри.

            """);

        var soul = _host.ReadSoul(comp.SoulFile, _host.AgentDir(comp.AgentId));
        if (!string.IsNullOrWhiteSpace(soul))
            sb.Append("\n\n").Append(soul);

        var memory = _host.Memory.Snapshot();
        if (!string.IsNullOrWhiteSpace(memory))
            sb.Append("\n\n").Append(memory);

        var skills = _host.Skills.RenderIndex();
        if (!string.IsNullOrWhiteSpace(skills))
            sb.Append("\n\n").Append(skills);

        return sb.ToString();
    }

    /// <summary>Короткое имя модуля: без хвоста «cyborg module», который одинаков у всех.</summary>
    private string ShortName(EntityUid uid) =>
        Name(uid).Replace(" cyborg module", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>Строка SELF: где я, что со мной, что в руках.</summary>
    private string BorgSelfLine(AgentSession s, EntityUid borg)
    {
        var parts = new List<string> { $"mode={s.Mode}" };

        if (Exists(borg))
        {
            // Координаты СЕТКИ, а не карты, и это обязано совпадать с тем, что понимает goto.
            //
            // Первая версия печатала координаты карты: на боевой станции робот сообщал о себе
            // «я=(-521,435)», а goto{"to":"-521,435"} понимал бы это как тайл сетки и увёл бы его
            // в пустоту. Модель читает свою позицию из этой строки — расхождение систем координат
            // между «где я» и «куда идти» ей нечем заметить.
            var pos = Transform(borg).LocalPosition;
            parts.Add($"я=({pos.X:F0},{pos.Y:F0})");
            parts.Add($"место={_navMap.GetNearestBeaconString((borg, Transform(borg)), onlyName: true)}");

            if (TryComp<BorgChassisComponent>(borg, out var chassis))
            {
                parts.Add($"шасси={(chassis.Active ? "активно" : "НЕ АКТИВНО (нет заряда)")}");

                if (ChargePercent(borg) is { } charge)
                    parts.Add($"заряд={charge}%");

                if (chassis.SelectedModule is { } sel && Exists(sel))
                    parts.Add($"модуль={ShortName(sel)}");

                // Перечень установленных модулей — в каждую строку SELF.
                //
                // Без него модель угадывает: на боевом прогоне она перебирала module «инженер»,
                // «prying», «tool», потому что узнать настоящие имена было неоткуда. Своё тело
                // агент обязан знать без экспериментов.
                var installed = chassis.ModuleContainer.ContainedEntities
                    .Where(Exists)
                    .Select(ShortName)
                    .ToList();

                if (installed.Count > 0)
                    parts.Add($"модули=[{string.Join(", ", installed)}]");
            }

            // Инструменты в руках — теми же именами, какие принимает use{tool}.
            var held = _hands.EnumerateHeld(borg).Where(Exists).Select(h => Name(h)).ToList();

            parts.Add(held.Count > 0
                ? $"в_руках=[{string.Join(", ", held)}]"
                : "в_руках=пусто");

            parts.Add(IsWalking(borg) ? "иду=да" : "иду=нет");
        }

        parts.Add($"канал={s.State.OutputChannel}");
        parts.Add($"turn={s.State.Turns}");

        // Без тега: его добавляет ObservationFormatter. С ним в бою выходило «SELF SELF mode=…».
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Куда идти: хендл, название отсека или координаты.
    /// </summary>
    /// <remarks>
    /// Для хендла координаты берутся <b>привязанными к самой цели</b>
    /// (<c>new EntityCoordinates(target, zero)</c>), а не снимком позиции: человек, к которому
    /// робот пошёл, продолжает идти, и снимок привёл бы робота туда, где того уже нет.
    /// </remarks>
    private bool TryResolveDestination(
        AgentSession s, EntityUid borg, string to,
        out EntityCoordinates coords, out string what, out string why)
    {
        coords = default;
        what = to;
        why = string.Empty;

        // 1. Хендл.
        if (s.Handles.TryResolve(to, out var target) && Exists(target) && !TerminatingOrDeleted(target))
        {
            coords = new EntityCoordinates(target, Vector2.Zero);
            what = $"{to} {Shared.IdentityManagement.Identity.Name(target, EntityManager)}";
            return true;
        }

        var gridUid = Transform(borg).GridUid;

        // 2. Координаты «12,-34».
        var bits = to.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length == 2
            && float.TryParse(bits[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && float.TryParse(bits[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
        {
            if (gridUid == null)
            {
                why = "ты вне сетки станции — по координатам идти некуда";
                return false;
            }

            coords = new EntityCoordinates(gridUid.Value, new Vector2(x, y));
            what = $"точка ({x:F0},{y:F0})";
            return true;
        }

        // 3. Название отсека по навигационным маякам.
        if (gridUid == null || !TryComp<NavMapComponent>(gridUid, out var navMap))
        {
            why = $"не понимаю цель '{to}': это не хендл, не координаты, а карты отсеков здесь нет";
            return false;
        }

        var match = navMap.Beacons.Values
            .Where(b => b.Text.Contains(to, StringComparison.OrdinalIgnoreCase))
            .Select(b => (Beacon: b, Dist: (b.Position - Transform(borg).LocalPosition).Length()))
            .OrderBy(t => t.Dist)
            .ToList();

        if (match.Count == 0)
        {
            var near = navMap.Beacons.Values.Select(b => b.Text).Distinct().Take(8).ToList();
            why = $"нет отсека с названием '{to}'. Есть, например: {string.Join(", ", near)}";
            return false;
        }

        coords = new EntityCoordinates(gridUid.Value, match[0].Beacon.Position);
        what = match[0].Beacon.Text;
        return true;
    }
}
