using Robust.Shared.Maths;

namespace Content.Server.AiAgent.Locale;

/// <summary>
/// Strings the model sees: JSON keys, SELF fields, observation labels, directions.
///
/// Two instances, not a live lookup, because the prefix is frozen. Call sites take the locale
/// off the session that was assembled with it.
/// </summary>
public sealed class AgentLocale
{
    public static readonly AgentLocale Ru = new(AgentLang.Ru);
    public static readonly AgentLocale En = new(AgentLang.En);

    public static AgentLocale Of(AgentLang lang) => lang == AgentLang.En ? En : Ru;

    public AgentLang Lang { get; }
    public bool English => Lang == AgentLang.En;

    private AgentLocale(AgentLang lang) => Lang = lang;

    public string T(string ru, string en) => English ? en : ru;

    // -------------------------------------------------------------- operator

    public string OperatorPrefix => English
        ? "[OUT-OF-GAME SERVER OPERATOR MESSAGE]"
        : "[ВНЕИГРОВОЕ СООБЩЕНИЕ ОПЕРАТОРА СЕРВЕРА]";

    // -------------------------------------------------------------- SELF

    public string SelfPlace => T("место", "place");
    public string SelfAlert => T("тревога", "alert");
    public string SelfTimers => T("таймеры", "timers");
    public string SelfScripts => T("скрипты", "scripts");
    public string SelfMe => T("я", "me");
    public string SelfChassis => T("шасси", "chassis");
    public string SelfCharge => T("заряд", "charge");
    public string SelfModule => T("модуль", "module");
    public string SelfModules => T("модули", "modules");
    public string SelfHands => T("в_руках", "hands");
    public string SelfGun => T("ствол", "gun");
    public string SelfWalking => T("иду", "walking");
    public string SelfSpeechWhere => T("ядро", "core");

    public string ChassisActive => T("активно", "active");
    public string ChassisDead => T("НЕ АКТИВНО (нет заряда)", "NOT ACTIVE (no charge)");
    public string HandsEmpty => T("пусто", "empty");
    public string GunBuiltin => T("встроенный лазер", "built-in laser");
    public string Yes => T("да", "yes");
    public string No => T("нет", "no");
    public string UnknownPlace => T("неизвестно", "unknown");

    // -------------------------------------------------------------- JSON keys

    public string Objects => T("объекты", "objects");
    public string Outcome => T("итог", "outcome");
    public string Status => T("статус", "status");
    public string Answer => T("ответ", "answer");
    public string Why => T("почему", "why");
    public string Changed => T("изменилось", "changed");
    public string Visible => T("видно", "visible");
    public string Truncated => T("обрезано", "truncated");
    public string HowToSeeRest => T("как_увидеть_остальное", "how_to_see_rest");
    public string Stale => T("устарело", "stale");
    public string Charge => T("заряд", "charge");
    public string Pressure => T("давление", "pressure");
    public string TurnEnded => T("ход_окончен", "turn_ended");
    public string ChannelWas => T("канал_был", "channel_was");
    public string ChannelBecame => T("канал_стал", "channel_became");
    public string HowToKnow => T("как_узнать", "how_to_know");
    public string Seconds => T("секунд", "seconds");
    public string Calls => T("вызовов", "calls");
    public string Output => T("вывод", "output");
    public string Function => T("функция", "function");
    public string Description => T("описание", "description");
    public string Arguments => T("аргументы", "arguments");
    public string Functions => T("функции", "functions");
    public string NewLines => T("новое", "new");
    public string Already => T("уже", "already");
    public string DoneKey => T("сделанное", "done");
    public string Stations => T("станций", "stations");
    public string Chargers => T("зарядки", "chargers");
    public string HowToReach => T("как_дойти", "how_to_reach");
    public string Stopped => T("остановился", "stopped");
    public string WalkingTo => T("иду_к", "walking_to");
    public string Stepping => T("шагаю", "stepping");
    public string This => T("это", "this");
    public string Was => T("было", "was");
    public string Became => T("стало", "became");
    public string PickedUp => T("взял", "picked_up");
    public string Dropped => T("положил", "dropped");
    public string Module => T("модуль", "module");
    public string Console => T("пульт", "console");
    public string Order => T("порядок", "order");
    public string Exit => T("выход", "exit");
    public string ConsoleApproach => T("подход_к_пульту", "console_approach");
    public string HowToPlace => T("как_класть", "how_to_place");
    public string Hit => T("ударил", "hit");
    public string With => T("чем", "with");
    public string Shot => T("выстрелил", "shot");
    public string Range => T("дистанция", "range");
    public string Timer => T("таймер", "timer");
    public string FiresAt => T("сработает", "fires_at");
    public string InSeconds => T("через_секунд", "in_seconds");
    public string Repeat => T("повтор", "repeat");
    public string TimerCount => T("всего_таймеров", "timer_count");
    public string Replaced => T("замена", "replaced");
    public string DurationClamped => T("срок_поправлен", "duration_clamped");
    public string Removed => T("снят", "removed");
    public string WasText => T("текст_был", "was_text");
    public string Name => T("имя", "name");
    public string Text => T("текст", "text");
    public string RepeatSeconds => T("повтор_секунд", "repeat_seconds");
    public string Timers => T("таймеры", "timers");
    public string Now => T("сейчас", "now");
    public string Controlling => T("управляю", "controllable");
    public string WireCut => T("провод перерезан", "wire cut");

