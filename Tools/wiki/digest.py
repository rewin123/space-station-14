#!/usr/bin/env python3
"""Сварить из прототипов читаемый источник для агента pi.

Две категории вики — химия и кухня — на самом деле пустые страницы: в guidebook там стоит
<GuideReagentGroupEmbed> и <GuideMicrowaveGroupEmbed>, а содержимое игра собирает из прототипов на
лету. Дать модели сырой YAML нельзя: 294 КиБ отступов и служебных полей, из которых половина
контекста уйдёт на `- type:` и `!type:`.

Поэтому здесь YAML сворачивается в строчки, которые человек читает вслух:

    Трикордразин (Tricordrazine) ×3 ← Dylovene 1 + Inaprovaline 1

Результат кладётся в wiki_skills/.source/ и попадает в репозиторий вместе со статьями: это
ровно тот текст, который видел агент, и без него нельзя проверить, откуда он взял цифру.

    python3 Tools/wiki/digest.py
"""

import re
from pathlib import Path

import yaml

OUT = Path("wiki_skills/.source")


class Loader(yaml.SafeLoader):
    """Тег `!type:HealthChange` — не мусор, а имя эффекта, и выбрасывать его нельзя.

    Раньше теги просто стирались регуляркой, и список эффектов реагента превращался в набор
    безымянных словарей: видно, что урона на -1.5, не видно, лечение это или отравление. Здесь
    имя тега кладётся в поле `__type__` и доезжает до текста.
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
    """reagent-name-bicaridine → «bicaridine». Имена и описания живут в локали, не в прототипе."""
    strings = {}
    for path in Path("Resources/Locale/en-US/reagents").rglob("*.ftl"):
        for line in path.read_text(encoding="utf-8", errors="replace").split("\n"):
            if "=" in line and not line.startswith((" ", "#", ".")):
                key, _, value = line.partition("=")
                strings[key.strip()] = value.strip()
    return strings


def amounts(block):
    """`{Silicon: {amount: 1}, Nitrogen: 1}` → `Silicon 1 + Nitrogen 1`, с пометкой катализатора."""
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
            # Поле `impact` сюда НЕ выводится, хотя выглядит заманчиво. Это `LogImpact`
            # (ReactionPrototype.cs:68) — важность строки в админском логе, а не сила чего-либо
            # в игре. Один раз оно уже уехало в статьи как «взрыв силы Low», и модель добросовестно
            # переписала выдуманную механику: у творога оказалась взрывчатость.
            for effect in entry.get("effects") or []:
                if isinstance(effect, dict) and effect.get("__type__") == "SpawnEntity":
                    spawn = effect.get("entity", "?")
                    made = f"{spawn} (предмет)" if made == "(без продукта)" else f"{made} + {spawn} (предмет)"

            # А вот это — настоящие механики, и они обязаны быть в тексте: по ним агент
            # предупреждает химика, что смесь рванёт. Имена типов взяты из самих прототипов.
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
    """Урон эффекта словами.

    В прототипе минус — это лечение, а плюс — вред, и агент читает справочник вслух живым людям:
    «бикаридин, Brute -1.5» в эфире звучит как отчёт об ошибке. Поэтому знак разворачивается в
    слово здесь, один раз, а не остаётся на совести читающей модели.
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
    """`с 15 ед.` — порог передозировки. Это главное, что врач хочет знать про лекарство."""
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
    """Что реагент делает в организме — построчно, в порядке из прототипа."""
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
