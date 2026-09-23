from __future__ import annotations

import json
import os
from datetime import datetime, timedelta, timezone

import pytest

from registry import RegistryCache
from config import BrokerConfig
from responses import ResponseVault
from store import BrokerStore


def test_catalog_pages_bind_to_project_filters_and_registry_and_refresh_index():
    registry = RegistryCache()
    source = [{"name": f"read_{i}", "annotations": {"readOnlyHint": True}} for i in range(280)]
    registry.update("a", "v1", source)
    first = registry.list_tools("a", {"_meta": {"profile": "read", "pageSize": 50}})
    source[0]["name"] = "mutated_by_caller"
    assert registry.descriptor("a", "read_0") is not None
    names = [tool["name"] for tool in first["tools"]]
    page = first
    while "nextCursor" in page:
        page = registry.list_tools("a", {"cursor": page["nextCursor"]})
        names.extend(tool["name"] for tool in page["tools"])
    assert len(names) == len(set(names)) == 288
    with pytest.raises(ValueError, match="STALE_CURSOR"):
        registry.list_tools("b", {"cursor": first["nextCursor"]})
    with pytest.raises(ValueError, match="STALE_CURSOR"):
        registry.list_tools("a", {"cursor": first["nextCursor"], "_meta": {"profile": "core"}})
    registry.update("a", "v2", [{"name": "new_tool"}])
    assert registry.descriptor("a", "read_0") is None
    assert registry.descriptor("a", "new_tool") is not None
    with pytest.raises(ValueError, match="STALE_CURSOR"):
        registry.list_tools("a", {"cursor": first["nextCursor"]})


def test_descriptor_lookup_does_not_serialize_the_catalog(monkeypatch):
    registry = RegistryCache()
    registry.update("a", "v1", [{"name": "read_scene"}])
    def unexpected(*args, **kwargs):
        raise AssertionError("catalog serialization on hot lookup")
    monkeypatch.setattr("registry.json.dumps", unexpected)
    for _ in range(100):
        assert registry.descriptor("a", "read_scene")["name"] == "read_scene"


def test_large_response_is_externalized_and_replayed_after_restart(tmp_path):
    path = tmp_path / "ledger.db"
    store = BrokerStore(path, response_inline_bytes=512)
    response = {"jsonrpc": "2.0", "id": 1, "result": {"text": "large payload " * 2000}}
    store.create_request("operation", "key", "project", "generation", "write", "hash", False, False)
    store.update_request("operation", "completed", response=response)
    persisted = store._connection.execute("SELECT response_json FROM requests").fetchone()[0]
    assert len(persisted) < 512
    assert store.get_request("operation")["response"] == response
    store.close()
    store = BrokerStore(path)
    record = store.create_request("retry", "key", "project", "next", "write", "hash", False, False)
    assert record["request_id"] == "operation"
    assert record["response"] == response
    assert len(list(store._vault.root.glob("*.json.gz"))) == 1
    store.close()


def test_corrupt_artifact_keeps_terminal_receipt_and_prevents_reexecution(tmp_path):
    store = BrokerStore(tmp_path / "ledger.db", response_inline_bytes=64)
    store.create_request("write", "key", "p", "g", "create_empty", "h", False, False)
    store.update_request("write", "completed", response={"result": "x" * 3000})
    next(store._vault.root.glob("*.json.gz")).write_bytes(b"corrupt")
    record = store.create_request("retry", "key", "p", "g", "create_empty", "h", False, False)
    assert record["request_id"] == "write" and record["state"] == "completed"
    assert "response" not in record and record["responseUnavailable"]
    store.close()


def test_retention_preserves_mutations_pending_operations_and_idempotency_keys(tmp_path):
    store = BrokerStore(tmp_path / "ledger.db", response_inline_bytes=128)
    old = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
    for name, read_only, state in [("read", True, "completed"), ("write", False, "completed"),
                                   ("pending", True, "running_in_unity")]:
        store.create_request(name, name, "p", "g", name, "hash", read_only, False)
        store.update_request(name, state, response={"result": name * 1000})
    store._connection.execute("UPDATE requests SET updated_utc=?", (old,))
    store._connection.commit()
    for blob in store._vault.root.glob("*.json.gz"):
        os.utime(blob, (0, 0))
    result = store.prune_history(retention_days=7)
    assert result["expiredResponses"] == 1 and result["deletedArtifacts"] == 1
    record = store.create_request("retry", "read", "p", "g", "read", "hash", True, False)
    assert record["request_id"] == "read" and record["state"] == "completed"
    assert "response" not in record and "RESPONSE_EXPIRED" in record["error"]
    assert store.get_request("write")["response"] == {"result": "write" * 1000}
    assert store.get_request("pending")["response"] == {"result": "pending" * 1000}
    assert store._connection.execute("SELECT COUNT(*) FROM requests").fetchone()[0] == 3
    store.close()


