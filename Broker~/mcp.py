from __future__ import annotations

import asyncio
import hashlib
import json
from typing import Any
from uuid import uuid4

from broker_version import BROKER_VERSION
from config import BrokerConfig
from registry import BROKER_TOOLS, RegistryCache
from router import BridgeRouter, BridgeUnavailable
from store import BrokerStore

SUPPORTED_PROTOCOLS = {"2024-11-05", "2025-03-26", "2025-06-18"}
TERMINAL_REQUEST_STATES = {"completed", "cancelled", "failed", "outcome_unknown"}


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
    def __init__(self, config: BrokerConfig, store: BrokerStore, registry: RegistryCache, router: BridgeRouter) -> None:
        self.config = config
        self.store = store
        self.registry = registry
        self.router = router

    async def process(self, payload: Any, protocol_header: str | None = None) -> tuple[int, Any | None]:
        if isinstance(payload, list):
            if not payload:
                return 200, error_response(None, -32600, "Invalid Request: empty batch")
            results = []
            for item in payload:
                result = await self._process_one(item, protocol_header)
                if result is not None:
                    results.append(result)
            return (200, results) if results else (202, None)
        result = await self._process_one(payload, protocol_header)
        return (200, result) if result is not None else (202, None)

    async def _process_one(self, request: Any, protocol_header: str | None) -> dict[str, Any] | None:
        if not isinstance(request, dict) or request.get("jsonrpc") != "2.0":
            return error_response(None, -32600, "Invalid Request")
        request_id = request.get("id")
        notification = "id" not in request
        method = request.get("method")
        params = request.get("params") if isinstance(request.get("params"), dict) else {}
        if not method:
            return None if notification else error_response(request_id, -32600, "Invalid Request: method is required")

        if method == "initialize":
            requested = params.get("protocolVersion")
            selected = requested if requested in SUPPORTED_PROTOCOLS else "2025-03-26"
            return None if notification else ok_response(request_id, {
                "protocolVersion": selected,
                "capabilities": {"tools": {"listChanged": True}},
                "serverInfo": {"name": "unity-mcp-broker", "version": BROKER_VERSION},
                "instructions": "Unity tools are routed through a reload-safe broker. Non-idempotent writes may return OUTCOME_UNKNOWN after a disconnect.",
            })
        if method in {"notifications/initialized", "ping"}:
            return None if notification else ok_response(request_id, {})
        if method == "notifications/cancelled":
            await self.router.cancel(str(params.get("requestId", "")))
            return None if notification else ok_response(request_id, {"cancelled": params.get("requestId")})
        if method == "tools/list":
            tools, registry_hash = self.registry.combined(self.router.active_project_key)
            return None if notification else ok_response(request_id, {
                "tools": tools,
                "_meta": {
                    "registryCount": len(tools),
                    "registryHash": registry_hash,
                    "unityProjectKey": self.router.active_project_key,
                },
            })
        if method == "tools/call":
            if notification:
                asyncio.create_task(self._call_tool(None, params, request))
                return None
            return await self._call_tool(request_id, params, request)
        return None if notification else error_response(request_id, -32601, f"Method not found: {method}")

    async def _call_tool(self, request_id: Any, params: dict[str, Any], raw_request: dict[str, Any]) -> dict[str, Any]:
        name = params.get("name")
        arguments = params.get("arguments") if isinstance(params.get("arguments"), dict) else {}
        if not name:
            return error_response(request_id, -32602, "Tool name is required.")
        if name.startswith("broker_"):
            return await self._call_broker_tool(request_id, name, arguments)

        descriptor = self.registry.descriptor(self.router.active_project_key, name)
        if descriptor is None:
            return error_response(request_id, -32602, f"Tool not found: {name}")
        annotations = descriptor.get("annotations", {})
        read_only = bool(annotations.get("readOnlyHint", False))
        idempotent = bool(annotations.get("idempotentHint", False))
        args_hash = hashlib.sha256(
            json.dumps(arguments, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()
        meta = params.get("_meta") if isinstance(params.get("_meta"), dict) else {}
        idempotency_key = meta.get("idempotencyKey")
        ledger_id = uuid4().hex
        try:
            connection = self.router.active_connection()
            existing = self.store.create_request(
                ledger_id, idempotency_key, connection.identity.project_key,
                connection.identity.generation_id, name, args_hash, read_only, idempotent,
            )
        except ValueError as exception:
            return tool_error(request_id, "IDEMPOTENCY_CONFLICT", str(exception))
        except BridgeUnavailable as exception:
            return tool_error(request_id, "UNITY_OFFLINE", str(exception))

        ledger_id = str(existing.get("request_id") or ledger_id)
        if existing.get("state") in TERMINAL_REQUEST_STATES:
            if existing.get("response"):
                response = dict(existing["response"])
                if request_id is not None:
                    response["id"] = request_id
                return response
            return tool_error(
                request_id,
                "REQUEST_TERMINAL",
                str(existing.get("error") or f"request is {existing.get('state')}"),
                {"requestId": ledger_id, "state": existing.get("state")},
            )

        project_key = str(existing.get("project_key") or connection.identity.project_key)
        self.store.update_request(ledger_id, "dispatched")
        deadline = asyncio.get_running_loop().time() + self.config.request_timeout_seconds
        while True:
            try:
                remaining = max(0.1, deadline - asyncio.get_running_loop().time())
                response = await self.router.dispatch(ledger_id, raw_request, timeout=remaining)
                self.store.update_request(ledger_id, "completed", response=response)
                return response
            except BridgeUnavailable as exception:
                current = self.store.get_request(ledger_id) or {}
                accepted = current.get("state") == "accepted_by_unity"
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
                if reconnected is None or self.router.active_project_key != project_key:
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
                accepted = current.get("state") == "accepted_by_unity"
                state = "waiting_for_reconnect" if not accepted or read_only or idempotent else "outcome_unknown"
                self.store.update_request(ledger_id, state, error="Unity tool timed out")
                code = "UNITY_RECONNECT_REQUIRED" if state == "waiting_for_reconnect" else "OUTCOME_UNKNOWN"
                return tool_error(
                    request_id,
                    code,
                    "Unity tool timed out after dispatch",
                    {"requestId": ledger_id, "retrySafe": state != "outcome_unknown"},
                )

    async def _call_broker_tool(self, request_id: Any, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        if name == "broker_get_status":
            active = self.router.active_project_key
            connection = self.router.connections.get(active or "")
            return self._broker_result(request_id, {
                "service": "unity-mcp-broker",
                "activeProjectKey": active,
                "bridgeConnected": connection is not None,
                "bridgeState": connection.state if connection else "offline",
                "generationId": connection.identity.generation_id if connection else None,
                "registryHash": connection.identity.registry_hash if connection else None,
                "connectedProjects": len(self.router.connections),
            })
        if name == "broker_list_projects":
            return self._broker_result(request_id, {"projects": self.store.list_projects(), "activeProjectKey": self.router.active_project_key})
        if name == "broker_activate_project":
            try:
                connection = await self.router.activate(str(arguments.get("projectKey", "")))
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
            job = await self.router.wait_job(str(arguments.get("jobId", "")), float(arguments.get("timeoutSeconds", 60)))
            return self._broker_result(request_id, job) if job else tool_error(request_id, "JOB_NOT_FOUND", "job was not found")
        if name == "broker_cancel_job":
            job_id = str(arguments.get("jobId", ""))
            job = self.store.get_job(job_id)
            if not job:
                return tool_error(request_id, "JOB_NOT_FOUND", "job was not found")
            self.store.update_job(job_id, "cancelled", progress=1, message="cancelled")
            await self.router.dispatch_control("cancel_job", {"jobId": job_id})
            return self._broker_result(request_id, self.store.get_job(job_id))
        if name == "broker_start_refresh_compile":
            try:
                connection = self.router.active_connection()
            except BridgeUnavailable as exception:
                return tool_error(request_id, "UNITY_OFFLINE", str(exception))
            job = self.store.create_job("refresh_compile", connection.identity.project_key)
            try:
                await self.router.dispatch_control("refresh_compile", {
                    "jobId": job["job_id"],
                    "includeWarnings": bool(arguments.get("includeWarnings", True)),
                })
                self.store.update_job(job["job_id"], "dispatched", progress=0.05, message="sent to Unity")
            except BridgeUnavailable as exception:
                self.store.update_job(job["job_id"], "waiting_for_reconnect", message=str(exception))
            return self._broker_result(request_id, self.store.get_job(job["job_id"]))
        return error_response(request_id, -32602, f"Broker tool not found: {name}")

    @staticmethod
    def _broker_result(request_id: Any, value: Any) -> dict[str, Any]:
        return ok_response(request_id, {
            "content": [{"type": "text", "text": json.dumps(value, separators=(",", ":"))}],
            "structuredContent": value,
            "isError": False,
            "_meta": {"source": "broker"},
        })
