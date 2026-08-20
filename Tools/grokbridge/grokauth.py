#!/usr/bin/env python3
"""
Активация устройством и хранение сессии xAI для моста.

ЗАЧЕМ ОТДЕЛЬНАЯ СЕССИЯ, А НЕ ЧУЖОЙ ТОКЕН ИЗ ~/.grok/auth.json
------------------------------------------------------------
У refresh-токена может быть ровно один владелец: он одноразовый и ротируется на стороне
вендора, поэтому второй потребитель, обновивший его, отзывает первого. Это не теория — в
~/.hermes/auth.json на этой машине до сих пор лежит `refresh_token_reused` от 07.08.2026.

Читать токен CLI на чтение и не обновлять его тоже нельзя: он живёт шесть часов, и раунд,
начатый в 02:50, кончился бы отказом провайдера в 02:54.

Активация устройством решает это без всяких договорённостей: игровой сервер проходит свой
собственный вход и получает СВОЙ refresh-токен. Дальше `grok` в терминале и мост живут
независимо — обновление одного не трогает второй.

ПОЧЕМУ ИМЕННО DEVICE FLOW, А НЕ БРАУЗЕР
---------------------------------------
Машина без графики и без доверенного браузера; вход по коду устройства (RFC 8628) не требует
ни того, ни другого — он показывает ссылку и код, а подтверждение делается с телефона.
`https://auth.x.ai/.well-known/openid-configuration` объявляет
`urn:ietf:params:oauth:grant-type:device_code` и `token_endpoint_auth_methods_supported: none`,
то есть публичный клиент без секрета — ровно наш случай.

ГДЕ ЛЕЖИТ РЕЗУЛЬТАТ
-------------------
`ai_data/grok.session.json`, права 600. Каталог gitignored — это единственное место, где
секретам разрешено находиться. В Resources/ их быть не может: ContentMagicAczProvider раздаёт
всю папку каждому подключившемуся игроку.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

ISSUER = "https://auth.x.ai"

# Публичный идентификатор клиента Grok CLI. Не секрет: у публичного клиента его и не бывает,
# вход подтверждается человеком в браузере, а не знанием этой строки.
CLIENT_ID = "b1a00492-073a-47ea-816f-4c329264a828"

# `offline_access` — это и есть право на refresh; без него сессия умрёт через шесть часов и
# починить её можно будет только руками.
#
# Про два «доступа». `grok-cli:access` пускает к cli-chat-proxy, но САМА МОДЕЛЬ за ним требует
# ещё и `api:access` — без него вход проходит, токен выдаётся, /health зелёный, и только первый
# настоящий ход отвечает 403 «OAuth2 token missing required scope». Проверено на себе.
#
# Дописать scope к уже выданной сессии нельзя: refresh-грант их не расширяет. Поэтому список
# правится только вместе с новым входом устройством.
SCOPE = "openid profile email offline_access grok-cli:access api:access"

# Обновляемся заранее. Ход агента длится до 150 секунд, и токен, живой на момент отправки, но
# умерший на момент ответа, выглядит как случайный 401 раз в шесть часов — самая дорогая форма
# поломки, потому что она невоспроизводима.
REFRESH_MARGIN_SECONDS = 300

USER_AGENT = "ss14-grok-bridge/1"


class AuthError(RuntimeError):
    """Отказ, который повтором не чинится: вход отклонён, код истёк, сессии нет."""


# --------------------------------------------------------------------------- сеть

def _opener() -> urllib.request.OpenerDirector:
    """
    Свой opener с ЯВНО выключенным прокси.

    На этой машине глобально экспортированы HTTP_PROXY и ALL_PROXY на немецкий выход, и
    urllib подхватывает их из окружения молча. auth.x.ai и cli-chat-proxy.grok.com отвечают
    напрямую (проверено), а лишний хоп — это лишняя причина, по которой вход однажды повиснет
    без объяснений. Кому нужен прокси — задаёт его GROK_BRIDGE_PROXY, и тогда он виден.
    """
    proxy = os.environ.get("GROK_BRIDGE_PROXY", "").strip()
    handler = urllib.request.ProxyHandler({"http": proxy, "https": proxy} if proxy else {})
    return urllib.request.build_opener(handler)


def _post_form(url: str, fields: dict[str, str], timeout: float = 30.0) -> dict:
    body = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={
            "Content-Type": "application/x-www-form-urlencoded",
            "Accept": "application/json",
            "User-Agent": USER_AGENT,
        },
    )

    try:
        with _opener().open(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", "replace")
        try:
            parsed = json.loads(raw)
        except ValueError:
            raise AuthError(f"{url}: HTTP {error.code}: {raw[:300]}") from None

        # Ошибки OAuth приезжают телом с кодом 400, и различать их обязан вызывающий:
        # authorization_pending — это не поломка, а «человек ещё не нажал».
        parsed["_http_status"] = error.code
        return parsed


def discover(issuer: str = ISSUER) -> dict:
    """Документ обнаружения. Спрашиваем, а не зашиваем: адреса эндпоинтов вендор вправе менять."""
    url = issuer.rstrip("/") + "/.well-known/openid-configuration"
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with _opener().open(request, timeout=30.0) as response:
        return json.loads(response.read().decode("utf-8"))


# ------------------------------------------------------------------------ хранение

def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _iso(when: datetime) -> str:
    return when.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _parse_iso(text: str) -> datetime:
    # Вендор пишет наносекунды («…33.553355501Z»), а fromisoformat до 3.11 понимает только
    # микросекунды. Обрезаем дробную часть целиком: точность секунды здесь и не нужна.
    text = text.strip().replace("Z", "+00:00")
    if "." in text:
        head, _, tail = text.partition(".")
        offset = ""
        for mark in ("+", "-"):
            index = tail.find(mark)
            if index > 0:
                offset = tail[index:]
                break
        text = head + offset
    return datetime.fromisoformat(text).astimezone(timezone.utc)


def save_session(path: str, session: dict) -> None:
    """
    Запись атомарная и с правами 600 ДО того, как в файле появится токен.

    Обычный `open(path, "w")` создал бы файл с 644 и заполнил бы его секретом — окно, в котором
    любой пользователь этой машины (а их тут несколько) читает refresh-токен.
    """
    directory = os.path.dirname(os.path.abspath(path)) or "."
    os.makedirs(directory, exist_ok=True)

    tmp = f"{path}.tmp.{os.getpid()}"
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    descriptor = os.open(tmp, flags, 0o600)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(session, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
    except BaseException:
        os.unlink(tmp)
        raise

    os.replace(tmp, path)


def load_session(path: str) -> dict:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def _session_from_token_response(payload: dict, previous: dict | None = None) -> dict:
    access = payload.get("access_token")
    if not access:
        raise AuthError(f"в ответе нет access_token: {json.dumps(payload)[:300]}")

    expires_in = int(payload.get("expires_in") or 0)
    expires_at = _utcnow() + timedelta(seconds=expires_in if expires_in > 0 else 3600)

    # Refresh-токен вендор вправе не присылать при обновлении — тогда действует прежний.
    # Потерять его здесь значит превратить рабочую сессию в шестичасовую.
    refresh = payload.get("refresh_token") or (previous or {}).get("refresh_token", "")

    session = dict(previous or {})
    session.update(
        {
            "issuer": ISSUER,
            "client_id": CLIENT_ID,
            "scope": payload.get("scope") or (previous or {}).get("scope") or SCOPE,
            "access_token": access,
            "refresh_token": refresh,
            "expires_at": _iso(expires_at),
            "obtained_at": _iso(_utcnow()),
        }
    )
    return session


# -------------------------------------------------------------------- вход по коду

def _echo(message: str = "") -> None:
    """
    Печать с немедленным сбросом буфера.

    Обычный print при перенаправлении в файл буферизуется по 8 КБ, и ссылка с кодом появилась бы
    в журнале уже после того, как код истёк. Для входа, который надо подтвердить за пятнадцать
    минут, это разница между «работает» и «не работает никогда».
    """
    print(message, flush=True)


def device_login(path: str, echo=_echo, timeout_seconds: float = 900.0) -> dict:
    """
    Полный вход устройством. Печатает ссылку с кодом и ждёт подтверждения человеком.

    Возвращает сохранённую сессию. Прежний файл переписывается только после успеха — неудачная
    попытка входа не должна лишать сервер работающей сессии.
    """
    config = discover()
    device_endpoint = config["device_authorization_endpoint"]
    token_endpoint = config["token_endpoint"]

    start = _post_form(device_endpoint, {"client_id": CLIENT_ID, "scope": SCOPE})
    if "device_code" not in start:
        raise AuthError(f"эндпоинт устройства отказал: {json.dumps(start)[:300]}")

    device_code = start["device_code"]
    interval = float(start.get("interval") or 5)
    complete = start.get("verification_uri_complete")
    plain = start.get("verification_uri", "")
    user_code = start.get("user_code", "")
    deadline = time.monotonic() + min(timeout_seconds, float(start.get("expires_in") or timeout_seconds))

    echo("")
    echo("  Подтвердите вход с любого устройства, где вы залогинены в X/Grok:")
    echo("")
    if complete:
        echo(f"    {complete}")
    if plain:
        echo(f"    либо {plain} и код {user_code}")
    echo("")
    echo("  Жду подтверждения…")

    while True:
        if time.monotonic() > deadline:
            raise AuthError("код устройства истёк — запустите вход заново")

        time.sleep(interval)

        payload = _post_form(
            token_endpoint,
            {
                "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                "device_code": device_code,
                "client_id": CLIENT_ID,
            },
        )

        error = payload.get("error")
        if not error:
            session = _session_from_token_response(payload)
            if not session.get("refresh_token"):
                # Без refresh-токена сессия проживёт часы, а не недели, и починить её сможет
                # только человек у телефона. Молчать об этом нельзя.
                echo("  ВНИМАНИЕ: вендор не выдал refresh_token — сессия умрёт с истечением "
                     "access_token. Проверьте, что в scope есть offline_access.")
            save_session(path, session)
            echo("")
            echo(f"  Готово. Сессия в {path}, действует до {session['expires_at']}.")
            return session

        if error == "authorization_pending":
            continue
        if error == "slow_down":
            interval += 5
            continue
        if error == "expired_token":
            raise AuthError("код устройства истёк — запустите вход заново")
        if error == "access_denied":
            raise AuthError("вход отклонён на устройстве подтверждения")

        raise AuthError(f"эндпоинт токена отказал: {json.dumps(payload)[:300]}")


def refresh_session(session: dict) -> dict:
    """Обновление по refresh-токену. Ротацию токена вендором сохраняем."""
    refresh = session.get("refresh_token")
    if not refresh:
        raise AuthError("в сессии нет refresh_token — нужен повторный вход устройством")

    config = discover(session.get("issuer") or ISSUER)
    payload = _post_form(
        config["token_endpoint"],
        {
            "grant_type": "refresh_token",
            "refresh_token": refresh,
            "client_id": session.get("client_id") or CLIENT_ID,
        },
    )

    if payload.get("error"):
        raise AuthError(f"обновление отклонено: {json.dumps(payload)[:300]}")

    return _session_from_token_response(payload, previous=session)


# ------------------------------------------------------------------- живой источник

class TokenSource:
    """
    Действующий access-токен, обновляемый по мере надобности.

    Обновление под одним замком (single-flight): четыре агента ходят в мост параллельно, и
    четыре одновременных обновления по одному refresh-токену — это гонка, в которой вендор
    отзовёт три из четырёх. Проверка «не обновил ли кто-то уже» стоит внутри замка, а не до.

    Файл перечитывается по mtime, поэтому `grokauth.py login`, выполненный при живом мосте,
    подхватывается без перезапуска сервиса.
    """

    def __init__(self, path: str):
        self.path = path
        self._lock = threading.Lock()
        self._session: dict | None = None
        self._mtime: float = 0.0

    def _read_if_changed(self) -> dict:
        try:
            mtime = os.path.getmtime(self.path)
        except OSError:
            raise AuthError(
                f"нет файла сессии {self.path} — выполните: python3 grokauth.py login"
            ) from None

        if self._session is None or mtime != self._mtime:
            self._session = load_session(self.path)
            self._mtime = mtime

        return self._session

    def status(self) -> dict:
        """Что можно показать наружу: сроки и владелец, но не токены."""
        session = self._read_if_changed()
        expires_at = session.get("expires_at", "")
        left = 0
        if expires_at:
            left = int((_parse_iso(expires_at) - _utcnow()).total_seconds())
        return {
            "issuer": session.get("issuer", ""),
            "scope": session.get("scope", ""),
            "expires_at": expires_at,
            "expires_in_seconds": left,
            "has_refresh_token": bool(session.get("refresh_token")),
            "obtained_at": session.get("obtained_at", ""),
        }

    def token(self) -> str:
        session = self._read_if_changed()
        if not self._stale(session):
            return session["access_token"]

        with self._lock:
            session = self._read_if_changed()
            if not self._stale(session):
                return session["access_token"]

            refreshed = refresh_session(session)
            save_session(self.path, refreshed)
            self._session = refreshed
            self._mtime = os.path.getmtime(self.path)
            return refreshed["access_token"]

    def force(self) -> str:
        """
        Обновить немедленно, не спрашивая срок.

        Нужно ровно для одного случая: вышестоящий ответил 401 при формально живом токене.
        Такое бывает после отзыва сессии на стороне вендора, и разбираться, кто прав — мы или
        их часы, — дешевле одним обновлением, чем разбором логов через сутки.
        """
        with self._lock:
            session = self._read_if_changed()
            refreshed = refresh_session(session)
            save_session(self.path, refreshed)
            self._session = refreshed
            self._mtime = os.path.getmtime(self.path)
            return refreshed["access_token"]

    @staticmethod
    def _stale(session: dict) -> bool:
        expires_at = session.get("expires_at")
        if not expires_at:
            return True
        return _parse_iso(expires_at) - _utcnow() < timedelta(seconds=REFRESH_MARGIN_SECONDS)


# ------------------------------------------------------------------------- командная

def default_session_path() -> str:
    env = os.environ.get("GROK_BRIDGE_SESSION", "").strip()
    if env:
        return env
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.abspath(os.path.join(here, "..", "..", "ai_data", "grok.session.json"))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Активация устройством для моста Grok.")
    parser.add_argument("command", choices=["login", "refresh", "status"])
    parser.add_argument("--session", default=default_session_path(), help="путь к файлу сессии")
    args = parser.parse_args(argv)

    try:
        if args.command == "login":
            device_login(args.session)
            return 0

        if args.command == "refresh":
            session = load_session(args.session)
            refreshed = refresh_session(session)
            save_session(args.session, refreshed)
            print(f"обновлено, действует до {refreshed['expires_at']}")
            return 0

        print(json.dumps(TokenSource(args.session).status(), ensure_ascii=False, indent=2))
        return 0
    except AuthError as error:
        print(f"ОШИБКА: {error}", file=sys.stderr)
        return 1
    except FileNotFoundError:
        print(
            f"ОШИБКА: нет файла сессии {args.session} — выполните: python3 grokauth.py login",
            file=sys.stderr,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
