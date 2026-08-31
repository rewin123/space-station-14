#!/usr/bin/env python3
"""Ретранслятор UDP с задержкой: между клиентом и сервером SS14.

Зачем он есть. Петля ресинков PVS видна только на длинном канале, а на этой машине клиент и
сервер стоят рядом. Три обычных способа добавить задержку не годятся:

* ``tc qdisc ... netem`` требует CAP_NET_ADMIN, а в песочнице нет root;
* ``net.fakelagmin`` (симуляция в Lidgren) живёт под ``#if DEBUG``, а собирать надо Release:
  только в Release определяется ``EXCEPTION_TOLERANCE``, без которого клиент на
  ``MissingMetadataException`` не просит полное состояние — то есть воспроизводится не то;
* ``tcpdump`` для съёма трафика — снова root.

Ретранслятор закрывает обе задачи разом: держит задержку И пишет каждую датаграмму в CSV,
то есть заменяет собой захват трафика.

    python3 Tools/laglink.py --listen 1213 --target 1212 --delay-ms 300 --csv /tmp/link.csv

Клиент подключается на ``--listen``, сервер слушает ``--target``. Задержка задаётся В ОДНУ
СТОРОНУ: ``--delay-ms 300`` даёт RTT 600 мс.
"""

import argparse
import asyncio
import random
import socket
import sys
import time


class Link:
    """Одна пара «адрес клиента ↔ свой сокет к серверу»."""

    def __init__(self, relay, addr):
        self.relay = relay
        self.addr = addr
        self.transport = None
        self.last_seen = time.monotonic()


class _Upstream(asyncio.DatagramProtocol):
    """Сокет в сторону сервера. Ответы уезжают обратно тому клиенту, чей это сокет."""

    def __init__(self, relay, link):
        self.relay = relay
        self.link = link

    def datagram_received(self, data, addr):
        self.relay.forward(data, to_client=True, link=self.link)


class Relay(asyncio.DatagramProtocol):
    def __init__(self, args):
        self.args = args
        self.transport = None
        self.links = {}
        self.csv = open(args.csv, "w", buffering=1) if args.csv else None
        if self.csv:
            self.csv.write("ts,dir,bytes\n")
        # Счётчики за окно отчёта: пакеты и байты в каждую сторону.
        self.stat = {"c2s": [0, 0], "s2c": [0, 0]}
        self.started = time.monotonic()

    # --- от клиента ---
    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        link = self.links.get(addr)
        if link is None:
            link = Link(self, addr)
            self.links[addr] = link
            asyncio.ensure_future(self._open_upstream(link))
            print(f"[laglink] новый клиент {addr[0]}:{addr[1]}", file=sys.stderr)
        link.last_seen = time.monotonic()
        self.forward(data, to_client=False, link=link)

    async def _open_upstream(self, link):
        loop = asyncio.get_running_loop()
        transport, _ = await loop.create_datagram_endpoint(
            lambda: _Upstream(self, link),
            remote_addr=(self.args.target_host, self.args.target),
            family=socket.AF_INET,
        )
        link.transport = transport

    # --- общая часть ---
    def forward(self, data, to_client, link):
        direction = "s2c" if to_client else "c2s"

        if self.args.loss and random.random() < self.args.loss:
            self._log(direction + "-drop", len(data))
            return

        delay = self.args.delay_ms / 1000.0
        if self.args.jitter_ms:
            delay += random.uniform(0, self.args.jitter_ms / 1000.0)

        loop = asyncio.get_running_loop()
        loop.call_later(delay, self._deliver, data, to_client, link, direction)

    def _deliver(self, data, to_client, link, direction):
        try:
            if to_client:
                self.transport.sendto(data, link.addr)
            else:
                # Сокет наверх мог ещё не открыться: первые датаграммы клиента приходят
                # раньше, чем create_datagram_endpoint успевает отработать.
                if link.transport is None:
                    loop = asyncio.get_running_loop()
                    loop.call_later(0.01, self._deliver, data, to_client, link, direction)
                    return
                link.transport.sendto(data)
        except OSError as e:  # сокет закрыт — клиент ушёл
            print(f"[laglink] {direction}: {e}", file=sys.stderr)
            return

        self._log(direction, len(data))

    def _log(self, direction, size):
        if self.csv:
            self.csv.write(f"{time.time():.6f},{direction},{size}\n")
        st = self.stat.get(direction)
        if st is not None:
            st[0] += 1
            st[1] += size

    async def report(self, period):
        while True:
            await asyncio.sleep(period)
            parts = []
            for d in ("c2s", "s2c"):
                pkts, byts = self.stat[d]
                self.stat[d] = [0, 0]
                parts.append(f"{d} {pkts:5d} пак {byts / period / 1024:8.1f} КБ/с")
            print(f"[laglink] {' | '.join(parts)}", file=sys.stderr)


async def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--listen", type=int, default=1213, help="порт для клиента")
    p.add_argument("--listen-host", default="127.0.0.1")
    p.add_argument("--target", type=int, default=1212, help="порт сервера")
    p.add_argument("--target-host", default="127.0.0.1")
    p.add_argument("--delay-ms", type=float, default=300.0, help="задержка В ОДНУ сторону")
    p.add_argument("--jitter-ms", type=float, default=0.0,
                   help="равномерный разброс поверх задержки; ненулевой переставляет пакеты")
    p.add_argument("--loss", type=float, default=0.0, help="доля потерь, 0..1")
    p.add_argument("--csv", default=None, help="куда писать журнал датаграмм")
    p.add_argument("--report", type=float, default=5.0, help="период сводки в секундах")
    args = p.parse_args()

    loop = asyncio.get_running_loop()
    relay = Relay(args)
    await loop.create_datagram_endpoint(
        lambda: relay, local_addr=(args.listen_host, args.listen), family=socket.AF_INET)

    print(f"[laglink] {args.listen_host}:{args.listen} -> {args.target_host}:{args.target}, "
          f"задержка {args.delay_ms} мс в одну сторону (RTT {args.delay_ms * 2:.0f} мс), "
          f"потери {args.loss * 100:.1f}%", file=sys.stderr)

    await relay.report(args.report)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
