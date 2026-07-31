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
        self.sent = []

    async def send_json(self, message) -> None:
        self.sent.append(message)


@pytest.mark.asyncio
async def test_started_unity_job_is_mirrored_and_auto_waited(tmp_path) -> None:
    store = BrokerStore(tmp_path / "broker.sqlite3")
    registry = RegistryCache()
    router = BridgeRouter(store, registry)
    broker = McpBroker(
        BrokerConfig(database_path=tmp_path / "broker.sqlite3", request_timeout_seconds=5),
        store,
        registry,
        router,
    )
    identity = BridgeIdentity(
        project_key="project", project_name="Game", project_path="D:/Game",
        process_id=123, process_start_utc="2026-01-01T00:00:00Z",
        generation_id="generation-a", package_version="1.0.0",
        registry_count=2, registry_hash="registry-a",
    )
    tools = [
        {
            "name": "run_tests",
            "inputSchema": {"type": "object"},
            "annotations": {"readOnlyHint": False, "idempotentHint": False},
            "_meta": {
                "executionRequirements": ["MainThread", "LongRunningJob"],
                "jobLifecycle": {"waitTool": "wait_tool_job", "autoWait": True},
            },
        },
        {
            "name": "wait_tool_job",
            "inputSchema": {"type": "object"},
            "annotations": {"readOnlyHint": True, "idempotentHint": True},
            "_meta": {"executionRequirements": ["MainThread", "EditorUpdates"]},
        },
    ]
    socket = FakeWebSocket()
    connection = await router.register(socket, identity, tools, "ready")
    call = asyncio.create_task(broker.process({
        "jsonrpc": "2.0", "id": 7, "method": "tools/call",
        "params": {"name": "run_tests", "arguments": {}},
    }))

    while not socket.sent:
        await asyncio.sleep(0)
    start_command = socket.sent[-1]
    await router.handle(connection, envelope(
        "execution_started", {"requestId": start_command["payload"]["requestId"]},
        identity=identity, correlation_id=start_command["correlationId"],
    ))
    running = {
        "jobId": "unity-job-1", "kind": "test", "status": "Running", "progress": 0.4,
        "message": "running", "error": None, "result": None, "input": {},
        "createdUtc": "2026-01-01T00:00:00Z", "startedUtc": "2026-01-01T00:00:01Z",
        "completedUtc": None, "canCancel": True,
    }
    await router.handle(connection, envelope(
        "result", {"jsonRpc": {"jsonrpc": "2.0", "id": 7, "result": {
            "content": [], "structuredContent": running, "isError": False,
        }}}, identity=identity, correlation_id=start_command["correlationId"],
    ))

    while len(socket.sent) < 2:
        await asyncio.sleep(0)
    wait_command = socket.sent[-1]
    await router.handle(connection, envelope(
        "execution_started", {"requestId": wait_command["payload"]["requestId"]},
        identity=identity, correlation_id=wait_command["correlationId"],
    ))
    completed = {**running, "status": "Completed", "progress": 1, "message": "done",
                 "result": {"passed": 3}, "completedUtc": "2026-01-01T00:00:02Z",
                 "canCancel": False}
    await router.handle(connection, envelope(
        "result", {"jsonRpc": {"jsonrpc": "2.0", "id": "job", "result": {
            "content": [], "structuredContent": completed, "isError": False,
        }}}, identity=identity, correlation_id=wait_command["correlationId"],
    ))

    status, response = await call
    assert status == 200
    assert response["result"]["structuredContent"]["status"] == "Completed"
    assert response["result"]["_meta"]["autoWaitedJob"] is True
    assert store.get_job("unity-job-1")["state"] == "completed"
    store.close()
