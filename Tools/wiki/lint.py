#!/usr/bin/env python3
"""Приёмка статей, написанных агентом pi в wiki_skills/.

check_skills.py проверяет формат и связность — это остаётся за ним, здесь он вызывается по
объединению обеих папок. Сверх того ловятся ровно те ошибки, которые делает именно чужая модель,
пишущая в нашу библиотеку:

  * выдуманный вызов — «scan_room {}» или настоящий инструмент с несуществующим аргументом.
    Сначала вызовы в wiki_skills/ были запрещены вовсе: слой инструментов живёт в skill_start/,
    а чужая локальная модель списка инструментов не знает и сочинила бы его. Запрет снят, когда
    статьи стал писать агент, который читает исходники: все его вызовы оказались настоящими,
    и вырезать полезные разделы ради проверки, которая ничего не нашла, — потеря без выгоды.
    Вместо запрета сверка по существу: имя инструмента и КАЖДЫЙ аргумент проверяются по
    SchemaJson из StationAiAgentSystem.Tools*.cs. Это ловит то, ради чего запрет и вводился, —
    правдоподобную выдумку вроде look {"radius":5} при настоящем параметре expand;
  * имя-близнец — «питание-teg-подробно» рядом с «питание-teg». SkillStore такое имя от самого
    агента не примет (FindOverlapping), с диска загрузит, и в индексе окажутся две статьи об
    одном предмете;
  * перезапись готовой статьи — файл с именем, которое уже занято в skill_start/;
  * непереведённый кусок — строка, где русского не осталось вовсе. Порог именно такой, потому
    что плотные латиницей строки в этой библиотеке чаще всего правильные: узел дерева
    технологий «Optimized Microgalvanism (10000), предпосылка: Advanced Power» обязан нести
    английское имя — под ним игрок его и ищет в консоли. Ловить надо абзац, оставленный как
    есть, а не строку с именами собственными.

    python3 Tools/wiki/lint.py [категория]
"""

import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import manifest  # noqa: E402

START = Path("skill_start")
WIKI = Path("wiki_skills")

TOOL_CALL = re.compile(r"\b([a-z_]{3,})\s*\{")

# Слова, за которыми фигурная скобка стоит по делу и вызовом не является.
NOT_A_CALL = {"json", "yml", "yaml", "type", "csv"}


def schemas():
    """Имя инструмента → множество допустимых аргументов, прямо из объявлений в коде.

    Читается из исходников, а не переписывается сюда руками: список, скопированный в линтер,
    расходится с кодом на первом же новом инструменте, и расхождение это молчаливое.
    """
    src = "".join(p.read_text(encoding="utf-8")
                  for p in Path("Content.Server/AiAgent").glob("StationAiAgentSystem.Tools*.cs"))
    out = {}
    for m in re.finditer(r'Name = "(\w+)".*?SchemaJson = """(.*?)"""', src, re.S):
        try:
            out[m.group(1)] = set(json.loads(m.group(2)).get("properties", {}))
        except json.JSONDecodeError:
            out[m.group(1)] = None  # схему не разобрать — аргументы не проверяем, имя проверяем
    return out


def call_args(body, at):
    """Достать `{...}` после имени инструмента, считая вложенные скобки и переносы строк."""
    depth, start = 0, body.index("{", at)
    for i in range(start, min(len(body), start + 600)):
        depth += (body[i] == "{") - (body[i] == "}")
        if depth == 0:
            return body[start:i + 1]
    return None
LATIN = re.compile(r"[A-Za-z]")
CYRIL = re.compile(r"[А-Яа-яЁё]")


def words(name):
    return {w for w in re.split(r"[-_ ]", name) if len(w) > 2}


