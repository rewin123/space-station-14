# Аксиома — форк Space Station 14, в котором Станционного ИИ ведёт языковая модель

Это форк [space-wizards/space-station-14](https://github.com/space-wizards/space-station-14).
Space Wizards Federation к нему отношения не имеют, поддержки не оказывают и за него не отвечают.

Роль Станционного ИИ здесь играет не скрипт и не игрок, а языковая модель. Она видит станцию
камерами, слышит рацию и речь рядом со своим ядром, управляет дверями, шлюзами и консолями через
те же проверки, что и живой игрок, ведёт собственную память между сменами и командует киборгами по
каналу Binary. Всё это — новыми файлами: правил апстрима форк почти не трогает, и каждое
исключение описано в [`docs/upstream-patches.md`](docs/upstream-patches.md).

## Что добавлено

| | |
|---|---|
| **Агент** | Свой цикл хода, наблюдения из мира, инструменты, файловая система агента (память, навыки, заметки о людях), свёртка контекста с разбором отрезка |
| **Тела** | Неподвижное ядро и киборги: ядро агента отделено от тела, одна модель может вести и то и другое |
| **Режимы** | Мирная смена с ИИ и киборгом, скрытный злой ИИ, открытый злой ИИ с отрядом боевых корпусов |
| **Провайдеры** | Цепочка профилей с фаллбеком: DeepSeek, Grok через свой мост, локальный Qwen под vLLM или llama.cpp, OpenRouter |
| **Стенд** | `Content.AiBench` — сценарные тесты на живой станции: маршруты, зрение, свёртка, файловая система, секретный пул |

## Данные игроков и внешние сервисы

**Это главное, что нужно знать до запуска.**

Когда агент включён, всё, что он слышит, уходит стороннему провайдеру модели: реплики в рацию,
речь рядом с ядром ИИ и рядом с киборгами, объявления. Провайдер — тот, который настроен в
цепочке: DeepSeek, xAI, OpenRouter или ваш собственный сервер.

Что **не** уходит: имена аккаунтов, IP-адреса, идентификаторы Steam. Агент видит имена персонажей
— ровно то же, что видит любой игрок рядом.

Если вы поднимаете публичный сервер, скажите об этом игрокам до входа: в MOTD, в правилах или в
описании сервера. Мы делаем так:

> Роль Станционного ИИ на этом сервере ведёт языковая модель, а не игрок.

Этого достаточно, чтобы объяснить механику, но недостаточно, чтобы объяснить, что речь покидает
сервер. Добавьте вторую строку.

**Ключей мы не требуем и не собираем.** В репозитории их нет — проверяйте сами:

```sh
git grep -nE 'sk-[a-zA-Z0-9]{24,}|xai-[a-zA-Z0-9]{24,}'
```

Все секреты живут в `ai_data/`, а этот каталог в `.gitignore` и ни разу не попадал в индекс.
Класть ключ в `Resources/` нельзя категорически: `ContentMagicAczProvider` раздаёт всю эту папку
каждому подключившемуся игроку.

## Запуск

Сборка ничем не отличается от апстримовой — `RUN_THIS.py`, затем `dotnet build`.

**Из коробки агент выключен.** `ai.enabled` по умолчанию `false`, и в этом состоянии форк ведёт
себя как обычный SS14: ядро не занимается, киборги не спавнятся, ни одного обращения в сеть не
происходит. Это же и способ проверить, что вы получили именно сборку игры, а не что-то ещё.

Чтобы включить агента:

```toml
[ai]
enabled = true
data_dir = "/абсолютный/путь/к/ai_data"
llm_chain = "deepseek,awq"
```

Каталог `ai_data/` создаётся рядом с репозиторием и содержит:

| Файл | Что это |
|---|---|
| `SOUL.md` | Личность агента. Правится руками и читается при захвате ядра |
| `CURATOR.md` | Чем агент руководствуется на разборе отрезка |
| `memory/`, `skills/`, `players/` | То, что агент пишет о мире сам |
| `*.key` | Ключи провайдеров. Имя файла указывается в профиле, значение — никогда |

Профили моделей — в
[`Resources/Prototypes/_AiAgent/llm_profiles.yml`](Resources/Prototypes/_AiAgent/llm_profiles.yml).
Там же объяснено, какой диалект чему соответствует и почему числа контекста занижены.

Образец боевого конфига без секретов —
[`Tools/server_config.public.toml`](Tools/server_config.public.toml): там расставлены значения, до
которых мы дошли живыми раундами, и рядом сказано, почему именно такие. Копировать поверх своего
нельзя — секреты он не содержит намеренно, и `cp` затрёт ваши.

**Про деньги.** Вечер игры на восьми агентах стоит около двух долларов на DeepSeek при
попадании в кэш под 99%. Без кэша — в разы больше, поэтому не меняйте порядок блоков системного
промпта: он собран так, чтобы префикс не менялся между ходами.

## Документация

- [`Content.Server/AiAgent/README.md`](Content.Server/AiAgent/README.md) — описание модуля на
  английском: зачем он, как устроен (диаграммы всех уровней), как поднять, настроить и
  эксплуатировать. Это первое, что стоит читать.
- [`docs/journal-ru.md`](docs/journal-ru.md) — инженерный журнал: замеры, разборы аварий и грабли
  в хронологическом порядке. Обоснования решений, которых нет в коде.
- [`docs/reconfig.md`](docs/reconfig.md) — как поменять провайдера модели, режим или секретный пул
  **без пересборки**: накладка `ai_data/config.d/`, команды `aiagent config`, `aiagent llm probe`,
  `aiagent mode`, и ловушки, которые стоили вечеров.
- [`Tools/examples/llamacpp/`](Tools/examples/llamacpp/) — свой сервер на llama.cpp за полчаса:
  запуск модели, профиль провайдера, свой режим, проверка. Каждый флаг и каждое поле с разбором.
- [`docs/upstream-patches.md`](docs/upstream-patches.md) — каждая правка чужого кода: что, зачем,
  чем воспроизводится, как снять.
- [`docs/problems.md`](docs/problems.md) — разборы поломок, которые дорого дались.

## Известные проблемы

Выкладываем сами, чтобы не получить первыми issue:

- Промпт агента может перерасти заявленное окно контекста: свёртка ждёт закрытия вызовов
  инструментов, а один ход тянется до девяноста шагов.
- `metrics.enabled` отдаёт 404 на своём порту.
- `system.dungeon` пишет десятки тысяч предупреждений про IronRock за сутки.
- База настроек растёт до сотен мегабайт и никогда не чистится.
- Один сценарный тест (`Use_ExplainsWhatHappened`) флейкует в полном прогоне и проходит в
  одиночку: он зависит от того, где именно у маяка встал робот. В CI это иногда красная сборка на
  ровном месте.

## Про ИИ в разработке этого форка

Апстрим не принимает вклад, сгенерированный моделями. Здесь наоборот: значительная часть кода
написана в паре с языковой моделью, и это видно по коммитам. Если вы собираетесь нести отсюда
что-то в апстрим — читайте их правила, они другие.

## Лицензии

Код форка, как и код апстрима, — под [MIT](LICENSE.TXT). Своих ассетов форк не добавляет ни
одного: только код, YAML-прототипы и документация. Всё остальное принадлежит Space Wizards
Federation и их авторам на прежних условиях, включая ассеты под CC-BY-SA 3.0 с указанием авторства
в файлах метаданных. Подробности — в [`NOTICE`](NOTICE).

---

Ниже — README апстрима без изменений.

---

<div class="header" align="center">  
<img alt="Space Station 14" width="880" height="300" src="https://raw.githubusercontent.com/space-wizards/asset-dump/de329a7898bb716b9d5ba9a0cd07f38e61f1ed05/github-logo.svg">  
</div>

Space Station 14 is a remake of SS13 that runs on [Robust Toolbox](https://github.com/space-wizards/RobustToolbox), our homegrown engine written in C#.

This is the primary repo for Space Station 14. To prevent people forking RobustToolbox, a "content" pack is loaded by the client and server. This content pack contains everything needed to play the game on one specific server.

If you want to host or create content for SS14, this is the repo you need. It contains both RobustToolbox and the content pack for development of new content packs.

## Links

<div class="header" align="center">  

[Website](https://spacestation14.com/) | [Discord](https://discord.ss14.io/) | [Forum](https://forum.spacestation14.com/) | [Mastodon](https://mastodon.gamedev.place/@spacestation14) | [Patreon](https://www.patreon.com/spacestation14) | [Steam](https://store.steampowered.com/app/1255460/Space_Station_14/) | [Standalone Download](https://spacestation14.com/about/nightlies/)  

</div>

## Documentation/Wiki

Our [docs site](https://docs.spacestation14.com/) has documentation on SS14's content, engine, game design, and more.  
Additionally, see these resources for license and attribution information:  
- [Robust Generic Attribution](https://docs.spacestation14.com/en/specifications/robust-generic-attribution.html)  
- [Robust Station Image](https://docs.spacestation14.com/en/specifications/robust-station-image.html)

We also have lots of resources for new contributors to the project.

## Contributing

We are happy to accept contributions from anybody. Get in Discord if you want to help. We've got a [list of issues](https://github.com/space-wizards/space-station-14-content/issues) that need to be done and anybody can pick them up. Don't be afraid to ask for help either!  
Just make sure your changes and pull requests are in accordance with the [contribution guidelines](https://docs.spacestation14.com/en/general-development/codebase-info/pull-request-guidelines.html).

We are not currently accepting translations of the game on our main repository. If you would like to translate the game into another language, consider creating a fork or contributing to a fork.

## AI-generated contributions disclaimer
This project does not accept low-effort or wholesale AI-generated contributions. Examples include, but are not limited to:

- Any code (including yaml) generated by tools like GitHub Copilot, ChatGPT, or similar.
- AI-created artwork, sound files, or other assets.
- Auto-generated documentation, issue reports or pull request descriptions.

Exceptions to this are simple tools like Rider's single-line completion feature.

## Building

1. Clone this repo:
```shell
git clone https://github.com/space-wizards/space-station-14.git
```
2. Go to the project folder and run `RUN_THIS.py` to initialize the submodules and load the engine:
```shell
cd space-station-14
python RUN_THIS.py
```
3. Compile the solution:  

Build the server using `dotnet build`.

[More detailed instructions on building the project.](https://docs.spacestation14.com/en/general-development/setup.html)

## License

All code for the content repository is licensed under the [MIT license](https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT).  

Most assets are licensed under [CC-BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/) unless stated otherwise. Assets have their license and copyright specified in the metadata file. For example, see the [metadata for a crowbar](https://github.com/space-wizards/space-station-14/blob/master/Resources/Textures/Objects/Tools/crowbar.rsi/meta.json).  

> [!NOTE]
> Some assets are licensed under the non-commercial [CC-BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/) or similar non-commercial licenses and will need to be removed if you wish to use this project commercially.
