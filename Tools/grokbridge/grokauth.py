#!/usr/bin/env python3
"""
Device activation and xAI session storage for the bridge.

WHY A SEPARATE SESSION, NOT SOMEONE ELSE'S TOKEN FROM ~/.grok/auth.json
------------------------------------------------------------
A refresh token can have exactly one owner: it is single-use and rotated on the vendor's
side, so a second consumer that refreshes it revokes the first. This is not theory — on this
machine ~/.hermes/auth.json still has a `refresh_token_reused` from 2026-08-07.

Reading the CLI's token read-only and never refreshing it doesn't work either: it lives six
hours, and a turn started at 02:50 would end in a provider refusal at 02:54.

Device activation solves this with no coordination needed at all: the game server goes
through its own login and gets ITS OWN refresh token. From then on `grok` in the terminal and
the bridge live independently — refreshing one does not touch the other.

WHY DEVICE FLOW SPECIFICALLY, NOT A BROWSER
---------------------------------------
The machine has no graphics and no trusted browser; device code login (RFC 8628) needs
neither — it shows a link and a code, and confirmation happens from a phone.
`https://auth.x.ai/.well-known/openid-configuration` advertises
`urn:ietf:params:oauth:grant-type:device_code` and `token_endpoint_auth_methods_supported: none`,
i.e. a public client with no secret — exactly our case.

WHERE THE RESULT LIVES
-------------------
`ai_data/grok.session.json`, mode 600. The directory is gitignored — the only place secrets
are allowed to live. They cannot go in Resources/: ContentMagicAczProvider hands out the
whole folder to every player who connects.
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

# Public client identifier of the Grok CLI. Not a secret: a public client never has one —
# login is confirmed by a human in the browser, not by knowing this string.
CLIENT_ID = "b1a00492-073a-47ea-816f-4c329264a828"

# `offline_access` is exactly the right to refresh; without it the session dies in six hours
# and can only be fixed by hand.
#
# On the two "accesses". `grok-cli:access` gets you into cli-chat-proxy, but the MODEL ITSELF
# behind it also requires `api:access` — without it, login succeeds, a token is issued,
# /health is green, and only the first real turn gets a 403 "OAuth2 token missing required
# scope". Verified firsthand.
#
# Scope cannot be added to an already-issued session: the refresh grant does not expand it.
# So the list can only be changed together with a new device login.
SCOPE = "openid profile email offline_access grok-cli:access api:access"

# Refresh ahead of time. An agent turn runs up to 150 seconds, and a token that's alive when
# sent but dead by the time of the reply looks like a random 401 once every six hours — the
# most expensive kind of failure, because it is irreproducible.
REFRESH_MARGIN_SECONDS = 300

USER_AGENT = "ss14-grok-bridge/1"


class AuthError(RuntimeError):
    """A failure that a retry won't fix: login was rejected, the code expired, no session exists."""


# --------------------------------------------------------------------------- network

def _opener() -> urllib.request.OpenerDirector:
    """
    Our own opener with the proxy EXPLICITLY disabled.

    On this machine, HTTP_PROXY and ALL_PROXY are globally exported to a German exit node,
    and urllib picks them up from the environment silently. auth.x.ai and
    cli-chat-proxy.grok.com respond directly (verified), and an extra hop is just one more
    reason login could hang someday with no explanation. Whoever needs a proxy sets it via
    GROK_BRIDGE_PROXY, and then it's visible.
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

        # OAuth errors arrive in the body with a 400 status, and the caller must tell them
        # apart: authorization_pending is not a failure, it's "the human hasn't clicked yet".
        parsed["_http_status"] = error.code
        return parsed


def discover(issuer: str = ISSUER) -> dict:
    """Discovery document. We ask instead of hardcoding: the vendor is free to change endpoint URLs."""
    url = issuer.rstrip("/") + "/.well-known/openid-configuration"
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with _opener().open(request, timeout=30.0) as response:
        return json.loads(response.read().decode("utf-8"))


# ------------------------------------------------------------------------ storage

def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _iso(when: datetime) -> str:
    return when.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _parse_iso(text: str) -> datetime:
    # The vendor writes nanoseconds ("...33.553355501Z"), and fromisoformat before 3.11 only
    # understands microseconds. We strip the fractional part entirely: second precision is
    # all that's needed here anyway.
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
    The write is atomic and mode 600 BEFORE the token ever lands in the file.

    A plain `open(path, "w")` would create the file with mode 644 and then fill it with the
    secret — a window in which any user on this machine (and there are several) can read the
    refresh token.
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

    # The vendor is free not to send a refresh token on refresh — the old one then stays in
    # effect. Losing it here means turning a working session into a six-hour one.
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


# -------------------------------------------------------------------- device code login

def _echo(message: str = "") -> None:
    """
    Print with an immediate flush.

    A plain print, when redirected to a file, buffers in 8 KB chunks, and the link with the
    code would show up in the log only after the code had already expired. For a login that
    must be confirmed within fifteen minutes, that's the difference between "works" and
    "never works".
    """
    print(message, flush=True)


def device_login(path: str, echo=_echo, timeout_seconds: float = 900.0) -> dict:
    """
    Full device login. Prints the link with the code and waits for human confirmation.

    Returns the saved session. The previous file is only overwritten after success — a failed
    login attempt must not deprive the server of a working session.
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
                # Without a refresh token the session lives hours, not weeks, and only a
                # human at the phone can fix it. This cannot pass silently.
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
    """Refresh using the refresh token. We keep whatever token rotation the vendor performs."""
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


# ------------------------------------------------------------------- live source

class TokenSource:
    """
    A live access token, refreshed as needed.

    Refresh happens under a single lock (single-flight): four agents hit the bridge in
    parallel, and four simultaneous refreshes on one refresh token is a race in which the
    vendor revokes three of the four. The "did someone already refresh it" check sits inside
    the lock, not before it.

    The file is re-read by mtime, so `grokauth.py login`, run while the bridge is alive, is
    picked up without restarting the service.
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
        """What is safe to show outside: expiry and ownership, but never tokens."""
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
        Refresh immediately, without checking the expiry.

        Needed for exactly one case: the upstream answered 401 while the token was, on paper,
        still alive. This happens after the vendor revokes a session on their side, and it's
        cheaper to settle who's right — us or their clock — with one refresh than by
        digging through logs a day later.
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


# ------------------------------------------------------------------------- command line

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
