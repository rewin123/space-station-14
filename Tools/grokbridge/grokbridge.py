#!/usr/bin/env python3
"""
Bridge between the game server and the Grok subscription.

WHY IT EXISTS AT ALL
---------------------
The fork's model profiles all speak one way: `POST {endpoint}/chat/completions`, `stream:
false`, key in the Authorization header (Llm/LlamaClient.cs). The Grok subscription does not
answer like that:

  1. it has its own address — `https://cli-chat-proxy.grok.com/v1`, and it requires two
     headers beyond the usual: `X-XAI-Token-Auth: xai-grok-cli` and
     `x-grok-model-override: <model>` (routing is done BY HEADER, not by the model field in
     the body);
  2. almost every model behind this address only responds by streaming, and our client
     doesn't know how to stream;
  3. instead of a key there's an OIDC token good for six hours, which has to be refreshed.

Each of these three points could be brought into the game server — and that would be worse.
Refreshing the token inside the game process means the game process becomes the owner of the
OAuth credentials, so any `grok` in a terminal revokes the token mid-round. Assembling a
stream in C# is one more parser in the same process that already runs the turn loop. And the
routing headers would turn into a new dialect for the sake of a single provider.

Hence the bridge: a separate process, the sole owner of the session, presenting to the
outside as an ordinary OpenAI-compatible endpoint, so the profile looks exactly like any
other.

WHAT THIS IS NOT
---------------
This is NOT CLIProxyAPI, which the fork's README talks about. That bridge is not installed
on this machine (no ~/.cli-proxy-api/ directory, nothing listening on port 8317), and the
`codex` profile still points into the void. Our bridge handles only Grok and listens on
8318, so as not to take a port that belongs to someone else: if CLIProxyAPI ever shows up
for ChatGPT, the two will coexist without conflict.

ACCESS PROTECTION
--------------
Loopback on this machine is NOT private — it has several users (see the history with ANSYS
Fluent on the same cards). So the bridge requires its own bearer key from
ai_data/grok_bridge.key and refuses to start at all without the key file: a quietly open
bridge means someone else spending the weekly quota.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from grokauth import AuthError, TokenSource, default_session_path  # noqa: E402

UPSTREAM = "https://cli-chat-proxy.grok.com/v1"
USER_AGENT = "ss14-grok-bridge/1"

# Client version the bridge presents itself as to the upstream.
#
# Not decoration: cli-chat-proxy answers 426 "Your Grok CLI version (none) is outdated" to a
# request without this header and refuses to work at all. The header names were taken from
# the CLI binary itself (`strings ~/.grok/downloads/grok-linux-x86_64`), because its README
# doesn't have them — it only documents Authorization, X-XAI-Token-Auth and
# x-grok-model-override, and a request built from that description does not get through.
#
# We take the value from the installed CLI instead of hardcoding it: the vendor raises the
# version threshold over time, and a hardcoded number would eventually turn into a 426
# mid-round. `grok` gets updated, so the bridge gets updated too, with no code change.
CLIENT_IDENTIFIER = "xai-grok-cli"
FALLBACK_CLIENT_VERSION = "1.0.5"
GROK_VERSION_FILE = os.path.expanduser("~/.grok/version.json")


def client_version() -> str:
    override = os.environ.get("GROK_BRIDGE_CLIENT_VERSION", "").strip()
    if override:
        return override

    try:
        with open(GROK_VERSION_FILE, encoding="utf-8") as handle:
            version = json.load(handle).get("version")
        if version:
            return str(version)
    except (OSError, ValueError):
        pass

    return FALLBACK_CLIENT_VERSION

# Fields that get forwarded upstream. This is a whitelist, not a blacklist, and deliberately
# so: our client sends `cache_prompt` and `id_slot` for llama.cpp, and a strict API answers
# 400 to an unknown field — making "provider is down" indistinguishable from "provider didn't
# understand the fourth field".
FORWARDED_FIELDS = frozenset(
    {
        "model",
        "messages",
        "tools",
        "tool_choice",
        "parallel_tool_calls",
        "temperature",
        "top_p",
        "max_tokens",
        "stop",
        "seed",
        "presence_penalty",
        "frequency_penalty",
        "response_format",
        "reasoning_effort",
        "user",
    }
)


class Upstream:
    """Talking to cli-chat-proxy: headers, the token, and one attempt to self-heal."""

    def __init__(self, base: str, source: TokenSource, log):
        self.base = base.rstrip("/")
        self.source = source
        self.log = log

        # Whether the upstream supports `stream_options.include_usage` is unknown, and without
        # it the response won't have `usage` and the spend counter will show zeros. We ask —
        # and on the first refusal we stop asking forever: a blind counter beats a dead
        # profile.
        self.include_usage = os.environ.get("GROK_BRIDGE_USAGE", "1") != "0"

        self.client_version = client_version()

        proxy = os.environ.get("GROK_BRIDGE_PROXY", "").strip()
        handler = urllib.request.ProxyHandler({"http": proxy, "https": proxy} if proxy else {})
        self.opener = urllib.request.build_opener(handler)

    def headers(self, model: str, token: str) -> dict[str, str]:
        return {
            "Content-Type": "application/json",
            "Accept": "text/event-stream",
            "Authorization": f"Bearer {token}",
            # Tells the vendor's middleware this is a CLI session token, not a console.x.ai key.
            "X-XAI-Token-Auth": CLIENT_IDENTIFIER,
            # The route is chosen by THIS header, not by the model field in the body.
            "x-grok-model-override": model,
            # Without these two: 426 and a refusal to serve. See the comment on client_version().
            "x-grok-client-identifier": CLIENT_IDENTIFIER,
            "x-grok-client-version": self.client_version,
            "User-Agent": USER_AGENT,
        }

    def open_stream(self, payload: dict, timeout: float):
        """
        Send the request and return the open response stream.

        Two attempts, each for a different kind of failure. A 401 with a token that's, on
        paper, still alive means the session was revoked on the other end: force a refresh
        and retry. A 400 mentioning stream_options means the vendor doesn't know that field:
        drop it and retry, permanently this time.
        """
        model = payload.get("model") or ""

        for attempt in (1, 2):
            body = dict(payload)
            body["stream"] = True
            if self.include_usage:
                body["stream_options"] = {"include_usage": True}

            token = self.source.token()
            request = urllib.request.Request(
                f"{self.base}/chat/completions",
                data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                method="POST",
                headers=self.headers(model, token),
            )

            try:
                return self.opener.open(request, timeout=timeout)
            except urllib.error.HTTPError as error:
                raw = error.read().decode("utf-8", "replace")

                if attempt == 1 and error.code == 401:
                    self.log("вышестоящий ответил 401 — обновляю сессию принудительно")
                    try:
                        self.source.force()
                        continue
                    except AuthError as auth_error:
                        raise UpstreamError(401, f"сессия недействительна: {auth_error}", None) from None

                if attempt == 1 and error.code == 400 and "stream_options" in raw and self.include_usage:
                    self.log("вышестоящий не знает stream_options — расход токенов будет неизвестен")
                    self.include_usage = False
                    continue

                raise UpstreamError(error.code, raw, error.headers.get("Retry-After")) from None

    def models(self, timeout: float = 30.0) -> tuple[int, bytes]:
        token = self.source.token()
        request = urllib.request.Request(
            f"{self.base}/models",
            headers={
                "Accept": "application/json",
                "Authorization": f"Bearer {token}",
                "X-XAI-Token-Auth": CLIENT_IDENTIFIER,
                "x-grok-client-identifier": CLIENT_IDENTIFIER,
                "x-grok-client-version": self.client_version,
                "User-Agent": USER_AGENT,
            },
        )
        try:
            with self.opener.open(request, timeout=timeout) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.read()


class UpstreamError(Exception):
    def __init__(self, status: int, body: str, retry_after: str | None):
        super().__init__(f"HTTP {status}: {body[:300]}")
        self.status = status
        self.body = body
        self.retry_after = retry_after


# ----------------------------------------------------------------- assembly from the stream

def assemble(lines, model: str) -> dict:
    """
    Assemble one ordinary chat.completion response out of an SSE stream.

    A separate function with not a single network call — because this is the one place where
    the bridge could lie without anyone noticing. A truncated tool-call argument doesn't look
    like an error: it looks like the agent's decision, and untangling that from round logs is
    the most expensive kind of debugging there is. Covered in test_assemble.py against
    recorded streams.

    `lines` is an iterator of byte strings, exactly as HTTPResponse yields them.
    """
    content: list[str] = []
    reasoning: list[str] = []
    calls: dict[object, dict] = {}
    order: list[object] = []
    finish_reason = None
    usage = None
    response_id = ""
    created = int(time.time())

    for raw in lines:
        line = raw.decode("utf-8", "replace").strip() if isinstance(raw, (bytes, bytearray)) else raw.strip()
        if not line or line.startswith(":"):
            continue
        if not line.startswith("data:"):
            continue

        data = line[5:].strip()
        if data == "[DONE]":
            break

        try:
            chunk = json.loads(data)
        except ValueError:
            # A corrupt frame can't be silently skipped, but crashing the whole turn over it
            # isn't right either: the other frames are intact, and a response missing one
            # token beats no response at all.
            continue

        response_id = chunk.get("id") or response_id
        created = chunk.get("created") or created
        if chunk.get("usage"):
            usage = chunk["usage"]

        for choice in chunk.get("choices") or []:
            if choice.get("finish_reason"):
                finish_reason = choice["finish_reason"]

            delta = choice.get("delta") or choice.get("message") or {}

            if delta.get("content"):
                content.append(delta["content"])

            # Reasoning arrives as a separate field and does NOT go into the response body:
            # our client only reads content, and merging the reasoning into the reply would
            # turn the agent's internal monologue into an in-game line.
            if delta.get("reasoning_content"):
                reasoning.append(delta["reasoning_content"])

            for call in delta.get("tool_calls") or []:
                # Index is the standard way to tell parallel calls apart. Providers that send
                # a call whole may not have one; then id serves as the key, and in the most
                # hopeless case, a sequential number.
                key = call.get("index")
                if key is None:
                    key = call.get("id") or f"#{len(order)}"

                slot = calls.get(key)
                if slot is None:
                    slot = {"id": "", "name": "", "arguments": []}
                    calls[key] = slot
                    order.append(key)

                if call.get("id"):
                    slot["id"] = call["id"]

                function = call.get("function") or {}
                name = function.get("name")
                if name and not slot["name"].endswith(name):
                    # The name arrives in one piece with every known provider, but
                    # concatenating pieces costs nothing; the repeat check guards against
                    # providers that send the name in every frame — otherwise we'd get
                    # "movemovemove".
                    slot["name"] += name

                if function.get("arguments"):
                    slot["arguments"].append(function["arguments"])

    tool_calls = []
    for key in order:
        slot = calls[key]
        tool_calls.append(
            {
                "id": slot["id"] or f"call_{len(tool_calls)}",
                "type": "function",
                "function": {
                    "name": slot["name"],
                    "arguments": "".join(slot["arguments"]) or "{}",
                },
            }
        )

    message = {"role": "assistant", "content": "".join(content)}
    if tool_calls:
        message["tool_calls"] = tool_calls

    if finish_reason is None:
        finish_reason = "tool_calls" if tool_calls else "stop"

    result = {
        "id": response_id or "chatcmpl-bridge",
        "object": "chat.completion",
        "created": created,
        "model": model,
        "choices": [{"index": 0, "finish_reason": finish_reason, "message": message}],
    }

    if usage:
        result["usage"] = usage

    # Not for the game, only for the log: the length of the reasoning is visible only from
    # here, and without it "produced 300 tokens" reads as a verbose reply, when it could have
    # been 215 tokens of thinking.
    if reasoning:
        result["_bridge_reasoning_chars"] = sum(len(part) for part in reasoning)

    return result


# ------------------------------------------------------------------------- server

class Handler(BaseHTTPRequestHandler):
    server_version = "GrokBridge/1"
    protocol_version = "HTTP/1.1"

    # Set from outside when the server is created.
    bridge: "Bridge" = None  # type: ignore[assignment]

    def log_message(self, fmt, *args):
        # The standard http.server log writes to stderr as a line with no timestamp; the
        # systemd journal will stamp its own time, but staying silent about requests isn't an
        # option — this is the only trace a request leaves.
        self.bridge.log(f"{self.address_string()} {fmt % args}")

    # ---------------------------------------------------------------- helpers

    def _send(self, status: int, payload: bytes, content_type="application/json", extra=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        for name, value in (extra or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(payload)

    def _error(self, status: int, message: str, extra=None):
        body = json.dumps({"error": {"message": message, "type": "grok_bridge"}}, ensure_ascii=False)
        self._send(status, body.encode("utf-8"), extra=extra)

    def _authorized(self) -> bool:
        expected = self.bridge.local_key
        header = self.headers.get("Authorization", "")
        given = header[7:].strip() if header.lower().startswith("bearer ") else ""
        # Constant-time comparison is a habit rather than a necessity: the key is local, but
        # writing `==` here would leave the reader wondering whether we'd thought about it.
        return len(given) == len(expected) and sum(a != b for a, b in zip(given, expected)) == 0

    # ------------------------------------------------------------------- routes

    def do_GET(self):
        path = self.path.split("?", 1)[0].rstrip("/") or "/"

        if path == "/health":
            # No key required: this is a liveness probe and the response holds no secrets. It
            # must work for whoever is fixing the server at three in the morning without
            # knowing where the key lives.
            try:
                status = self.bridge.source.status()
                ok = status["expires_in_seconds"] > 0 or status["has_refresh_token"]
            except (AuthError, OSError, ValueError) as error:
                self._send(503, json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False).encode())
                return

            body = {"ok": ok, "upstream": self.bridge.upstream.base, "session": status}
            self._send(200 if ok else 503, json.dumps(body, ensure_ascii=False, indent=2).encode())
            return

        if path in ("/v1/models", "/models"):
            if not self._authorized():
                self._error(401, "нужен Authorization: Bearer <ai_data/grok_bridge.key>")
                return
            try:
                status, payload = self.bridge.upstream.models()
            except (AuthError, urllib.error.URLError, OSError) as error:
                self._error(502, f"вышестоящий недоступен: {error}")
                return
            self._send(status, payload)
            return

        self._error(404, f"нет маршрута {path}")

    def do_POST(self):
        path = self.path.split("?", 1)[0].rstrip("/")

        if path not in ("/v1/chat/completions", "/chat/completions"):
            self._error(404, f"нет маршрута {path}")
            return

        if not self._authorized():
            self._error(401, "нужен Authorization: Bearer <ai_data/grok_bridge.key>")
            return

        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""

        try:
            request = json.loads(raw.decode("utf-8"))
        except ValueError as error:
            self._error(400, f"тело не разбирается как JSON: {error}")
            return

        wants_stream = bool(request.get("stream"))
        payload = {k: v for k, v in request.items() if k in FORWARDED_FIELDS}

        dropped = sorted(set(request) - FORWARDED_FIELDS - {"stream", "stream_options"})
        if dropped:
            self.bridge.log(f"поля не переданы вверх (не в белом списке): {', '.join(dropped)}")

        if not payload.get("model"):
            self._error(400, "в теле нет поля model")
            return

        started = time.monotonic()

        try:
            response = self.bridge.upstream.open_stream(payload, timeout=self.bridge.timeout)
        except UpstreamError as error:
            extra = {"Retry-After": error.retry_after} if error.retry_after else None
            # The upstream body is passed through as-is: it holds the real reason (quota
            # exhausted, unknown model, context too long), and without it every failure looks
            # the same.
            self._send(error.status, error.body.encode("utf-8", "replace"), extra=extra)
            return
        except AuthError as error:
            self._error(401, f"сессия недоступна: {error}")
            return
        except (urllib.error.URLError, OSError) as error:
            self._error(502, f"вышестоящий недоступен: {error}")
            return

        with response:
            if wants_stream:
                # Transparent passthrough — for curl and debugging. The game server never
                # reaches this branch: LlamaClient always sends stream: false.
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("Connection", "close")
                self.close_connection = True
                self.end_headers()
                for line in response:
                    self.wfile.write(line)
                return

            try:
                result = assemble(response, payload["model"])
            except (urllib.error.URLError, OSError) as error:
                self._error(502, f"поток оборвался: {error}")
                return

        took = time.monotonic() - started
        calls = result["choices"][0]["message"].get("tool_calls") or []
        self.bridge.log(
            f"{payload['model']}: {took:.1f}с, {len(result['choices'][0]['message']['content'])} симв., "
            f"вызовов {len(calls)}, finish={result['choices'][0]['finish_reason']}"
        )

        self._send(200, json.dumps(result, ensure_ascii=False).encode("utf-8"))


class Bridge:
    def __init__(self, session_path: str, key_path: str, upstream: str, timeout: float):
        self.source = TokenSource(session_path)
        self.local_key = self._read_key(key_path)
        self.upstream = Upstream(upstream, self.source, self.log)
        self.timeout = timeout
        self._log_lock = threading.Lock()

    @staticmethod
    def _read_key(path: str) -> str:
        """
        The key is mandatory, and its absence means refusing to start, not an open bridge.

        Loopback here is shared with other users of the machine. A bridge without a key is
        someone else's access to the weekly subscription quota, noticeable only via the spend
        counter.
        """
        try:
            with open(path, encoding="utf-8") as handle:
                key = handle.read().strip()
        except OSError as error:
            raise SystemExit(
                f"нет файла ключа {path}: {error}\n"
                f"создайте его: umask 077 && head -c 32 /dev/urandom | base64 > {path}\n"
                "и положите то же значение в keyFile профилей grok/grok46"
            ) from None

        if len(key) < 16:
            raise SystemExit(f"ключ в {path} короче 16 символов — так нельзя")

        mode = os.stat(path).st_mode & 0o777
        if mode & 0o077:
            raise SystemExit(f"права на {path} — {mode:o}, должно быть 600: chmod 600 {path}")

        return key

    def log(self, message: str) -> None:
        with self._log_lock:
            print(message, file=sys.stderr, flush=True)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="OpenAI-совместимый мост к подписке Grok.")
    parser.add_argument("--bind", default=os.environ.get("GROK_BRIDGE_BIND", "127.0.0.1:8318"))
    parser.add_argument("--session", default=default_session_path())
    parser.add_argument(
        "--key-file",
        default=os.environ.get(
            "GROK_BRIDGE_KEY",
            os.path.join(os.path.dirname(default_session_path()), "grok_bridge.key"),
        ),
    )
    parser.add_argument("--upstream", default=os.environ.get("GROK_BRIDGE_UPSTREAM", UPSTREAM))
    parser.add_argument("--timeout", type=float, default=float(os.environ.get("GROK_BRIDGE_TIMEOUT", "300")))
    args = parser.parse_args(argv)

    host, _, port = args.bind.rpartition(":")
    bridge = Bridge(args.session, args.key_file, args.upstream, args.timeout)

    Handler.bridge = bridge

    server = ThreadingHTTPServer((host or "127.0.0.1", int(port)), Handler)
    server.daemon_threads = True
    # An agent turn takes minutes; a socket closed by a short timeout looks in-game like "the
    # model didn't answer", even though it did.
    server.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

    bridge.log(f"мост слушает {args.bind}, вышестоящий {args.upstream}, версия клиента {bridge.upstream.client_version}")
    try:
        status = bridge.source.status()
        bridge.log(f"сессия действует до {status['expires_at']} (refresh: {status['has_refresh_token']})")
    except (AuthError, OSError, ValueError) as error:
        # Not a reason to refuse to start: the session can be activated after startup too, and
        # a bridge that crashes over a stale file takes down /health with it, which is where
        # this would be visible.
        bridge.log(f"ВНИМАНИЕ: сессия недоступна ({error}) — выполните python3 grokauth.py login")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
