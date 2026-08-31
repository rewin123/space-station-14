#!/usr/bin/env python3
"""Разбор трассы сетевого взаимодействия (NET TRACE) из журнала сервера.

Зачем. После патчей PVS №1–№16 в журнале кончились ресинки, а вис остался. Сводка раз в 30
тиков не ловит такт, на котором встаёт окно Lidgren, leave-PVS отбирает слоты у состояния,
fromTick замирает или киборг пересекает чанк. Сервер пишет одну строку ``NET TRACE`` на
событие — этот скрипт собирает из них картину виса и называет наиболее похожую болезнь.

    python3 Tools/net_trace_report.py <журнал> [<журнал> ...]

Уровень трассы на сервере: ``cvar net.pvs_trace 2`` (умолчание). 3 — каждый такт, короткий
захват. 0 — выключить всё кроме ресинков/HOLD/жирных пакетов.

Строка — набор ``k=v`` после префикса ``NET TRACE``. Пробелы внутри значений вычищены на
стороне сервера.
"""

from __future__ import annotations

import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field

LINE = re.compile(r"NET TRACE\s+(.*)$")
KV = re.compile(r"(\S+?)=(\S+)")


def parse_kv(rest: str) -> dict[str, str]:
    return {m.group(1): m.group(2) for m in KV.finditer(rest)}


def i(d: dict, key: str, default: int = 0) -> int:
    try:
        return int(float(d.get(key, default)))
    except (TypeError, ValueError):
        return default


@dataclass
class Event:
    kind: str
    session: str
    tick: int
    raw: dict[str, str]


@dataclass
class Session:
    name: str
    events: list[Event] = field(default_factory=list)
    kinds: Counter = field(default_factory=Counter)

    def add(self, ev: Event) -> None:
        self.events.append(ev)
        self.kinds[ev.kind] += 1

    @property
    def ticks(self) -> list[int]:
        return [e.tick for e in self.events if e.tick]


def load(path: str) -> dict[str, Session]:
    sessions: dict[str, Session] = defaultdict(lambda: Session(name=""))
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = LINE.search(line)
            if not m:
                continue
            kv = parse_kv(m.group(1))
            kind = kv.get("kind", "?")
            name = kv.get("session", "?")
            ev = Event(kind=kind, session=name, tick=i(kv, "tick"), raw=kv)
            s = sessions[name]
            s.name = name
            s.add(ev)
    return sessions


def series(session: Session, kind: str, key: str) -> list[tuple[int, int]]:
    out = []
    for e in session.events:
        if e.kind != kind and not (kind == "send*" and e.kind in ("send", "send_unrel", "full")):
            continue
        if key not in e.raw:
            continue
        out.append((e.tick, i(e.raw, key)))
    return out


def pct(xs: list[int], p: float) -> int:
    if not xs:
        return 0
    xs = sorted(xs)
    idx = min(len(xs) - 1, max(0, int(round((p / 100) * (len(xs) - 1)))))
    return xs[idx]


