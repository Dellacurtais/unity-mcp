from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from typing import Any


BROKER_TOOLS: list[dict[str, Any]] = [
    {
        "name": "broker_get_status",
        "title": "broker get status",
        "description": "Reports broker health, active Unity project, bridge generation, registry and request counts.",
        "inputSchema": {"type": "object", "additionalProperties": False},
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "Read", "risk": "Low", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_list_projects",
        "title": "broker list projects",
        "description": "Lists Unity Editor bridges known to the broker.",
        "inputSchema": {"type": "object", "additionalProperties": False},
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "Read", "risk": "Low", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_activate_project",
        "title": "broker activate project",
        "description": "Routes subsequent Unity tool calls to the selected connected project.",
        "inputSchema": {"type": "object", "properties": {"projectKey": {"type": "string"}}, "required": ["projectKey"], "additionalProperties": False},
        "annotations": {"readOnlyHint": False, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "ProjectSettings", "risk": "Medium", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_get_request",
        "title": "broker get request",
        "description": "Returns the durable ledger entry for a broker request.",
        "inputSchema": {"type": "object", "properties": {"requestId": {"type": "string"}}, "required": ["requestId"], "additionalProperties": False},
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "Read", "risk": "Low", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_get_job",
        "title": "broker get job",
        "description": "Returns a durable broker job.",
        "inputSchema": {"type": "object", "properties": {"jobId": {"type": "string"}}, "required": ["jobId"], "additionalProperties": False},
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "Read", "risk": "Low", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_wait_job",
        "title": "broker wait job",
        "description": "Waits outside Unity until a durable broker job changes or reaches a terminal state.",
        "inputSchema": {"type": "object", "properties": {"jobId": {"type": "string"}, "timeoutSeconds": {"type": "number", "default": 60}}, "required": ["jobId"], "additionalProperties": False},
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "Read", "risk": "Low", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_cancel_job",
        "title": "broker cancel job",
        "description": "Requests cooperative cancellation of a durable broker job.",
        "inputSchema": {"type": "object", "properties": {"jobId": {"type": "string"}}, "required": ["jobId"], "additionalProperties": False},
        "annotations": {"readOnlyHint": False, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "ProjectSettings", "risk": "Medium", "confirmationRequired": False, "editorMode": "Any"},
    },
    {
        "name": "broker_start_refresh_compile",
        "title": "broker start refresh compile",
        "description": "Starts a durable Unity AssetDatabase refresh and compile/reload job.",
        "inputSchema": {"type": "object", "properties": {"includeWarnings": {"type": "boolean", "default": True}}, "additionalProperties": False},
        "annotations": {"readOnlyHint": False, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
        "_meta": {"category": "broker", "capabilities": "AssetWrite", "risk": "Medium", "confirmationRequired": False, "editorMode": "EditModeOnly"},
    },
]


@dataclass
class RegistrySnapshot:
    project_key: str
    registry_hash: str
    tools: list[dict[str, Any]]


class RegistryCache:
    def __init__(self) -> None:
        self._snapshots: dict[str, RegistrySnapshot] = {}

    def update(self, project_key: str, registry_hash: str, tools: list[dict[str, Any]]) -> RegistrySnapshot:
        snapshot = RegistrySnapshot(project_key, registry_hash, tools)
        self._snapshots[project_key] = snapshot
        return snapshot

    def get(self, project_key: str | None) -> RegistrySnapshot | None:
        return self._snapshots.get(project_key or "")

    def combined(self, project_key: str | None) -> tuple[list[dict[str, Any]], str]:
        snapshot = self.get(project_key)
        unity_tools = snapshot.tools if snapshot else []
        tools = [*unity_tools, *BROKER_TOOLS]
        raw = json.dumps(tools, sort_keys=True, separators=(",", ":")).encode("utf-8")
        return tools, hashlib.sha256(raw).hexdigest()

    def descriptor(self, project_key: str | None, tool_name: str) -> dict[str, Any] | None:
        tools, _ = self.combined(project_key)
        return next((tool for tool in tools if tool.get("name") == tool_name), None)