    // -------------------------------------------------------------- outcome values

    public string OutcomeArrived => T("дошёл", "arrived");
    public string OutcomeOk => T("получилось", "done");
    public string OutcomeFailed => T("НЕ ПОЛУЧИЛОСЬ", "FAILED");
    public string OutcomeInterrupted => T("ПРЕРВАНО", "INTERRUPTED");
    public string OutcomeStarted => T(
        "действие НАЧАЛОСЬ и занимает время. СТОЙ НА МЕСТЕ и жди наблюдения — шаг в сторону отменит его",
        "the action STARTED and takes time. STAND STILL and wait for an observation — stepping away cancels it");

    public string ScriptRunning => T("идёт", "running");
    public string ScriptDone => T("готово", "done");
    public string ScriptFailed => T("ошибка", "error");
    public string ScriptStopped => T("снят", "stopped");
    public string ScriptStopping => T("снимаю", "stopping");
    public string ScriptFinishedOnItsOwn => T("закончился сам", "already finished");
    public string ScriptDoneStays => T("не отменяется", "is not undone");

    // -------------------------------------------------------------- directions

    public string DirEnumJson => English
        ? """["north","south","west","east"]"""
        : """["север","юг","запад","восток"]""";

    public string Adjacent => T("вплотную", "adjacent");

    public string Dir(Direction dir) => dir switch
    {
        Direction.North => T("север", "north"),
        Direction.NorthEast => T("северо-восток", "northeast"),
        Direction.East => T("восток", "east"),
        Direction.SouthEast => T("юго-восток", "southeast"),
        Direction.South => T("юг", "south"),
        Direction.SouthWest => T("юго-запад", "southwest"),
        Direction.West => T("запад", "west"),
        Direction.NorthWest => T("северо-запад", "northwest"),
        _ => T("рядом", "nearby"),
    };

    // -------------------------------------------------------------- observation labels

    public string ObsHand => T("рукой", "hand");
    public string ObsUsing => T("предметом", "item");
    public string ObsRanged => T("издали", "ranged");
    public string ObsActivate => T("включил", "activated");
    public string ObsInserted => T("вложил", "inserted");
    public string ObsRemoved => T("вынул", "removed");
    public string ObsPullStart => T("тащит", "pulling");
    public string ObsPullStop => T("отпустил", "released");
    public string ObsEquipped => T("надел", "equipped");
    public string ObsUnequipped => T("снял", "unequipped");
    public string ObsState => T("состояние", "state");
    public string ObsDamage => T("урон", "damage");
    public string ObsShot => T("выстрел", "shot");
    public string ObsDoor => T("дверь", "door");
    public string ObsAppeared => T("появилось", "appeared");
    public string ObsGone => T("исчезло", "gone");
    public string ObsChanged => T("изменилось", "changed");
    public string ObsHit => T("УДАР", "HIT");

    public string MobAlive => T("жив", "alive");
    public string MobCrit => T("крит", "crit");
    public string MobDead => T("мёртв", "dead");

    public string DoorOpened => T("открылась", "opened");
    public string DoorClosed => T("закрылась", "closed");
    public string DoorDenied => T("отказ", "denied");
    public string DoorEmag => T("взлом", "emagged");
    public string DoorWelded => T("заварена", "welded");

    public string MobState(Content.Shared.Mobs.MobState state) => state switch
    {
        Content.Shared.Mobs.MobState.Alive => MobAlive,
        Content.Shared.Mobs.MobState.Critical => MobCrit,
        Content.Shared.Mobs.MobState.Dead => MobDead,
        _ => "?",
    };

