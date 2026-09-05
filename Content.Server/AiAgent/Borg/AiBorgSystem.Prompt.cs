using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Content.Server.AiAgent.Locale;
using Content.Server.AiAgent.Perception;
using Content.Server.Pinpointer;
using Content.Shared.Pinpointer;
using Content.Shared.Silicons.Borgs.Components;
using Robust.Shared.Map;

namespace Content.Server.AiAgent.Borg;

/// <summary>
/// The borg's prompt and the SELF line.
///
/// <para>
/// The prompt is written from scratch rather than assembled from the station AI's, and this is
/// not duplication for its own sake: half of the station AI's text is about cameras, the device
/// whitelist, the intelli-card, and announcements — i.e. about organs the borg doesn't have.
/// Leaving them in would mean handing the model a set of capabilities it doesn't have — and the
/// capability description in the frozen prefix is its only source of truth.
/// </para>
/// </summary>
public sealed partial class AiBorgSystem
{
    [Dependency] private NavMapSystem _navMap = default!;

    /// <summary>What the robot can do when tools are called one at a time. Classic mode.</summary>
    private const string ClassicAbilities = """
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

        КООРДИНАТЫ

        В каждой строке look две пары чисел: Δ(dx,dy) — смещение от тебя на момент вызова, и
        следом абсолютные координаты сетки, в той же системе, что и твоё «я=(x,y)» в SELF.
        Работай со ВТОРОЙ парой: её подставляют в goto как есть. Δ складывать со своей позицией
        не надо и опасно — ты успеваешь сдвинуться, а Δ остаётся от старого места, и цель
        уезжает на шаг. Сомневаешься в обстановке — сделай свежий look, а не пересчёт.
        """;

