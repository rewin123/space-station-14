#!/usr/bin/env python3
"""File new articles into the reference library's tree.

An article outside the tree isn't lost — the zone-0 index has everything — but in practice it
rarely gets opened: the model navigates the reference library by links, from the root to a
section and from a section to an article. So it isn't enough for the new article to link
"upward": traversal goes the other way, and the section must link to it.

Edits `справочник-*` hubs in skill_start/. This is the one thing for which the manually
proofread files here get touched: a hub is a table of contents, and a table of contents
missing new chapters is wrong.

    python3 Tools/wiki/link.py [--dry]
"""

import re
import sys
from pathlib import Path

MAX_BODY = 4800

# Article name prefix → the hub that must link to it.
HUB = {
    "антаг": "справочник-антаг", "атмосфера": "справочник-атмосфера",
    "виды": "справочник-виды", "должности": "справочник-должности",
    "закон": "справочник-закон", "мед": "справочник-мед", "наука": "справочник-наука",
    "питание": "справочник-питание", "правила": "справочник-правила", "сб": "справочник-сб",
    "связь": "справочник-связь", "сервис": "справочник-сервис",
    "силикон": "справочник-силикон", "снабжение": "справочник-снабжение",
    "события": "справочник-события", "строй": "справочник-строй", "химия": "справочник-химия",
    # The glossary of terms, survival, and command — have no section of their own and don't
    # deserve one: not a station department, but what gets read before the departments. They
    # attach directly to the root.
    "словарь": "справочник",
}

BLOCK = "ГЛУБЖЕ — статьи с внутренней вики игры"


def when_of(path):
    line = path.read_text(encoding="utf-8").split("\n")[1]
    return line.split(":", 1)[1].strip() if line.lower().startswith("когда:") else ""


def body_of(text):
    return "\n".join(text.split("\n")[2:]).strip()


def main():
    dry = "--dry" in sys.argv
    fresh = {}

    for path in sorted(Path("wiki_skills").glob("*.md")):
        if path.stem == "README":
            continue
        hub = HUB.get(path.stem.split("-")[0])
        if hub is None:
            print(f"  НЕКУДА подшить {path.stem} — нет хаба для префикса", file=sys.stderr)
            continue
        fresh.setdefault(hub, []).append(path)

    for hub, paths in sorted(fresh.items()):
        target = Path(f"skill_start/{hub}.md")
        text = target.read_text(encoding="utf-8")

        # Already-filed entries are skipped: the script must survive a repeated run, otherwise
        # a second pass would leave two identical lists in the hub.
        listed = set(re.findall(r"\[\[([^\]]+)\]\]", text))
        add = [p for p in paths if p.stem not in listed]
        if not add:
            continue

        lines = [f"  [[{p.stem}]] — {when_of(p)}" for p in add]

        if BLOCK in text:
            block = "\n".join(lines) + "\n"
            text = text.replace(BLOCK + "\n", BLOCK + "\n" + block, 1)
        else:
            block = "\n" + BLOCK + "\n" + "\n".join(lines) + "\n"
            anchor = "\nЧТО Я МОГУ СДЕЛАТЬ" if "\nЧТО Я МОГУ СДЕЛАТЬ" in text else "\nНаверх:"
            text = text.replace(anchor, block + anchor, 1)

        size = len(body_of(text))
        flag = "" if size <= MAX_BODY else f"  ПЕРЕБОР на {size - MAX_BODY}"
        print(f"{hub:24} +{len(add):2} ссылок, тело {size:5}{flag}")

        if not dry and not flag:
            target.write_text(text, encoding="utf-8")

    return 0


if __name__ == "__main__":
    sys.exit(main())
