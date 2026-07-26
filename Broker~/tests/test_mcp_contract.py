from __future__ import annotations

import pytest

from config import BrokerConfig
from mcp import McpBroker
from registry import RegistryCache
from router import BridgeRouter
from store import BrokerStore


@pytest.fixture
def broker(tmp_path):
    store = BrokerStore(tmp_path / "broker.sqlite3")
    registry = RegistryCache()
    router = BridgeRouter(store, registry)
    instance = McpBroker(BrokerConfig(database_path=tmp_path / "broker.sqlite3"), store, registry, router)
    yield instance
    store.close()


@pytest.mark.asyncio
async def test_initialize_and_broker_tools_list_work_without_unity(broker: McpBroker) -> None:
    status, initialized = await broker.process({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2025-03-26"},
    })
    assert status == 200
    assert initialized["result"]["serverInfo"]["name"] == "unity-mcp-broker"

    _, listed = await broker.process({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    names = {tool["name"] for tool in listed["result"]["tools"]}
    assert "broker_get_status" in names
    assert "broker_start_refresh_compile" in names


@pytest.mark.asyncio
async def test_batch_omits_notifications(broker: McpBroker) -> None:
    status, result = await broker.process([
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 3, "method": "ping"},
    ])
    assert status == 200
    assert result == [{"jsonrpc": "2.0", "result": {}, "id": 3}]


@pytest.mark.asyncio
async def test_unity_tool_without_bridge_returns_structured_offline_error(broker: McpBroker) -> None:
    broker.registry.update("project", "hash", [{
        "name": "read_scene",
        "inputSchema": {"type": "object"},
        "annotations": {"readOnlyHint": True, "idempotentHint": True},
    }])
    broker.store.set_active_project("project")
    _, response = await broker.process({
        "jsonrpc": "2.0", "id": 4, "method": "tools/call",
        "params": {"name": "read_scene", "arguments": {}},
    })
    assert response["result"]["isError"] is True
    assert response["result"]["structuredContent"]["code"] == "UNITY_OFFLINE"