def diagnose(s: Session) -> list[str]:
    """Назвать болезнь по подписи в трассе, не по впечатлению."""
    findings: list[str] = []

    sends = [e for e in s.events if e.kind in ("send", "send_unrel", "full", "summary")]
    frees = [i(e.raw, "free") for e in sends if "free" in e.raw]
    queued = [i(e.raw, "queued") for e in sends if "queued" in e.raw]
    inflight = [i(e.raw, "inflight") for e in sends if "inflight" in e.raw]
    wires = [i(e.raw, "wire") for e in s.events if e.kind in ("send", "send_unrel", "full") and "wire" in e.raw]
    stuck = [i(e.raw, "from_stuck") for e in s.events if "from_stuck" in e.raw]
    entered = [i(e.raw, "entered") for e in s.events if "entered" in e.raw]
    entered_full = [i(e.raw, "entered_full") for e in s.events if "entered_full" in e.raw]
    vis = [i(e.raw, "vis") for e in s.events if "vis" in e.raw]
    states = [i(e.raw, "states") for e in s.events if "states" in e.raw]
    leaves = [e for e in s.events if e.kind == "leave"]
    holds = [e for e in s.events if e.kind in ("hold_on", "hold_off", "skip") and e.raw.get("reason") == "hold"]
    skips = [e for e in s.events if e.kind == "skip"]
    resyncs = [e for e in s.events if e.kind == "resync"]
    stale = [e for e in s.events if e.kind == "ack_stale"]
    misses = [e for e in s.events if e.kind == "ack_miss"]
    chunks = [e for e in s.events if e.kind == "borg_chunk"]
    moves = [e for e in s.events if e.kind == "borg_move"]
    fulls = [e for e in s.events if e.kind == "full"]

    if frees and min(frees) <= 0:
        findings.append(
            f"окно Lidgren вставало (free min={min(frees)}, queued max={max(queued) if queued else 0}, "
            f"inflight max={max(inflight) if inflight else 0}): сервер отдавал в канал, который не выпускал. "
            "Вис без ресинка. Лечить размером/числом надёжных сообщений, не тактом сервера."
        )
    elif frees and min(frees) <= 8:
        findings.append(
            f"окно Lidgren почти полное (free min={min(frees)} из 64). Следующий жирный пакет его закроет."
        )

    if queued and max(queued) > 0:
        findings.append(
            f"очередь Lidgren доходила до {max(queued)} сообщений — они уже не в окне, клиент их ещё не видит, "
            "а сервер считает отправленными."
        )

    if stuck and max(stuck) >= 15:
        findings.append(
            f"FromTick стоял {max(stuck)} тиков подряд: дельта считалась от одной и той же базы и росла. "
            "Либо ack не доходит, либо ack_stale/ack_miss."
        )

    if entered and max(entered) >= 50:
        findings.append(
            f"вход в PVS скачком до {max(entered)} сущностей за такт. Подпись патча №14 "
            "(аккумулятор полных состояний входящих), если vis при этом тоже растёт."
        )

    if vis and states and max(states) > max(vis) * 1.5 and max(vis) > 50:
        findings.append(
            f"в пакете состояний {max(states)} при видимых {max(vis)}: в пакет попадают сущности, "
            "которых нет в ToSend — либо повторная пересылка входящих, либо грязь шире зоны."
        )

    if entered_full and max(entered_full) >= 20:
        findings.append(
            f"входящих полным состоянием сущности до {max(entered_full)} за такт "
            "(патч №8: EntityLastAcked старше fromTick)."
        )

    if leaves:
        big = [i(e.raw, "ents") + i(e.raw, "chunk_ents") for e in leaves]
        findings.append(
            f"leave-PVS: {len(leaves)} сообщений, пик {max(big) if big else 0} сущностей. "
            "Они едут тем же ReliableUnordered, что и MsgState, и отбирают слоты окна."
        )

    if holds or any(e.raw.get("reason") == "hold" for e in skips):
        findings.append(
            f"HOLD полного состояния: on={s.kinds['hold_on']} off={s.kinds['hold_off']} "
            f"skip-hold={sum(1 for e in skips if e.raw.get('reason') == 'hold')}. "
            "Пока HOLD, входящие в зону не штампуются (патч №16); после снятия — пачка entered_full."
        )

    empty_off = [e for e in s.events if e.kind == "hold_off" and e.raw.get("reason") == "empty"]
    if empty_off:
        findings.append(
            f"HOLD снят по пустой трубе Lidgren: {len(empty_off)} раз (патч №19, дыра). "
            "Это не «клиент применил мир» — на 208 КБ полное отпускалось в тот же тик, "
            "когда ушли последние фрагменты. Патч №20 это снимает: только живой ack."
        )

    drain = sum(1 for e in skips if e.raw.get("reason") == "drain")
    window_skips = sum(1 for e in skips if e.raw.get("reason") == "window")
    if drain or window_skips:
        findings.append(
            f"патч №19: пропуск пока труба Lidgren полна "
            f"(drain={drain} полное ждёт схлыва, window={window_skips} дельта не встала в хвост)."
        )

    if resyncs:
        findings.append(f"ресинков полного мира: {len(resyncs)}. Смотреть missing= и kind=ack_miss рядом.")

    if stale:
        findings.append(
            f"устаревших ack: {len(stale)}. После аванса LastReceivedAck живой ack клиента отбрасывается — "
            "LastRealAck при этом обновляется. Если HOLD снимается по «живой ack», а клиент полное не применил, "
            "это оно."
        )

    if misses:
        findings.append(
            f"ack не нашёл PreviouslySent: {len(misses)}. История короче задержки ack (болезнь патча №4) "
            "либо overflow. EntityLastAcked после этого застывает."
        )

    if chunks and wires:
        # чанк киборга vs скачок wire в окне ±15 тиков
        correlated = 0
        wire_by_tick = {e.tick: i(e.raw, "wire") for e in s.events if e.kind in ("send", "send_unrel", "full") and "wire" in e.raw}
        for c in chunks:
            around = [w for t, w in wire_by_tick.items() if abs(t - c.tick) <= 15]
            if around and max(around) >= 8 * 1024:
                correlated += 1
        if correlated:
            findings.append(
                f"пересечение чанка киборгом совпало со скачком пакета ≥8 КБ в {correlated} из {len(chunks)} случаев. "
                "Запал — движение, содержимое — смотреть comps=/protos= на этих тактах."
            )
        else:
            findings.append(
                f"киборг сменил чанк {len(chunks)} раз, пакетов ≥8 КБ рядом не видно. "
                "Движение само по себе пакет не раздувает."
            )

    if moves and not chunks:
        findings.append(
            f"киборг ходил ({s.kinds['borg_move']} событий), но чанк не менял. "
            "Вис «без движения» это не этот робот, либо чанки крупнее шага."
        )

    if fulls:
        sizes = [i(e.raw, "wire") for e in fulls if "wire" in e.raw]
        findings.append(
            f"полных состояний мира: {len(fulls)}"
            + (f", медиана {pct(sizes, 50)} Б, max {max(sizes)} Б" if sizes else "")
            + f". Фрагментов при MTU 1200: медиана ~{(pct(sizes, 50) + 1199) // 1200 if sizes else 0}."
        )

    if not findings:
        findings.append(
            "явной подписи виса нет. Включить net.pvs_trace 3 на минуту вокруг воспроизведения "
            "и передать этот же журнал."
        )

    return findings


