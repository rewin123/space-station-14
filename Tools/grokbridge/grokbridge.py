#!/usr/bin/env python3
"""
Мост между игровым сервером и подпиской Grok.

ЗАЧЕМ ОН ВООБЩЕ НУЖЕН
---------------------
Профили моделей форка ходят одним способом: `POST {endpoint}/chat/completions`, `stream: false`,
ключ в заголовке Authorization (Llm/LlamaClient.cs). Подписка Grok так не отвечает:

  1. у неё свой адрес — `https://cli-chat-proxy.grok.com/v1`, и он требует двух заголовков
     сверх обычного: `X-XAI-Token-Auth: xai-grok-cli` и `x-grok-model-override: <модель>`
     (маршрутизация идёт ПО ЗАГОЛОВКУ, а не по полю model в теле);
  2. почти все модели за этим адресом отвечают только потоком, а наш клиент потока не умеет;
  3. вместо ключа там OIDC-токен на шесть часов, который надо обновлять.

Каждый из трёх пунктов можно было бы внести в игровой сервер — и это было бы хуже. Обновление
токена в игровом процессе означает, что владельцем OAuth-кредов становится он, а значит любой
`grok` в терминале отзывает токен посреди раунда. Потоковая сборка в C# — это ещё один парсер в
том же процессе, где уже живёт петля хода. А заголовки маршрутизации превратились бы в новый
диалект ради одного провайдера.

Поэтому мост: отдельный процесс, единственный владелец сессии, снаружи — обычный
OpenAI-совместимый эндпоинт, для которого профиль выглядит ровно как любой другой.

ЧТО ЭТО НЕ ЕСТЬ
---------------
Это НЕ CLIProxyAPI, о котором говорит README форка. Тот мост на этой машине не установлен
(каталога ~/.cli-proxy-api/ нет, порт 8317 не слушает никто), и профиль `codex` до сих пор
указывает в пустоту. Наш мост занимается только Grok и слушает 8318, чтобы не занимать чужой
порт: если CLIProxyAPI однажды появится ради ChatGPT, они разойдутся без конфликта.

ЗАЩИТА ДОСТУПА
--------------
Loopback на этой машине НЕ приватен — у неё несколько пользователей (см. историю с ANSYS
Fluent на тех же картах). Поэтому мост требует свой bearer-ключ из ai_data/grok_bridge.key и
без файла ключа не стартует вовсе: тихо открытый мост означает чужой расход недельной квоты.
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

# Версия клиента, которой мост представляется вышестоящему.
#
# Не украшение: cli-chat-proxy отвечает 426 «Your Grok CLI version (none) is outdated» на запрос
# без этого заголовка и отказывается работать вовсе. Имена заголовков взяты из самого бинарника
# CLI (`strings ~/.grok/downloads/grok-linux-x86_64`), потому что в его README их нет — там
# описаны только Authorization, X-XAI-Token-Auth и x-grok-model-override, и по этому описанию
# запрос не проходит.
#
# Значение берём из установленного CLI, а не зашиваем: порог версии вендор поднимает, и
# захардкоженное число однажды превратится в 426 посреди раунда. Обновился `grok` — обновился и
# мост, без правки кода.
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

# Поля, которые уходят вверх. Список белый, а не чёрный, и это осознанно: наш клиент шлёт
# `cache_prompt` и `id_slot` для llama.cpp, а строгая API отвечает на незнакомое поле 400 —
# и «провайдер лежит» становится не отличить от «провайдер не понял четвёртое поле».
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
    """Разговор с cli-chat-proxy: заголовки, токен и одна попытка починиться."""

    def __init__(self, base: str, source: TokenSource, log):
        self.base = base.rstrip("/")
        self.source = source
        self.log = log

        # Поддерживает ли вышестоящий `stream_options.include_usage`, неизвестно, а без него в
        # ответе не будет `usage` и счётчик расхода покажет нули. Спрашиваем — и при первом же
        # отказе перестаём спрашивать навсегда: лучше слепой счётчик, чем мёртвый профиль.
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
            # Говорит middleware вендора, что это сессионный токен CLI, а не ключ из console.x.ai.
            "X-XAI-Token-Auth": CLIENT_IDENTIFIER,
            # Маршрут выбирается ЭТИМ заголовком, а не полем model в теле.
            "x-grok-model-override": model,
            # Без этих двух — 426 и отказ обслуживать. См. комментарий у client_version().
            "x-grok-client-identifier": CLIENT_IDENTIFIER,
            "x-grok-client-version": self.client_version,
            "User-Agent": USER_AGENT,
        }

    def open_stream(self, payload: dict, timeout: float):
        """
        Отправить запрос и вернуть открытый поток ответа.

        Две попытки, и обе — про разные поломки. 401 при формально живом токене означает, что
        сессию отозвали на той стороне: обновляемся принудительно и повторяем. 400 с упоминанием
        stream_options означает, что вендор такого поля не знает: выкидываем его и повторяем,
        уже навсегда.
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


