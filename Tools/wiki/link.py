#!/usr/bin/env python3
"""Подшить новые статьи в дерево справочника.

Статья вне дерева не потеряна — в индексе зоны 0 есть всё, — но открывается она на практике
редко: модель ходит по справочнику ссылками, от корня к разделу и от раздела к статье. Поэтому
недостаточно, чтобы новая статья ссылалась «наверх»: обход идёт в обратную сторону, и раздел
обязан ссылаться на неё.

Правит хабы `справочник-*` в skill_start/. Это единственное, ради чего вычитанные вручную файлы
здесь трогаются: хаб — оглавление, и оглавление без новых глав неверно.

    python3 Tools/wiki/link.py [--dry]
"""

import re
import sys
from pathlib import Path

MAX_BODY = 4800

# Префикс имени статьи → хаб, который обязан на неё ссылаться.
HUB = {
    "антаг": "справочник-антаг", "атмосфера": "справочник-атмосфера",
    "виды": "справочник-виды", "должности": "справочник-должности",
    "закон": "справочник-закон", "мед": "справочник-мед", "наука": "справочник-наука",
    "питание": "справочник-питание", "правила": "справочник-правила", "сб": "справочник-сб",
    "связь": "справочник-связь", "сервис": "справочник-сервис",
    "силикон": "справочник-силикон", "снабжение": "справочник-снабжение",
    "события": "справочник-события", "строй": "справочник-строй", "химия": "справочник-химия",
    # Словарь терминов, выживание и управление — своего раздела не имеют и не заслуживают:
    # это не отдел станции, а то, что читают до отделов. Вешаются прямо на корень.
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

        # Уже подшитые пропускаются: скрипт должен переживать повторный запуск, иначе после
        # второго прогона в хабе окажется два одинаковых списка.
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
