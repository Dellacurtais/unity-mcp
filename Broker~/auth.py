"""Bearer-token checks that survive token drift between Unity and MCP clients.

The broker is launched with the token Unity holds in EditorPrefs, while MCP clients
(Claude Code, Codex) read the per-user environment variable UNITY_MCP_BROKER_TOKEN.
When the two drift apart (a regenerated token, another Editor, a crash while writing
preferences) every client call used to fail with 401 until someone fixed the config
by hand. Both values belong to the same Windows user, so the broker accepts either:
the token it was launched with and the current value of the user variable, re-read
from the registry so a rotation takes effect without restarting the broker.
"""
from __future__ import annotations

import hmac
import os
import sys
import threading
import time
from typing import Callable

MIN_TOKEN_LENGTH = 16


def read_user_environment_token(name: str) -> str | None:
    """Current value of a per-user environment variable (HKCU\\Environment on Windows)."""
    if sys.platform == "win32":
        try:
            import winreg

            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
                value, _ = winreg.QueryValueEx(key, name)
        except OSError:
            return None
        return str(value).strip() if value else None
    value = os.getenv(name, "").strip()
    return value or None


class TokenSource:
    def __init__(
        self,
        launch_token: str,
        *,
        env_name: str = "UNITY_MCP_BROKER_TOKEN",
        reader: Callable[[str], str | None] | None = None,
        cache_seconds: float = 2.0,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._launch_token = (launch_token or "").strip()
        self._env_name = env_name
        self._reader = reader or read_user_environment_token
        self._cache_seconds = cache_seconds
        self._clock = clock
        self._lock = threading.Lock()
        self._cached: str | None = None
        self._cached_at = float("-inf")

    @property
    def configured(self) -> bool:
        return bool(self._launch_token) or bool(self._user_token(force=True))

    def _user_token(self, *, force: bool = False) -> str | None:
        now = self._clock()
        with self._lock:
            if force or now - self._cached_at >= self._cache_seconds:
                try:
                    value = self._reader(self._env_name)
                except Exception:
                    value = None
                self._cached = value if value and len(value) >= MIN_TOKEN_LENGTH else None
                self._cached_at = now
            return self._cached

    def candidates(self, *, refresh: bool = False) -> list[str]:
        tokens = [self._launch_token] if self._launch_token else []
        user = self._user_token(force=refresh)
        if user and user not in tokens:
            tokens.append(user)
        return tokens

    def accepts_header(self, value: str | None) -> bool:
        if not value or not value.lower().startswith("bearer "):
            return False
        return self.accepts(value[7:].strip())

    def accepts(self, presented: str | None) -> bool:
        if not presented:
            return False
        if any(hmac.compare_digest(presented, token) for token in self.candidates()):
            return True
        # A rotated user token may be newer than the cache; re-read once before refusing.
        return any(hmac.compare_digest(presented, token) for token in self.candidates(refresh=True))

    def drift(self) -> dict[str, object]:
        """Whether the launch token and the client-facing user token differ (no secrets)."""
        user = self._user_token(force=True)
        return {
            "launchTokenSet": bool(self._launch_token),
            "userTokenSet": bool(user),
            "inSync": bool(user) and bool(self._launch_token) and hmac.compare_digest(user, self._launch_token),
            "variable": self._env_name,
        }
