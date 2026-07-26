from __future__ import annotations

import asyncio

import pytest

from config import BrokerConfig
from mcp import McpBroker
from protocol import BridgeIdentity, envelope
from registry import RegistryCache
from router import BridgeRouter
from store import BrokerStore


class FakeWebSocket:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    async def send_json(self, message: dict) -> None:
        self.sent.append(message)


def identity(generation: str) -> BridgeIdentity:
    return BridgeIdentity(
        project_key="project",
        project_name="Game",
        project_path="D:/Game",
        process_id=123,
        process_start_utc="2026-01-01T00:00:00Z",
        generation_id=generation,
        package_version="1.0.0",
        registry_count=1,
        registry_hash="registry-a",
    )


def read_tool() -> dict:
    return {
        "name": "read_scene",
        "inputSchema": {"type": "object"},
        "annotations": {"readOnlyHint": True, "idempotentHint": True},
    }


@pytest.mark.asyncio
async def test_terminal_idempotency_replay_uses_new_json_rpc_id(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    registry = RegistryCache()
    router = BridgeRouter(store, registry)
    broker = McpBroker(
        BrokerConfig(database_path=tmp_path / "broker.sqlite3", request_timeout_seconds=1),
        store,
        registry,
        router,
    )
    connection = await router.register(FakeWebSocket(), identity("generation-a"), [read_tool()], "ready")
    store.create_request(
        "ledger-one", "same-key", "project", "generation-a", "read_scene",
        "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", True, True,
    )
    store.update_request(
        "ledger-one",
        "completed",
        response={"jsonrpc": "2.0", "id": 1, "result": {"ok": True}},
    )

    _, response = await broker.process({
        "jsonrpc": "2.0",
        "id": 99,
        "method": "tools/call",
        "params": {
            "name": "read_scene",
            "arguments": {},
            "_meta": {"idempotencyKey": "same-key"},
        },
    })

    assert response["id"] == 99
    assert response["result"]["ok"] is True
    await router.unregister(connection)
    store.close()


@pytest.mark.asyncio
async def test_read_request_retries_after_new_generation_connects(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    registry = RegistryCache()
    router = BridgeRouter(store, registry)
    broker = McpBroker(
        BrokerConfig(database_path=tmp_path / "broker.sqlite3", request_timeout_seconds=2),
        store,
        registry,
        router,
    )
    first_socket = FakeWebSocket()
    first = await router.register(first_socket, identity("generation-a"), [read_tool()], "ready")

    request_task = asyncio.create_task(broker.process({
        "jsonrpc": "2.0",
        "id": 7,
        "method": "tools/call",
        "params": {"name": "read_scene", "arguments": {}},
    }))
    while not first_socket.sent:
        await asyncio.sleep(0)
    first_command = first_socket.sent[-1]
    await router.handle(first, envelope(
        "accepted",
        {"requestId": first_command["payload"]["requestId"]},
        identity=first.identity,
        correlation_id=first_command["correlationId"],
    ))
    await router.unregister(first, "before_reload")

    second_socket = FakeWebSocket()
    second = await router.register(second_socket, identity("generation-b"), [read_tool()], "ready")
    while not second_socket.sent:
        await asyncio.sleep(0)
    second_command = second_socket.sent[-1]
    assert second_command["payload"]["requestId"] == first_command["payload"]["requestId"]
    await router.handle(second, envelope(
        "result",
        {"jsonRpc": {"jsonrpc": "2.0", "id": 7, "result": {"ok": True}}},
        identity=second.identity,
        correlation_id=second_command["correlationId"],
    ))

    _, response = await request_task
    assert response["result"]["ok"] is True
    ledger = store.get_request(first_command["payload"]["requestId"])
    assert ledger["state"] == "completed"
    assert ledger["generation_id"] == "generation-b"
    await router.unregister(second)
    store.close()
