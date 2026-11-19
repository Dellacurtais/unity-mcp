"""headersHelper for MCP clients: prints the current Unity MCP Authorization header.

Claude Code runs a server's ``headersHelper`` command on every connection and again
after a 401, so a client configured with it always presents the token that is
current now, instead of the one its process environment held when it started.

Prints ``{"Authorization": "Bearer <token>"}`` and exits 0, or prints ``{}`` and
exits 1 when no token is configured. It never prints anything else.
"""
from __future__ import annotations

import json
import os
import sys

TOKEN_VARIABLE = "UNITY_MCP_BROKER_TOKEN"


def current_token() -> str | None:
    if sys.platform == "win32":
        try:
            import winreg

            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
                value, _ = winreg.QueryValueEx(key, TOKEN_VARIABLE)
            if value and str(value).strip():
                return str(value).strip()
        except OSError:
            pass
    value = os.getenv(TOKEN_VARIABLE, "").strip()
    return value or None


def main() -> int:
    token = current_token()
    if not token:
        sys.stdout.write("{}")
        return 1
    sys.stdout.write(json.dumps({"Authorization": "Bearer " + token}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