    /// <summary>
    /// Both languages of a witness label, so <c>ai.observe_kinds</c> keeps working after a
    /// language switch without renaming every word.
    /// </summary>
    public static string KindAlias(string head) => head.ToLowerInvariant() switch
    {
        "урон" or "damage" => "урон",
        "выстрел" or "shot" => "выстрел",
        "рукой" or "hand" => "рукой",
        "предметом" or "item" => "предметом",
        "издали" or "ranged" => "издали",
        "включил" or "activated" => "включил",
        "вложил" or "inserted" => "вложил",
        "вынул" or "removed" => "вынул",
        "тащит" or "pulling" => "тащит",
        "отпустил" or "released" => "отпустил",
        "надел" or "equipped" => "надел",
        "снял" or "unequipped" => "снял",
        "состояние" or "state" => "состояние",
        "дверь" or "door" => "дверь",
        "появилось" or "appeared" => "появилось",
        "исчезло" or "gone" => "исчезло",
        "изменилось" or "changed" => "изменилось",
        _ => head,
    };

    // -------------------------------------------------------------- observation chrome

    public string NewEventsHeader => T("пришло, пока ты работал:", "arrived while you were working:");

    public string FormatNote(string name, string count, string slug) => English
        ? $"NOTE notes on \"{name}\" exist ({count}) — /players/{slug}"
        : $"NOTE о «{name}» есть заметки ({count}) — /players/{slug}";

    public string RoundWord => T("раунд", "round");

    // -------------------------------------------------------------- VFS zone 0

    public string VfsHeading => English
        ? """
          FILESYSTEM
          Everything you know lives in files. They are not in this message — walk them yourself.
            sh {"cmd":"ls /wiki_en"}                      — what's in a section
            sh {"cmd":"grep pump /wiki_en"}               — search by words
            sh {"cmd":"cat /wiki_en/engineering"}         — read a page
            write_file / edit_file                        — write your own
          """
        : """
          ФАЙЛОВАЯ СИСТЕМА
          Всё, что ты знаешь, лежит файлами. В этом сообщении их нет — ходи сам.
            sh {"cmd":"ls /wiki_ru"}                      — что есть в разделе
            sh {"cmd":"grep насос /wiki_ru"}              — искать по словам
            sh {"cmd":"cat /wiki_ru/атмосфера/насосы"}    — прочитать целиком
            write_file / edit_file                        — записать своё
          """;

    public string WikiRuDesc => T(
        "справочник по игре: отделы, машины, процедуры",
        "Russian game handbook: departments, machines, procedures");

    public string WikiEnDesc => T(
        "вика игры по-английски: точные имена машин на экранах экипажа",
        "in-game guidebook: exact machine names as the crew sees them on screens");

    public string SkillsDesc => T("что ты понял сам", "what you figured out yourself");
    public string PlayersDesc => T("твои заметки о людях, по файлу на человека", "your notes on people, one file each");
    public string MemoryDesc => T(
        "факты о станции и мире — они же в блоке ПАМЯТЬ выше",
        "facts about the station and the world — same text as the MEMORY block above");
    public string CuratorDesc => T(
        "чем ты руководствуешься на разборе отрезка",
        "what you follow when reviewing a stretch of play");

    public string ScriptPrelude => English ? ScriptPreludeEn : ScriptPreludeRu;

    private const string ScriptPreludeRu = """
        -- find(текст [, вид]) -> список хендлов
        --
        -- Обёртка над look: осмотреться и оставить только то, в чьей строке встречается подстрока.
        -- Сравнение точное, без приведения регистра: в Lua string.lower работает побайтово и кириллицу
        -- не трогает, так что мнимая нечувствительность к регистру обманывала бы в самом частом случае.
        function find(what, kind)
            local r = look(kind and { kind = kind } or {})
            local rows = r.effect and r.effect['объекты'] or {}
            local out = {}

            for _, row in ipairs(rows) do
                if string.find(row, what, 1, true) then
                    out[#out + 1] = string.match(row, '^([^ |]+)')
                end
            end

            return out
        end
        """;

    private const string ScriptPreludeEn = """
        -- find(text [, kind]) -> list of handles
        --
        -- Wrapper over look: look around and keep only rows whose line contains the substring.
        -- Exact match, no case folding: Lua's string.lower is bytewise and would lie about
        -- anything that isn't ASCII.
        function find(what, kind)
            local r = look(kind and { kind = kind } or {})
            local rows = r.effect and r.effect['objects'] or {}
            local out = {}

            for _, row in ipairs(rows) do
                if string.find(row, what, 1, true) then
                    out[#out + 1] = string.match(row, '^([^ |]+)')
                end
            end

            return out
        end
        """;
}
