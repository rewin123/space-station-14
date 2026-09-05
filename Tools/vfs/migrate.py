#!/usr/bin/env python3
"""Break a flat library of skills apart into the agent's file-system tree.

Why
-----
`ai_data/skills/` is 232 files in one folder, and 229 of them aren't skills but a proofread
reference library: 168 were written by hand, 61 were pulled from the game's internal wiki.
Their index took up 16,425 characters of the frozen system prompt and was the only way to
find out that an article existed.

Now the reference library lives as a tree in `ai_data/wiki_ru/`, read-only, and
`ai_data/skills/` stays for what the agent writes itself. The prefix map is taken from
`Tools/wiki/link.py`, so the sections match the ones the reference library was already built
around.

What it does
----------
1. `атмосфера-насосы.md`      -> `wiki_ru/атмосфера/насосы.md`
2. `справочник-атмосфера.md`  -> `wiki_ru/атмосфера/_index.md`   (section description and overview)
3. `справочник.md`            -> `wiki_ru/_index.md`
4. `словарь-*.md`             -> `wiki_ru/словарь-*.md`          (doesn't deserve its own section)
5. Rewrites 1466 `[[name]]` links into paths. **Every unresolved one goes into the report**:
   a dead link in a reference library is worse than a missing one, and it can't be left silent.
6. Rewrites calls to retired tools inside articles. Otherwise the reference library teaches
   the agent to call `skill_view`, which no longer exists, wasting a turn on every such piece
   of advice.
7. Discards junk left by tests, keeps the agent's real entries in `skills/`.

Usage
-------------
    python3 Tools/vfs/migrate.py --dry     # print the plan, touch nothing
    python3 Tools/vfs/migrate.py           # run it, making a backup first
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

# Name prefix -> section. Same list as in Tools/wiki/link.py: the sections are already built
# around it, and keeping a second one would produce two different reference libraries.
SECTIONS = [
    "антаг", "атмосфера", "виды", "должности", "закон", "мед", "наука", "питание",
    "правила", "сб", "связь", "сервис", "силикон", "снабжение", "события", "строй", "химия",
]

# Have no section of their own and don't deserve one: not a station department, but what gets
# read before the departments.
FLAT = ["словарь"]

# Junk from test runs: timestamped names and trial entries.
JUNK = re.compile(r"^(проба-|после-перезапуска-)")

# The agent's real entries — stay in skills/.
KEEP_IN_SKILLS = {"restore-core-power"}

LINK = re.compile(r"\[\[([^\]]+)\]\]")

# An ellipsis instead of a name is not a link but a placeholder for the intended article:
# "open chemistry-...". These are rewritten to the section path rather than counted as broken.
PLACEHOLDER = re.compile(r"^(?P<section>[^-]+)-?[.\u2026]{2,}$")
SKILL_VIEW = re.compile(r"""skill_view\s*\{\s*["']name["']\s*:\s*["']([^"']+)["']\s*\}""")


def flatten(name: str) -> str:
    """Collapse a line break inside a link.

    In an article's text a link can legitimately wrap at the column width:
    "[[химия-\nнаркотики]]". Without this, two such links would count as broken even though
    they're intact.
    """
    return re.sub(r"\s+", "", name)


def target_of(stem: str) -> str | None:
    """Where the file goes. None means it stays in skills/ or is discarded."""
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
    plan: dict[str, str] = {}      # old name without .md -> new path inside wiki_ru
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
            # An unrecognized prefix can't be silently left in skills/: that would put a
            # reference-library article among the agent's personal entries and make it
            # writable by the agent.
            print(f"НЕ РАЗЛОЖЕНО: {stem} — нет раздела под этот префикс", file=sys.stderr)
            stays.append(stem)
            continue

        plan[stem] = target

    # ---------------------------------------------------------------- links

    # A link points to the section if its target was the section's hub: cat on the folder
    # returns its table of contents.
    link_map: dict[str, str] = {}

    for stem, target in plan.items():
        if target == "_index":
            link_map[stem] = "/wiki_ru"
        elif target.endswith("/_index"):
            link_map[stem] = "/wiki_ru/" + target[: -len("/_index")]
        else:
            link_map[stem] = "/wiki_ru/" + target

    def placeholder_path(name: str) -> str | None:
        """"chemistry-..." is not an article name, it's a pointer to a section. Route to the section."""
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

    # ------------------------------------------------------------------ report

    print(f"файлов в {SRC.relative_to(ROOT)}: {len(files)}")
    print(f"  в справочник: {len(plan)}")
    print(f"  остаётся у агента: {len(stays)} ({', '.join(stays) or '—'})")
    print(f"  выбрасывается как мусор прогонов: {len(junk)}")

    by_section: dict[str, int] = defaultdict(int)
    for target in plan.values():
        by_section[target.split("/")[0] if "/" in target else "(корень)"] += 1

    for section, count in sorted(by_section.items(), key=lambda kv: -kv[1]):
        print(f"    {section:<14} {count}")

    # The rewrite pass runs over all bodies — in dry mode too, so that the report on broken
    # links is available BEFORE anything is touched.
    def retitle(text: str, target: str) -> str:
        """The heading is the name in the new tree, not the old flat one.

        The folder already carries the section, and "# атмосфера-насосы" inside
        /wiki_ru/атмосфера/насосы would repeat it twice. For a hub, the heading becomes the
        section itself.
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

    # ------------------------------------------------------------- execution

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
