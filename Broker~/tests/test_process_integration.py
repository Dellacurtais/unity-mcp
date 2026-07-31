from __future__ import annotations

import asyncio
import json
import os
import socket
import sqlite3
import subprocess
import sys
import time
from contextlib import closing
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import httpx
import pytest
import websockets
from websockets.exceptions import InvalidStatus


BROKER_ROOT = Path(__file__).resolve().parents[1]
TOKEN = "process-integration-secret"


def reserve_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


@dataclass
class BrokerProcess:
    root: Path
    port: int = field(default_factory=reserve_loopback_port)
    environment_overrides: dict[str, str] = field(default_factory=dict)
    process: subprocess.Popen[str] | None = None
    _log: Any = None

    @property
    def database_path(self) -> Path:
        return self.root / "broker.sqlite3"

    @property
    def http_url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    @property
    def websocket_url(self) -> str:
        return f"ws://127.0.0.1:{self.port}/bridge"

    @property
    def headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {TOKEN}",
            "MCP-Protocol-Version": "2025-03-26",
        }

    def start(self) -> None:
        if self.process is not None:
            raise RuntimeError("broker process is already started")
        self.root.mkdir(parents=True, exist_ok=True)
        self._log = (self.root / "process.log").open("a", encoding="utf-8")
        environment = os.environ.copy()
        environment.update({
            "PYTHONUNBUFFERED": "1",
            "UNITY_MCP_BROKER_HOST": "127.0.0.1",
            "UNITY_MCP_BROKER_PORT": str(self.port),
            "UNITY_MCP_BROKER_TOKEN": TOKEN,
            "UNITY_MCP_BROKER_DB": str(self.database_path),
            "UNITY_MCP_BROKER_TIMEOUT": "2",
            "UNITY_MCP_BROKER_RECONNECT_GRACE": "0.25",
            "UNITY_MCP_BROKER_MAX_REQUEST_BYTES": "1024",
        })
        environment.update(self.environment_overrides)
        self.process = subprocess.Popen(
            [sys.executable, str(BROKER_ROOT / "main.py")],
            cwd=BROKER_ROOT,
            env=environment,
            stdout=self._log,
            stderr=subprocess.STDOUT,
            text=True,
        )
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                break
            try:
                response = httpx.get(f"{self.http_url}/health", timeout=0.25)
                if response.status_code == 200:
                    return
            except httpx.HTTPError:
                pass
            time.sleep(0.05)
        exit_code = self.process.poll()
        self.stop()
        log = (self.root / "process.log").read_text(encoding="utf-8", errors="replace")
        raise AssertionError(f"broker did not become healthy (exit={exit_code})\n{log}")

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if self._log is not None:
            self._log.close()
            self._log = None

    def latest_request_id(self) -> str:
        with closing(sqlite3.connect(self.database_path)) as connection:
            row = connection.execute(
                "SELECT request_id FROM requests ORDER BY created_utc DESC LIMIT 1"
            ).fetchone()
        if row is None:
            raise AssertionError("request ledger is empty")
        return str(row[0])


@pytest.fixture
def broker_process(tmp_path: Path):
    broker = BrokerProcess(tmp_path / "broker process with spaces")
    broker.start()
    try:
        yield broker
    finally:
        broker.stop()


