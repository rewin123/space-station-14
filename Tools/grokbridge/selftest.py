#!/usr/bin/env python3
"""
Check of the LIVE bridge: things that can't be verified without a session.

The split from test_assemble.py isn't a formality. There, it's stream assembly, checked
against recorded frames, and it must pass green always, whether in CI or offline. Here it's
four questions whose answers only the vendor knows, and each of them has changed at least
once before:

  1. does cli-chat-proxy know the requested model (grok-4.6 might not have existed);
  2. does it survive our set of fields (tools, parallel_tool_calls, temperature);
  3. does it return a tool call in the shape LlamaClient expects;
  4. does it report usage — without it `aiagent cost` shows zeros, and there is no way to
     know where the weekly quota went.

Run: python3 selftest.py [--model grok-4.6]
Spends subscription quota: two short turns.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_BASE = "http://127.0.0.1:8318/v1"


def call(base: str, key: str, payload: dict, timeout: float = 300.0):
    request = urllib.request.Request(
        f"{base.rstrip('/')}/chat/completions",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        method="POST",
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}"},
    )
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=timeout) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default=os.environ.get("GROK_BRIDGE_BASE", DEFAULT_BASE))
    parser.add_argument("--model", default="grok-4.6")
    parser.add_argument(
        "--key-file",
        default=os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "..", "..", "ai_data", "grok_bridge.key"
        ),
    )
    args = parser.parse_args(argv)

    with open(args.key_file, encoding="utf-8") as handle:
        key = handle.read().strip()

    failures = 0

    # --- 1. health and session lifetime -----------------------------------------------------
    health_url = args.base.rstrip("/").removesuffix("/v1") + "/health"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(health_url, timeout=15) as response:
            health = json.loads(response.read().decode("utf-8"))
        left = health["session"]["expires_in_seconds"]
        print(f"[1/4] мост жив, сессия ещё {left // 60} мин, refresh: {health['session']['has_refresh_token']}")
        if not health["session"]["has_refresh_token"]:
            print("      ВНИМАНИЕ: без refresh_token сессия не переживёт ночь")
            failures += 1
    except (urllib.error.URLError, OSError, KeyError, ValueError) as error:
        print(f"[1/4] ПРОВАЛ: {health_url} не отвечает ({error})")
        return 1

    # --- 2. simple turn ----------------------------------------------------------------------
    status, body = call(
        args.base,
        key,
        {
            "model": args.model,
            "messages": [{"role": "user", "content": "Ответь ровно одним словом: работает"}],
            "temperature": 0.2,
            "max_tokens": 2048,
            "stream": False,
        },
    )
    if status != 200:
        print(f"[2/4] ПРОВАЛ: HTTP {status}: {str(body)[:400]}")
        return 1

    text = body["choices"][0]["message"]["content"]
    print(f"[2/4] простой ход: {status}, ответ {text[:60]!r}, finish={body['choices'][0]['finish_reason']}")
    if not text.strip():
        print("      ВНИМАНИЕ: пустой content — модель могла потратить весь бюджет на размышления")
        failures += 1

    # --- 3. usage ----------------------------------------------------------------------------
    usage = body.get("usage")
    if usage:
        print(f"[3/4] расход: промпт {usage.get('prompt_tokens')}, выдача {usage.get('completion_tokens')}")
    else:
        print("[3/4] ВНИМАНИЕ: вендор не прислал usage — `aiagent cost` покажет нули")
        failures += 1

    # --- 4. tool call ------------------------------------------------------------------------
    status, body = call(
        args.base,
        key,
        {
            "model": args.model,
            "messages": [
                {"role": "system", "content": "Ты робот. Отвечай только вызовом инструмента."},
                {"role": "user", "content": "Иди к двери door-11."},
            ],
            "tools": [
                {
                    "type": "function",
                    "function": {
                        "name": "goto",
                        "description": "Идти к объекту по его хендлу.",
                        "parameters": {
                            "type": "object",
                            "properties": {"handle": {"type": "string"}},
                            "required": ["handle"],
                        },
                    },
                }
            ],
            "tool_choice": "auto",
            "parallel_tool_calls": False,
            "max_tokens": 2048,
            "stream": False,
        },
    )
    if status != 200:
        print(f"[4/4] ПРОВАЛ: HTTP {status}: {str(body)[:400]}")
        return 1

    calls = body["choices"][0]["message"].get("tool_calls") or []
    if not calls:
        print("[4/4] ПРОВАЛ: модель не вызвала инструмент — агент так работать не сможет")
        return 1

    name = calls[0]["function"]["name"]
    raw_args = calls[0]["function"]["arguments"]
    print(f"[4/4] вызов инструмента: {name}({raw_args})")

    try:
        parsed = json.loads(raw_args)
    except ValueError:
        print("      ПРОВАЛ: аргументы не разбираются как JSON — склейка потока сломана")
        return 1

    if parsed.get("handle") != "door-11":
        print(f"      ВНИМАНИЕ: ожидался handle=door-11, пришло {parsed}")
        failures += 1

    print("")
    print("итог: " + ("всё в порядке" if failures == 0 else f"замечаний: {failures}"))
    return 0 if failures == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
