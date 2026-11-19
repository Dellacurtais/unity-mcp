from __future__ import annotations

import os
import math
from dataclasses import dataclass
from pathlib import Path


def _env_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    normalized = value.strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    raise ValueError(f"{name} must be a boolean value")


@dataclass(frozen=True)
class BrokerConfig:
    host: str = "127.0.0.1"
    port: int = 8765
    token: str = ""
    database_path: Path = Path.home() / ".unity-mcp" / "broker.sqlite3"
    request_timeout_seconds: float = 900.0
    reconnect_grace_seconds: float = 45.0
    max_request_bytes: int = 8 * 1024 * 1024
    focus_assist_enabled: bool = True
    focus_assist_delay_seconds: float = 2.0
    auto_wait_jobs: bool = True
    response_inline_bytes: int = 256 * 1024
    history_retention_days: int = 7
    history_max_response_bytes: int = 256 * 1024 * 1024
    history_maintenance_seconds: float = 300.0
    max_sessions: int = 1024
    session_idle_seconds: float = 2 * 60 * 60
    # Longest time a tools/call answer is held before the broker returns a pending
    # handle. MCP clients abort silent HTTP calls (Claude Code after 5 min idle,
    # Codex after 60 s by default), so long work must never block the reply.
    sync_wait_seconds: float = 45.0
    # Longest single wait inside broker_wait_request / broker_wait_job.
    max_wait_call_seconds: float = 55.0
    # How long the broker keeps following a Unity job (tests, builds) for a pending call.
    job_wait_seconds: float = 6 * 60 * 60
    persistent: bool = False
    stall_dump_seconds: float = 15.0
    stall_exit_seconds: float = 90.0
    # Per-user variable also accepted as bearer token ("" disables; from_env enables it).
    token_env_name: str = ""
    adopt_unknown_sessions: bool = True
    # Mutation payloads (never their receipts) expire after this many days; 0 keeps them.
    write_history_retention_days: int = 14
    registry_snapshots_per_project: int = 3

    def __post_init__(self) -> None:
        if self.host not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("The broker must bind to a loopback host.")
        if not 1 <= self.port <= 65535:
            raise ValueError("The broker port must be between 1 and 65535.")
        if any(not math.isfinite(value) or value <= 0 for value in (self.request_timeout_seconds, self.reconnect_grace_seconds)):
            raise ValueError("Broker timeouts must be positive.")
        if self.max_request_bytes <= 0:
            raise ValueError("The broker request limit must be positive.")
        if not math.isfinite(self.focus_assist_delay_seconds) or self.focus_assist_delay_seconds <= 0:
            raise ValueError("The broker focus assist delay must be positive.")
        if self.response_inline_bytes < 1 or self.history_retention_days < 0 or self.history_max_response_bytes < 0:
            raise ValueError("Broker response/retention limits are invalid")
        if not math.isfinite(self.history_maintenance_seconds) or self.history_maintenance_seconds <= 0:
            raise ValueError("Broker history maintenance interval must be positive")
        if self.write_history_retention_days < 0 or self.registry_snapshots_per_project < 0:
            raise ValueError("Broker history limits cannot be negative")
        if self.max_sessions < 1:
            raise ValueError("The broker session limit must be positive")
        if not math.isfinite(self.session_idle_seconds) or self.session_idle_seconds <= 0:
            raise ValueError("The broker session idle timeout must be positive")
        if any(not math.isfinite(value) or value <= 0 for value in (self.sync_wait_seconds, self.max_wait_call_seconds, self.job_wait_seconds)):
            raise ValueError("Broker wait budgets must be positive")
        if any(not math.isfinite(value) or value < 0 for value in (self.stall_dump_seconds, self.stall_exit_seconds)):
            raise ValueError("Broker stall limits must be zero (disabled) or positive")

    @classmethod
    def from_env(cls) -> "BrokerConfig":
        return cls(
            host=os.getenv("UNITY_MCP_BROKER_HOST", "127.0.0.1"),
            port=int(os.getenv("UNITY_MCP_BROKER_PORT", "8765")),
            token=os.getenv("UNITY_MCP_BROKER_TOKEN", ""),
            database_path=Path(os.getenv(
                "UNITY_MCP_BROKER_DB",
                str(Path.home() / ".unity-mcp" / "broker.sqlite3"),
            )),
            request_timeout_seconds=float(os.getenv("UNITY_MCP_BROKER_TIMEOUT", "900")),
            # A Domain Reload of a large project, or reopening the Editor, takes minutes.
            reconnect_grace_seconds=float(os.getenv("UNITY_MCP_BROKER_RECONNECT_GRACE", "300")),
            max_request_bytes=int(os.getenv("UNITY_MCP_BROKER_MAX_REQUEST_BYTES", str(8 * 1024 * 1024))),
            focus_assist_enabled=_env_bool("UNITY_MCP_BROKER_FOCUS_ASSIST", True),
            focus_assist_delay_seconds=float(os.getenv("UNITY_MCP_BROKER_FOCUS_DELAY", "2")),
            auto_wait_jobs=_env_bool("UNITY_MCP_BROKER_AUTO_WAIT_JOBS", True),
            response_inline_bytes=int(os.getenv("UNITY_MCP_BROKER_RESPONSE_INLINE_BYTES", str(256 * 1024))),
            history_retention_days=int(os.getenv("UNITY_MCP_BROKER_HISTORY_DAYS", "7")),
            history_max_response_bytes=int(os.getenv("UNITY_MCP_BROKER_HISTORY_BYTES", str(256 * 1024 * 1024))),
            history_maintenance_seconds=float(os.getenv("UNITY_MCP_BROKER_HISTORY_INTERVAL", "300")),
            max_sessions=int(os.getenv("UNITY_MCP_BROKER_MAX_SESSIONS", "1024")),
            session_idle_seconds=float(os.getenv("UNITY_MCP_BROKER_SESSION_IDLE_SECONDS", str(2 * 60 * 60))),
            sync_wait_seconds=float(os.getenv("UNITY_MCP_BROKER_SYNC_WAIT", "45")),
            max_wait_call_seconds=float(os.getenv("UNITY_MCP_BROKER_MAX_WAIT_CALL", "55")),
            job_wait_seconds=float(os.getenv("UNITY_MCP_BROKER_JOB_WAIT", str(6 * 60 * 60))),
            persistent=_env_bool("UNITY_MCP_BROKER_PERSISTENT", False),
            stall_dump_seconds=float(os.getenv("UNITY_MCP_BROKER_STALL_DUMP", "15")),
            stall_exit_seconds=float(os.getenv("UNITY_MCP_BROKER_STALL_EXIT", "90")),
            adopt_unknown_sessions=_env_bool("UNITY_MCP_BROKER_ADOPT_SESSIONS", True),
            token_env_name=os.getenv("UNITY_MCP_BROKER_TOKEN_ENV", "UNITY_MCP_BROKER_TOKEN").strip(),
            write_history_retention_days=int(os.getenv("UNITY_MCP_BROKER_WRITE_HISTORY_DAYS", "14")),
            registry_snapshots_per_project=int(os.getenv("UNITY_MCP_BROKER_REGISTRY_SNAPSHOTS", "3")),
        )
