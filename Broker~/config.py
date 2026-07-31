from __future__ import annotations

import os
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

    def __post_init__(self) -> None:
        if self.host not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("The broker must bind to a loopback host.")
        if not 1 <= self.port <= 65535:
            raise ValueError("The broker port must be between 1 and 65535.")
        if self.request_timeout_seconds <= 0 or self.reconnect_grace_seconds <= 0:
            raise ValueError("Broker timeouts must be positive.")
        if self.max_request_bytes <= 0:
            raise ValueError("The broker request limit must be positive.")
        if self.focus_assist_delay_seconds <= 0:
            raise ValueError("The broker focus assist delay must be positive.")

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
            reconnect_grace_seconds=float(os.getenv("UNITY_MCP_BROKER_RECONNECT_GRACE", "45")),
            max_request_bytes=int(os.getenv("UNITY_MCP_BROKER_MAX_REQUEST_BYTES", str(8 * 1024 * 1024))),
            focus_assist_enabled=_env_bool("UNITY_MCP_BROKER_FOCUS_ASSIST", True),
            focus_assist_delay_seconds=float(os.getenv("UNITY_MCP_BROKER_FOCUS_DELAY", "2")),
            auto_wait_jobs=_env_bool("UNITY_MCP_BROKER_AUTO_WAIT_JOBS", True),
        )
