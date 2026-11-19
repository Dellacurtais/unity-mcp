from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import math
import time
from collections import Counter
from typing import Any
from uuid import uuid4

from broker_version import BROKER_VERSION
from config import BrokerConfig
import editor_control
from lifecycle import EditorLifecycle, LifecycleError
from registry import RegistryCache
from router import BridgeRouter, BridgeUnavailable
from store import BrokerStore

SUPPORTED_PROTOCOLS = {"2024-11-05", "2025-03-26", "2025-06-18"}
TERMINAL_REQUEST_STATES = {"completed", "cancelled", "failed", "outcome_unknown"}
UNITY_ACCEPTED_STATES = {"accepted_by_unity", "running_in_unity"}
UNCERTAIN_REQUEST_STATES = UNITY_ACCEPTED_STATES | {"sending_to_unity"}
TERMINAL_JOB_STATES = {"completed", "failed", "cancelled"}
SESSION_LOG = logging.getLogger("uvicorn.error").getChild("sessions")
NEWEST_IMPLEMENTED_PROTOCOL = "2025-06-18"


def ok_response(request_id: Any, result: Any) -> dict[str, Any]:
    response: dict[str, Any] = {"jsonrpc": "2.0", "result": result}
    if request_id is not None:
        response["id"] = request_id
    return response


def error_response(request_id: Any, code: int, message: str, data: Any = None) -> dict[str, Any]:
    error: dict[str, Any] = {"code": code, "message": message}
    if data is not None:
        error["data"] = data
    response: dict[str, Any] = {"jsonrpc": "2.0", "error": error}
    if request_id is not None:
        response["id"] = request_id
    return response


def tool_error(request_id: Any, code: str, message: str, data: Any = None) -> dict[str, Any]:
    structured: dict[str, Any] = {"code": code, "message": message}
    if data is not None:
        structured["data"] = data
    return ok_response(request_id, {
        "content": [{"type": "text", "text": f"[{code}] {message}"}],
        "structuredContent": structured,
        "isError": True,
    })


