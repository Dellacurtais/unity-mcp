from __future__ import annotations

import asyncio

import pytest

from protocol import BridgeIdentity, envelope
from registry import RegistryCache
from router import BridgeRouter
from store import BrokerStore


class FakeWebSocket:
    def __init__(self) -> None:
        self.sent = []

    async def send_json(self, message) -> None:
        self.sent.append(message)


@pytest.fixture
def identity() -> BridgeIdentity:
    return BridgeIdentity(
        project_key="project", project_name="Game", project_path="D:/Game",
        process_id=123, process_start_utc="2026-01-01T00:00:00Z",
        generation_id="generation-a", package_version="1.0.0",
        registry_count=1, registry_hash="registry-a",
    )


@pytest.mark.asyncio
async def test_dispatch_correlates_result_and_rejects_stale_generation(tmp_path, identity) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    router = BridgeRouter(store, RegistryCache())
    socket = FakeWebSocket()
    connection = await router.register(socket, identity, [], "ready")

    task = asyncio.create_task(router.dispatch(
        "request", {"jsonrpc": "2.0", "id": 1, "method": "ping"}, timeout=1,
    ))
    await asyncio.sleep(0)
    command = socket.sent[-1]
    await router.handle(connection, envelope(
        "result", {"jsonRpc": {"jsonrpc": "2.0", "id": 1, "result": {}}},
        identity=identity, correlation_id=command["correlationId"],
    ))
    assert (await task)["result"] == {}

    stale = dict(envelope("heartbeat", {}, identity=identity))
    stale["generationId"] = "generation-old"
    with pytest.raises(ValueError, match="stale"):
        await router.handle(connection, stale)
    store.close()


@pytest.mark.asyncio
async def test_waiting_refresh_job_completes_on_new_generation(tmp_path, identity) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    router = BridgeRouter(store, RegistryCache())
    job = store.create_job("refresh_compile", identity.project_key)
    store.update_job(
        job["job_id"],
        "waiting_for_reconnect",
        progress=0.75,
        result={"messages": [{"severity": "Warning", "message": "unused variable"}]},
    )

    new_identity = BridgeIdentity(**{**identity.__dict__, "generation_id": "generation-b"})
    await router.register(FakeWebSocket(), new_identity, [], "ready")
    restored = store.get_job(job["job_id"])
    assert restored["state"] == "completed"
    assert restored["result"]["reconnected"] is True
    assert restored["result"]["messages"][0]["severity"] == "Warning"
    store.close()


@pytest.mark.asyncio
async def test_replaced_connection_cannot_mark_new_generation_offline(tmp_path, identity) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    router = BridgeRouter(store, RegistryCache())
    first = await router.register(FakeWebSocket(), identity, [], "ready")
    new_identity = BridgeIdentity(**{**identity.__dict__, "generation_id": "generation-b"})
    second = await router.register(FakeWebSocket(), new_identity, [], "ready")

    await router.unregister(first, "stale_disconnect")

    assert router.connections[identity.project_key] is second
    project = store.list_projects()[0]
    assert project["generation_id"] == "generation-b"
    assert project["connected"] == 1
    store.close()


@pytest.mark.asyncio
async def test_first_live_bridge_replaces_a_persisted_offline_active_route(tmp_path, identity) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    store.set_active_project("offline-project")
    router = BridgeRouter(store, RegistryCache())

    await router.register(FakeWebSocket(), identity, [], "ready")

    assert router.active_project_key == identity.project_key
    assert router.active_connection().identity.project_key == identity.project_key
    store.close()


@pytest.mark.asyncio
async def test_activate_publishes_route_state_to_every_live_bridge(tmp_path, identity) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    router = BridgeRouter(store, RegistryCache())
    first_socket = FakeWebSocket()
    second_socket = FakeWebSocket()
    await router.register(first_socket, identity, [], "ready")
    second_identity = BridgeIdentity(**{
        **identity.__dict__,
        "project_key": "project-b",
        "project_name": "Game B",
        "project_path": "D:/Game B",
        "generation_id": "generation-b",
    })
    await router.register(second_socket, second_identity, [], "ready")

    await router.activate(second_identity.project_key)

    assert first_socket.sent[-1]["type"] == "route_state"
    assert first_socket.sent[-1]["payload"]["active"] is False
    assert second_socket.sent[-1]["type"] == "route_state"
    assert second_socket.sent[-1]["payload"]["active"] is True
    store.close()


@pytest.mark.asyncio
async def test_running_refresh_job_waits_when_bridge_disconnects(tmp_path, identity) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    router = BridgeRouter(store, RegistryCache())
    connection = await router.register(FakeWebSocket(), identity, [], "ready")
    job = store.create_job("refresh_compile", identity.project_key)
    store.update_job(job["job_id"], "running", progress=0.25)

    await router.unregister(connection, "before_reload")

    waiting = store.get_job(job["job_id"])
    assert waiting["state"] == "waiting_for_reconnect"
    assert waiting["progress"] == 0.75
    store.close()
