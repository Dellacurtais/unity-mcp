# Unity MCP broker

The broker is the always-on side of Unity MCP. MCP clients talk to it over Streamable HTTP; every Unity Editor talks to it over an outbound WebSocket bridge. It keeps the endpoint, the registry of tools and the request/job ledger alive while Unity compiles, reloads its domain or restarts.

Unity ignores this folder (the `~` suffix). The Editor installs it into `%LOCALAPPDATA%\UnityMcp\Broker\installations\<version>` with a private virtual environment; nothing here is modified at runtime.

## Processes

| File | Role |
| --- | --- |
| `supervisor.py` | One per user (named mutex). Runs `main.py` in persistent mode, checks `/health` every 5 s, restarts the broker when it exits or fails 4 checks in a row, cleans orphans every 10 min, follows a newer installation when `current.json` changes, and stops on `runtime/supervisor.stop`. Holds itself and the broker in a kill-on-close job object. `--detach` relaunches it outside the caller's console and job. |
| `main.py` | Uvicorn/FastAPI server. Redirects stdout/stderr to `logs/broker.stderr.log`, starts the event-loop watchdog, and exits when its owner (supervisor or legacy Unity parent) is gone. |
| `app.py` | HTTP endpoints (`/health`, `/mcp/`, `/bridge`): auth, origin and protocol checks, session adoption, bridge `hello` timeout. |
| `mcp.py` | JSON-RPC handling, request ledger, pending answers (`status: running` after `sync_wait_seconds`), job auto-wait, broker tools. |
| `router.py` | Bridge connections, routing to the active project, reconnect waits, refresh/compile job events. |
| `store.py` | SQLite ledger (WAL). Maintenance scans through a read-only connection outside the store lock. |
| `auth.py` | Bearer tokens: the launch token and the current per-user `UNITY_MCP_BROKER_TOKEN`. |
| `watchdog.py` | Thread outside the loop: stack dump after a 15 s stall, exit after 90 s. |
| `lifecycle.py`, `editor_control.py` | Editor status, close/open/restart, native dialogs, process snapshots (Windows). |
| `orphans.py` | Orphan cleanup by process tree; `python orphans.py [--apply] [--all] [--json]`. |
| `token_header.py` | Prints the current `Authorization` header, for clients with a headers helper. |
| `installer.py` | Managed installation used by the Unity window (**Install**/**Repair**). |

## Running it by hand

```bash
python -m venv .venv
.venv/Scripts/python -m pip install -r requirements.txt      # Windows; use .venv/bin/python elsewhere
set UNITY_MCP_BROKER_TOKEN=<long random token>                # or $env:... in PowerShell
.venv/Scripts/python supervisor.py                           # supervised, restarts itself
.venv/Scripts/python main.py                                 # or a single broker, no supervision
```

Then click **Connect** under **Unity bridge** in **Window → Unity MCP Transport**. `GET http://127.0.0.1:8765/health` needs no token. All settings are environment variables; the table is in the package [README](../README.md#configuration). For an isolated test instance set `UNITY_MCP_BROKER_ROOT`, `UNITY_MCP_BROKER_PORT`, `UNITY_MCP_BROKER_DB`, `UNITY_MCP_BROKER_LOG` and `UNITY_MCP_BROKER_SUPERVISOR_MUTEX`.

## Tests

```bash
python -m pip install -r requirements-dev.txt
python -m pytest tests -q
```

`tests/test_process_integration.py` starts real broker processes on free ports; the rest are in-process. `tests/test_resilience.py` covers pending answers, reconnect waits, token drift, session adoption, the watchdog, orphan decisions and history pruning.

## Behaviour worth knowing

- A tool call never holds its HTTP answer longer than `UNITY_MCP_BROKER_SYNC_WAIT` (45 s). The work continues; `broker_wait_request` returns its result.
- Reads and idempotent tools queue while the bridge is away for a reason that ends by itself (reload, restart). Non-idempotent writes wait only until the sync budget runs out and are never resent once Unity accepted them: an interrupted one ends as `OUTCOME_UNKNOWN`.
- An unknown `Mcp-Session-Id` is adopted (the broker restarted); one closed with `DELETE` or evicted stays 404.
- The ledger keeps request receipts forever; read payloads expire after 7 days or a size budget, write payloads after 14 days, and only the last 3 registry snapshots per project are kept.