# ----------------------------------------------------------------- сборка из потока

def assemble(lines, model: str) -> dict:
    """
    Собрать один обычный ответ chat.completion из потока SSE.

    Отдельная функция без единого обращения к сети — потому что это единственное место, где мост
    может соврать незаметно. Оборванный аргумент вызова инструмента не выглядит ошибкой: он
    выглядит как решение агента, и разбирать такое по журналам раунда — самая дорогая отладка,
    какая бывает. Проверяется в test_assemble.py на записанных потоках.

    `lines` — итератор байтовых строк, как их отдаёт HTTPResponse.
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
            # Битый кадр молча пропускать нельзя, но и ронять из-за него весь ход — тоже:
            # остальные кадры целы, а ответ без одного токена лучше отсутствующего ответа.
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

            # Рассуждения приходят отдельным полем и в тело ответа НЕ попадают: наш клиент
            # читает только content, а склейка размышлений с ответом превратила бы внутренний
            # монолог в реплику агента в игре.
            if delta.get("reasoning_content"):
                reasoning.append(delta["reasoning_content"])

            for call in delta.get("tool_calls") or []:
                # Индекс — штатный способ различить параллельные вызовы. Его может не быть у
                # провайдеров, которые шлют вызов целиком; тогда ключом служит id, а в самом
                # безнадёжном случае — порядковый номер.
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
                    # Имя приходит одним куском у всех известных провайдеров, но склейка кусков
                    # ничего не стоит; проверка на повтор защищает от тех, кто шлёт имя в каждом
                    # кадре — иначе получилось бы «movemovemove».
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

    # Не для игры, а для журнала: длину размышлений видно только отсюда, и без неё «выдал 300
    # токенов» читается как многословный ответ, хотя это могли быть 215 токенов раздумий.
    if reasoning:
        result["_bridge_reasoning_chars"] = sum(len(part) for part in reasoning)

    return result


# ------------------------------------------------------------------------- сервер

class Handler(BaseHTTPRequestHandler):
    server_version = "GrokBridge/1"
    protocol_version = "HTTP/1.1"

    # Поднимается извне при создании сервера.
    bridge: "Bridge" = None  # type: ignore[assignment]

    def log_message(self, fmt, *args):
        # Стандартный лог http.server пишет в stderr строкой без времени; журнал systemd своё
        # время проставит, а вот молчать про запросы нельзя — это единственный след обращения.
        self.bridge.log(f"{self.address_string()} {fmt % args}")

    # ---------------------------------------------------------------- вспомогательное

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
        # Сравнение постоянного времени — привычка, а не необходимость: ключ локальный, но
        # написать здесь `==` значит оставить читателю вопрос, подумали ли об этом.
        return len(given) == len(expected) and sum(a != b for a, b in zip(given, expected)) == 0

    # ------------------------------------------------------------------- маршруты

    def do_GET(self):
        path = self.path.split("?", 1)[0].rstrip("/") or "/"

        if path == "/health":
            # Без ключа: это проба живости, и секретов в ответе нет. Она обязана работать у
            # того, кто чинит сервер в три часа ночи, не зная, где лежит ключ.
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
            # Тело вышестоящего отдаётся как есть: в нём настоящая причина (исчерпана квота,
            # неизвестная модель, слишком длинный контекст), и без неё все отказы на одно лицо.
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
                # Прозрачный проброс — для curl и отладки. Игровой сервер сюда не попадает:
                # LlamaClient всегда шлёт stream: false.
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
        Ключ обязателен, и его отсутствие — отказ стартовать, а не открытый мост.

        Loopback здесь общий с другими пользователями машины. Мост без ключа — это чужой доступ
        к недельной квоте подписки, причём заметный только по счётчику расхода.
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
    # Ход агента длится минутами; сокет, закрытый по короткому таймауту, выглядит в игре как
    # «модель не ответила», хотя она отвечала.
    server.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

    bridge.log(f"мост слушает {args.bind}, вышестоящий {args.upstream}, версия клиента {bridge.upstream.client_version}")
    try:
        status = bridge.source.status()
        bridge.log(f"сессия действует до {status['expires_at']} (refresh: {status['has_refresh_token']})")
    except (AuthError, OSError, ValueError) as error:
        # Не отказ стартовать: сессию можно активировать и после запуска, а мост, падающий из-за
        # протухшего файла, уносит с собой и /health, по которому это видно.
        bridge.log(f"ВНИМАНИЕ: сессия недоступна ({error}) — выполните python3 grokauth.py login")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
