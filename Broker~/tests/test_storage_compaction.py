from __future__ import annotations

import logging
import sqlite3
from datetime import datetime, timedelta, timezone

import pytest

from app import create_app
from config import BrokerConfig
from store import BrokerStore


def legacy_database_with_free_pages(path) -> None:
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA auto_vacuum=NONE")
    connection.execute("CREATE TABLE filler (payload BLOB)")
    connection.executemany("INSERT INTO filler VALUES (?)", [(b"x" * 4096,) for _ in range(1000)])
    connection.commit()
    connection.execute("DELETE FROM filler")
    connection.commit()
    connection.close()


def pragma(store: BrokerStore, name: str) -> int:
    return store._connection.execute(f"PRAGMA {name}").fetchone()[0]


def test_new_database_uses_incremental_auto_vacuum(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    assert pragma(store, "auto_vacuum") == 2
    assert store.compact_storage()["rebuilt"] == 0
    store.close()


def test_legacy_database_is_rebuilt_once_and_keeps_its_rows(tmp_path) -> None:
    path = tmp_path / "broker.sqlite3"
    legacy_database_with_free_pages(path)
    store = BrokerStore(path)
    store.create_request("kept", "key", "p", "g", "write", "hash", False, False)
    assert pragma(store, "auto_vacuum") == 0
    result = store.compact_storage()
    assert result["rebuilt"] == 1
    assert result["bytesAfter"] < result["bytesBefore"] - 3_000_000
    assert pragma(store, "auto_vacuum") == 2 and pragma(store, "freelist_count") == 0
    assert store.get_request("kept")["state"] == "accepted_by_broker"
    assert store.compact_storage()["rebuilt"] == 0
    store.close()


def test_history_maintenance_releases_a_bounded_number_of_pages(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3", response_inline_bytes=1024 * 1024)
    old = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
    for name in ("a", "b"):
        store.create_request(name, None, "p", "g", "read", "hash", True, True)
        store.update_request(name, "completed", response={"result": name * 400_000})
    store._connection.execute("UPDATE requests SET updated_utc=?", (old,))
    store._connection.commit()
    limited = store.prune_history(retention_days=7, reclaim_pages=1)
    assert limited["expiredResponses"] == 2 and limited["reclaimedPages"] == 1
    remaining = pragma(store, "freelist_count")
    assert remaining > 100
    assert store.prune_history(retention_days=7)["reclaimedPages"] == remaining
    assert pragma(store, "freelist_count") == 0
    with pytest.raises(ValueError):
        store.prune_history(reclaim_pages=-1)
    store.close()


@pytest.mark.asyncio
async def test_startup_compacts_a_legacy_database_before_serving(tmp_path, caplog) -> None:
    path = tmp_path / "broker.sqlite3"
    legacy_database_with_free_pages(path)
    caplog.set_level(logging.INFO, logger="uvicorn.error.broker")
    app = create_app(BrokerConfig(token="secret", database_path=path))
    context = app.router.lifespan_context(app)
    await context.__aenter__()
    try:
        assert pragma(app.state.store, "auto_vacuum") == 2
        assert any("Broker database compacted from" in record.getMessage() for record in caplog.records)
    finally:
        await context.__aexit__(None, None, None)