class McpBroker:
    MAX_INFLIGHT_REQUESTS = 128
    MAX_REQUESTS_PER_CLIENT = 32

    def __init__(self, config: BrokerConfig, store: BrokerStore, registry: RegistryCache, router: BridgeRouter) -> None:
        self.config = config
        self.store = store
        self.registry = registry
        self.router = router
        self.max_sessions = config.max_sessions
        self.session_idle_seconds = config.session_idle_seconds
        self.sessions: dict[str, str | None] = {}
        # Ordered from least to most recently used; every access moves a session to the end.
        self._session_access: dict[str, float] = {}
        self._session_clients: dict[str, str] = {}
        self._inflight: dict[str, asyncio.Task[dict[str, Any]]] = {}
        self._operation_jobs: dict[str, str] = {}
        self._client_requests: dict[tuple[str, str], tuple[str, str]] = {}
        self._notifications: set[asyncio.Task[Any]] = set()
        # ledger id -> (tool name, loop time started, project key), for pending answers.
        self._started: dict[str, tuple[str, float, str | None]] = {}
        # Sessions this process closed (DELETE) or evicted must keep answering 404.
        self._retired_sessions: dict[str, None] = {}
        self.lifecycle = EditorLifecycle(self)

    def create_session(self, client: str | None = None, origin: str | None = None) -> str:
        """Open a session, reclaiming idle ones first.

        Clients often disappear without DELETE. Idle sessions expire, and at capacity the
        least recently used session without pending work is evicted; its owner gets 404
        and must initialize again, as the MCP transport requires.
        """
        now = time.monotonic()
        busy = self._busy_sessions()
        self._expire_sessions(now, busy)
        while len(self.sessions) >= self.max_sessions:
            victim = next((session for session in self._session_access if session not in busy), None)
            if victim is None:
                SESSION_LOG.warning("MCP session rejected client=%r sessions=%d/%d (all have pending work)",
                                    client or "unknown", len(self.sessions), self.max_sessions)
                raise ValueError("MCP session capacity reached; every session has pending work")
            self._forget_session(victim, "evicted", now)
        session_id = uuid4().hex
        self.sessions[session_id] = self.router.active_project_key
        self._session_access[session_id] = now
        self._session_clients[session_id] = client or "unknown"
        SESSION_LOG.info("MCP session opened id=%s client=%r from=%s sessions=%d/%d",
                         session_id[:8], client or "unknown", origin or "-", len(self.sessions), self.max_sessions)
        return session_id

    def can_adopt(self, session_id: str) -> bool:
        return session_id not in self._session_access and session_id not in self._retired_sessions

    def adopt_session(self, session_id: str, client: str | None = None, origin: str | None = None) -> None:
        """Keep a session id the broker forgot (it restarted); see app.mcp_endpoint."""
        if session_id in self._session_access:
            return
        if session_id in self._retired_sessions:
            raise ValueError("MCP session was closed; initialize a new session")
        now = time.monotonic()
        busy = self._busy_sessions()
        self._expire_sessions(now, busy)
        while len(self.sessions) >= self.max_sessions:
            victim = next((session for session in self._session_access if session not in busy), None)
            if victim is None:
                raise ValueError("MCP session capacity reached; every session has pending work")
            self._forget_session(victim, "evicted", now)
        label = "".join(character if character.isprintable() else "?" for character in (client or "unknown"))[:200]
        self.sessions[session_id] = self.router.active_project_key
        self._session_access[session_id] = now
        self._session_clients[session_id] = label
        SESSION_LOG.info("MCP session adopted id=%s client=%r from=%s sessions=%d/%d",
                         session_id[:8], label, origin or "-", len(self.sessions), self.max_sessions)

    def has_session(self, session_id: str) -> bool:
        last_access = self._session_access.get(session_id)
        if last_access is None:
            return False
        now = time.monotonic()
        if now - last_access > self.session_idle_seconds and session_id not in self._busy_sessions():
            self._forget_session(session_id, "expired", now)
            return False
        del self._session_access[session_id]
        self._session_access[session_id] = now
        return True

    def drop_session(self, session_id: str) -> None:
        if session_id in self._session_access:
            self._forget_session(session_id, "closed", time.monotonic())

    def expire_sessions(self) -> int:
        return self._expire_sessions(time.monotonic(), self._busy_sessions())

    def session_stats(self) -> dict[str, Any]:
        return {
            "active": len(self.sessions),
            "max": self.max_sessions,
            "idleSeconds": self.session_idle_seconds,
            "byClient": dict(Counter(self._session_clients.values()).most_common(10)),
        }

    def _busy_sessions(self) -> set[str]:
        return {key[0] for key in self._client_requests}

    def _expire_sessions(self, now: float, busy: set[str]) -> int:
        expired = 0
        for session_id, last_access in list(self._session_access.items()):
            if now - last_access <= self.session_idle_seconds:
                break
            if session_id not in busy:
                self._forget_session(session_id, "expired", now)
                expired += 1
        return expired

    def _forget_session(self, session_id: str, reason: str, now: float) -> None:
        if reason in {"closed", "evicted"}:
            self._retired_sessions[session_id] = None
            while len(self._retired_sessions) > 4096:
                self._retired_sessions.pop(next(iter(self._retired_sessions)))
        last_access = self._session_access.pop(session_id, now)
        self.sessions.pop(session_id, None)
        client = self._session_clients.pop(session_id, "unknown")
        SESSION_LOG.info("MCP session %s id=%s client=%r idle=%ds sessions=%d/%d",
                         reason, session_id[:8], client, now - last_access, len(self.sessions), self.max_sessions)

    def _project_key(self, session_id: str | None, meta: dict[str, Any] | None = None) -> str | None:
        explicit = (meta or {}).get("projectKey")
        if isinstance(explicit, str) and explicit:
            return explicit
        if session_id is not None:
            if session_id not in self.sessions:
                return None
            project = self.sessions.get(session_id)
            if project is None:
                project = self.router.active_project_key
                self.sessions[session_id] = project
            return project
        return self.router.active_project_key

    @staticmethod
    def _client_key(session_id: str | None, request_id: Any) -> tuple[str, str]:
        return session_id or "legacy", json.dumps(request_id, sort_keys=True, separators=(",", ":"))

    @staticmethod
    def _client_response(response: dict[str, Any], request_id: Any) -> dict[str, Any]:
        result = dict(response)
        if request_id is None:
            result.pop("id", None)
        else:
            result["id"] = request_id
        return result

    async def close(self) -> None:
        tasks = [*self._inflight.values(), *self._notifications]
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

    async def process(self, payload: Any, protocol_header: str | None = None, session_id: str | None = None) -> tuple[int, Any | None]:
        if isinstance(payload, list):
            if not payload:
                return 200, error_response(None, -32600, "Invalid Request: empty batch")
            results = []
            for item in payload:
                result = await self._process_one(item, protocol_header, session_id)
                if result is not None:
                    results.append(result)
            return (200, results) if results else (202, None)
        result = await self._process_one(payload, protocol_header, session_id)
        return (200, result) if result is not None else (202, None)

    async def _process_one(self, request: Any, protocol_header: str | None, session_id: str | None = None) -> dict[str, Any] | None:
        if not isinstance(request, dict) or request.get("jsonrpc") != "2.0":
            return error_response(None, -32600, "Invalid Request")
        request_id = request.get("id")
        notification = "id" not in request
        method = request.get("method")
        params = request.get("params") if isinstance(request.get("params"), dict) else {}
        if not isinstance(method, str) or not method:
            return None if notification else error_response(request_id, -32600, "Invalid Request: method is required")

        if method == "initialize":
            requested = params.get("protocolVersion")
            # Newer clients are offered the newest revision this broker implements.
            selected = requested if isinstance(requested, str) and requested in SUPPORTED_PROTOCOLS else NEWEST_IMPLEMENTED_PROTOCOL
            return None if notification else ok_response(request_id, {
                "protocolVersion": selected,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": "unity-mcp-broker", "version": BROKER_VERSION},
                "instructions": (
                    "Unity tools are routed through a reload-safe broker. A call that is still running after "
                    "a short time returns status=running with a requestId instead of timing out: call "
                    "broker_wait_request with that requestId to get the result, never repeat the call. "
                    "broker_editor_status shows whether the Editor runs, reloads or is blocked by a dialog; "
                    "broker_close_editor / broker_open_editor quit and reopen it safely. Non-idempotent writes "
                    "may return OUTCOME_UNKNOWN after a disconnect."),
            })
        if method in {"notifications/initialized", "ping"}:
            return None if notification else ok_response(request_id, {})
        if method == "notifications/cancelled":
            target = self._client_requests.get(self._client_key(session_id, params.get("requestId")))
            if target is not None:
                ledger_id, project_key = target
                try:
                    job_id = self._operation_jobs.get(ledger_id)
                    if job_id:
                        await self._call_broker_tool(None, "broker_cancel_job", {"jobId": job_id}, project_key, session_id)
                    else:
                        await self.router.cancel(ledger_id, project_key=project_key)
                except (BridgeUnavailable, asyncio.TimeoutError):
                    pass
            return None if notification else ok_response(request_id, {"cancelled": params.get("requestId")})
        if method == "tools/list":
            meta = params.get("_meta") if isinstance(params.get("_meta"), dict) else {}
            try:
                result = self.registry.list_tools(self._project_key(session_id, meta), params)
            except ValueError as exception:
                return None if notification else error_response(request_id, -32602, str(exception))
            return None if notification else ok_response(request_id, result)
        if method == "tools/call":
            if notification:
                if len(self._notifications) >= self.MAX_INFLIGHT_REQUESTS:
                    return None
                task = asyncio.create_task(self._call_tool(None, params, request, session_id))
                self._notifications.add(task)
                def retire_notification(completed: asyncio.Task[Any]) -> None:
                    self._notifications.discard(completed)
                    if not completed.cancelled():
                        completed.exception()
                task.add_done_callback(retire_notification)
                return None
            return await self._call_tool(request_id, params, request, session_id)
        return None if notification else error_response(request_id, -32601, f"Method not found: {method}")

    async def _call_tool(self, request_id: Any, params: dict[str, Any], raw_request: dict[str, Any], session_id: str | None = None) -> dict[str, Any]:
        name = params.get("name")
        arguments = params.get("arguments") if isinstance(params.get("arguments"), dict) else {}
        if not isinstance(name, str) or not name:
            return error_response(request_id, -32602, "Tool name is required.")
        meta = params.get("_meta") if isinstance(params.get("_meta"), dict) else {}
        project_key = self._project_key(session_id, meta)
        if name.startswith("broker_"):
            try:
                return await self._call_broker_tool(request_id, name, arguments, project_key, session_id)
            except (ValueError, TypeError) as exception:
                return error_response(request_id, -32602, str(exception))

        descriptor = self.registry.descriptor(project_key, name)
        if descriptor is None:
            return error_response(request_id, -32602, f"Tool not found: {name}")
        annotations = descriptor.get("annotations", {})
        read_only = bool(annotations.get("readOnlyHint", False))
        idempotent = bool(annotations.get("idempotentHint", False))
        args_hash = hashlib.sha256(
            json.dumps(arguments, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()
        idempotency_key = meta.get("idempotencyKey")
        if idempotency_key is not None and (not isinstance(idempotency_key, str) or not idempotency_key or len(idempotency_key) > 256):
            return error_response(request_id, -32602, "idempotencyKey must be a nonempty string of at most 256 characters")
        client_key = self._client_key(session_id, request_id)
        if request_id is not None and client_key in self._client_requests:
            return error_response(request_id, -32600, "A request with this id is already running in this client session")
        if sum(key[0] == client_key[0] for key in self._client_requests) >= self.MAX_REQUESTS_PER_CLIENT:
            return tool_error(request_id, "BROKER_BUSY", "This client has too many pending requests; await an existing operation")
        if len(self._inflight) >= self.MAX_INFLIGHT_REQUESTS:
            return tool_error(request_id, "BROKER_BUSY", "Broker execution capacity reached; await an existing operation")
        ledger_id = uuid4().hex
        loop = asyncio.get_running_loop()
        sync_deadline = loop.time() + self.config.sync_wait_seconds
        connection = None
        try:
            connection = self.router.connection_for(project_key)
        except BridgeUnavailable as exception:
            # A Domain Reload or an Editor restart is temporary. Nothing was sent yet, so
            # waiting is safe for every tool; reads and idempotent tools may even queue.
            transient = self.router.is_transient(project_key, self.config.reconnect_grace_seconds)
            queueable = transient and (read_only or idempotent) and self.registry.get(project_key) is not None
            if not queueable:
                remaining = max(0.0, sync_deadline - loop.time() - 2.0) if transient else 0.0
                connection = await self.router.wait_for_connection(project_key or "", remaining) if project_key and remaining else None
                if connection is None:
                    return self._offline_error(request_id, project_key, str(exception))
        try:
            expected_generation = meta.get("expectedGenerationId")
            if (connection is not None and expected_generation is not None
                    and expected_generation != connection.identity.generation_id):
                return tool_error(request_id, "STALE_GENERATION", "Unity generation changed; inspect the project before retrying")
            existing = self.store.create_request(
                ledger_id, idempotency_key,
                connection.identity.project_key if connection is not None else project_key,
                connection.identity.generation_id if connection is not None else None,
                name, args_hash, read_only, idempotent,
            )
        except ValueError as exception:
            return tool_error(request_id, "IDEMPOTENCY_CONFLICT", str(exception))

        ledger_id = str(existing.get("request_id") or ledger_id)
        if existing.get("state") in TERMINAL_REQUEST_STATES:
            if existing.get("response"):
                return self._client_response(existing["response"], request_id)
            return tool_error(
                request_id,
                "REQUEST_TERMINAL",
                str(existing.get("error") or f"request is {existing.get('state')}"),
                {"requestId": ledger_id, "state": existing.get("state")},
            )

        project_key = str(existing.get("project_key") or (connection.identity.project_key if connection else project_key))
        task = self._inflight.get(ledger_id)
        if task is None:
            # No await separates the ledger reservation from the task reservation.
            # All duplicate callers join this task instead of resetting accepted state.
            if existing.get("state") in UNCERTAIN_REQUEST_STATES:
                return tool_error(request_id, "REQUEST_IN_PROGRESS", "The existing operation is still accepted by Unity", {"requestId": ledger_id})
            internal_request = {**raw_request, "id": ledger_id}
            task = asyncio.create_task(self._execute_tool(
                ledger_id, internal_request, descriptor, meta, project_key, read_only, idempotent,
            ))
            self._inflight[ledger_id] = task
            self._started[ledger_id] = (name, loop.time(), project_key)
            def retire(completed: asyncio.Task[dict[str, Any]]) -> None:
                if self._inflight.get(ledger_id) is completed:
                    self._inflight.pop(ledger_id, None)
                    self._operation_jobs.pop(ledger_id, None)
                    self._started.pop(ledger_id, None)
                if not completed.cancelled():
                    completed.exception()
            task.add_done_callback(retire)
        if request_id is not None:
            self._client_requests[client_key] = (ledger_id, project_key)
        try:
            remaining = max(0.0, sync_deadline - loop.time())
            try:
                response = await asyncio.wait_for(asyncio.shield(task), timeout=remaining)
            except asyncio.TimeoutError:
                # Never hold an MCP reply long enough for the client to give up on it.
                return self._pending_response(request_id, ledger_id)
            return self._client_response(response, request_id)
        finally:
            if request_id is not None:
                self._client_requests.pop(client_key, None)

    def _pending_response(self, request_id: Any, ledger_id: str) -> dict[str, Any]:
        name, started, project_key = self._started.get(ledger_id, ("tool", asyncio.get_running_loop().time(), None))
        record = self.store.get_request(ledger_id) or {}
        job_id = self._operation_jobs.get(ledger_id)
        job = self.store.get_job(job_id) if job_id else None
        liveness = self.router.connection_status(project_key)
        payload: dict[str, Any] = {
            "status": "running",
            "pending": True,
            "requestId": ledger_id,
            "tool": name,
            "requestState": record.get("state"),
            "elapsedSeconds": round(asyncio.get_running_loop().time() - started, 1),
            "projectKey": project_key,
            "bridgeState": liveness.get("bridgeState"),
            "next": {"tool": "broker_wait_request", "arguments": {"requestId": ledger_id}},
            "message": (f"{name} has not finished yet; it keeps running. Call broker_wait_request with this "
                        f"requestId to wait for the result. Do not call {name} again."),
        }
        if job_id:
            payload["unityJobId"] = job_id
            if job:
                payload["job"] = {key: job.get(key) for key in ("state", "progress", "message")}
        text = (f"[PENDING] {name} is still running (requestId {ledger_id}, {payload['elapsedSeconds']} s). "
                f"It has NOT finished. Call broker_wait_request {{\"requestId\": \"{ledger_id}\"}} to wait for its result.")
        return ok_response(request_id, {
            "content": [{"type": "text", "text": text}],
            "structuredContent": payload,
            "isError": False,
            "_meta": {"pending": True, "brokerRequestId": ledger_id, "source": "broker"},
        })

    def _offline_error(self, request_id: Any, project_key: str | None, message: str) -> dict[str, Any]:
        liveness = self.router.connection_status(project_key)
        data: dict[str, Any] = {"projectKey": project_key, "retrySafe": True, **liveness}
        connection = self.router.connections.get(project_key or "")
        if connection is not None:
            try:
                dialogs = [dialog.as_dict() for dialog in editor_control.list_dialogs(connection.identity.process_id)]
            except Exception:
                dialogs = []
            if dialogs:
                data["dialogs"] = dialogs
                message += "; a dialog is open in the Editor: " + ", ".join(repr(item["title"]) for item in dialogs)
        data["hint"] = ("Nothing was sent to Unity, so retrying is safe. broker_editor_status shows whether the "
                        "Editor is running, reloading or blocked by a dialog; broker_open_editor starts it.")
        return tool_error(request_id, "UNITY_OFFLINE", message, data)

    async def _execute_tool(
        self, ledger_id: str, raw_request: dict[str, Any], descriptor: dict[str, Any],
        meta: dict[str, Any], project_key: str, read_only: bool, idempotent: bool,
    ) -> dict[str, Any]:
        request_id = ledger_id
        self.store.update_request(ledger_id, "dispatched")
        deadline = asyncio.get_running_loop().time() + self.config.request_timeout_seconds
        while True:
            try:
                remaining = deadline - asyncio.get_running_loop().time()
                if remaining <= 0:
                    raise asyncio.TimeoutError()
                expected_generation = meta.get("expectedGenerationId")
                if expected_generation is not None:
                    current_connection = self.router.connection_for(project_key)
                    if current_connection.identity.generation_id != expected_generation:
                        response = tool_error(request_id, "STALE_GENERATION", "Unity generation changed before execution; inspect the project before retrying")
                        self.store.update_request(ledger_id, "failed", response=response)
                        return response
                response = await self.router.dispatch(ledger_id, raw_request, timeout=remaining, project_key=project_key)
                if self._should_auto_wait_job(descriptor, meta, response):
                    # The client no longer waits on this task, so a Unity job may run as
                    # long as it needs (tests, builds) instead of the per-call deadline.
                    response = await self._auto_wait_unity_job(
                        response,
                        descriptor,
                        ledger_id,
                        project_key,
                        max(deadline, asyncio.get_running_loop().time() + self.config.job_wait_seconds),
                    )
                self.store.update_request(ledger_id, "completed", response=response)
                return response
            except BridgeUnavailable as exception:
                current = self.store.get_request(ledger_id) or {}
                accepted = current.get("state") in UNCERTAIN_REQUEST_STATES
                retry_safe = not accepted or read_only or idempotent
                if not retry_safe:
                    self.store.update_request(ledger_id, "outcome_unknown", error=str(exception))
                    return tool_error(
                        request_id,
                        "OUTCOME_UNKNOWN",
                        str(exception),
                        {"requestId": ledger_id, "retrySafe": False},
                    )
                self.store.update_request(ledger_id, "waiting_for_reconnect", error=str(exception))
                remaining = deadline - asyncio.get_running_loop().time()
                if remaining <= 0:
                    return tool_error(
                        request_id,
                        "UNITY_RECONNECT_REQUIRED",
                        str(exception),
                        {"requestId": ledger_id, "retrySafe": True},
                    )
                reconnect_wait = min(remaining, self.config.reconnect_grace_seconds)
                reconnected = await self.router.wait_for_connection(project_key, reconnect_wait)
                if reconnected is None:
                    return tool_error(
                        request_id,
                        "UNITY_RECONNECT_REQUIRED",
                        "Unity did not reconnect before the request timeout.",
                        {"requestId": ledger_id, "retrySafe": True},
                    )
                self.store.update_request(
                    ledger_id,
                    "dispatched",
                    generation_id=reconnected.identity.generation_id,
                )
            except asyncio.TimeoutError:
                current = self.store.get_request(ledger_id) or {}
                accepted = current.get("state") in UNCERTAIN_REQUEST_STATES
                state = "waiting_for_reconnect" if not accepted or read_only or idempotent else "outcome_unknown"
                self.store.update_request(ledger_id, state, error="Unity tool timed out")
                code = "UNITY_RECONNECT_REQUIRED" if state == "waiting_for_reconnect" else "OUTCOME_UNKNOWN"
                return tool_error(
                    request_id,
                    code,
                    "Unity tool timed out after dispatch",
                    {"requestId": ledger_id, "retrySafe": state != "outcome_unknown"},
                )

    def _should_auto_wait_job(
        self,
        descriptor: dict[str, Any],
        request_meta: dict[str, Any],
        response: dict[str, Any],
    ) -> bool:
        if not self.config.auto_wait_jobs or request_meta.get("autoWaitJob") is False:
            return False
        descriptor_meta = descriptor.get("_meta")
        if not isinstance(descriptor_meta, dict):
            return False
        lifecycle = descriptor_meta.get("jobLifecycle")
        if not isinstance(lifecycle, dict) or lifecycle.get("autoWait") is not True:
            return False
        result = response.get("result")
        return isinstance(result, dict) and result.get("isError") is not True

    @staticmethod
    def _structured_content(response: dict[str, Any]) -> dict[str, Any] | None:
        result = response.get("result")
        if not isinstance(result, dict):
            return None
        structured = result.get("structuredContent")
        return structured if isinstance(structured, dict) else None

    @staticmethod
    def _job_state(status: Any) -> str:
        normalized = str(status or "queued").replace("_", "").lower()
        return {
            "queued": "queued",
            "running": "running",
            "completed": "completed",
            "failed": "failed",
            "cancelrequested": "cancel_requested",
            "cancelled": "cancelled",
        }.get(normalized, "running")

    def _mirror_unity_job(self, project_key: str, record: dict[str, Any]) -> dict[str, Any]:
        job_id = str(record.get("jobId", ""))
        kind = str(record.get("kind", "unity_tool"))
        self.store.ensure_job(job_id, kind, project_key, str(record.get("message") or "queued"))
        state = self._job_state(record.get("status"))
        self.store.update_job(
            job_id,
            state,
            progress=float(record.get("progress") or 0),
            message=str(record.get("message") or state),
            result=record,
            error=str(record.get("error")) if record.get("error") else None,
        )
        self.router.notify_job_changed(job_id)
        return self.store.get_job(job_id) or {}

    @staticmethod
    def _job_response(
        response: dict[str, Any],
        job: dict[str, Any],
        *,
        auto_wait_timed_out: bool = False,
    ) -> dict[str, Any]:
        result = response.get("result")
        if not isinstance(result, dict):
            return response
        final = dict(response)
        final_result = dict(result)
        final["result"] = final_result
        unity_record = job.get("result") if isinstance(job.get("result"), dict) else job
        final_result["structuredContent"] = unity_record
        final_result["content"] = [{
            "type": "text",
            "text": json.dumps(unity_record, separators=(",", ":")),
        }]
        final_result["isError"] = job.get("state") in {"failed", "cancelled"}
        result_meta = final_result.get("_meta")
        final_result["_meta"] = {
            **(result_meta if isinstance(result_meta, dict) else {}),
            "autoWaitedJob": True,
            "brokerJobId": job.get("job_id"),
            "autoWaitTimedOut": auto_wait_timed_out,
        }
        return final

    async def _auto_wait_unity_job(
        self,
        response: dict[str, Any],
        descriptor: dict[str, Any],
        ledger_id: str,
        project_key: str,
        deadline: float,
    ) -> dict[str, Any]:
        record = self._structured_content(response)
        if not record or not record.get("jobId"):
            return response
        job = self._mirror_unity_job(project_key, record)
        if job.get("state") in TERMINAL_JOB_STATES:
            return self._job_response(response, job)

        descriptor_meta = descriptor.get("_meta") if isinstance(descriptor.get("_meta"), dict) else {}
        lifecycle = descriptor_meta.get("jobLifecycle") if isinstance(descriptor_meta, dict) else {}
        wait_tool = str(lifecycle.get("waitTool") or "wait_tool_job")
        job_id = str(record["jobId"])
        self._operation_jobs[ledger_id] = job_id
        poll_number = 0
        while True:
            poll_number += 1
            # A fresh id per poll: a poll still stuck behind a busy Editor must not
            # collide with the next one ("Duplicate MCP request id").
            poll_id = f"{ledger_id}:job:{poll_number}"
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                return self._job_response(
                    response,
                    self.store.get_job(job_id) or job,
                    auto_wait_timed_out=True,
                )
            poll_seconds = min(15.0, max(0.1, remaining - 0.1))
            poll_request = {
                "jsonrpc": "2.0",
                "id": poll_id,
                "method": "tools/call",
                "params": {
                    "name": wait_tool,
                    "arguments": {"jobId": job_id, "timeoutSeconds": poll_seconds},
                },
            }
            try:
                poll_response = await self.router.dispatch(
                    poll_id,
                    poll_request,
                    timeout=min(remaining, poll_seconds + 5.0),
                    project_key=project_key,
                )
            except (BridgeUnavailable, asyncio.TimeoutError) as exception:
                if isinstance(exception, asyncio.TimeoutError):
                    try:
                        await self.router.cancel(poll_id, project_key=project_key)
                    except (BridgeUnavailable, asyncio.TimeoutError):
                        pass
                self.store.update_job(
                    job_id,
                    "waiting_for_reconnect",
                    message="waiting for Unity bridge",
                )
                remaining = deadline - asyncio.get_running_loop().time()
                if remaining <= 0:
                    continue
                await self.router.wait_for_connection(
                    project_key,
                    min(remaining, self.config.reconnect_grace_seconds),
                )
                continue

            poll_result = poll_response.get("result")
            record = self._structured_content(poll_response)
            if isinstance(poll_result, dict) and poll_result.get("isError") is True:
                message = "Unity job could not be recovered after reconnect"
                if record and record.get("message"):
                    message = str(record["message"])
                self.store.update_job(job_id, "failed", progress=1, message=message, error=message)
                self.router.notify_job_changed(job_id)
                return self._job_response(response, self.store.get_job(job_id) or job)
            if not record or str(record.get("jobId", "")) != job_id:
                continue
            job = self._mirror_unity_job(project_key, record)
            if job.get("state") in TERMINAL_JOB_STATES:
                return self._job_response(response, job)

    async def _call_broker_tool(self, request_id: Any, name: str, arguments: dict[str, Any], project_key: str | None = None, session_id: str | None = None) -> dict[str, Any]:
        if name == "broker_get_status":
            active = project_key
            connection = self.router.connections.get(active or "")
            liveness = self.router.connection_status(active)
            return self._broker_result(request_id, {
                "service": "unity-mcp-broker",
                "activeProjectKey": active,
                "activeProjectName": connection.identity.project_name if connection else None,
                "activeProjectPath": connection.identity.project_path if connection else None,
                "bridgeConnected": connection is not None,
                **liveness,
                "generationId": connection.identity.generation_id if connection else None,
                "registryHash": connection.identity.registry_hash if connection else None,
                "connectedProjects": len(self.router.connections),
                "focusAssistEnabled": self.config.focus_assist_enabled,
                "focusAssistDelaySeconds": self.config.focus_assist_delay_seconds,
                "autoWaitJobs": self.config.auto_wait_jobs,
                "syncWaitSeconds": self.config.sync_wait_seconds,
                "pendingRequests": len(self._inflight),
                "sessions": self.session_stats(),
            })
        if name == "broker_list_projects":
            return self._broker_result(request_id, {"projects": self.store.list_projects(), "activeProjectKey": project_key})
        if name == "broker_activate_project":
            try:
                selected = str(arguments.get("projectKey", ""))
                if session_id is None:
                    connection = await self.router.activate(selected)
                else:
                    connection = self.router.connection_for(selected)
                    if session_id in self.sessions:
                        self.sessions[session_id] = selected
            except BridgeUnavailable as exception:
                return tool_error(request_id, "PROJECT_OFFLINE", str(exception))
            return self._broker_result(request_id, {"activeProjectKey": connection.identity.project_key})
        if name == "broker_get_request":
            record = self.store.get_request(str(arguments.get("requestId", "")))
            return self._broker_result(request_id, record) if record else tool_error(request_id, "REQUEST_NOT_FOUND", "request was not found")
        if name == "broker_get_job":
            job = self.store.get_job(str(arguments.get("jobId", "")))
            return self._broker_result(request_id, job) if job else tool_error(request_id, "JOB_NOT_FOUND", "job was not found")
        if name == "broker_wait_job":
            timeout = float(arguments.get("timeoutSeconds", self.config.max_wait_call_seconds))
            if not math.isfinite(timeout) or timeout < 0:
                raise ValueError("timeoutSeconds must be a finite nonnegative number")
            job = await self.router.wait_job(str(arguments.get("jobId", "")),
                                             min(timeout, self.config.max_wait_call_seconds, self.config.request_timeout_seconds))
            return self._broker_result(request_id, job) if job else tool_error(request_id, "JOB_NOT_FOUND", "job was not found")
        if name == "broker_cancel_job":
            job_id = str(arguments.get("jobId", ""))
            job = self.store.get_job(job_id)
            if not job:
                return tool_error(request_id, "JOB_NOT_FOUND", "job was not found")
            if job.get("state") in TERMINAL_JOB_STATES:
                return self._broker_result(request_id, job)
            job_project = str(job.get("project_key") or "")
            if job.get("kind") == "refresh_compile":
                self.store.update_job(job_id, "cancel_requested", message="cancellation requested; awaiting Unity acknowledgement")
                try:
                    await self.router.dispatch_control("cancel_job", {"jobId": job_id}, project_key=job_project,
                                                       timeout=min(60.0, self.config.request_timeout_seconds))
                except (BridgeUnavailable, asyncio.TimeoutError) as exception:
                    return tool_error(request_id, "UNITY_OFFLINE", str(exception))
                return self._broker_result(request_id, self.store.get_job(job_id))
            self.store.update_job(job_id, "cancel_requested", message="cancellation requested")
            try:
                cancel_response = await self.router.dispatch(
                    f"cancel:{job_id}",
                    {
                        "jsonrpc": "2.0",
                        "id": f"cancel:{job_id}",
                        "method": "tools/call",
                        "params": {"name": "cancel_tool_job", "arguments": {"jobId": job_id}},
                    },
                    timeout=min(60.0, self.config.request_timeout_seconds),
                    project_key=job_project,
                )
            except (BridgeUnavailable, asyncio.TimeoutError) as exception:
                return tool_error(request_id, "UNITY_OFFLINE", str(exception))
            cancel_result = cancel_response.get("result")
            record = self._structured_content(cancel_response)
            if isinstance(cancel_result, dict) and cancel_result.get("isError") is True:
                message = str((record or {}).get("message") or "Unity rejected job cancellation")
                return tool_error(request_id, "JOB_CANCEL_REJECTED", message, record)
            if record and record.get("jobId"):
                self._mirror_unity_job(str(job.get("project_key") or ""), record)
            return self._broker_result(request_id, self.store.get_job(job_id))
        if name == "broker_wait_request":
            return await self._wait_request(request_id, arguments)
        if name == "broker_list_requests":
            limit = int(arguments.get("limit", 20))
            records = self.store.list_requests(
                project_key=arguments.get("projectKey") or None, state=arguments.get("state") or None,
                tool_name=arguments.get("tool") or None, limit=limit)
            for record in records:
                record["inFlight"] = record["request_id"] in self._inflight
                if record["request_id"] in self._operation_jobs:
                    record["unityJobId"] = self._operation_jobs[record["request_id"]]
            return self._broker_result(request_id, {"requests": records, "inFlight": len(self._inflight)})
        if name == "broker_list_jobs":
            records = self.store.list_recent_jobs(
                project_key=arguments.get("projectKey") or None, state=arguments.get("state") or None,
                kind=arguments.get("kind") or None, limit=int(arguments.get("limit", 20)))
            return self._broker_result(request_id, {"jobs": records})
        if name == "broker_cancel_request":
            ledger_id = str(arguments.get("requestId", ""))
            if ledger_id not in self._inflight:
                record = self.store.get_request(ledger_id)
                if record is None:
                    return tool_error(request_id, "REQUEST_NOT_FOUND", "request was not found")
                return self._broker_result(request_id, {"requestId": ledger_id, "state": record.get("state"),
                                                        "cancelled": False, "reason": "not running"})
            job_id = self._operation_jobs.get(ledger_id)
            if job_id:
                return await self._call_broker_tool(request_id, "broker_cancel_job", {"jobId": job_id}, project_key, session_id)
            _, _, request_project = self._started.get(ledger_id, (None, None, project_key))
            try:
                await self.router.cancel(ledger_id, project_key=request_project)
            except (BridgeUnavailable, asyncio.TimeoutError) as exception:
                return tool_error(request_id, "UNITY_OFFLINE", str(exception))
            return self._broker_result(request_id, {"requestId": ledger_id, "cancelRequested": True,
                                                    "note": "Unity cancels cooperatively; a write that already ran is not undone."})
        if name in {"broker_editor_status", "broker_close_editor", "broker_open_editor", "broker_restart_editor",
                    "broker_press_dialog_button", "broker_cleanup_orphans"}:
            return await self._lifecycle_tool(request_id, name, arguments, project_key)
        if name == "broker_start_refresh_compile":
            try:
                connection = self.router.connection_for(project_key)
            except BridgeUnavailable as exception:
                return tool_error(request_id, "UNITY_OFFLINE", str(exception))
            job = self.store.create_job("refresh_compile", connection.identity.project_key)
            try:
                self.store.update_job(job["job_id"], "dispatched", progress=0.05, message="sending to Unity")
                await self.router.dispatch_control("refresh_compile", {
                    "jobId": job["job_id"],
                    "includeWarnings": bool(arguments.get("includeWarnings", True)),
                }, project_key=connection.identity.project_key, timeout=min(60.0, self.config.request_timeout_seconds))
            except (BridgeUnavailable, asyncio.TimeoutError) as exception:
                self.store.update_job(job["job_id"], "waiting_for_reconnect", message=str(exception))
            return self._broker_result(request_id, self.store.get_job(job["job_id"]))
        return error_response(request_id, -32602, f"Broker tool not found: {name}")

    async def _wait_request(self, request_id: Any, arguments: dict[str, Any]) -> dict[str, Any]:
        ledger_id = str(arguments.get("requestId", ""))
        timeout = float(arguments.get("timeoutSeconds", self.config.max_wait_call_seconds))
        if not math.isfinite(timeout) or timeout < 0:
            raise ValueError("timeoutSeconds must be a finite nonnegative number")
        timeout = min(timeout, self.config.max_wait_call_seconds)
        task = self._inflight.get(ledger_id)
        if task is not None:
            try:
                response = await asyncio.wait_for(asyncio.shield(task), timeout=timeout)
            except asyncio.TimeoutError:
                return self._pending_response(request_id, ledger_id)
            return self._client_response(response, request_id)
        record = self.store.get_request(ledger_id)
        if record is None:
            return tool_error(request_id, "REQUEST_NOT_FOUND", "request was not found; broker_list_requests lists recent ones")
        state = str(record.get("state"))
        if state == "completed" and isinstance(record.get("response"), dict):
            return self._client_response(record["response"], request_id)
        details = {key: record.get(key) for key in ("request_id", "tool_name", "state", "error", "created_utc", "updated_utc")}
        if state in TERMINAL_REQUEST_STATES:
            return tool_error(request_id, "REQUEST_" + state.upper(), str(record.get("error") or state), details)
        return tool_error(
            request_id, "REQUEST_ORPHANED",
            "The broker restarted while this request was running, so its result is no longer tracked. "
            "Inspect the Unity state before retrying; for a Unity job use wait_tool_job with its unityJobId.",
            details)

    async def _lifecycle_tool(self, request_id: Any, name: str, arguments: dict[str, Any], project_key: str | None) -> dict[str, Any]:
        selected = arguments.get("projectKey") or project_key
        try:
            if name == "broker_editor_status":
                value = await self.lifecycle.status(arguments.get("projectKey") or None)
            elif name == "broker_close_editor":
                value = await self.lifecycle.close(
                    selected, confirm=arguments.get("confirm") is True,
                    save_dirty_scenes=arguments.get("saveDirtyScenes") is True,
                    timeout_seconds=float(arguments.get("timeoutSeconds", 120)))
            elif name == "broker_open_editor":
                value = await self.lifecycle.open(
                    arguments.get("projectKey") or None, arguments.get("projectPath") or None,
                    confirm=arguments.get("confirm") is True)
            elif name == "broker_restart_editor":
                value = await self.lifecycle.restart(
                    selected, confirm=arguments.get("confirm") is True,
                    save_dirty_scenes=arguments.get("saveDirtyScenes") is True)
            elif name == "broker_press_dialog_button":
                value = await self.lifecycle.press_dialog_button(
                    selected, str(arguments.get("title", "")), str(arguments.get("button", "")),
                    confirm=arguments.get("confirm") is True)
            else:
                value = await self.lifecycle.cleanup(apply=arguments.get("apply") is True,
                                                     verbose=arguments.get("verbose") is True)
        except LifecycleError as exception:
            return tool_error(request_id, exception.code, str(exception), exception.data)
        except BridgeUnavailable as exception:
            return self._offline_error(request_id, selected, str(exception))
        except (OSError, RuntimeError, asyncio.TimeoutError) as exception:
            return tool_error(request_id, "LIFECYCLE_FAILED", f"{type(exception).__name__}: {exception}")
        return self._broker_result(request_id, value)

    @staticmethod
    def _broker_result(request_id: Any, value: Any) -> dict[str, Any]:
        return ok_response(request_id, {
            "content": [{"type": "text", "text": json.dumps(value, separators=(",", ":"))}],
            "structuredContent": value,
            "isError": False,
            "_meta": {"source": "broker"},
        })