def test_real_process_stops_when_managed_parent_exits(tmp_path: Path) -> None:
    guardian = subprocess.Popen(
        [sys.executable, "-c", "import time; time.sleep(60)"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    broker = BrokerProcess(
        tmp_path / "parent-owned broker",
        environment_overrides={"UNITY_MCP_BROKER_PARENT_PID": str(guardian.pid)},
    )
    try:
        broker.start()
        guardian.terminate()
        guardian.wait(timeout=5)
        assert broker.process is not None
        assert broker.process.wait(timeout=10) == 0
    finally:
        broker.stop()
        if guardian.poll() is None:
            guardian.kill()
            guardian.wait(timeout=5)


def bridge_hello(generation: str = "generation-a") -> dict[str, Any]:
    tool = {
        "name": "read_scene",
        "description": "Process integration probe",
        "inputSchema": {"type": "object", "additionalProperties": False},
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    }
    return {
        "version": 1,
        "type": "hello",
        "messageId": f"hello-{generation}",
        "correlationId": None,
        "projectKey": "process-project",
        "processId": 4242,
        "generationId": generation,
        "payload": {
            "projectKey": "process-project",
            "projectName": "Process Project",
            "projectPath": "D:/Process Project",
            "processId": 4242,
            "processStartUtc": "2026-07-26T00:00:00+00:00",
            "generationId": generation,
            "packageVersion": "0.16.12",
            "registryCount": 1,
            "registryHash": f"registry-{generation}",
            "state": "ready",
            "tools": [tool],
        },
    }


def bridge_message(
    message_type: str,
    generation: str,
    payload: dict[str, Any],
    *,
    correlation_id: str,
) -> dict[str, Any]:
    return {
        "version": 1,
        "type": message_type,
        "messageId": f"{message_type}-{time.monotonic_ns()}",
        "correlationId": correlation_id,
        "projectKey": "process-project",
        "processId": 4242,
        "generationId": generation,
        "payload": payload,
    }


@pytest.mark.asyncio
async def test_real_process_routes_websocket_command_and_recovers_sqlite_after_restart(
    broker_process: BrokerProcess,
) -> None:
    async with httpx.AsyncClient(base_url=broker_process.http_url, timeout=5) as client:
        unauthorized = await client.post(
            "/mcp/", json={"jsonrpc": "2.0", "id": 1, "method": "ping"}
        )
        assert unauthorized.status_code == 401

        initialized = await client.post(
            "/mcp/",
            headers=broker_process.headers,
            json={
                "jsonrpc": "2.0",
                "id": 2,
                "method": "initialize",
                "params": {"protocolVersion": "2025-03-26"},
            },
        )
        assert initialized.json()["result"]["serverInfo"]["version"] == "0.16.12"

        with pytest.raises(InvalidStatus):
            async with websockets.connect(
                broker_process.websocket_url,
                additional_headers={"Authorization": "Bearer wrong"},
                open_timeout=2,
            ):
                pass

        async with websockets.connect(
            broker_process.websocket_url,
            additional_headers={"Authorization": f"Bearer {TOKEN}"},
            origin="http://127.0.0.1",
            open_timeout=2,
        ) as bridge:
            await bridge.send(json.dumps(bridge_hello()))
            acknowledgement = json.loads(await asyncio.wait_for(bridge.recv(), timeout=2))
            assert acknowledgement["type"] == "hello_ack"
            assert acknowledgement["payload"]["active"] is True
            health = (await client.get("/health")).json()
            assert health["bridgeConnected"] is True
            assert health["routeReady"] is True
            assert health["activeProjectName"] == "Process Project"
            assert health["registryCount"] == 1

            listed = await client.post(
                "/mcp/",
                headers=broker_process.headers,
                json={"jsonrpc": "2.0", "id": 3, "method": "tools/list"},
            )
            names = {tool["name"] for tool in listed.json()["result"]["tools"]}
            assert {"read_scene", "broker_get_status"} <= names

            request_task = asyncio.create_task(client.post(
                "/mcp/",
                headers=broker_process.headers,
                json={
                    "jsonrpc": "2.0",
                    "id": 4,
                    "method": "tools/call",
                    "params": {
                        "name": "read_scene",
                        "arguments": {},
                        "_meta": {"idempotencyKey": "process-read"},
                    },
                },
            ))
            command = json.loads(await asyncio.wait_for(bridge.recv(), timeout=2))
            assert command["type"] == "command"
            assert command["payload"]["jsonRpc"]["params"]["name"] == "read_scene"
            await bridge.send(json.dumps(bridge_message(
                "accepted",
                "generation-a",
                {"requestId": command["payload"]["requestId"]},
                correlation_id=command["correlationId"],
            )))
            await bridge.send(json.dumps(bridge_message(
                "result",
                "generation-a",
                {
                    "jsonRpc": {
                        "jsonrpc": "2.0",
                        "id": 4,
                        "result": {"content": [{"type": "text", "text": "scene-ok"}]},
                    }
                },
                correlation_id=command["correlationId"],
            )))
            routed = await request_task
            assert routed.json()["result"]["content"][0]["text"] == "scene-ok"

    request_id = broker_process.latest_request_id()
    broker_process.stop()
    broker_process.start()

    async with httpx.AsyncClient(base_url=broker_process.http_url, timeout=5) as client:
        health = (await client.get("/health")).json()
        assert health["activeProjectKey"] == "process-project"
        assert health["bridgeConnected"] is False
        assert health["routeReady"] is False

        listed = await client.post(
            "/mcp/",
            headers=broker_process.headers,
            json={"jsonrpc": "2.0", "id": 5, "method": "tools/list"},
        )
        assert "read_scene" in {tool["name"] for tool in listed.json()["result"]["tools"]}

        ledger = await client.post(
            "/mcp/",
            headers=broker_process.headers,
            json={
                "jsonrpc": "2.0",
                "id": 6,
                "method": "tools/call",
                "params": {"name": "broker_get_request", "arguments": {"requestId": request_id}},
            },
        )
        persisted = ledger.json()["result"]["structuredContent"]
        assert persisted["state"] == "completed"
        assert persisted["response"]["result"]["content"][0]["text"] == "scene-ok"


@pytest.mark.asyncio
async def test_real_process_marks_accepted_non_idempotent_disconnect_as_outcome_unknown(
    broker_process: BrokerProcess,
) -> None:
    hello = bridge_hello()
    hello["payload"]["tools"] = [{
        "name": "create_empty",
        "description": "Mutation probe",
        "inputSchema": {"type": "object"},
        "annotations": {"readOnlyHint": False, "idempotentHint": False},
    }]
    hello["payload"]["registryHash"] = "registry-mutation"

    async with httpx.AsyncClient(base_url=broker_process.http_url, timeout=5) as client:
        bridge = await websockets.connect(
            broker_process.websocket_url,
            additional_headers={"Authorization": f"Bearer {TOKEN}"},
            open_timeout=2,
        )
        await bridge.send(json.dumps(hello))
        await asyncio.wait_for(bridge.recv(), timeout=2)

        request_task = asyncio.create_task(client.post(
            "/mcp/",
            headers=broker_process.headers,
            json={
                "jsonrpc": "2.0",
                "id": 7,
                "method": "tools/call",
                "params": {"name": "create_empty", "arguments": {"name": "Probe"}},
            },
        ))
        command = json.loads(await asyncio.wait_for(bridge.recv(), timeout=2))
        await bridge.send(json.dumps(bridge_message(
            "accepted",
            "generation-a",
            {"requestId": command["payload"]["requestId"]},
            correlation_id=command["correlationId"],
        )))
        await bridge.close()

        response = await request_task
        error = response.json()["result"]["structuredContent"]
        assert error["code"] == "OUTCOME_UNKNOWN"
        assert error["data"]["retrySafe"] is False

        oversized = await client.post(
            "/mcp/",
            headers={**broker_process.headers, "Content-Type": "application/json"},
            content=b"{" + (b" " * 2048) + b"}",
        )
        assert oversized.status_code == 413
