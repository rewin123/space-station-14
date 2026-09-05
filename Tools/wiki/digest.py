#!/usr/bin/env python3
"""Cook a readable source for the pi agent out of the prototypes.

Two wiki categories — chemistry and cooking — are actually empty pages: the guidebook has
<GuideReagentGroupEmbed> and <GuideMicrowaveGroupEmbed> there, and the game assembles the
content from prototypes on the fly. Feeding the model raw YAML won't work: 294 KiB of
indentation and boilerplate fields, half of which context would go to `- type:` and `!type:`.

So here the YAML is folded into lines a human can read aloud:

    Трикордразин (Tricordrazine) ×3 ← Dylovene 1 + Inaprovaline 1

The result is placed into wiki_skills/.source/ and goes into the repository alongside the
articles: this is exactly the text the agent saw, and without it there's no way to check
where it got a number from.

    python3 Tools/wiki/digest.py
"""

import re
from pathlib import Path

import yaml

OUT = Path("wiki_skills/.source")


class Loader(yaml.SafeLoader):
    """The `!type:HealthChange` tag is not junk, it's the effect's name, and can't be dropped.

    The tags used to just get stripped by a regex, and a reagent's effect list turned into a
    set of unnamed dicts: you could see -1.5 damage, but not whether it was healing or harm.
    Here the tag's name is placed into the `__type__` field and carries through to the text.
    """


Loader.add_multi_constructor(
    "!type:", lambda loader, suffix, node: dict(loader.construct_mapping(node, deep=True),
                                                __type__=suffix)
    if isinstance(node, yaml.MappingNode) else {"__type__": suffix})


def load(path):
    try:
        return yaml.load(path.read_text(encoding="utf-8", errors="replace"), Loader=Loader) or []
    except yaml.YAMLError as e:
        print(f"  пропущен {path}: {e}")
        return []


def locale():
    """reagent-name-bicaridine → "bicaridine". Names and descriptions live in the locale, not the prototype."""
    strings = {}
    for path in Path("Resources/Locale/en-US/reagents").rglob("*.ftl"):
        for line in path.read_text(encoding="utf-8", errors="replace").split("\n"):
            if "=" in line and not line.startswith((" ", "#", ".")):
                key, _, value = line.partition("=")
                strings[key.strip()] = value.strip()
    return strings


def amounts(block):
    """`{Silicon: {amount: 1}, Nitrogen: 1}` → `Silicon 1 + Nitrogen 1`, with a catalyst note."""
    parts = []
    for name, spec in (block or {}).items():
        if isinstance(spec, dict):
            amount = spec.get("amount", 1)
            if spec.get("catalyst"):
                parts.append(f"{name} {amount} (катализатор, не тратится)")
                continue
        else:
            amount = spec
        parts.append(f"{name} {amount}")
    return " + ".join(parts)


def reactions():
    lines = ["РЕАКЦИИ ХИМИЧЕСКОГО ДИСПЕНСЕРА И ЁМКОСТЕЙ",
             "Читать так: продукт ×сколько ← из чего. Числа — части, а не миллилитры:",
             "рецепт 1+1 в стакане на 100 ед. даёт 50 и 50.", ""]

    for path in sorted(Path("Resources/Prototypes/Recipes/Reactions").glob("*.yml")):
        block = [f"## {path.stem}"]
        for entry in load(path):
            if not isinstance(entry, dict) or entry.get("type") != "reaction":
                continue

            products = entry.get("products") or {}
            made = ", ".join(f"{k} ×{v}" for k, v in products.items()) or "(без продукта)"
            note = []


            if entry.get("minTemp"):
                note.append(f"от {entry['minTemp']} K")
            if entry.get("maxTemp"):
                note.append(f"до {entry['maxTemp']} K")
            if entry.get("requiredMixerCategories"):
                note.append("нужен миксер: " + ", ".join(entry["requiredMixerCategories"]))
            # The `impact` field is NOT output here, even though it looks tempting. It's
            # `LogImpact` (ReactionPrototype.cs:68) — the importance of a line in the admin
            # log, not the strength of anything in-game. It already leaked into articles once
            # as "blast force Low", and the model faithfully wrote up the fabricated mechanic:
            # cheese ended up explosive.
            for effect in entry.get("effects") or []:
                if isinstance(effect, dict) and effect.get("__type__") == "SpawnEntity":
                    spawn = effect.get("entity", "?")
                    made = f"{spawn} (предмет)" if made == "(без продукта)" else f"{made} + {spawn} (предмет)"

            # This part, though, is genuine mechanics, and it has to be in the text: the agent
            # uses it to warn a chemist that a mix will blow up. Type names are taken straight
            # from the prototypes.
            for effect in entry.get("effects") or []:
                if not isinstance(effect, dict):
                    continue
                kind = effect.get("__type__")
                if kind == "Explosion":
                    note.append("ВЗРЫВ" + (f" (интенсивность {effect['intensity']})"
                                           if effect.get("intensity") else ""))
                elif kind == "Emp":
                    note.append("ЭМИ-импульс")
                elif kind == "Flash":
                    note.append("вспышка")
                elif kind == "CreateGas":
                    note.append(f"выделяет газ {effect.get('gas', '?')}")
                elif kind == "AreaReactionEffect":
                    note.append("облако по площади")

            tail = f"   [{'; '.join(note)}]" if note else ""
            block.append(f"{made} ← {amounts(entry.get('reactants'))}{tail}")

        if len(block) > 1:
            lines += block + [""]

    return "\n".join(lines)


