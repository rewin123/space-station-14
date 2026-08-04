# AiAgent — роль Station AI, которую играет LLM

Форк SS14, в котором роль Station AI ведёт локальная модель (Qwen через llama-swap на
`http://127.0.0.1:9292/v1`). Весь код форка живёт здесь, в `Content.Server/AiAgent/`;
апстримовые файлы не правятся, чтобы `git rebase upstream/master` оставался дешёвым.

Полный план: `~/.claude/plans/precious-jingling-forest.md`.

## Почему всё в Content.Server

`Content.Server` **не** проверяется песочницей (`ServerOptions.Sandboxing = false`, а
`Content.IntegrationTests/Tests/Utility/SandboxTest.cs` чекает только `Content.Client` и
`Content.Shared`). Значит `System.Net.Http` и `Task.Run` здесь разрешены.

**Следствие: ни строчки агента в `Content.Shared`** — там песочница включена и сборка упадёт.

Отдельный csproj тоже не подходит: обнаружение `EntitySystem` и `[CVarDefs]` идёт рефлексией
только по ассемблям, которые `ModLoader` загрузил как content-модули.

## Точки расширения без правки апстрима

| Что | Механизм |
|---|---|
| Игровая логика | новые `EntitySystem` — `IReflectionManager` находит их сам |
| Конфиг | свой класс с `[CVarDefs]` (`CCVars.cs` прямо предписывает форкам этот путь) |
| Прототипы | новые YAML в `Resources/Prototypes/_AiAgent/` |
| Команды устройствам | `RaiseLocalEvent(target, (object)msg, true)` с `msg.Actor = brainUid` |
| Консольные команды | классы `IConsoleCommand`, регистрируются рефлексией |

Единственная правка апстрима — строка `ai_data/` в корневом `.gitignore`.

## Сборка и запуск

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"   # уже в ~/.bashrc
cd /home/rewin/projects/ss14_ai

dotnet build SpaceStation14.slnx -c Release        # ~2.5 мин на 72 ядрах
cd bin/Content.Server && ./Content.Server
```

Проверить, что сервер жив, без клиента:

```bash
curl -s --noproxy '*' http://127.0.0.1:1212/status
# {"name":"MyServer","players":0,...,"run_level":1,"map":"Packed"}
# run_level: 0 = лобби, 1 = раунд идёт, 2 = конец раунда
```

Headless-клиент:

```bash
cd bin/Content.Client
./Content.Client --headless --connect --connect-address udp://127.0.0.1:1212 \
    --username dev --cvar res.texturepreloadingenabled=false
```

## Грабли, на которые уже наступили

**1. `--headless` сам по себе не хватает — клиент падает на OpenGL.**
`ResourceCache.PreloadRsis` зовёт `GL.GetInteger` напрямую, мимо абстракции `ClydeHeadless`,
и без GL-контекста процесс валится с `Aborted (core dumped)`. Лечится флагом
`--cvar res.texturepreloadingenabled=false` — он пропускает весь блок предзагрузки
(`ResourceCache.Preload.cs:39`). Альтернатива — запуск под `xvfb-run -a`, но это дороже.

**2. `players: 0` в `/status` не значит, что клиент не подключился.**
При `loginlocal = true` локальный игрок становится полным админом, а
`admins_count_in_playercount = false` вычитает админов из счётчика. Проверять надо по логу
сервера (`net: Approved ... Connected`) или по смене `run_level`/`map`, а не по `players`.

**3. Прокси глотает localhost.**
В окружении заданы `HTTP_PROXY=http://127.0.0.1:10809` и `ALL_PROXY=socks5h://127.0.0.1:10808`,
поэтому запросы на `127.0.0.1:9292` (llama-swap) и `127.0.0.1:1212` (status API) через них
виснут. В shell — `curl --noproxy '*'`; в C# — `new HttpClient(new SocketsHttpHandler
{ UseProxy = false, Proxy = null })`, потому что `HttpClient.DefaultProxy` читает окружение
при старте процесса и семантика `NO_PROXY` разнится.

## Консоль сервера без интерактивного терминала

Сервер читает команды со stdin. Чтобы гонять команды из скрипта, самый надёжный способ —
`tail -F` командного файла:

```bash
: > /tmp/ss14cmds.txt
tail -n0 -F /tmp/ss14cmds.txt | ./Content.Server
# в другом месте:
echo "aiagent status" >> /tmp/ss14cmds.txt
```

FIFO (`mkfifo`) для этого не годится: открытие на чтение блокируется, пока нет писателя, и при
перезапусках сервер молча зависал с пустым логом.

`startround` запускает раунд без единого игрока — иначе лобби ждёт подключений вечно и
авто-захват ядра ИИ никогда не срабатывает.

Команды агента: `aiagent status | claim [uid] | release | inject <канал> <текст> | dryrun on|off`.

## Ещё грабли

**4. Дубли подписок запрещены движком.**
`SharedStationAiSystem` уже подписан на пару `(StationAiCoreComponent, EntInsertedIntoContainerMessage)`,
и вторая подписка на ту же пару роняет сервер на старте с `Duplicate Subscriptions`. Мы вешаемся
на `EntGotInsertedIntoContainerMessage` / `EntGotRemovedFromContainerMessage` — они поднимаются на
самой перемещаемой сущности, что и семантически правильнее: нас интересует наш мозг, а не все
ядра на карте.

**5. Два счётчика поколений — это один баг.**
Первая версия инкрементила счётчик на компоненте до создания сессии, сессия стартовала с нуля, и
первый же маршалинг на главный поток отвергался как протухший. Петля выходила после нуля ходов,
молча, без единой ошибки в логе. Теперь источник истины один — сессия, а копия на компоненте нужна
только для ViewVariables. Из этого же следует правило: **всякий выход петли обязан называть причину**.

**6. Радиоканал `Common` требует запитанный телеком-сервер, `Binary` — нет.**
У `Common` в прототипе `longRange: false`, и `RadioSystem.HasActiveServer` отсекает передачу, если
на карте нет живого `TelecomServerComponent`. У `Binary` `longRange: true`, он работает всегда.
Для тестов восприятия используйте `Binary`: если проверять на `Common`, отсутствие реакции
выглядит как баг агента, хотя сообщение просто не ушло.

## Данные агента

`ai_data/` в корне репозитория, в git не попадает: `SOUL.md`, `memory/{MEMORY,CREW}.md`,
`skills/`, `sessions/`, `logs/`, `bench/`. Лежит вне исходников намеренно — апстримовый
rebase не должен уносить память агента, а ручная правка этих файлов есть главный
отладочный аффорданс.
