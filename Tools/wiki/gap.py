#!/usr/bin/env python3
"""Where the reference library actually lost material from the wiki, and where it didn't.

There's no way to measure the "completeness" of a retelling, but one thing can be counted
exactly: numbers. A formula, a threshold, a capacity, a timer — this is exactly what the
agent gets asked about, and a number either made it into the library or it didn't. The script
takes every number out of a category's sources and checks whether each one shows up in
articles with that category's prefix.

The measure is crude: a number could make it across in a different form ("eight hundred
times" instead of 800), and a match could be coincidental. So this isn't a score, it's a map
— of where to send the agent first.

    python3 Tools/wiki/gap.py [category]
"""

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import manifest  # noqa: E402

# A number together with whatever follows it: "200 L/s", "50 kJ", "30 seconds". Bare numbers
# with no unit are dropped — those are usually list numbering, not a fact.
FACT = re.compile(r"\b(\d[\d.,]*)\s*(%|[A-Za-zА-Яа-я°/]{1,12})")

# Units that indicate a real fact stands behind the number. Everything else ("2 of them",
# "3 way") is noise.
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