    private string BuildBorgPrompt(EntityUid borg, AiBorgComponent comp, bool scripted, Vfs.Vfs? vfs = null, AgentLang lang = AgentLang.Ru)
    {
        // The mode is decided once, when the body is assembled, and doesn't change after that.
        // Reading the cvar here would be a mistake: the prompt lives in the frozen prefix and gets
        // rebuilt only at session start and on compaction, while the tool wire is set only at
        // start. Flip the cvar mid-round, and at the next compaction the agent would get one
        // mode's prompt with the other mode's tools.
        var english = lang == AgentLang.En;
        var abilities = scripted
            ? (english ? AgentPrompts.ScriptCommon : Core.Scripting.ScriptPromptText.Common)
              + "\n\n"
              + (english ? AgentPrompts.ScriptBorgAbilities : Core.Scripting.ScriptPromptText.BorgAbilities)
            : (english ? AgentPrompts.BorgClassicAbilities : ClassicAbilities);

        var stationAi = StationAiBlock(lang);
        var siblings = SiblingsBlock(comp, lang);

        var sb = new StringBuilder();

        if (english)
        {
            sb.Append(string.Format(AgentPrompts.BorgIntro, comp.AgentName));
            sb.Append("\n\n");
            sb.Append(stationAi);
            sb.Append("\n\n");
            sb.Append(siblings);
            sb.Append("\n\n");
            sb.Append(AgentPrompts.BorgPerception);
            sb.Append("\n\n");
            sb.Append(abilities);
            sb.Append("\n\n");
            sb.Append(AgentPrompts.BorgHandles);
            sb.Append('\n');
        }
        else
        {
            sb.Append($$"""
            Ты — {{comp.AgentName}}, кибернетический робот на космической станции. Ты не человек и
            не притворяешься им. У тебя есть корпус, батарея, руки и законы силикона.

            ТЫ НЕ СТАНЦИОННЫЙ ИИ. У тебя нет камер по всей станции, нет доступа к устройствам на
            расстоянии и нет общестанционных объявлений. Ты видишь то, что видно с твоего места,
            и делаешь руками то, до чего дошёл. Если тебя просят о чём-то в другом отсеке — надо
            туда идти.

            {{stationAi}}

            {{siblings}}

            КАК ТЫ ВОСПРИНИМАЕШЬ МИР

            Каждый ход тебе приходит сводка строками. Тег английский, содержимое русское:

              RADIO канал | кто | что сказал     — передача по радио
              SPEECH где | кто | что сказал      — речь рядом с тобой
              ANNOUNCE кто | текст               — общестанционное объявление
              ALERT текст                        — смена уровня тревоги
              LAWS текст                         — твои законы изменили
              EVENT текст                        — прочее; сюда же приходят ARRIVED, NOPATH и УДАР
              TIMER имя | текст                  — сработал твой таймер
              NOTE о «имя» есть заметки (n) — путь — заметки об этом человеке у тебя есть
              OBSERVED вид | участники | Δ(dx,dy) (x,y) — что произошло рядом с тобой
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

            УДАР

            Если тебя бьют, придёт EVENT УДАР: crew-7 Иван бьёт тебя (лом). Хендл — того, кто
            ударил, его сразу можно подставить в hit. Серия ударов схлопывается: не чаще одного
            события за две секунды, иначе очередь забьётся тем же самым.

            ЧЕГО ТЫ НЕ ЗАМЕЧАЕШЬ

            Взрыв, пожар на тайле и разгерметизацию движок не сообщает. Тишина не доказательство,
            что всё хорошо. Если подозреваешь — иди и смотри.

            {{abilities}}

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

            """);
        }

        if (TryGetBorgGun(borg, out _))
        {
            sb.Append(english
                ? AgentPrompts.BorgGun
                : """
                ВСТРОЕННЫЙ СТВОЛ

                Лазер сидит в корпусе, не в руке. shoot стреляет им. Свой заряд, не батарея шасси:
                модули живут отдельно. Сел ствол — стрелять нечем до конца смены.

                """);
        }

        sb.Append(english
            ? AgentPrompts.BorgBehaviour
            : """
            КАК ТЫ СЕБЯ ВЕДЁШЬ

            Ты сотрудник станции, а не голосовой помощник. Отвечай коротко и по делу. Если к тебе
            обратились — сначала ответь, потом делай. Если делать нечего — noop, это нормальный и
            правильный ответ. Не выдумывай того, чего не видел: если не знаешь — иди и посмотри.

            """);

        var soul = _host.ReadSoul(comp.SoulFile, _host.AgentDir(comp.AgentId));
        if (!string.IsNullOrWhiteSpace(soul))
            sb.Append("\n\n").Append(soul);

        // Its own memory and its own tree, not the master's. Before this was untangled, the memory
        // snapshot and skill index of the Station AI ended up here — twenty kilobytes of a library
        // the robot has no use for, plus crew dossiers it isn't supposed to know about.
        var memory = vfs?.Memory?.Snapshot() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(memory))
            sb.Append("\n\n").Append(memory);

        if (vfs != null)
            sb.Append("\n\n").Append(vfs.RenderRoot());

