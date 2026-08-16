#!/usr/bin/env python3
"""Проверка библиотеки скиллов агента.

Ловит ровно то, что иначе всплывает на живом раунде и выглядит как «ИИ тупит»:
файл, который SkillStore не разберёт и молча пропустит; строку `когда`, которая не доедет до
индекса; ссылку [[имя]] в никуда, на которую агент потратит ход; статью, до которой нельзя
дойти от корня.

Правила разбора повторяют SkillStore.Parse (Content.Server/AiAgent/Skills/SkillStore.cs).

    python3 Tools/check_skills.py [каталог]
"""

import re
import sys
from collections import deque
from pathlib import Path

# Лимит «когда» — из SkillStore.MaxWhenLength, он жёсткий: всё сверх него просто не попадёт
# в системный промпт. Лимит тела у нас строже кодового (5000): справочник читается кусками,
# и статья, которая не влезает в 2000, почти всегда должна быть двумя статьями.
MAX_WHEN = 60

# Ниже кодового потолка SkillStore.MaxBodyLength (5000), и намеренно.
#
# Запас нужен куратору: skill_edit с пустым match дописывает в конец и отказывает, если результат
# вылезет за 5000. Статья, упёршаяся в самый лимит, — это статья, к которой агент больше никогда
# не сможет добавить ни строчки из своего опыта.
#
# Но это порог ЗАПАСА, а не корректности, и валить на нём прогон оказалось неверно. Ровно так
# растут статьи, которые агент дописал сам за смену: он писал через skill_edit, SkillStore принял,
# на диске лежит 4988 — и снимок этого опыта не проходил бы собственную проверку. Поэтому выше
# 4800 это предупреждение (запас кончился, пора делить статью надвое), а ошибка — только выше
# кодового потолка, где файл уже не примет и сам агент.
SOFT_BODY = 4800
MAX_BODY = 5000

ROOT_SKILL = "справочник"
LINK = re.compile(r"\[\[([^\[\]\n]{1,64})\]\]")


def normalise(name):
    """SkillStore.Normalise: регистр и пробелы не различают скиллы."""
    return name.strip().lower().replace(" ", "-")


def parse(text):
    """Возвращает (имя, когда, тело) или None, если SkillStore это не примет."""
    lines = text.replace("\r\n", "\n").split("\n")
    if len(lines) < 2 or not lines[0].strip().startswith("#"):
        return None

    name = normalise(lines[0].strip().lstrip("#").strip())
    if not name:
        return None

    when, body_start = "", 1
    for i in range(1, len(lines)):
        line = lines[i].strip()
        if not line:
            continue
        if line.lower().startswith("когда:"):
            when = line[len("когда:"):].strip()
            body_start = i + 1
        break

    return name, when, "\n".join(lines[body_start:]).strip()


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "ai_data/skills")
    if not root.is_dir():
        print(f"нет каталога {root}")
        return 1

    skills, problems, crowded = {}, [], []

    for path in sorted(root.glob("*.md")):
        # README рядом со скиллами — документация каталога, а не статья. SkillStore его тоже
        # отбрасывает (нет строки 'когда:'), просто молча, с предупреждением в лог.
        if path.name.lower() == "readme.md":
            continue

        parsed = parse(path.read_text(encoding="utf-8"))
        if parsed is None:
            problems.append(f"{path.name}: не разбирается — нужны '# имя' и строка 'когда:'")
            continue

        name, when, body = parsed

        # Имя берётся из заголовка, а файл ищется по имени: разойдясь, они дают скилл, который
        # виден в индексе и не открывается.
        if name != normalise(path.stem):
            problems.append(f"{path.name}: имя в заголовке '{name}' не совпадает с именем файла")
        if not when:
            problems.append(f"{name}: пустая строка 'когда' — скилл не попадёт в индекс")
        if len(when) > MAX_WHEN:
            problems.append(f"{name}: 'когда' {len(when)} символов, предел {MAX_WHEN}")
        if len(body) > MAX_BODY:
            problems.append(f"{name}: тело {len(body)} символов, потолок кода {MAX_BODY}")
        elif len(body) > SOFT_BODY:
            crowded.append(f"{name}: тело {len(body)} — запаса на дописку почти нет")
        if name in skills:
            problems.append(f"{name}: дубликат имени")

        skills[name] = body

    for name, body in skills.items():
        for target in {normalise(m) for m in LINK.findall(body)}:
            if target not in skills:
                problems.append(f"{name}: ссылка [[{target}]] в никуда")

    # Достижимость от корня — заметка, а не ошибка.
    #
    # В индексе зоны 0 есть всё, так что «сирота» не потерян. Но справочник модель читает по
    # ссылкам, и статья вне дерева на практике не открывается. Скиллы, которые агент написал
    # себе сам, в дерево не входят по определению — валить на них прогон было бы неверно.
    outside = []
    if ROOT_SKILL in skills:
        seen, queue = {ROOT_SKILL}, deque([ROOT_SKILL])
        while queue:
            for target in {normalise(m) for m in LINK.findall(skills[queue.popleft()])}:
                if target in skills and target not in seen:
                    seen.add(target)
                    queue.append(target)

        outside = sorted(set(skills) - seen)

    index = sum(len(f"  {n} — ") + MAX_WHEN for n in skills)
    print(f"скиллов: {len(skills)}, индекс в зоне 0: не больше ~{index // 1024} КБ")

    if outside:
        print(f"вне дерева '{ROOT_SKILL}' ({len(outside)}): {', '.join(outside)}")

    for c in crowded:
        print(f"  тесно  {c}")

    for p in problems:
        print(f"  ОШИБКА {p}")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