def test_retention_budget_expires_oldest_read_payload_without_deleting_receipts(tmp_path):
    store = BrokerStore(tmp_path / "ledger.db")
    for name in ["a", "b", "c"]:
        store.create_request(name, None, "p", "g", "read", "hash", True, True)
        store.update_request(name, "completed", response={"result": name * 100})
    result = store.prune_history(retention_days=0, max_response_bytes=150)
    assert result["expiredResponses"] == 2
    assert "response" not in store.get_request("a")
    assert store.get_request("c")["response"] == {"result": "c" * 100}
    store.close()


def test_testing_profile_exposes_actual_inferred_categories_and_project_context():
    registry = RegistryCache()
    names = ["run_tests", "list_tests", "get_test_job", "cancel_test_job", "wait_tool_job", "get_tool_job"]
    registry.update("project", "v1", [
        {"name": name, "_meta": {"category": "job" if "tool_job" in name else name.split("_")[0]}}
        for name in names
    ] + [{"name": "mesh_create", "_meta": {"category": "mesh"}}])
    result = registry.list_tools("project", {"profile": "testing"})
    listed = {tool["name"] for tool in result["tools"]}
    assert set(names) <= listed
    assert "mesh_create" not in listed
    assert result["_meta"]["unityProjectKey"] == "project"
    for invalid in ("", False, 0):
        with pytest.raises(ValueError, match="cursor"):
            registry.list_tools("project", {"cursor": invalid})


def test_core_profile_contains_the_shipped_inspection_and_property_tools():
    registry = RegistryCache()
    required = {"query_scene", "read_component", "set_property", "read_console"}
    registry.update("p", "v1", [{"name": name} for name in sorted(required | {"mesh_create"})])
    listed = {tool["name"] for tool in registry.list_tools("p", {"profile": "core"})["tools"]}
    assert required <= listed
    assert "mesh_create" not in listed


def test_modeling_profile_pages_include_model_scale_and_existing_mesh_rig_tools():
    registry = RegistryCache()
    required = {"model_preview_scale", "model_apply_scale", "mesh_analyze_cleanup",
                "mesh_preview_cleanup", "mesh_apply_cleanup", "rig_inspect", "animation_list", "ai3d_generate"}
    registry.update("p", "v1", [{"name": name} for name in sorted(required | {"read_scene", "create_empty"})])
    page = registry.list_tools("p", {"_meta": {"profile": "modeling", "pageSize": 3}})
    names = []
    while True:
        names.extend(tool["name"] for tool in page["tools"])
        if "nextCursor" not in page:
            break
        page = registry.list_tools("p", {"cursor": page["nextCursor"]})
    assert len(names) == len(set(names))
    assert {name for name in names if not name.startswith("broker_")} == required
    assert any(name.startswith("broker_") for name in names)


def test_invalid_response_markers_are_reported_without_losing_terminal_receipts(tmp_path):
    store = BrokerStore(tmp_path / "markers.db")
    valid = {"encoding": "gzip-json-v1", "sha256": "a" * 64, "bytes": 10, "storedBytes": 20}
    corrupt = [None, [], "marker", 1, True, {},
               {**valid, "bytes": True}, {**valid, "bytes": -1},
               {**valid, "storedBytes": "20"}, {**valid, "storedBytes": 0},
               {**valid, "sha256": ["a"]}, {**valid, "encoding": ["gzip-json-v1"]}]
    for index, marker in enumerate(corrupt):
        request_id = str(index)
        store.create_request(request_id, request_id, "p", "g", "write", "hash", False, False)
        serialized = json.dumps({ResponseVault.MARKER: marker})
        with pytest.raises(ValueError, match="descriptor"):
            store._vault.reference(serialized)
        store._connection.execute("UPDATE requests SET state='completed', response_json=? WHERE request_id=?",
                                  (serialized, request_id))
        store._connection.commit()
        replay = store.create_request("retry", request_id, "p", "g", "write", "hash", False, False)
        assert replay["state"] == "completed" and replay["request_id"] == request_id
        assert replay.get("responseUnavailable") and "response" not in replay
    store.close()


def test_corrupt_read_marker_does_not_prevent_retention_of_other_expired_payloads(tmp_path):
    store = BrokerStore(tmp_path / "retention.db")
    old = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
    for name in ("corrupt", "healthy"):
        store.create_request(name, name, "p", "g", "read", "hash", True, True)
        store.update_request(name, "completed", response={"result": "data"})
    store._connection.execute("UPDATE requests SET response_json=? WHERE request_id='corrupt'",
                              (json.dumps({ResponseVault.MARKER: []}),))
    store._connection.execute("UPDATE requests SET updated_utc=?", (old,))
    store._connection.commit()
    result = store.prune_history(retention_days=7)
    assert result["expiredResponses"] == 2
    assert store.get_request("healthy")["state"] == "completed"
    assert store.get_request("corrupt")["state"] == "completed"
    store.close()


def test_nonfinite_deadlines_and_maintenance_intervals_are_rejected():
    for field in ("request_timeout_seconds", "reconnect_grace_seconds", "focus_assist_delay_seconds", "history_maintenance_seconds"):
        for value in (float("nan"), float("inf"), -float("inf")):
            with pytest.raises(ValueError):
                BrokerConfig(**{field: value})
