#!/usr/bin/env python3
"""Где справочник действительно потерял вику, а где нет.

Мерить «полноту» пересказа нечем, но одна вещь считается точно: числа. Формула, порог, ёмкость,
таймер — это то, ради чего агента вообще спрашивают, и число либо доехало до библиотеки, либо нет.
Скрипт берёт все числа из источников категории и смотрит, встречается ли каждое в статьях с её
префиксом.

Мера грубая: число может доехать в другой форме («восемьсот раз» вместо 800), а совпасть может
случайно. Поэтому это не оценка, а карта — куда посылать агента в первую очередь.

    python3 Tools/wiki/gap.py [категория]
"""

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import manifest  # noqa: E402

# Число вместе с тем, что за ним следует: «200 L/s», «50 kJ», «30 seconds». Голые числа без
# единицы отбрасываются — это чаще всего нумерация пунктов, а не факт.
FACT = re.compile(r"\b(\d[\d.,]*)\s*(%|[A-Za-zА-Яа-я°/]{1,12})")

# Единицы, за которыми стоит настоящий факт. Всё прочее («2 of them», «3 way») — шум.
UNITS = {
    "kpa", "mpa", "pa", "atm", "kw", "w", "mw", "kj", "j", "mj", "k", "c", "°c", "l", "l/s",
    "u", "units", "unit", "seconds", "second", "sec", "s", "minutes", "minute", "m", "tiles",
    "tile", "damage", "%", "credits", "spesos", "moles", "mol",
}


def numbers(text, drop_markup=True):
    if drop_markup:
        text = re.sub(r"<[^>]*>", " ", text)
        text = re.sub(r"\[/?[^\]]*\]", " ", text)
    out = set()
    for value, unit in FACT.findall(text):
        if unit.lower() in UNITS:
            out.add(value.rstrip(".,").replace(",", ""))
    return out


def main():
    keys = [sys.argv[1]] if len(sys.argv) > 1 else [c[0] for c in manifest.CATEGORIES]
    print(f"{'категория':12} {'чисел в вике':>13} {'доехало':>8} {'потеряно':>9}   примеры потерянного")

    for key in keys:
        cat = manifest.category(key)
        prefix = cat[1]

        src = set()
        for f in manifest.files_of(cat):
            try:
                src |= numbers(Path(f).read_text(encoding="utf-8", errors="replace"))
            except OSError:
                pass

        have = set()
        for f in Path("skill_start").glob(f"{prefix}*.md" if prefix else "*.md"):
            have |= set(re.findall(r"\d[\d.]*", f.read_text(encoding="utf-8")))

        lost = sorted(src - have, key=lambda x: -len(x))
        got = len(src) - len(lost)
        pct = f"{100 * got // max(1, len(src))}%"
        print(f"{key:12} {len(src):13} {pct:>8} {len(lost):9}   {', '.join(lost[:8])}")


if __name__ == "__main__":
    main()
