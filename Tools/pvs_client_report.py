#!/usr/bin/env python3
"""Сводка клиента и сервера по одной петле ресинков.

`pvs_resync_report.py` считает, СКОЛЬКО раз клиент попросил полное состояние. Здесь отвечают на
другой вопрос — ПОЧЕМУ: серверный журнал знает только «клиент получил сущность без метаданных» и
не отличает три случая, которые лечатся по-разному:

* сущность на клиенте не создавалась никогда — потерялось создание;
* создавалась и была снесена ``PartialStateReset`` — виновато полное состояние, применённое
  позже своего снимка;
* создавалась и была удалена состоянием — виновата рассинхронизация удалений.

Различает их клиентская диагностика (``FORK PATCH`` в ``Robust.Client/GameStates``), а этот
скрипт сводит её с серверной.

    python3 Tools/pvs_client_report.py <журнал клиента> [журнал сервера]
"""

import re
import sys
from collections import Counter

CL_MISSING = re.compile(r'НЕТ МЕТАДАННЫХ у (\S+?): (.*?)\. Состояние (\d+)→(\d+), '
                        r'LastRealTick (\d+), LastProcessedTick (\d+), в буфере (\d+)')
CL_RESET = re.compile(r'PartialStateReset до тика (\d+): в состоянии (\d+) сущностей, УДАЛЕНО (\d+)')
CL_FULL_RECV = re.compile(r'Received Full GameState: to=(\d+), sz=(\d+)')
CL_FULL_REQ = re.compile(r'Requesting full server state')
CL_DROP_FULL = re.compile(r'ВЫБРАСЫВАЮ полученное полное состояние to=(\d+) sz=(\d+)')
CL_DROP_BUF = re.compile(r'ВЫБРАСЫВАЮ (\d+) буферизованных состояний')
CL_OVERFLOW = re.compile(r'Exceeded maximum state buffer size')

SV_RESYNC = re.compile(r'Client (\S+) requested full state on tick (\d+)\. Last Acked: (\d+)\. '
                       r'Curtick: (\d+)\.(?:.*?metadata: (.*?) \((\d+)/n\d+, (\w+)\))?')
SV_FULL = re.compile(r'полное состояние для (\S+) — (\d+) Б на проводе, MTU (\d+), '
                     r'то есть ~(\d+) фрагментов')
SV_RATE = re.compile(r'PVS DIAG: (\S+) за 30 тиков — состояний (\d+) \((\d+) Б\), '
                     r'из них надёжных (\d+) \((\d+) Б\); пропущено бюджетом (\d+); '
                     r'максимум одного (\d+) Б; отставание ack (\d+)')


def read(path):
    with open(path, encoding='utf-8', errors='replace') as f:
        return f.read().splitlines()


def client_part(lines):
    missing, resets, recvs, drops = [], [], [], []
    reqs = overflow = 0
    for l in lines:
        if (m := CL_MISSING.search(l)):
            missing.append(m.groups())
        elif (m := CL_RESET.search(l)):
            resets.append(tuple(int(x) for x in m.groups()))
        elif (m := CL_FULL_RECV.search(l)):
            recvs.append((int(m[1]), int(m[2])))
        elif (m := CL_DROP_FULL.search(l)):
            drops.append(('полное', int(m[1]), int(m[2])))
        elif (m := CL_DROP_BUF.search(l)):
            drops.append(('буфер', int(m[1]), 0))
        elif CL_FULL_REQ.search(l):
            reqs += 1
        elif CL_OVERFLOW.search(l):
            overflow += 1
    return missing, resets, recvs, drops, reqs, overflow


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    missing, resets, recvs, drops, reqs, overflow = client_part(read(sys.argv[1]))

    print(f"=== клиент: {sys.argv[1]} ===")
    print(f"  запросов полного состояния: {reqs}")
    print(f"  полных состояний получено:  {len(recvs)}"
          + (f", суммарно {sum(s for _, s in recvs) / 1024 / 1024:.2f} МБ" if recvs else ""))
    print(f"  полных состояний применено: {len(resets)}")
    if reqs > len(resets):
        print(f"  --> {reqs - len(resets)} запросов не довели дела до применения")
    if drops:
        d_full = [d for d in drops if d[0] == 'полное']
        print(f"  ВЫБРОШЕНО не применив: полных {len(d_full)}"
              + (f" ({sum(d[2] for d in d_full) / 1024 / 1024:.2f} МБ впустую)" if d_full else "")
              + f", очисток буфера {len(drops) - len(d_full)}")
    if overflow:
        print(f"  переполнений буфера состояний: {overflow}")

    if resets:
        deleted = [d for _, _, d in resets]
        print(f"\n  PartialStateReset: {len(resets)} раз, удалено сущностей "
              f"всего {sum(deleted)}, максимум за раз {max(deleted)}")
        for tick, inst, dele in resets:
            if dele:
                print(f"    тик {tick}: в состоянии {inst}, УДАЛЕНО {dele}")

    if missing:
        print(f"\n  «нет метаданных»: {len(missing)}")
        why = Counter()
        for ent, life, a, b, real, proc, buf in missing:
            key = ('снесена PartialStateReset' if 'PartialStateReset' in life
                   else 'не было никогда' if 'НИКОГДА' in life
                   else life.split(' на тике')[0])
            why[key] += 1
        for k, v in why.most_common():
            print(f"    {v:5d}  {k}")
        print("\n  первые десять:")
        for ent, life, a, b, real, proc, buf in missing[:10]:
            print(f"    {ent}: {life}; состояние {a}→{b}, LastRealTick {real}, буфер {buf}")

    if len(sys.argv) > 2:
        sv = read(sys.argv[2])
        res = [SV_RESYNC.search(l) for l in sv]
        res = [m for m in res if m]
        fulls = [SV_FULL.search(l) for l in sv]
        fulls = [m for m in fulls if m]
        rates = [SV_RATE.search(l) for l in sv]
        rates = [m for m in rates if m]

        print(f"\n=== сервер: {sys.argv[2]} ===")
        print(f"  строк о ресинке: {len(res)}")
        if fulls:
            sizes = sorted(int(m[2]) for m in fulls)
            frags = sorted(int(m[4]) for m in fulls)
            print(f"  полных состояний отправлено: {len(fulls)}; "
                  f"медиана {sizes[len(sizes)//2]} Б, максимум {sizes[-1]} Б; "
                  f"фрагментов медиана {frags[len(frags)//2]}, максимум {frags[-1]} "
                  f"(надёжное окно Lidgren — 64 за круг RTT)")
        if rates:
            byts = sorted(int(m[3]) for m in rates)
            rel = sorted(int(m[5]) for m in rates)
            lag = sorted(int(m[8]) for m in rates)
            n = len(rates)
            print(f"  поток к клиенту, за секунду: медиана {byts[n//2]/1024:.1f} КБ, "
                  f"p90 {byts[int(n*0.9)]/1024:.1f} КБ, максимум {byts[-1]/1024:.1f} КБ")
            print(f"  из них надёжным каналом:     медиана {rel[n//2]/1024:.1f} КБ, "
                  f"p90 {rel[int(n*0.9)]/1024:.1f} КБ")
            print(f"  отставание ack, тиков:       медиана {lag[n//2]}, "
                  f"p90 {lag[int(n*0.9)]}, максимум {lag[-1]}")
        if res:
            ents = Counter(m[5] for m in res if m[5])
            print("  чаще всего теряется:")
            for e, c in ents.most_common(8):
                print(f"    {e:12s} {c}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
