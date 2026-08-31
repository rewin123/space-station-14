#!/usr/bin/env python3
"""Разложить плоскую библиотеку скиллов в дерево файловой системы агента.

Зачем
-----
`ai_data/skills/` — это 232 файла в одной папке, и 229 из них не скиллы, а вычитанный справочник:
168 написаны руками, 61 снят с внутренней вики игры. Их индекс занимал 16 425 символов
замороженного системного промпта и был единственным способом узнать, что статья существует.

Теперь справочник живёт деревом в `ai_data/wiki_ru/`, только на чтение, а `ai_data/skills/`
остаётся под то, что агент пишет сам. Карта префиксов взята из `Tools/wiki/link.py`, чтобы
разделы совпадали с теми, по которым справочник уже собран.

Что делает
----------
1. `атмосфера-насосы.md`      -> `wiki_ru/атмосфера/насосы.md`
2. `справочник-атмосфера.md`  -> `wiki_ru/атмосфера/_index.md`   (описание и обзор раздела)
3. `справочник.md`            -> `wiki_ru/_index.md`
4. `словарь-*.md`             -> `wiki_ru/словарь-*.md`          (своего раздела не заслуживает)
5. Переписывает 1466 ссылок `[[имя]]` в пути. **Каждая неразрешившаяся попадает в отчёт**:
   мёртвая ссылка в справочнике хуже отсутствующей, и молча оставлять её нельзя.
6. Переписывает вызовы снятых инструментов внутри статей. Иначе справочник учит агента звать
   `skill_view`, которого больше нет, — и тратит ход на каждый такой совет.
7. Выбрасывает мусор от тестов, оставляет настоящие записи агента в `skills/`.

Использование
-------------
    python3 Tools/vfs/migrate.py --dry     # напечатать план, ничего не трогая
    python3 Tools/vfs/migrate.py           # выполнить, сделав резервную копию
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "ai_data"
SRC = DATA / "skills"
WIKI = DATA / "wiki_ru"

# Префикс имени -> раздел. Тот же список, что в Tools/wiki/link.py: разделы уже собраны по нему,
# и заводить второй значило бы получить два разных справочника.
SECTIONS = [
    "антаг", "атмосфера", "виды", "должности", "закон", "мед", "наука", "питание",
    "правила", "сб", "связь", "сервис", "силикон", "снабжение", "события", "строй", "химия",
]

# Своего раздела не имеют и не заслуживают: это не отдел станции, а то, что читают до отделов.
FLAT = ["словарь"]

# Мусор от прогонов: имена с отметкой времени и пробные записи.
JUNK = re.compile(r"^(проба-|после-перезапуска-)")

# Настоящие записи агента — остаются в skills/.
KEEP_IN_SKILLS = {"restore-core-power"}

LINK = re.compile(r"\[\[([^\]]+)\]\]")

# Многоточие вместо имени — не ссылка, а место для нужной статьи: «открой химия-...».
# Такие переписываются в путь раздела, а не считаются битыми.
PLACEHOLDER = re.compile(r"^(?P<section>[^-]+)-?[.\u2026]{2,}$")
SKILL_VIEW = re.compile(r"""skill_view\s*\{\s*["']name["']\s*:\s*["']([^"']+)["']\s*\}""")


def flatten(name: str) -> str:
    """Схлопнуть перенос строки внутри ссылки.

    В тексте статьи ссылка законно разрывается по ширине колонки: «[[химия-\nнаркотики]]».
    Без этого две такие ссылки числились бы битыми, хотя целы.
    """
    return re.sub(r"\s+", "", name)


def target_of(stem: str) -> str | None:
    """Куда ложится файл. None — значит остаётся в skills/ или выбрасывается."""
    if stem == "справочник":
        return "_index"

    if stem.startswith("справочник-"):
        return f"{stem[len('справочник-'):]}/_index"

    for section in SECTIONS:
        if stem.startswith(section + "-"):
            return f"{section}/{stem[len(section) + 1:]}"

    for flat in FLAT:
        if stem == flat or stem.startswith(flat + "-"):
            return stem

    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry", action="store_true", help="напечатать план и выйти")
    args = parser.parse_args()

    if not SRC.is_dir():
        print(f"нет каталога {SRC}", file=sys.stderr)
        return 1

    files = sorted(SRC.glob("*.md"))
    plan: dict[str, str] = {}      # старое имя без .md -> новый путь внутри wiki_ru
    stays: list[str] = []
    junk: list[str] = []

    for path in files:
        stem = path.stem

        if JUNK.match(stem):
            junk.append(stem)
            continue

        if stem in KEEP_IN_SKILLS:
            stays.append(stem)
            continue

        target = target_of(stem)

        if target is None:
            # Молча оставить незнакомый префикс в skills/ нельзя: так статья справочника
            # попала бы в личные записи агента и стала бы доступной ему на запись.
            print(f"НЕ РАЗЛОЖЕНО: {stem} — нет раздела под этот префикс", file=sys.stderr)
            stays.append(stem)
            continue

        plan[stem] = target

    # ---------------------------------------------------------------- ссылки

    # Ссылка ведёт на раздел, если целью был его хаб: cat по папке отдаёт её оглавление.
    link_map: dict[str, str] = {}

    for stem, target in plan.items():
        if target == "_index":
            link_map[stem] = "/wiki_ru"
        elif target.endswith("/_index"):
            link_map[stem] = "/wiki_ru/" + target[: -len("/_index")]
        else:
            link_map[stem] = "/wiki_ru/" + target

    def placeholder_path(name: str) -> str | None:
        """«химия-...» — это не имя статьи, а указание на раздел. Ведём в раздел."""
        match = PLACEHOLDER.match(name)

        if match is None:
            return None

        section = match.group("section")
        return f"/wiki_ru/{section}/…" if section in SECTIONS else None

    broken: dict[str, list[str]] = defaultdict(list)
    rewrites = {"links": 0, "skill_view": 0, "tools": 0}

    def fix_links(text: str, where: str) -> str:
        def one(match: re.Match) -> str:
            name = flatten(match.group(1))

            if name in link_map:
                rewrites["links"] += 1
                return f"[[{link_map[name]}]]"

            if (generic := placeholder_path(name)) is not None:
                rewrites["links"] += 1
                return f"[[{generic}]]"

            broken[where].append(name)
            return match.group(0)

        return LINK.sub(one, text)

    def fix_calls(text: str, where: str) -> str:
        def one(match: re.Match) -> str:
            name = flatten(match.group(1))
            path = link_map.get(name) or placeholder_path(name)

            if path is not None:
                rewrites["skill_view"] += 1
                return 'sh {"cmd":"cat %s"}' % path

            broken[where].append(name)
            return match.group(0)

        text = SKILL_VIEW.sub(one, text)

        before = text
        text = text.replace("skill_view", "cat")
        text = text.replace("skill_write", "write_file")
        text = text.replace("skill_edit", "edit_file")
        text = text.replace("read_player_related_memory", "cat /players/<имя>")
        text = text.replace("edit_player_related_memory", "edit_file /players/<имя>")
        text = text.replace("search_player_related_notes", "ls /players")

        if text != before:
            rewrites["tools"] += 1

        return text

    # ------------------------------------------------------------------ отчёт

    print(f"файлов в {SRC.relative_to(ROOT)}: {len(files)}")
    print(f"  в справочник: {len(plan)}")
    print(f"  остаётся у агента: {len(stays)} ({', '.join(stays) or '—'})")
    print(f"  выбрасывается как мусор прогонов: {len(junk)}")

    by_section: dict[str, int] = defaultdict(int)
    for target in plan.values():
        by_section[target.split("/")[0] if "/" in target else "(корень)"] += 1

    for section, count in sorted(by_section.items(), key=lambda kv: -kv[1]):
        print(f"    {section:<14} {count}")

    # Прогон переписывания на всех телах — и в сухом режиме тоже, чтобы отчёт о битых
    # ссылках был доступен ДО того, как что-то тронуто.
    def retitle(text: str, target: str) -> str:
        """Заголовок — имя в новом дереве, а не старое плоское.

        Папка уже несёт раздел, и «# атмосфера-насосы» внутри /wiki_ru/атмосфера/насосы повторяет
        его дважды. У оглавления заголовком становится сам раздел.
        """
        if target == "_index":
            name = "справочник"
        elif target.endswith("/_index"):
            name = target[: -len("/_index")]
        else:
            name = target.split("/")[-1]

        lines = text.split("\n")

        if lines and lines[0].startswith("#"):
            lines[0] = f"# {name}"

        return "\n".join(lines)

    bodies: dict[str, str] = {}

    for stem, target in plan.items():
        text = (SRC / f"{stem}.md").read_text(encoding="utf-8")
        text = fix_links(text, stem)
        text = fix_calls(text, stem)
        bodies[target] = retitle(text, target)

    for stem in stays:
        text = (SRC / f"{stem}.md").read_text(encoding="utf-8")
        text = fix_links(text, stem)
        bodies["skills/" + stem] = fix_calls(text, stem)

    print(f"  переписано ссылок: {rewrites['links']}, вызовов cat: {rewrites['skill_view']}, "
          f"файлов с прочими инструментами: {rewrites['tools']}")

    if broken:
        total = sum(len(v) for v in broken.values())
        print(f"\nНЕРАЗРЕШЁННЫХ ССЫЛОК: {total} в {len(broken)} файлах")
        for where, names in sorted(broken.items()):
            print(f"  {where}: {', '.join(sorted(set(names)))}")

    if args.dry:
        print("\n--dry: ничего не изменено")
        return 1 if broken else 0

    # ------------------------------------------------------------- выполнение

    backup = SRC.with_name(f"skills.bak-{datetime.now():%Y%m%d-%H%M%S}")
    shutil.copytree(SRC, backup)
    print(f"\nрезервная копия: {backup.relative_to(ROOT)}")

    WIKI.mkdir(parents=True, exist_ok=True)

    for target, text in bodies.items():
        if target.startswith("skills/"):
            destination = SRC / (target[len("skills/"):] + ".md")
        else:
            destination = WIKI / (target + ".md")

        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text, encoding="utf-8")

    for stem in list(plan) + junk:
        (SRC / f"{stem}.md").unlink(missing_ok=True)

    print(f"справочник: {len(plan)} статей в {WIKI.relative_to(ROOT)}")
    print(f"у агента осталось: {len(list(SRC.glob('*.md')))} файлов")
    return 1 if broken else 0


if __name__ == "__main__":
    raise SystemExit(main())