def report(path: str, sessions: dict[str, Session]) -> None:
    print(f"=== {path} ===")
    if not sessions:
        print("  строк NET TRACE нет. На сервере net.pvs_trace > 0? Сборка с PvsSystem.Trace.cs?")
        return

    for name, s in sorted(sessions.items(), key=lambda kv: kv[0]):
        span = (max(s.ticks) - min(s.ticks)) if s.ticks else 0
        print(f"\n  сессия {name}: {len(s.events)} событий, {span} тиков")
        print(f"    kinds: " + ", ".join(f"{k}={v}" for k, v in s.kinds.most_common()))

        wires = [i(e.raw, "wire") for e in s.events if "wire" in e.raw and e.kind in ("send", "send_unrel", "full")]
        lags = [i(e.raw, "ack_lag") for e in s.events if "ack_lag" in e.raw]
        frees = [i(e.raw, "free") for e in s.events if "free" in e.raw]
        if wires:
            print(f"    wire Б: n={len(wires)} p50={pct(wires, 50)} p90={pct(wires, 90)} max={max(wires)}")
        if lags:
            print(f"    ack_lag тиков: p50={pct(lags, 50)} p90={pct(lags, 90)} max={max(lags)}")
        if frees:
            print(f"    win free: min={min(frees)} p50={pct(frees, 50)}")

        print("    диагноз:")
        for line in diagnose(s):
            print(f"      — {line}")


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    for path in sys.argv[1:]:
        report(path, load(path))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
