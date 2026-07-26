from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from typing import Any
from uuid import uuid4

from fastapi import WebSocket

from protocol import BridgeIdentity, envelope, validate_envelope
from registry import RegistryCache
from store import BrokerStore


class BridgeUnavailable(RuntimeError):
    pass


class OutcomeUnknown(RuntimeError):
    pass


@dataclass
class BridgeConnection:
    websocket: WebSocket
    identity: BridgeIdentity
    state: str = "ready"
    send_lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    pending: dict[str, asyncio.Future[dict[str, Any]]] = field(default_factory=dict)

    async def send(self, message: dict[str, Any]) -> None:
        async with self.send_lock:
            await self.websocket.send_json(message)


class BridgeRouter:
    def __init__(self, store: BrokerStore, registry: RegistryCache) -> None:
        self.store = store
        self.registry = registry
        self.connections: dict[str, BridgeConnection] = {}
        self._lock = asyncio.Lock()
        self._connection_events: dict[str, asyncio.Event] = {}
        self._job_events: dict[str, asyncio.Event] = {}

    @property
    def active_project_key(self) -> str | None:
        return self.store.get_active_project()

    async def register(
        self,
        websocket: WebSocket,
        identity: BridgeIdentity,
        tools: list[dict[str, Any]],
        state: str,
    ) -> BridgeConnection:
        connection = BridgeConnection(websocket, identity, state)
        async with self._lock:
            previous = self.connections.get(identity.project_key)
            if previous:
                await self._fail_pending(previous, "Unity bridge connection was replaced")
            self.connections[identity.project_key] = connection
            self._connection_events.setdefault(identity.project_key, asyncio.Event()).set()
            if self.active_project_key is None:
                self.store.set_active_project(identity.project_key)
        self.registry.update(identity.project_key, identity.registry_hash, tools)
        self.store.save_registry(identity.project_key, identity.registry_hash, tools)
        self.store.upsert_project(identity, state, True)
        self.store.record_event(identity.project_key, identity.generation_id, "connected", {"state": state})
        await self._resume_refresh_jobs(identity.project_key)
        return connection

    async def unregister(self, connection: BridgeConnection, reason: str = "disconnected") -> None:
        was_current = False
        async with self._lock:
            current = self.connections.get(connection.identity.project_key)
            if current is connection:
                was_current = True
                self.connections.pop(connection.identity.project_key, None)
                self._connection_events.setdefault(
                    connection.identity.project_key, asyncio.Event()
                ).clear()
        await self._fail_pending(connection, reason)
        if not was_current:
            return
        self.store.mark_project_state(
            connection.identity.project_key, connection.identity.generation_id, reason, False
        )
        self.store.record_event(
            connection.identity.project_key, connection.identity.generation_id, reason, {}
        )
        for state in ("dispatched", "running"):
            for job in self.store.list_jobs(connection.identity.project_key, state):
                if job.get("kind") == "refresh_compile":
                    self.store.update_job(
                        job["job_id"],
                        "waiting_for_reconnect",
                        progress=max(float(job.get("progress", 0)), 0.75),
                        message=reason,
                    )
                    self._job_events.setdefault(str(job["job_id"]), asyncio.Event()).set()

    async def _fail_pending(self, connection: BridgeConnection, reason: str) -> None:
        for future in list(connection.pending.values()):
            if not future.done():
                future.set_exception(BridgeUnavailable(reason))
        connection.pending.clear()

    def active_connection(self) -> BridgeConnection:
        project_key = self.active_project_key
        connection = self.connections.get(project_key or "")
        if not connection:
            raise BridgeUnavailable("no active Unity bridge is connected")
        if connection.state not in {"ready", "compiling", "compile_failed"}:
            raise BridgeUnavailable(f"active Unity bridge is {connection.state}")
        return connection

    async def activate(self, project_key: str) -> BridgeConnection:
        connection = self.connections.get(project_key)
        if not connection:
            raise BridgeUnavailable(f"Unity project is not connected: {project_key}")
        self.store.set_active_project(project_key)
        return connection

    async def wait_for_connection(
        self, project_key: str, timeout: float
    ) -> BridgeConnection | None:
        connection = self.connections.get(project_key)
        if connection and connection.state in {"ready", "compiling", "compile_failed"}:
            return connection
        event = self._connection_events.setdefault(project_key, asyncio.Event())
        try:
            await asyncio.wait_for(event.wait(), timeout=max(0.1, timeout))
        except asyncio.TimeoutError:
            return None
        connection = self.connections.get(project_key)
        return connection if connection and connection.state in {"ready", "compiling", "compile_failed"} else None

    async def dispatch(
        self,
        request_id: str,
        json_rpc: dict[str, Any],
        *,
        timeout: float,
    ) -> dict[str, Any]:
        connection = self.active_connection()
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        correlation_id = uuid4().hex
        connection.pending[correlation_id] = future
        message = envelope(
            "command",
            {"requestId": request_id, "jsonRpc": json_rpc},
            identity=connection.identity,
            correlation_id=correlation_id,
        )
        try:
            await connection.send(message)
            return await asyncio.wait_for(future, timeout=timeout)
        finally:
            connection.pending.pop(correlation_id, None)

    async def dispatch_control(self, control: str, payload: dict[str, Any]) -> None:
        connection = self.active_connection()
        await connection.send(envelope(
            "command",
            {"control": control, **payload},
            identity=connection.identity,
            correlation_id=payload.get("jobId"),
        ))

    async def cancel(self, request_id: str) -> None:
        try:
            connection = self.active_connection()
        except BridgeUnavailable:
            return
        await connection.send(envelope(
            "cancel", {"requestId": request_id}, identity=connection.identity, correlation_id=request_id
        ))

    async def handle(self, connection: BridgeConnection, message: dict[str, Any]) -> None:
        validate_envelope(message)
        current = self.connections.get(connection.identity.project_key)
        if current is not connection:
            raise ValueError("stale Unity generation connection")
        if message.get("generationId") != connection.identity.generation_id:
            raise ValueError("stale Unity generation message")
        message_type = message["type"]
        payload = message["payload"]
        correlation_id = message.get("correlationId")

        if message_type == "accepted":
            request_id = str(payload.get("requestId", ""))
            if request_id:
                self.store.update_request(
                    request_id, "accepted_by_unity", generation_id=connection.identity.generation_id
                )
            return
        if message_type == "result":
            future = connection.pending.get(str(correlation_id))
            if future and not future.done():
                future.set_result(payload["jsonRpc"])
            return
        if message_type == "heartbeat":
            self.store.mark_project_state(
                connection.identity.project_key, connection.identity.generation_id, connection.state, True
            )
            return
        if message_type == "event":
            await self._handle_event(connection, payload)
            return
        raise ValueError(f"unsupported bridge message type: {message_type}")

    async def _handle_event(self, connection: BridgeConnection, payload: dict[str, Any]) -> None:
        event_type = str(payload.get("event", "unknown"))
        state = str(payload.get("state", connection.state))
        connection.state = state
        self.store.mark_project_state(
            connection.identity.project_key, connection.identity.generation_id, state, True
        )
        self.store.record_event(
            connection.identity.project_key, connection.identity.generation_id, event_type, payload
        )
        job_id = payload.get("jobId")
        if not job_id:
            return
        if event_type in {"refresh_accepted", "compilation_started"}:
            self.store.update_job(job_id, "running", progress=0.25, message=event_type)
        elif event_type in {"compilation_finished", "before_reload"}:
            self.store.update_job(
                job_id, "waiting_for_reconnect", progress=0.75, message=event_type,
                result=payload if event_type == "compilation_finished" else None,
            )
        elif event_type == "compile_failed":
            self.store.update_job(job_id, "failed", progress=1, message=event_type, error=payload.get("error"), result=payload)
        elif event_type == "refresh_completed":
            self.store.update_job(job_id, "completed", progress=1, message=event_type, result=payload)
        self._job_events.setdefault(str(job_id), asyncio.Event()).set()

    async def _resume_refresh_jobs(self, project_key: str) -> None:
        rows = self.store.list_jobs(project_key, "waiting_for_reconnect")
        for row in rows:
            result = dict(row.get("result") or {})
            result["reconnected"] = True
            self.store.update_job(
                row["job_id"], "completed", progress=1, message="Unity reconnected after reload",
                result=result,
            )
            self._job_events.setdefault(str(row["job_id"]), asyncio.Event()).set()

    async def wait_job(self, job_id: str, timeout: float) -> dict[str, Any] | None:
        current = self.store.get_job(job_id)
        if current is None or current["state"] in {"completed", "failed", "cancelled"}:
            return current
        event = self._job_events.setdefault(job_id, asyncio.Event())
        try:
            await asyncio.wait_for(event.wait(), timeout=max(0.1, timeout))
        except asyncio.TimeoutError:
            pass
        event.clear()
        return self.store.get_job(job_id)
