from __future__ import annotations

import asyncio
import threading

import httpx
import pytest

from app import create_app
from config import BrokerConfig


@pytest.fixture
def app(tmp_path):
    instance = create_app(BrokerConfig(
        token="secret",
        database_path=tmp_path / "broker.sqlite3",
        max_request_bytes=256,
    ))
    yield instance
    instance.state.store.close()


@pytest.mark.asyncio
async def test_mcp_http_auth_and_initialize(app) -> None:
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        health = await client.get("/health")
        assert health.status_code == 200
        assert health.json()["bridgeConnected"] is False
        assert health.json()["routeReady"] is False
        assert health.json()["routeError"]

        unauthorized = await client.post("/mcp/", json={"jsonrpc": "2.0", "id": 1, "method": "ping"})
        assert unauthorized.status_code == 401

        response = await client.post(
            "/mcp/",
            headers={
                "Authorization": "Bearer secret",
                "MCP-Protocol-Version": "2025-03-26",
            },
            json={
                "jsonrpc": "2.0",
                "id": 2,
                "method": "initialize",
                "params": {"protocolVersion": "2025-03-26"},
            },
        )
        assert response.status_code == 200
        assert response.json()["result"]["serverInfo"]["name"] == "unity-mcp-broker"
        assert response.json()["result"]["capabilities"]["tools"]["listChanged"] is False
        session_id = response.headers["Mcp-Session-Id"]
        session_headers = {"Authorization": "Bearer secret", "Mcp-Session-Id": session_id}
        ping = await client.post("/mcp/", headers=session_headers, json={"jsonrpc": "2.0", "id": 3, "method": "ping"})
        assert ping.status_code == 200
        assert ping.headers["Mcp-Session-Id"] == session_id
        deleted = await client.delete("/mcp/", headers=session_headers)
        assert deleted.status_code == 204
        expired = await client.post("/mcp/", headers=session_headers, json={"jsonrpc": "2.0", "id": 4, "method": "ping"})
        assert expired.status_code == 404
        legacy = await client.post("/mcp/", headers={"Authorization": "Bearer secret"}, json={"jsonrpc": "2.0", "id": 5, "method": "ping"})
        assert legacy.status_code == 200


@pytest.mark.asyncio
async def test_mcp_http_rejects_remote_origin_media_protocol_and_oversize(app) -> None:
    transport = httpx.ASGITransport(app=app)
    headers = {"Authorization": "Bearer secret"}
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        remote = await client.post(
            "/mcp/", headers={**headers, "Origin": "https://example.com"}, json={},
        )
        assert remote.status_code == 403

        media = await client.post(
            "/mcp/", headers={**headers, "Content-Type": "text/plain"}, content="{}",
        )
        assert media.status_code == 415

        protocol = await client.post(
            "/mcp/",
            headers={**headers, "MCP-Protocol-Version": "unsupported"},
            json={},
        )
        assert protocol.status_code == 400

        oversized = await client.post(
            "/mcp/",
            headers={**headers, "Content-Type": "application/json"},
            content=b"{" + (b" " * 300) + b"}",
        )
        assert oversized.status_code == 413


@pytest.mark.asyncio
async def test_shutdown_joins_history_maintenance_before_closing_sqlite(tmp_path, monkeypatch) -> None:
    instance = create_app(BrokerConfig(token="secret", database_path=tmp_path / "maintenance.sqlite3",
                                       history_maintenance_seconds=0.01))
    store = instance.state.store
    started, release, finished = threading.Event(), threading.Event(), threading.Event()
    observed = {}

    def maintenance(**_):
        started.set()
        release.wait(timeout=2)
        observed["schema"] = store.schema_version()
        finished.set()

    original_close = store.close

    def close():
        observed["closed_after_worker"] = finished.is_set()
        original_close()

    monkeypatch.setattr(store, "prune_history", maintenance)
    monkeypatch.setattr(store, "close", close)
    context = instance.router.lifespan_context(instance)
    await context.__aenter__()
    assert not started.is_set(), "startup must not purge history before its maintenance interval"
    try:
        assert await asyncio.to_thread(started.wait, 1)
        shutdown = asyncio.create_task(context.__aexit__(None, None, None))
        await asyncio.sleep(0.02)
        assert not shutdown.done()
        release.set()
        await asyncio.wait_for(shutdown, timeout=1)
        assert observed["closed_after_worker"] is True
        assert observed["schema"] >= 2
    finally:
        release.set()
