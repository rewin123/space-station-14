#!/usr/bin/env python3
"""Отчёт по полным ресинкам PVS в журнале сервера.

Зачем нужен отдельный инструмент, а не grep -c
--------------------------------------------------
Голое число ресинков за раунд ничего не значит и прямо вводит в заблуждение: раунды разной
длины, игроков разное число, и раунд вдвое короче честно даёт вдвое меньше строк при вдвое
худшей игре. Замер 20.08.2026 это показал наглядно — 194 ресинка против 89 выглядели как
улучшение вдвое, а на самом деле для одного и того же игрока стало в шестнадцать раз хуже:
1.1 на тысячу тиков против 17.4.

Поэтому главная цифра здесь — РЕСИНКОВ НА ТЫСЯЧУ ТИКОВ НА ИГРОКА. Она и только она сравнима
между прогонами.

Что разбираем
-------------
Строку, которую печатает Robust.Server/GameStates/PvsSystem.cs при получении
MsgStateRequestFull с непустым MissingEntity:

    [WARN] system.pvs: Client rewin123 requested full state on tick 22325. Last Acked: 22366.
    Curtick: 22368. Apparently they received an entity without metadata: Клин (58185/n58185,
    AiBorgCombatChassis).

Форма без сущности («No entity found») считается отдельно: она означает совсем другую поломку —
клиент упал при создании или применении состояния, а не потерял базу дельты. Смешивать их нельзя,
это разные болезни с разным лечением.

Использование
-------------
    python3 Tools/pvs_resync_report.py <журнал> [<журнал> ...]

Несколько журналов — колонки рядом, для сравнения «до» и «после».
"""

from __future__ import annotations

import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field

# Клиент назвал сущность: потеряна база дельты.
NAMED = re.compile(
    r"Client (?P<player>\S+) requested full state on tick (?P<asked>\d+)\. "
    r"Last Acked: (?P<acked>\d+)\. Curtick: (?P<cur>\d+)\..*?"
    r"metadata: (?P<name>.*?) \((?P<uid>\d+)/"
)

# Клиент сущность не назвал: он упал сам (_brokenEnts) либо переполнил буфер состояний.
ANON = re.compile(
    r"Client (?P<player>\S+) requested full state on tick (?P<asked>\d+)\. "
    r"Last Acked: (?P<acked>\d+)\. Curtick: (?P<cur>\d+)\..*?No entity found"
)

# Любая строка, где виден номер такта — по ним меряем длину прогона. Своего счётчика тиков в
# журнале нет, а брать первую и последнюю строку файла нельзя: сервер пишет и до начала раунда.
TICKS = re.compile(r"(?:Curtick|cT): (\d+)")


@dataclass
class Report:
    path: str
    named: list[dict] = field(default_factory=list)
    anon: list[dict] = field(default_factory=list)
    ticks: list[int] = field(default_factory=list)

    @property
    def span(self) -> int:
        """Длина прогона в тиках. Ноль — если по журналу её не определить."""
        if not self.ticks:
            return 0
        return max(self.ticks) - min(self.ticks)

    @property
    def players(self) -> Counter:
        return Counter(r["player"] for r in self.named + self.anon)


def parse(path: str) -> Report:
    rep = Report(path)

    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if (m := TICKS.search(line)) is not None:
                rep.ticks.append(int(m.group(1)))

            if "requested full state" not in line:
                continue

            if (m := NAMED.search(line)) is not None:
                rep.named.append(m.groupdict())
            elif (m := ANON.search(line)) is not None:
                rep.anon.append(m.groupdict())

    return rep


def quantiles(values: list[int]) -> tuple[int, int, int]:
    """Медиана, 90-й процентиль, максимум. Пустой список — нули."""
    if not values:
        return 0, 0, 0

    ordered = sorted(values)
    return (
        ordered[len(ordered) // 2],
        ordered[min(len(ordered) - 1, int(len(ordered) * 0.9))],
        ordered[-1],
    )


def periods(rows: list[dict]) -> dict[str, list[int]]:
    """Промежутки между ресинками ОДНОЙ И ТОЙ ЖЕ сущности, в тиках.

    Это главный признак петли. Разовый сбой стоит в журнале один раз; петля выдаёт ту же
    сущность каждые несколько десятков тиков, и ровный период означает, что полное состояние
    её не чинит, а только перезапускает цикл.
    """
    by_uid: dict[str, list[int]] = defaultdict(list)

    for row in rows:
        by_uid[f"{row['name']} #{row['uid']}"].append(int(row["cur"]))

    out: dict[str, list[int]] = {}

    for key, ticks in by_uid.items():
        if len(ticks) < 3:
            continue

        ticks.sort()
        out[key] = [b - a for a, b in zip(ticks, ticks[1:])]

    return out


def render(rep: Report) -> None:
    print(f"\n=== {rep.path} ===")

    total = len(rep.named) + len(rep.anon)
    players = len(rep.players)

    if rep.span == 0 or players == 0:
        print(f"  ресинков: {total}; длину прогона по журналу определить не удалось")
    else:
        rate = total * 1000 / rep.span / players
        print(
            f"  прогон {min(rep.ticks)}..{max(rep.ticks)} = {rep.span} тиков, "
            f"игроков {players}"
        )
        print(f"  ресинков всего {total}  ->  {rate:.2f} НА 1000 ТИКОВ НА ИГРОКА")

    if rep.anon:
        # Отдельная болезнь: клиент не смог создать или применить сущность и удалил её сам.
        print(f"  из них без названной сущности: {len(rep.anon)} (клиент упал на состоянии)")

    for player, count in rep.players.most_common():
        per = count * 1000 / rep.span if rep.span else 0
        print(f"    {player:<20} {count:>5}   {per:.2f} на 1000 тиков")

    if rep.named:
        print("  чаще всего теряется:")
        by_ent = Counter(f"{r['name']} #{r['uid']}" for r in rep.named)
        for name, count in by_ent.most_common(8):
            print(f"    {name:<48} {count:>5}")

        lag = [int(r["acked"]) - int(r["asked"]) for r in rep.named]
        med, p90, mx = quantiles(lag)
        print(f"  отставание клиента (Last Acked - requested): медиана {med}, p90 {p90}, макс {mx}")

        gap = [int(r["cur"]) - int(r["acked"]) for r in rep.named]
        med, p90, mx = quantiles(gap)
        print(f"  Curtick - Last Acked:                       медиана {med}, p90 {p90}, макс {mx}")

    loops = periods(rep.named)
    if loops:
        print("  петли (одна сущность повторно, период в тиках):")
        for name, gaps in sorted(loops.items(), key=lambda kv: -len(kv[1]))[:5]:
            med, _, mx = quantiles(gaps)
            print(f"    {name:<48} повторов {len(gaps):>4}, медиана {med}, макс {mx}")


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2

    for path in argv[1:]:
        try:
            render(parse(path))
        except OSError as err:
            print(f"не читается {path}: {err}", file=sys.stderr)
            return 1

    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