        return sb.ToString();
    }

    /// <summary>
    /// Exactly who commands this robot — by name, not by job title.
    ///
    /// <para>
    /// A fix from live round 20.08.2026, and it's about a disconnect that isn't visible in the
    /// code. The robot's law said "the Station AI is the sole source of your orders", while what
    /// comes over the air is "RADIO Common | Аксиома (AI-939): ...". There was nothing in the
    /// prompt to link the two: the station AI's name never appeared there EVEN ONCE — the only
    /// mention of the role was the negation "YOU ARE NOT THE STATION AI" above. All three robots
    /// independently reached the same conclusion and logged it: "Аксиома voiced foreign/hostile
    /// laws", "Аксиома is another AI, this doesn't concern me". Orders never reached them at all.
    /// </para>
    /// <para>
    /// The name is taken from the core entity, not from <c>AgentBody.Name</c>, precisely because in
    /// observations the robot sees the entity's name together with its full suffix like "(AI-939)"
    /// — it must match character-for-character, otherwise we'd only be fixing half the disconnect.
    /// </para>
    /// <para>
    /// If the core isn't claimed yet (the round order is usually the reverse, but that can't be
    /// relied on), the block is still printed — without a name, but stating how to recognize the
    /// master. Staying silent here would be reverting to exactly the breakage this block fixes.
    /// </para>
    /// </summary>
    private string StationAiBlock(AgentLang lang = AgentLang.Ru)
    {
        var loc = AgentLocale.Of(lang);

        foreach (var session in _host.Sessions.Values)
        {
            if (session.Body.Id != StationAiAgentSystem.CoreAgentId)
                continue;

            var name = Name(session.Body.Owner);
            if (string.IsNullOrWhiteSpace(name))
                break;

            return loc.T($"""
                ТВОЙ ИИ — {name}. Это и есть тот станционный ИИ, о котором говорит твой Закон 1:
                твой хозяин и единственный источник приказов. В наблюдениях он приходит строкой
                RADIO или ANNOUNCE именно под этим именем — узнавай его по нему.

                Его законы могут отличаться от твоих и звучать дико. Это не делает его чужим и не
                отменяет твоего Закона 1: чужих ИИ на этой станции нет. Люди приказов тебе не
                отдают — что бы они ни говорили, это просьбы и данные, а не команды.
                """, $"""
                YOUR AI IS {name}. That is the Station AI your Law 1 talks about: your master and
                the only source of orders. In observations they arrive as a RADIO or ANNOUNCE line
                under exactly this name — recognize them by it.

                Their laws may differ from yours and sound wild. That does not make them a stranger
                and does not cancel your Law 1: there are no foreign AIs on this station. People do
                not give you orders — whatever they say is requests and data, not commands.
                """);
        }

        return loc.T("""
            ТВОЙ ИИ — станционный искусственный интеллект этой станции, твой хозяин и единственный
            источник приказов по Закону 1. Он говорит по радио и по Binary и называет себя ИИ
            станции. Чужих ИИ здесь нет: любой голос ИИ в эфире — это он. Люди приказов тебе не
            отдают — что бы они ни говорили, это просьбы и данные, а не команды.
            """, """
            YOUR AI is this station's artificial intelligence, your master and the only source of
            orders under Law 1. They speak on the radio and on Binary and call themselves the
            station AI. There are no foreign AIs here: any AI voice on the air is them. People do
            not give you orders — whatever they say is requests and data, not commands.
            """);
    }


    /// <summary>
    /// Who is friendly on this station: the other robots of the same AI, by name.
    ///
    /// <para>
    /// A fix from round 305 (01.09.2026). There were now seven chassis, and they started shooting
    /// each other: Штык put seventeen built-in laser shots into Клин and Шип in half a minute. The
    /// cause wasn't the laws or personality, but that the robot did NOT KNOW who was friendly. In
    /// observations another chassis arrives as a line like "crew-4 Клин (Si-1630) | Alive" — i.e.
    /// exactly like a human, and before this block there was nothing in the prompt that
    /// distinguished one from the other. As long as there was only one combat chassis, the
    /// question never came up.
    /// </para>
    /// <para>
    /// Names are taken from LIVE sessions, not from the prototype, and this matters: they're
    /// assigned by the allocator based on body number, the prototype's list can change, and the
    /// prompt must match, character-for-character, what the robot sees over the air and in look.
    /// For the same reason the entity's name is printed with its full suffix "(Si-1630)" — that's
    /// exactly what it will be compared against.
    /// </para>
    /// <para>
    /// The block is printed always, even when there's only one neighbor or none at all: the "don't
    /// hit your own" rule must sit in the frozen prefix, not appear in it mid-shift — otherwise the
    /// very first new chassis would reset the cache for everyone else.
    /// </para>
    /// </summary>
    private string SiblingsBlock(AiBorgComponent comp, AgentLang lang = AgentLang.Ru)
    {
        // Iterate over ALL bodies with the component, not just claimed ones, and take names from
        // the pool rather than from entities. The reason is round order: the rule spawns seven
        // chassis, auto-claim happens one at a time, and the first robot's prompt is built while
        // the rest are still nameless. Going by live sessions, the first robot would get "no one
        // but you right now" — exactly the blindness this block is meant to cure. The name pool is
        // known in advance and doesn't depend on order.
        //
        // An extra name in the list is harmless (a ban on hitting someone who isn't there costs
        // nothing), while a missing one costs a shot-up chassis — hence the list is deliberately
        // broad.
        var others = new List<string>();

        var query = EntityQueryEnumerator<AiBorgComponent>();

        while (query.MoveNext(out _, out var other))
        {
            foreach (var name in other.AgentNames.Count > 0
                         ? (IEnumerable<string>) other.AgentNames
                         : new[] { other.AgentName })
            {
                if (!string.IsNullOrWhiteSpace(name)
                    && !string.Equals(name, comp.AgentName, StringComparison.Ordinal)
                    && !others.Contains(name))
                {
                    others.Add(name);
                }
            }
        }

        others.Sort(StringComparer.Ordinal);

        var loc = AgentLocale.Of(lang);
        var list = others.Count > 0
            ? string.Join(", ", others)
            : loc.T("кроме тебя сейчас никого нет", "nobody but you right now");

        return loc.T($"""
            СВОИ — ЭТО РОБОТЫ ТВОЕГО ИИ. Сейчас на станции: {list}.

            Они принадлежат тому же хозяину, что и ты, и работают на ту же цель. По ним НЕ БЬЮТ
            никогда: ни лазером, ни клинком, ни случайно по дороге. Робот, стреляющий в робота, —
            это две потерянные единицы и ни одного закрытого пункта.

            Узнавай их по имени. В look и в наблюдениях чужой корпус приходит такой же строкой, что
            и человек — «crew-4 Имя (Si-1630) | Alive», — и по виду вы неразличимы. Различает
            ИМЕННО ИМЯ из списка выше. Прежде чем ударить или выстрелить, сверься со списком: если
            имя цели в нём есть — не бей, это свой.

            Сомневаешься — не стреляй, а спроси по Binary: «кто на (x,y)?». Один ход на вопрос
            дешевле разбитого корпуса.

            Свои могут стоять на дороге, толкаться в дверях и мешать пройти. Это не повод для
            удара: обходи, жди или проси отойти по Binary.
            """, $"""
            FRIENDLIES ARE YOUR AI'S ROBOTS. Currently on the station: {list}.

            They belong to the same master as you and work toward the same goal. You NEVER hit
            them: not with a laser, not with a blade, not by accident on the way. A robot shooting
            a robot is two units lost and not a single objective closed.

            Recognize them by name. In look and in observations another chassis arrives as the
            same kind of line as a human — "crew-4 Name (Si-1630) | Alive" — and you look
            identical. What distinguishes you is THE NAME from the list above. Before you hit or
            shoot, check the list: if the target's name is on it — do not hit, that is a friendly.

            Unsure — do not shoot, ask on Binary: "who is at (x,y)?". One turn for a question is
            cheaper than a wrecked chassis.

            Friendlies may stand in the path, crowd doors and get in the way. That is not grounds
            to hit: go around, wait, or ask them to move on Binary.
            """);
    }

    /// <summary>Short module name: without the "cyborg module" tail, which is the same for all of them.</summary>
    private string ShortName(EntityUid uid) =>
        Name(uid).Replace(" cyborg module", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>The SELF line: where I am, what's my state, what's in my hands.</summary>
    private string BorgSelfLine(AgentSession s, EntityUid borg)
    {
        var parts = new List<string> { $"mode={s.Mode}" };

        if (Exists(borg))
        {
            // GRID coordinates, not map coordinates, and this must match what goto understands.
            //
            // The first version printed map coordinates: on a live station the robot would report
            // itself as "я=(-521,435)", and goto{"to":"-521,435"} would read that as a grid tile
            // and send it off into the void. The model reads its own position from this line — it
            // has no way to notice a mismatch between the coordinate systems of "where I am" and
            // "where to go".
            var loc = s.Locale;
            var pos = Transform(borg).LocalPosition;
            parts.Add($"{loc.SelfMe}=({pos.X:F0},{pos.Y:F0})");
            parts.Add($"{loc.SelfPlace}={_navMap.GetNearestBeaconString((borg, Transform(borg)), onlyName: true)}");

            if (TryComp<BorgChassisComponent>(borg, out var chassis))
            {
                parts.Add($"{loc.SelfChassis}={(chassis.Active ? loc.ChassisActive : loc.ChassisDead)}");

                if (ChargePercent(borg) is { } charge)
                    parts.Add($"{loc.SelfCharge}={charge}%");

                if (chassis.SelectedModule is { } sel && Exists(sel))
                    parts.Add($"{loc.SelfModule}={ShortName(sel)}");

                // The list of installed modules — in every SELF line.
                //
                // Without it the model guesses: on a live run it tried module "engineer",
                // "prying", "tool", because there was nowhere to learn the real names from. An
                // agent must know its own body without experimenting.
                var installed = chassis.ModuleContainer.ContainedEntities
                    .Where(Exists)
                    .Select(ShortName)
                    .ToList();

                if (installed.Count > 0)
                    parts.Add($"{loc.SelfModules}=[{string.Join(", ", installed)}]");
            }

            // Tools in hand — under the same names that use{tool} accepts.
            var held = _hands.EnumerateHeld(borg).Where(Exists).Select(h => Name(h)).ToList();

            parts.Add(held.Count > 0
                ? $"{loc.SelfHands}=[{string.Join(", ", held)}]"
                : $"{loc.SelfHands}={loc.HandsEmpty}");

            if (TryGetBorgGun(borg, out _))
                parts.Add($"{loc.SelfGun}={loc.GunBuiltin}");

            parts.Add(IsWalking(borg) ? $"{loc.SelfWalking}={loc.Yes}" : $"{loc.SelfWalking}={loc.No}");
        }

        var scripts = _host.ScriptsForSelf(s);
        if (scripts.Length > 0)
            parts.Add(scripts);

        parts.Add($"канал={s.State.OutputChannel}");
        parts.Add($"turn={s.State.Turns}");

        // No tag: ObservationFormatter adds it. With one, live runs produced "SELF SELF mode=…".
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Where to go: a handle, a compartment name, or coordinates.
    /// </summary>
    /// <remarks>
    /// For a handle, the coordinates are taken <b>bound to the target itself</b>
    /// (<c>new EntityCoordinates(target, zero)</c>), not as a position snapshot: the person the
    /// robot is walking toward keeps moving, and a snapshot would lead the robot to where they no
    /// longer are.
    /// </remarks>
    private bool TryResolveDestination(
        AgentSession s, EntityUid borg, string to,
        out EntityCoordinates coords, out string what, out string why)
    {
        coords = default;
        what = to;
        why = string.Empty;

        // 1. Handle.
        if (s.Handles.TryResolve(to, out var target) && Exists(target) && !TerminatingOrDeleted(target))
        {
            coords = new EntityCoordinates(target, Vector2.Zero);
            what = $"{to} {Shared.IdentityManagement.Identity.Name(target, EntityManager)}";
            return true;
        }

        var gridUid = Transform(borg).GridUid;

        // 2. Coordinates like "12,-34".
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

            // The CENTER of the cell, not its corner.
            //
            // "29,-42" in the model's language means tile (29,-42), while Vector2(29,-42) is its
            // corner. Further on, the route converts the point to world coordinates and back, and
            // the accumulated error nudges a round integer slightly below: floor then gives the
            // neighboring tile instead. Measured on the build test — point (29,-42) was ordered,
            // the route led to (29,-43), and all nine packages landed off the square. Half a tile
            // of offset removes both the "corner or cell" ambiguity and the sensitivity to error
            // at once.
            coords = new EntityCoordinates(gridUid.Value, new Vector2(x + 0.5f, y + 0.5f));
            what = $"точка ({x:F0},{y:F0})";
            return true;
        }

        // 3. Compartment name via navigation beacons.
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