def read(path):
    text = path.read_text(encoding="utf-8")
    lines = text.replace("\r\n", "\n").split("\n")
    name = lines[0].strip().lstrip("#").strip().lower().replace(" ", "-")
    body = "\n".join(lines[2:]) if len(lines) > 2 else ""
    return name, body


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    prefix = manifest.category(only)[1] if only else None

    if not WIKI.is_dir():
        print("нет каталога wiki_skills/")
        return 1

    tools = schemas()
    start = {read(p)[0]: p for p in sorted(START.glob("*.md")) if p.name.lower() != "readme.md"}
    fresh = [p for p in sorted(WIKI.glob("*.md")) if p.name.lower() != "readme.md"]
    if prefix:
        fresh = [p for p in fresh if p.stem.startswith(prefix)]

    if not fresh:
        print(f"в wiki_skills/ нет статей{' по префиксу ' + prefix if prefix else ''}")
        return 1

    problems = []

    for path in fresh:
        name, body = read(path)

        if name in start:
            problems.append(f"{name}: такая статья уже есть в skill_start/ — правь её, а не дублируй")

        for m in TOOL_CALL.finditer(body):
            call = m.group(1)
            if call in NOT_A_CALL:
                continue
            if call not in tools:
                problems.append(f"{name}: '{call} {{...}}' — такого инструмента нет")
                continue

            allowed, raw = tools[call], call_args(body, m.start())
            if allowed is None or raw is None:
                continue

            # Аргументы в статьях пишутся для человека и бывают с многоточием или комментарием,
            # поэтому берутся ключи, а не разбирается JSON целиком.
            for key in set(re.findall(r'"(\w+)"\s*:', raw)):
                if key not in allowed:
                    problems.append(f"{name}: {call} {{\"{key}\": …}} — у инструмента нет такого "
                                    f"аргумента (есть: {', '.join(sorted(allowed)) or 'никаких'})")

        for other in list(start) + [read(p)[0] for p in fresh if p != path]:
            a, b = words(name), words(other)
            if not a or not b or not (a < b or b < a):
                continue

            # Двухбуквенные хвосты — «должности-сб», «сб-улики» — до сравнения не доживают:
            # words() отбрасывает слова короче трёх букв, и от имени остаётся один префикс.
            # Тогда ЛЮБОЙ сосед по префиксу выглядит близнецом: «должности-инструменты» против
            # «должности-сб». Это артефакт правила, а не дубликат, и skill_start это доказывает —
            # там восемь статей «должности-*» рядом с «должности-сб» живут с самого начала.
            smaller = a if len(a) < len(b) else b
            if len(smaller) == 1 and smaller <= {name.split("-")[0], other.split("-")[0]}:
                continue

            problems.append(f"{name}: имя-близнец к '{other}' — одно есть другое с довеском")
            break

        for n, line in enumerate(body.split("\n"), 3):
            lat, cyr = len(LATIN.findall(line)), len(CYRIL.findall(line))

            # Перечисление имён собственных — не непереведённый абзац. В статье про имена
            # персонажей строка «XxRobustxX, SDpksSodjdfk, Greytide, Urist McHands» — это и есть
            # содержание: примеры запрещённых ников, которые переводить нечего и нельзя.
            names = [w for w in re.findall(r"[A-Za-z][\w.-]*", line)]
            if line.count(",") >= 1 and len(names) >= 2 and all(w[0].isupper() for w in names):
                continue

            if lat > 30 and cyr == 0:
                problems.append(f"{name}:{n}: строка не переведена — {line.strip()[:60]}")

    # Формат и связность — по объединению: ссылки из новых статей ведут в старые и обратно.
    with tempfile.TemporaryDirectory() as tmp:
        for src in list(start.values()) + fresh:
            shutil.copy(src, Path(tmp) / src.name)
        base = subprocess.run(
            [sys.executable, "Tools/check_skills.py", tmp],
            capture_output=True, text=True)
        print(base.stdout.strip())

    for p in problems:
        print(f"  ОШИБКА {p}")

    print(f"\nновых статей: {len(fresh)}, всего в библиотеке: {len(start) + len(fresh)}")
    return 1 if problems or base.returncode else 0


if __name__ == "__main__":
    sys.exit(main())
