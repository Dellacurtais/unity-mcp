from __future__ import annotations

import copy
import json
import logging
import os
import time
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

import uvicorn

from app import create_app
from config import BrokerConfig


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def logging_config() -> dict[str, object]:
    config = copy.deepcopy(uvicorn.config.LOGGING_CONFIG)
    log_path = os.getenv("UNITY_MCP_BROKER_LOG", "").strip()
    if not log_path:
        return config

    path = Path(log_path).expanduser()
    path.parent.mkdir(parents=True, exist_ok=True)
    config["handlers"]["managed_file"] = {
        "class": "logging.handlers.RotatingFileHandler",
        "formatter": "default",
        "filename": str(path),
        "maxBytes": 5 * 1024 * 1024,
        "backupCount": 3,
        "encoding": "utf-8",
    }
    for logger_name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        config["loggers"].setdefault(logger_name, {})["handlers"] = ["managed_file"]
    return config


def update_managed_state(state: str, error: str | None = None) -> None:
    state_path = os.getenv("UNITY_MCP_BROKER_STATE", "").strip()
    instance_id = os.getenv("UNITY_MCP_BROKER_INSTANCE_ID", "").strip()
    if not state_path or not instance_id:
        return

    path = Path(state_path).expanduser()
    try:
        payload: dict[str, object] | None = None
        last_error: Exception | None = None
        for attempt in range(40):
            try:
                candidate = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError, TypeError) as exception:
                last_error = exception
                if attempt < 39:
                    time.sleep(0.05)
                    continue
                raise
            if candidate.get("instanceId") != instance_id:
                return
            payload = candidate
            if payload.get("pid") and payload.get("processStartUtc"):
                break
            if attempt < 39:
                time.sleep(0.05)
        if payload is None:
            if last_error is not None:
                raise last_error
            return
        payload["state"] = state
        payload["updatedUtc"] = utc_now()
        if state == "running":
            payload["readyUtc"] = utc_now()
            payload.pop("error", None)
        elif error:
            payload["error"] = error
        temporary = path.with_name(path.name + f".{uuid4().hex}.tmp")
        temporary.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")
        os.replace(temporary, path)
    except (OSError, ValueError, TypeError):
        logging.getLogger("uvicorn.error").exception("Could not update managed broker state")


class ManagedServer(uvicorn.Server):
    async def startup(self, sockets=None) -> None:
        await super().startup(sockets=sockets)
        if not self.should_exit:
            update_managed_state("running")


def run() -> None:
    broker_config = BrokerConfig.from_env()
    server_config = uvicorn.Config(
        create_app(broker_config),
        host=broker_config.host,
        port=broker_config.port,
        ws_max_size=broker_config.max_request_bytes,
        log_config=logging_config(),
    )
    server = ManagedServer(server_config)
    try:
        server.run()
    except BaseException as exception:
        update_managed_state("failed", f"{type(exception).__name__}: {exception}")
        raise
    else:
        if getattr(server, "started", False):
            update_managed_state("stopped")
        else:
            update_managed_state("failed", "Broker stopped before its health endpoint became ready")


if __name__ == "__main__":
    run()
