from __future__ import annotations

import logging

import httpx
import pytest

from app import create_app
from config import BrokerConfig
from mcp import McpBroker
from registry import RegistryCache
from router import BridgeRouter
from store import BrokerStore


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


@pytest.fixture
def clock(monkeypatch) -> Clock:
    instance = Clock()
    monkeypatch.setattr("mcp.time.monotonic", instance)
    return instance


def make_broker(tmp_path, **overrides) -> McpBroker:
    config = BrokerConfig(database_path=tmp_path / "broker.sqlite3", **overrides)
    store = BrokerStore(config.database_path)
    registry = RegistryCache()
    return McpBroker(config, store, registry, BridgeRouter(store, registry, focus_assist_enabled=False))


@pytest.fixture
def broker(tmp_path):
    instance = make_broker(tmp_path, max_sessions=3, session_idle_seconds=60)
    yield instance
    instance.store.close()


def test_capacity_evicts_least_recently_used_session(broker, clock) -> None:
    first, second, third = (broker.create_session("client") for _ in range(3))
    clock.now += 1
    assert broker.has_session(first)
    fourth = broker.create_session("client")
    assert not broker.has_session(second)
    assert all(broker.has_session(session) for session in (first, third, fourth))
    assert len(broker.sessions) == 3


def test_capacity_never_evicts_a_session_with_pending_work(broker, clock) -> None:
    sessions = [broker.create_session() for _ in range(3)]
    broker._client_requests[(sessions[0], "1")] = ("ledger", "project")
    newest = broker.create_session()
    assert broker.has_session(sessions[0]) and broker.has_session(newest)
    assert not broker.has_session(sessions[1])
    for index, session in enumerate((sessions[2], newest)):
        broker._client_requests[(session, str(index))] = ("ledger", "project")
    with pytest.raises(ValueError, match="every session has pending work"):
        broker.create_session()


def test_idle_sessions_expire_unless_busy(broker, clock) -> None:
    idle, busy, active = (broker.create_session() for _ in range(3))
    broker._client_requests[(busy, "1")] = ("ledger", "project")
    clock.now += 30
    assert broker.has_session(active)
    clock.now += 31
    assert broker.expire_sessions() == 1
    assert idle not in broker.sessions
    assert broker.has_session(busy) and broker.has_session(active)
    clock.now += 61
    assert not broker.has_session(active)


def test_session_events_name_the_client(broker, clock, caplog) -> None:
    caplog.set_level(logging.INFO, logger="uvicorn.error.sessions")
    sessions = [broker.create_session(f"client-{index}", "127.0.0.1:5000") for index in range(3)]
    broker.drop_session(sessions[0])
    broker.drop_session(sessions[0])
    clock.now += 61
    broker.create_session("client-new")
    messages = [record.getMessage() for record in caplog.records]
    assert "MCP session opened id=%s client='client-0' from=127.0.0.1:5000 sessions=1/3" % sessions[0][:8] in messages
    assert sum(" closed " in message for message in messages) == 1
    assert any(f"expired id={sessions[1][:8]} client='client-1' idle=61s" in message for message in messages)
    assert broker.session_stats() == {"active": 1, "max": 3, "idleSeconds": 60, "byClient": {"client-new": 1}}


def test_session_limits_are_validated_and_read_from_environment(monkeypatch) -> None:
    with pytest.raises(ValueError, match="session limit"):
        BrokerConfig(max_sessions=0)
    with pytest.raises(ValueError, match="idle timeout"):
        BrokerConfig(session_idle_seconds=0)
    monkeypatch.setenv("UNITY_MCP_BROKER_MAX_SESSIONS", "16")
    monkeypatch.setenv("UNITY_MCP_BROKER_SESSION_IDLE_SECONDS", "90")
    config = BrokerConfig.from_env()
    assert (config.max_sessions, config.session_idle_seconds) == (16, 90)


@pytest.mark.asyncio
async def test_http_initialize_at_capacity_replaces_the_oldest_session(tmp_path) -> None:
    app = create_app(BrokerConfig(token="secret", database_path=tmp_path / "broker.sqlite3", max_sessions=1))
    headers = {"Authorization": "Bearer secret", "User-Agent": "probe/1.0"}
    initialize = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
                  "params": {"protocolVersion": "2025-03-26", "clientInfo": {"name": "codex", "version": "0.9"}}}
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
            first = (await client.post("/mcp/", headers=headers, json=initialize)).headers["Mcp-Session-Id"]
            second = await client.post("/mcp/", headers=headers, json=initialize)
            assert second.status_code == 200
            ping = {"jsonrpc": "2.0", "id": 2, "method": "ping"}
            stale = await client.post("/mcp/", headers={**headers, "Mcp-Session-Id": first}, json=ping)
            assert stale.status_code == 404
            current = {**headers, "Mcp-Session-Id": second.headers["Mcp-Session-Id"]}
            assert (await client.post("/mcp/", headers=current, json=ping)).status_code == 200
            assert (await client.get("/health")).json()["sessions"] == {"active": 1, "max": 1}
            status = await client.post("/mcp/", headers=current, json={
                "jsonrpc": "2.0", "id": 3, "method": "tools/call",
                "params": {"name": "broker_get_status", "arguments": {}},
            })
            sessions = status.json()["result"]["structuredContent"]["sessions"]
            assert sessions["byClient"] == {"codex 0.9 [probe/1.0]": 1}
    finally:
        app.state.store.close()
