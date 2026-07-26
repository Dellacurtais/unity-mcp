from __future__ import annotations

import sqlite3
from contextlib import closing

import pytest

from store import BrokerStore, CURRENT_SCHEMA_VERSION


def test_store_rejects_database_from_newer_schema(tmp_path) -> None:
    path = tmp_path / "future.sqlite3"
    with closing(sqlite3.connect(path)) as connection:
        connection.execute("CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        connection.execute(
            "INSERT INTO metadata(key, value) VALUES('schema_version', ?)",
            (str(CURRENT_SCHEMA_VERSION + 1),),
        )
        connection.commit()

    with pytest.raises(RuntimeError, match="newer than supported"):
        BrokerStore(path)

    path.unlink()
    assert not path.exists()


def test_request_idempotency_survives_restart(tmp_path) -> None:
    path = tmp_path / "broker.sqlite3"
    first = BrokerStore(path)
    record = first.create_request(
        "request-1", "key-1", "project", "generation", "read_scene",
        "same-hash", True, True,
    )
    assert record["state"] == "accepted_by_broker"
    first.update_request("request-1", "completed", response={"jsonrpc": "2.0", "id": 1, "result": {"ok": True}})
    first.close()

    second = BrokerStore(path)
    duplicate = second.create_request(
        "request-2", "key-1", "project", "new-generation", "read_scene",
        "same-hash", True, True,
    )
    assert duplicate["request_id"] == "request-1"
    assert second.get_request("request-1")["response"]["result"]["ok"] is True
    second.close()


def test_idempotency_keys_are_isolated_per_project(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    first = store.create_request(
        "one", "shared-key", "project-a", "generation", "read_scene", "hash", True, True,
    )
    second = store.create_request(
        "two", "shared-key", "project-b", "generation", "read_scene", "hash", True, True,
    )

    assert first["request_id"] == "one"
    assert second["request_id"] == "two"
    store.close()


def test_restart_restores_registry_and_reconciles_inflight_state(tmp_path) -> None:
    path = tmp_path / "broker.sqlite3"
    first = BrokerStore(path)
    first.save_registry("project", "registry", [{"name": "read_scene"}])
    first.create_request(
        "safe", None, "project", "generation", "read_scene", "hash", True, True,
    )
    first.update_request("safe", "accepted_by_unity")
    first.create_request(
        "unsafe", None, "project", "generation", "create_empty", "hash", False, False,
    )
    first.update_request("unsafe", "accepted_by_unity")
    job = first.create_job("refresh_compile", "project")
    first.update_job(job["job_id"], "running", progress=0.25)
    first.close()

    second = BrokerStore(path)
    second.reconcile_startup()
    snapshots = second.load_registry_snapshots()

    assert snapshots[0]["tools"][0]["name"] == "read_scene"
    assert second.get_request("safe")["state"] == "waiting_for_reconnect"
    assert second.get_request("unsafe")["state"] == "outcome_unknown"
    assert second.get_job(job["job_id"])["state"] == "waiting_for_reconnect"
    second.close()


def test_idempotency_key_rejects_changed_arguments(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    store.create_request("one", "key", "project", "generation", "tool", "hash-a", False, False)
    try:
        store.create_request("two", "key", "project", "generation", "tool", "hash-b", False, False)
    except ValueError as exception:
        assert "different tool arguments" in str(exception)
    else:
        raise AssertionError("expected idempotency conflict")
    store.close()
