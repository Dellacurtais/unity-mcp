from __future__ import annotations

import asyncio
import time
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable
from uuid import uuid4

from fastapi import WebSocket, WebSocketDisconnect

from focus_assist import FocusActivation, ProcessFocusAssist
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
    started: dict[str, asyncio.Event] = field(default_factory=dict)
    last_heartbeat: float = field(default_factory=time.monotonic)
    main_thread_last_tick_utc: str | None = None
    dispatcher_queue_depth: int | None = None
    in_flight_requests: int | None = None

    async def send(self, message: dict[str, Any], on_sending: Callable[[], None] | None = None) -> None:
        async with self.send_lock:
            if on_sending is not None:
                on_sending()
            try:
                await self.websocket.send_json(message)
            except (WebSocketDisconnect, RuntimeError, OSError) as exception:
                self.state = "disconnected"
                raise BridgeUnavailable(str(exception)) from exception


class BridgeRouter:
    def __init__(
        self,
        store: BrokerStore,
        registry: RegistryCache,
        *,
        focus_assist: ProcessFocusAssist | None = None,
        focus_assist_enabled: bool = True,
        focus_assist_delay_seconds: float = 2.0,
    ) -> None:
        self.store = store
        self.registry = registry
        self.focus_assist = focus_assist or ProcessFocusAssist()
        self.focus_assist_enabled = focus_assist_enabled
        self.focus_assist_delay_seconds = focus_assist_delay_seconds
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
            active_connection = self.connections.get(self.active_project_key or "")
            if active_connection is None:
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
        for event in list(connection.started.values()):
            event.set()
        connection.started.clear()

    def active_connection(self) -> BridgeConnection:
        return self.connection_for(self.active_project_key)

    def connection_for(self, project_key: str | None) -> BridgeConnection:
        connection = self.connections.get(project_key or "")
        if not connection:
            raise BridgeUnavailable(f"Unity project is not connected: {project_key or 'no project selected'}")
        if connection.state not in {"ready", "compiling", "compile_failed"}:
            raise BridgeUnavailable(f"Unity bridge is {connection.state}: {project_key}")
        status = self.connection_status(project_key)
        if not status["routeReady"]:
            raise BridgeUnavailable(str(status["routeError"]))
        return connection

    def connection_status(self, project_key: str | None) -> dict[str, Any]:
        connection = self.connections.get(project_key or "")
        if connection is None:
            return {"bridgeState": "offline", "routeReady": False, "routeError": "Unity bridge is not connected",
                    "heartbeatAgeSeconds": None, "mainThreadAgeSeconds": None,
                    "dispatcherQueueDepth": None, "inFlightRequests": None}
        heartbeat_age = max(0.0, time.monotonic() - connection.last_heartbeat)
        main_age = None
        if connection.main_thread_last_tick_utc:
            try:
                tick = datetime.fromisoformat(connection.main_thread_last_tick_utc.replace("Z", "+00:00"))
                if tick.tzinfo is not None:
                    main_age = max(0.0, (datetime.now(timezone.utc) - tick).total_seconds())
            except (ValueError, TypeError):
                pass
        state = connection.state
        if heartbeat_age > 35:
            state = "unresponsive"
        elif main_age is not None and main_age > 30 and state not in {"compiling", "reloading", "before_reload"}:
            state = "blocked"
        ready = state in {"ready", "compiling", "compile_failed"}
        return {"bridgeState": state, "routeReady": ready,
                "routeError": None if ready else f"Unity bridge is {state}",
                "heartbeatAgeSeconds": round(heartbeat_age, 3),
                "mainThreadAgeSeconds": round(main_age, 3) if main_age is not None else None,
                "dispatcherQueueDepth": connection.dispatcher_queue_depth,
                "inFlightRequests": connection.in_flight_requests}

    async def activate(self, project_key: str) -> BridgeConnection:
        connection = self.connections.get(project_key)
        if not connection:
            raise BridgeUnavailable(f"Unity project is not connected: {project_key}")
        self.store.set_active_project(project_key)
        await self._publish_route_state()
        return connection

    async def _publish_route_state(self) -> None:
        active = self.active_project_key
        sends = [
            asyncio.wait_for(connection.send(envelope(
                "route_state",
                {"active": project_key == active},
                identity=connection.identity,
            )), timeout=5)
            for project_key, connection in self.connections.items()
        ]
        if sends:
            await asyncio.gather(*sends, return_exceptions=True)

    async def wait_for_connection(
        self, project_key: str, timeout: float
    ) -> BridgeConnection | None:
        connection = self.connections.get(project_key)
        if connection and self.connection_status(project_key)["routeReady"]:
            return connection
        event = self._connection_events.setdefault(project_key, asyncio.Event())
        try:
            await asyncio.wait_for(event.wait(), timeout=max(0, timeout))
        except asyncio.TimeoutError:
            return None
        connection = self.connections.get(project_key)
        return connection if connection and self.connection_status(project_key)["routeReady"] else None

    async def dispatch(
        self,
        request_id: str,
        json_rpc: dict[str, Any],
        *,
        timeout: float,
        project_key: str | None = None,
    ) -> dict[str, Any]:
        connection = self.connection_for(project_key if project_key is not None else self.active_project_key)
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        correlation_id = uuid4().hex
        connection.pending[correlation_id] = future
        started = asyncio.Event()
        connection.started[correlation_id] = started
        message = envelope(
            "command",
            {"requestId": request_id, "jsonRpc": json_rpc},
            identity=connection.identity,
            correlation_id=correlation_id,
        )
        activation: FocusActivation | None = None
        started_task: asyncio.Task[bool] | None = None
        deadline = asyncio.get_running_loop().time() + timeout
        activation_task: asyncio.Task[FocusActivation] | None = None
        try:
            def mark_sending() -> None:
                if self.connection_for(connection.identity.project_key) is not connection:
                    raise BridgeUnavailable("Unity generation changed while this command waited to send")
                self.store.update_request(request_id, "sending_to_unity")

            async def wait_for_result() -> dict[str, Any]:
                nonlocal started_task, activation_task, activation
                await connection.send(message, on_sending=mark_sending)
                if self.focus_assist_enabled and self._requires_editor_attention(json_rpc, connection.identity.project_key):
                    started_task = asyncio.create_task(started.wait())
                    done, _ = await asyncio.wait(
                        {future, started_task},
                        timeout=min(max(0, deadline - asyncio.get_running_loop().time()), self.focus_assist_delay_seconds),
                        return_when=asyncio.FIRST_COMPLETED,
                    )
                    if future in done:
                        return future.result()
                    if not started.is_set():
                        activation_task = asyncio.create_task(asyncio.to_thread(
                            self.focus_assist.activate, connection.identity.process_id,
                        ))
                        activation = await asyncio.shield(activation_task)
                        self.store.record_event(
                            connection.identity.project_key,
                            connection.identity.generation_id,
                            "focus_assist",
                            {"requestId": request_id, **asdict(activation)},
                        )
                return await future
            return await asyncio.wait_for(wait_for_result(), timeout=timeout)
        finally:
            connection.pending.pop(correlation_id, None)
            connection.started.pop(correlation_id, None)
            if not future.done():
                future.cancel()
            elif not future.cancelled():
                future.exception()
            if started_task is not None and not started_task.done():
                started_task.cancel()
            if activation_task is not None:
                async def restore_focus() -> None:
                    try:
                        acquired = await activation_task
                        await asyncio.to_thread(self.focus_assist.restore, acquired)
                    except Exception:
                        pass

                restoration = asyncio.create_task(restore_focus())
                remaining = deadline - asyncio.get_running_loop().time()
                if remaining > 0:
                    try:
                        await asyncio.wait_for(asyncio.shield(restoration), timeout=remaining)
                    except asyncio.TimeoutError:
                        pass

    def _requires_editor_attention(self, json_rpc: dict[str, Any], project_key: str | None = None) -> bool:
        if json_rpc.get("method") != "tools/call":
            return False
        params = json_rpc.get("params")
        if not isinstance(params, dict):
            return False
        descriptor = self.registry.descriptor(
            project_key if project_key is not None else self.active_project_key,
            str(params.get("name", "")),
        )
        if not descriptor:
            return False
        meta = descriptor.get("_meta")
        if not isinstance(meta, dict):
            return False
        requirements = meta.get("executionRequirements")
        attention = {"EditorUpdates", "SceneView", "GameView", "LongRunningJob"}
        return (
            isinstance(requirements, list)
            and any(str(item) in attention for item in requirements)
        ) or meta.get("headlessSafe") is False

    async def dispatch_control(self, control: str, payload: dict[str, Any], *, project_key: str | None = None, timeout: float = 60) -> None:
        connection = self.connection_for(project_key if project_key is not None else self.active_project_key)
        await asyncio.wait_for(connection.send(envelope(
            "command",
            {"control": control, **payload},
            identity=connection.identity,
            correlation_id=payload.get("jobId"),
        )), timeout=timeout)

    async def cancel(self, request_id: str, *, project_key: str | None = None, timeout: float = 5) -> None:
        try:
            connection = self.connection_for(project_key if project_key is not None else self.active_project_key)
        except BridgeUnavailable:
            return
        await asyncio.wait_for(connection.send(envelope(
            "cancel", {"requestId": request_id}, identity=connection.identity, correlation_id=request_id
        )), timeout=timeout)

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
            record = self.store.get_request(request_id) if request_id else None
            if record and record.get("state") not in {"completed", "cancelled", "failed", "outcome_unknown"}:
                self.store.update_request(
                    request_id, "accepted_by_unity", generation_id=connection.identity.generation_id
                )
            return
        if message_type == "execution_started":
            request_id = str(payload.get("requestId", ""))
            record = self.store.get_request(request_id) if request_id else None
            if record and record.get("state") not in {"completed", "cancelled", "failed", "outcome_unknown"}:
                self.store.update_request(
                    request_id, "running_in_unity", generation_id=connection.identity.generation_id
                )
                self.store.record_event(
                    connection.identity.project_key,
                    connection.identity.generation_id,
                    "execution_started",
                    payload,
                )
            started = connection.started.get(str(correlation_id))
            if started:
                started.set()
            return
        if message_type == "result":
            future = connection.pending.get(str(correlation_id))
            if future and not future.done():
                future.set_result(payload["jsonRpc"])
            return
        if message_type == "heartbeat":
            connection.last_heartbeat = time.monotonic()
            if isinstance(payload.get("state"), str):
                connection.state = payload["state"]
            if isinstance(payload.get("mainThreadLastTickUtc"), str):
                connection.main_thread_last_tick_utc = payload["mainThreadLastTickUtc"]
            for field_name, attribute in (("dispatcherQueueDepth", "dispatcher_queue_depth"),
                                          ("inFlightRequests", "in_flight_requests")):
                value = payload.get(field_name)
                if isinstance(value, int) and value >= 0:
                    setattr(connection, attribute, value)
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
        job = self.store.get_job(str(job_id))
        if not job or job.get("project_key") != connection.identity.project_key:
            return
        if job.get("state") in {"completed", "failed", "cancelled"}:
            return
        if event_type in {"refresh_accepted", "compilation_started"}:
            self.store.update_job(job_id, "running", progress=0.25, message=event_type)
        elif event_type in {"compilation_finished", "before_reload"}:
            evidence = {**payload, "generationId": connection.identity.generation_id}
            self.store.update_job(
                job_id, "waiting_for_reconnect", progress=0.75, message=event_type,
                result=evidence if event_type == "compilation_finished" else None,
            )
        elif event_type == "compile_failed":
            self.store.update_job(job_id, "failed", progress=1, message=event_type, error=payload.get("error"), result=payload)
        elif event_type == "refresh_completed":
            self.store.update_job(job_id, "completed", progress=1, message=event_type, result=payload)
        elif event_type == "refresh_cancelled":
            self.store.update_job(job_id, "cancelled", progress=1, message=event_type, result=payload)
        elif event_type == "refresh_cancel_rejected":
            self.store.update_job(job_id, "running", message=str(payload.get("error") or event_type))
        self._job_events.setdefault(str(job_id), asyncio.Event()).set()

    async def _resume_refresh_jobs(self, project_key: str) -> None:
        connection = self.connections.get(project_key)
        if connection is None or connection.state != "ready":
            return
        rows = self.store.list_jobs(project_key, "waiting_for_reconnect")
        for row in rows:
            result = dict(row.get("result") or {})
            if row.get("kind") != "refresh_compile":
                continue
            if (result.get("event") != "compilation_finished"
                    or not result.get("generationId")
                    or result["generationId"] == connection.identity.generation_id):
                continue
            if result.get("error") or any(
                str(item.get("severity", "")).lower() == "error"
                for item in result.get("messages", []) if isinstance(item, dict)
            ):
                continue
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

    def notify_job_changed(self, job_id: str) -> None:
        self._job_events.setdefault(str(job_id), asyncio.Event()).set()
