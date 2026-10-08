from __future__ import annotations

import asyncio
import copy
import json
import logging
import os
import time
from contextlib import suppress
from datetime import datetime, timezone
from pathlib import Path
from typing import Protocol
from uuid import uuid4

import uvicorn
import uvicorn.logging

from app import create_app
from config import BrokerConfig


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


PROCESS_ID = os.getpid()
PROCESS_START_UTC = utc_now()


class ProcessMonitor(Protocol):
    def is_alive(self) -> bool: ...
    def close(self) -> None: ...


class ParentProcessMonitor:
    """Keeps a stable reference to the Unity process that launched this broker."""

    def __init__(self, process_id: int) -> None:
        if process_id <= 0:
            raise ValueError("Managed broker parent PID must be positive")
        self.process_id = process_id
        self._handle = None
        self._kernel32 = None
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel32.WaitForSingleObject.restype = wintypes.DWORD
            kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel32.CloseHandle.restype = wintypes.BOOL
            handle = kernel32.OpenProcess(0x00100000, False, process_id)
            if not handle:
                raise ProcessLookupError(f"Managed broker parent process {process_id} is not available")
            self._kernel32 = kernel32
            self._handle = handle

    def is_alive(self) -> bool:
        if self._handle is not None:
            return self._kernel32.WaitForSingleObject(self._handle, 0) == 0x00000102
        try:
            os.kill(self.process_id, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True

    def close(self) -> None:
        if self._handle is not None:
            self._kernel32.CloseHandle(self._handle)
            self._handle = None


def infer_managed_parent_process_id() -> int:
    if os.name != "nt":
        return os.getppid()

    import ctypes
    from ctypes import wintypes

    class ProcessEntry(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", wintypes.WCHAR * 260),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel32.Process32FirstW.restype = wintypes.BOOL
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel32.Process32NextW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    snapshot = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise RuntimeError("Could not inspect the managed broker parent process")
    processes: dict[int, tuple[int, str]] = {}
    try:
        entry = ProcessEntry()
        entry.dwSize = ctypes.sizeof(entry)
        available = bool(kernel32.Process32FirstW(snapshot, ctypes.byref(entry)))
        while available:
            processes[int(entry.th32ProcessID)] = (
                int(entry.th32ParentProcessID),
                str(entry.szExeFile),
            )
            available = bool(kernel32.Process32NextW(snapshot, ctypes.byref(entry)))
    finally:
        kernel32.CloseHandle(snapshot)

    process_id = PROCESS_ID
    visited: set[int] = set()
    while process_id > 0 and process_id not in visited:
        visited.add(process_id)
        process = processes.get(process_id)
        if process is None:
            break
        parent_process_id, executable_name = process
        if executable_name.casefold() == "unity.exe":
            return process_id
        process_id = parent_process_id
    raise RuntimeError("Managed broker was not launched by a Unity process")


def parent_monitor_from_environment() -> ParentProcessMonitor | None:
    raw_process_id = os.getenv("UNITY_MCP_BROKER_PARENT_PID", "").strip()
    if raw_process_id:
        try:
            process_id = int(raw_process_id)
        except ValueError as exception:
            raise RuntimeError("UNITY_MCP_BROKER_PARENT_PID must be a positive integer") from exception
    elif (
        os.getenv("UNITY_MCP_BROKER_STATE", "").strip()
        and os.getenv("UNITY_MCP_BROKER_INSTANCE_ID", "").strip()
    ):
        process_id = infer_managed_parent_process_id()
    else:
        return None
    try:
        return ParentProcessMonitor(process_id)
    except ValueError as exception:
        raise RuntimeError("UNITY_MCP_BROKER_PARENT_PID must be a positive integer") from exception


async def stop_when_parent_exits(
    server: uvicorn.Server,
    monitor: ProcessMonitor,
    poll_interval: float = 0.5,
) -> None:
    try:
        while monitor.is_alive():
            await asyncio.sleep(poll_interval)
        logging.getLogger("uvicorn.error").info(
            "Managed broker owner process exited; stopping broker"
        )
        server.should_exit = True
    finally:
        monitor.close()


class ManagedLogFormatter(uvicorn.logging.DefaultFormatter):
    """Prefixes every managed log line with a UTC timestamp."""

    def formatTime(self, record: logging.LogRecord, datefmt: str | None = None) -> str:
        return datetime.fromtimestamp(record.created, timezone.utc).isoformat(timespec="milliseconds")


def logging_config() -> dict[str, object]:
    config = copy.deepcopy(uvicorn.config.LOGGING_CONFIG)
    log_path = os.getenv("UNITY_MCP_BROKER_LOG", "").strip()
    if not log_path:
        return config

    path = Path(log_path).expanduser()
    path.parent.mkdir(parents=True, exist_ok=True)
    config["formatters"]["managed"] = {
        "()": ManagedLogFormatter,
        "fmt": "%(asctime)s %(levelprefix)s %(message)s",
        "use_colors": False,
    }
    config["handlers"]["managed_file"] = {
        "class": "logging.handlers.RotatingFileHandler",
        "formatter": "managed",
        "filename": str(path),
        "maxBytes": 5 * 1024 * 1024,
        "backupCount": 3,
        "encoding": "utf-8",
    }
    for logger_name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        logger = config["loggers"].setdefault(logger_name, {})
        logger["handlers"] = ["managed_file"]
        # uvicorn.error propagates to uvicorn by default, which wrote its lines twice.
        logger["propagate"] = False
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
            payload["pid"] = PROCESS_ID
            payload["processStartUtc"] = PROCESS_START_UTC
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
    def __init__(
        self,
        config: uvicorn.Config,
        parent_monitor: ProcessMonitor | None = None,
    ) -> None:
        super().__init__(config)
        self.parent_monitor = parent_monitor
        self.parent_watch_task: asyncio.Task[None] | None = None

    async def startup(self, sockets=None) -> None:
        await super().startup(sockets=sockets)
        if not self.should_exit:
            update_managed_state("running")
            if self.parent_monitor is not None:
                self.parent_watch_task = asyncio.create_task(
                    stop_when_parent_exits(self, self.parent_monitor),
                    name="unity-mcp-parent-watch",
                )

    async def shutdown(self, sockets=None) -> None:
        if self.parent_watch_task is not None:
            self.parent_watch_task.cancel()
            with suppress(asyncio.CancelledError):
                await self.parent_watch_task
            self.parent_watch_task = None
        if self.parent_monitor is not None:
            self.parent_monitor.close()
            self.parent_monitor = None
        await super().shutdown(sockets=sockets)


def run() -> None:
    broker_config = BrokerConfig.from_env()
    parent_monitor = parent_monitor_from_environment()
    server_config = uvicorn.Config(
        create_app(broker_config),
        host=broker_config.host,
        port=broker_config.port,
        ws_max_size=broker_config.max_request_bytes,
        log_config=logging_config(),
    )
    server = ManagedServer(server_config, parent_monitor)
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
