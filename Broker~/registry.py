from __future__ import annotations

import hashlib
import json
import base64
import copy
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
        self._combined: dict[str, tuple[list[dict[str, Any]], str]] = {}
        self._descriptors: dict[str, dict[str, dict[str, Any]]] = {}
        self._cache("", [])

    def _cache(self, project_key: str, unity_tools: list[dict[str, Any]]) -> None:
        tools = [*unity_tools, *copy.deepcopy(BROKER_TOOLS)]
        raw = json.dumps(tools, sort_keys=True, separators=(",", ":")).encode("utf-8")
        self._combined[project_key] = (tools, hashlib.sha256(raw).hexdigest())
        self._descriptors[project_key] = {tool["name"]: tool for tool in tools}

    def update(self, project_key: str, registry_hash: str, tools: list[dict[str, Any]]) -> RegistrySnapshot:
        # Own the snapshot: a caller mutating hello's list must not invalidate its index/hash.
        snapshot = RegistrySnapshot(project_key, registry_hash, copy.deepcopy(tools))
        self._snapshots[project_key] = snapshot
        self._cache(project_key, snapshot.tools)
        return snapshot

    def get(self, project_key: str | None) -> RegistrySnapshot | None:
        return self._snapshots.get(project_key or "")

    def combined(self, project_key: str | None) -> tuple[list[dict[str, Any]], str]:
        # Internal immutable-by-contract view; serializers must not modify descriptors.
        return self._combined.get(project_key or "", self._combined[""])

    def descriptor(self, project_key: str | None, tool_name: str) -> dict[str, Any] | None:
        return self._descriptors.get(project_key or "", self._descriptors[""]).get(tool_name)

    def list_tools(self, project_key: str | None, params: dict[str, Any] | None = None) -> dict[str, Any]:
        params = params or {}
        meta = params.get("_meta") or {}
        if not isinstance(meta, dict):
            raise ValueError("tools/list _meta must be an object")
        cursor = params.get("cursor")
        state: dict[str, Any] = {}
        if cursor is not None:
            try:
                if not isinstance(cursor, str) or len(cursor) > 4096:
                    raise ValueError()
                state = json.loads(base64.urlsafe_b64decode(cursor + "=" * (-len(cursor) % 4)))
                if not isinstance(state, dict) or state.get("v") != 1:
                    raise ValueError()
            except (ValueError, TypeError, UnicodeError):
                raise ValueError("Invalid tools/list cursor") from None
        profile = meta.get("profile", params.get("profile", state.get("profile", "all")))
        module = meta.get("module", params.get("module", state.get("module")))
        limit = meta.get("pageSize", params.get("limit", state.get("limit", 100)))
        if not isinstance(profile, str) or profile not in {"all", "core", "read", "authoring", "testing", "modeling", "admin"}:
            raise ValueError("Unknown tool profile; use all, core, read, authoring, testing, modeling or admin")
        if module is not None and not isinstance(module, str):
            raise ValueError("module must be a string")
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 250:
            raise ValueError("pageSize must be an integer between 1 and 250")
        tools, version = self.combined(project_key)
        binding = {"project": project_key, "hash": version, "profile": profile, "module": module, "limit": limit}
        offset = state.get("offset", 0)
        if state and any(state.get(key) != value for key, value in binding.items()):
            raise ValueError("STALE_CURSOR: project, registry or filters changed; restart tools/list")
        if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0:
            raise ValueError("Invalid tools/list cursor offset")
        selected = [tool for tool in tools if self._in_profile(tool, profile)
                    and (not module or tool.get("_meta", {}).get("module") == module
                         or tool["name"].startswith("broker_"))]
        if offset > len(selected):
            raise ValueError("Invalid tools/list cursor offset")
        result: dict[str, Any] = {
            "tools": selected[offset:offset + limit],
            "_meta": {"registryHash": version, "registryCount": len(tools),
                      "filteredCount": len(selected), "profile": profile,
                      "unityProjectKey": project_key},
        }
        if offset + limit < len(selected):
            next_state = {"v": 1, **binding, "offset": offset + limit}
            result["nextCursor"] = base64.urlsafe_b64encode(
                json.dumps(next_state, separators=(",", ":")).encode()).decode().rstrip("=")
        return result

    @staticmethod
    def _in_profile(tool: dict[str, Any], profile: str) -> bool:
        name = tool["name"]
        if profile == "all" or name.startswith("broker_"):
            return True
        meta = tool.get("_meta", {})
        read_only = tool.get("annotations", {}).get("readOnlyHint") is True
        if profile == "read":
            return read_only
        if profile == "core":
            return name in {"get_editor_context", "get_project_meta", "get_server_health", "self_test",
                            "get_tool_inventory", "read_hierarchy", "read_scene", "query_scene", "read_component",
                            "get_play_state", "get_compilation_status", "read_console",
                            "list_loaded_scenes", "get_tool_job", "wait_tool_job", "cancel_tool_job",
                            "create_empty", "set_transform", "add_component", "set_property",
                            "apply_changeset", "save_scene"}
        if profile == "testing":
            return (meta.get("category") in {"test", "tests", "play", "runtime", "job", "jobs", "profiling"}
                    or meta.get("module") == "test-framework" or name in {
                        "run_tests", "list_tests", "get_test_job", "cancel_test_job",
                        "get_tool_job", "wait_tool_job", "cancel_tool_job", "enter_play_mode",
                        "exit_play_mode", "set_play_paused", "step_frame", "get_play_state",
                        "get_compilation_status", "read_console", "self_test"})
        if profile == "modeling":
            return name.startswith(("mesh_", "model_", "rig_", "animation_", "ai3d_"))
        advanced = meta.get("risk") == "Critical" or meta.get("category") in {"reflect", "reflection", "packages", "build"}
        return advanced if profile == "admin" else not advanced and not read_only