def damage_of(effect):
    """The effect's damage, spelled out in words.

    In the prototype, minus means healing and plus means harm, and the agent reads the
    reference library aloud to live people: "bicaridine, Brute -1.5" over comms sounds like a
    bug report. So the sign gets spelled out into a word here, once, instead of being left to
    the reading model's judgment.
    """
    dmg = effect.get("damage")
    if isinstance(dmg, dict):
        dmg = dmg.get("types", dmg)
    if not isinstance(dmg, dict):
        return None

    heal = [f"{k} {-v:g}" for k, v in dmg.items() if isinstance(v, (int, float)) and v < 0]
    hurt = [f"{k} {v:g}" for k, v in dmg.items() if isinstance(v, (int, float)) and v > 0]

    parts = []
    if heal:
        parts.append("лечит " + ", ".join(heal))
    if hurt:
        parts.append("вредит " + ", ".join(hurt))
    return "; ".join(parts) or None


def condition_of(effect):
    """`from 15 u.` is the overdose threshold. It's the main thing a doctor wants to know about a drug."""
    notes = []
    for cond in effect.get("conditions") or []:
        if not isinstance(cond, dict):
            continue
        if cond.get("__type__") == "ReagentCondition":
            lo, hi = cond.get("min"), cond.get("max")
            if lo is not None and hi is not None:
                notes.append(f"при {lo}-{hi} ед.")
            elif lo is not None:
                notes.append(f"от {lo} ед.")
            elif hi is not None:
                notes.append(f"до {hi} ед.")
        elif cond.get("__type__") == "TotalDamage":
            notes.append(f"при уроне от {cond.get('min', '?')}")
    return " ".join(notes)


def effects_of(entry):
    """What the reagent does inside the body — one line per effect, in prototype order."""
    out = []
    for where, block in (entry.get("metabolisms") or {}).items():
        for effect in (block or {}).get("effects") or []:
            if not isinstance(effect, dict):
                continue

            kind = effect.get("__type__", "?")
            dmg = damage_of(effect)
            when = condition_of(effect)
            chance = effect.get("probability")

            if dmg:
                what = dmg
            elif kind in ("Vomit", "ChemVomit"):
                what = "рвота"
            elif kind == "Jitter":
                what = "тряска"
            elif kind == "Drunk":
                what = "опьянение"
            elif kind in ("Emote", "PopupMessage", "ChemCleanBloodstream", "ResetNarcolepsy"):
                continue
            else:
                what = kind

            bits = [b for b in (when, what,
                                f"{chance * 100:g}%" if isinstance(chance, (int, float)) and chance < 1 else "")
                    if b]
            line = " ".join(bits)
            if where != "Bloodstream":
                line += f" ({where})"
            if line not in out:
                out.append(line)
    return out


def reagents():
    strings = locale()
    lines = ["РЕАГЕНТЫ: что это, что делает в организме, с какой дозы вредит",
             "Читать так: имя (id в игре) — эффекты по тактам метаболизма.",
             "«от N ед.» — порог: пока в крови меньше N, этого не происходит. Это и есть передоз.", ""]

    for path in sorted(Path("Resources/Prototypes/Reagents").rglob("*.yml")):
        block = [f"## {path.stem}"]
        for entry in load(path):
            if not isinstance(entry, dict) or entry.get("type") != "reagent":
                continue

            rid = entry.get("id", "?")
            name = strings.get(str(entry.get("name", "")), "")
            desc = strings.get(str(entry.get("desc", "")), "")

            head = f"{name} ({rid})" if name and name.lower() != rid.lower() else rid
            block.append(head + (f" — {desc}" if desc else ""))

            for line in effects_of(entry):
                block.append(f"    {line}")

        if len(block) > 1:
            lines += block + [""]

    return "\n".join(lines)


def cooking():
    lines = ["РЕЦЕПТЫ МИКРОВОЛНОВКИ И КУХНИ",
             "Читать так: блюдо ← ингредиенты, время в секундах.", ""]

    for path in sorted(Path("Resources/Prototypes/Recipes/Cooking").glob("*.yml")):
        block = [f"## {path.stem}"]
        for entry in load(path):
            if not isinstance(entry, dict) or "microwaveMealRecipe" not in str(entry.get("type", "")):
                continue

            parts = []
            if entry.get("solids"):
                parts.append(amounts(entry["solids"]))
            if entry.get("reagents"):
                parts.append(amounts(entry["reagents"]))

            block.append(f"{entry.get('result', '?')} ← {' + '.join(parts) or '(ничего)'}"
                         f"   [{entry.get('time', '?')} с, группа {entry.get('group', '—')}]")

        if len(block) > 1:
            lines += block + [""]

    return "\n".join(lines)


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    for name, text in (("химия-реакции.txt", reactions()),
                       ("химия-реагенты.txt", reagents()),
                       ("сервис-рецепты.txt", cooking())):
        (OUT / name).write_text(text + "\n", encoding="utf-8")
        print(f"{name}: {len(text)} символов, {text.count(chr(10))} строк")
