"""Resilience behaviour added on 2026-11-19: pending answers, reconnect waits, token
drift, session adoption, loop watchdog, orphan decisions and lighter history."""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone

import httpx
import pytest

from app import create_app
from auth import TokenSource
from config import BrokerConfig
from editor_control import ProcessInfo
from mcp import McpBroker
from orphans import plan
from protocol import BridgeIdentity, envelope
from registry import RegistryCache
from router import BridgeRouter
from store import BrokerStore
from watchdog import LoopWatchdog


class FakeWebSocket:
    def __init__(self) -> None:
        self.sent = []

    async def send_json(self, message) -> None:
        self.sent.append(message)


def identity(generation: str = "generation-a") -> BridgeIdentity:
    return BridgeIdentity(
        project_key="project", project_name="Game", project_path="D:/Game",
        process_id=123, process_start_utc="2026-01-01T00:00:00Z",
        generation_id=generation, package_version="1.0.0",
        registry_count=1, registry_hash="registry-a",
    )


TOOLS = [
    {"name": "slow_write", "inputSchema": {"type": "object"},
     "annotations": {"readOnlyHint": False, "idempotentHint": False}},
    {"name": "read_scene", "inputSchema": {"type": "object"},
     "annotations": {"readOnlyHint": True, "idempotentHint": True}},
]


def make_broker(tmp_path, **overrides):
    store = BrokerStore(tmp_path / "broker.sqlite3")
    registry = RegistryCache()
    router = BridgeRouter(store, registry, focus_assist_enabled=False)
    config = BrokerConfig(database_path=tmp_path / "broker.sqlite3", **{
        "request_timeout_seconds": 30, "sync_wait_seconds": 0.3, "max_wait_call_seconds": 2,
        "reconnect_grace_seconds": 5, **overrides})
    return McpBroker(config, store, registry, router), store, router


async def wait_for_command(socket: FakeWebSocket, count: int = 1):
    for _ in range(500):
        commands = [message for message in socket.sent if message["type"] == "command"]
        if len(commands) >= count:
            return commands[count - 1]
        await asyncio.sleep(0.01)
    raise AssertionError("no command was sent to Unity")


async def answer(router, connection, command, result):
    await router.handle(connection, envelope(
        "result", {"jsonRpc": {"jsonrpc": "2.0", "id": command["payload"]["jsonRpc"]["id"], "result": result}},
        identity=connection.identity, correlation_id=command["correlationId"]))


@pytest.mark.asyncio
async def test_slow_call_answers_pending_then_wait_request_returns_the_real_result(tmp_path) -> None:
    broker, store, router = make_broker(tmp_path)
    socket = FakeWebSocket()
    connection = await router.register(socket, identity(), TOOLS, "ready")
    _, pending = await broker.process({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                                       "params": {"name": "slow_write", "arguments": {}}})
    structured = pending["result"]["structuredContent"]
    assert pending["id"] == 1 and pending["result"]["isError"] is False
    assert structured["status"] == "running" and structured["next"]["tool"] == "broker_wait_request"
    request_id = structured["requestId"]

    command = await wait_for_command(socket)
    waiting = asyncio.create_task(broker.process({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                                                  "params": {"name": "broker_wait_request",
                                                             "arguments": {"requestId": request_id}}}))
    await asyncio.sleep(0.05)
    await answer(router, connection, command, {"content": [], "structuredContent": {"done": True}, "isError": False})
    _, final = await waiting
    assert final["id"] == 2
    assert final["result"]["structuredContent"] == {"done": True}
    # Once finished, the ledger still answers after the task is gone.
    _, again = await broker.process({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                                     "params": {"name": "broker_wait_request", "arguments": {"requestId": request_id}}})
    assert again["result"]["structuredContent"] == {"done": True}
    listed = store.list_requests(limit=5)
    assert listed[0]["request_id"] == request_id and listed[0]["state"] == "completed"
    store.close()


@pytest.mark.asyncio
async def test_read_during_domain_reload_queues_and_runs_after_reconnect(tmp_path) -> None:
    broker, store, router = make_broker(tmp_path, sync_wait_seconds=3)
    first = FakeWebSocket()
    connection = await router.register(first, identity("generation-a"), TOOLS, "ready")
    await router.unregister(connection, "reloading")
    call = asyncio.create_task(broker.process({"jsonrpc": "2.0", "id": 9, "method": "tools/call",
                                               "params": {"name": "read_scene", "arguments": {}}}))
    await asyncio.sleep(0.2)
    assert not call.done()
    second = FakeWebSocket()
    reconnected = await router.register(second, identity("generation-b"), TOOLS, "ready")
    command = await wait_for_command(second)
    await answer(router, reconnected, command, {"content": [], "structuredContent": {"scene": "Main"}, "isError": False})
    _, response = await call
    assert response["result"]["structuredContent"] == {"scene": "Main"}
    store.close()


@pytest.mark.asyncio
async def test_project_that_never_connected_fails_fast_with_a_hint(tmp_path) -> None:
    broker, store, _ = make_broker(tmp_path, sync_wait_seconds=10)
    broker.registry.update("project", "hash", TOOLS)
    store.set_active_project("project")
    started = asyncio.get_running_loop().time()
    _, response = await broker.process({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
                                        "params": {"name": "read_scene", "arguments": {}}})
    assert asyncio.get_running_loop().time() - started < 2
    assert response["result"]["structuredContent"]["code"] == "UNITY_OFFLINE"
    assert "broker_editor_status" in response["result"]["structuredContent"]["data"]["hint"]
    store.close()


@pytest.mark.asyncio
async def test_job_polls_use_fresh_ids(tmp_path) -> None:
    broker, store, router = make_broker(tmp_path, sync_wait_seconds=5)
    tools = [
        {"name": "run_tests", "inputSchema": {"type": "object"},
         "annotations": {"readOnlyHint": False, "idempotentHint": False},
         "_meta": {"jobLifecycle": {"waitTool": "wait_tool_job", "autoWait": True}}},
        {"name": "wait_tool_job", "inputSchema": {"type": "object"},
         "annotations": {"readOnlyHint": True, "idempotentHint": True}},
    ]
    socket = FakeWebSocket()
    connection = await router.register(socket, identity(), tools, "ready")
    call = asyncio.create_task(broker.process({"jsonrpc": "2.0", "id": 7, "method": "tools/call",
                                               "params": {"name": "run_tests", "arguments": {}}}))
    running = {"jobId": "job-1", "status": "Running", "progress": 0.1, "message": "running"}
    await answer(router, connection, await wait_for_command(socket, 1),
                 {"content": [], "structuredContent": running, "isError": False})
    await answer(router, connection, await wait_for_command(socket, 2),
                 {"content": [], "structuredContent": running, "isError": False})
    third = await wait_for_command(socket, 3)
    done = {**running, "status": "Completed", "progress": 1}
    await answer(router, connection, third, {"content": [], "structuredContent": done, "isError": False})
    _, response = await call
    commands = [message for message in socket.sent if message["type"] == "command"]
    poll_ids = [command["payload"]["jsonRpc"]["id"] for command in commands[1:]]
    assert len(poll_ids) == len(set(poll_ids)) == 2
    assert response["result"]["structuredContent"]["status"] == "Completed"
    store.close()


def test_token_source_accepts_the_user_token_and_follows_rotation() -> None:
    values = {"UNITY_MCP_BROKER_TOKEN": "a" * 32}
    now = [0.0]
    tokens = TokenSource("launch-token", reader=values.get, clock=lambda: now[0], cache_seconds=60)
    assert tokens.accepts("launch-token")
    assert tokens.accepts("a" * 32)
    values["UNITY_MCP_BROKER_TOKEN"] = "b" * 32
    assert tokens.accepts("b" * 32), "a rotated user token is re-read before refusing"
    assert not tokens.accepts("a" * 32)
    values["UNITY_MCP_BROKER_TOKEN"] = "short"
    assert not tokens.accepts("short"), "too-short user values are ignored"
    assert not tokens.accepts_header("Basic launch-token")


@pytest.mark.asyncio
async def test_unknown_session_is_adopted_after_restart_but_closed_one_stays_closed(tmp_path) -> None:
    config = BrokerConfig(token="secret", database_path=tmp_path / "broker.sqlite3")
    headers = {"Authorization": "Bearer secret"}
    ping = {"jsonrpc": "2.0", "id": 2, "method": "ping"}
    app = create_app(config)
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
            adopted = await client.post("/mcp/", headers={**headers, "Mcp-Session-Id": "f" * 32}, json=ping)
            assert adopted.status_code == 200
            assert app.state.broker.has_session("f" * 32)
            assert (await client.delete("/mcp/", headers={**headers, "Mcp-Session-Id": "f" * 32})).status_code == 204
            closed = await client.post("/mcp/", headers={**headers, "Mcp-Session-Id": "f" * 32}, json=ping)
            assert closed.status_code == 404
            newer = await client.post("/mcp/", headers={**headers, "MCP-Protocol-Version": "2025-11-25"}, json={
                "jsonrpc": "2.0", "id": 3, "method": "initialize", "params": {"protocolVersion": "2026-07-28"}})
            assert newer.status_code == 200
            assert newer.json()["result"]["protocolVersion"] == "2025-06-18"
            health = (await client.get("/health")).json()
            assert health["syncWaitSeconds"] == config.sync_wait_seconds and "pid" in health
    finally:
        app.state.store.close()


def test_watchdog_dumps_once_then_exits_on_a_long_stall(tmp_path) -> None:
    now = [100.0]
    exits = []
    watchdog = LoopWatchdog(dump_after=15, exit_after=90, dump_path=tmp_path / "watchdog.log",
                            exit_process=exits.append, clock=lambda: now[0])
    watchdog.last_pulse = 100.0
    assert watchdog.check_once() == "ok"
    now[0] = 120.0
    assert watchdog.check_once() == "dumped"
    assert "Current thread" in (tmp_path / "watchdog.log").read_text(encoding="utf-8")
    assert watchdog.check_once() == "ok"
    now[0] = 200.0
    assert watchdog.check_once() == "exited" and exits == [70]


def test_watchdog_exits_when_owner_is_gone_and_loop_is_blocked(tmp_path) -> None:
    now = [0.0]
    exits = []
    watchdog = LoopWatchdog(dump_after=15, exit_after=90, parent_alive=lambda: False,
                            exit_process=exits.append, clock=lambda: now[0])
    watchdog.last_pulse = 0.0
    now[0] = 1.0
    assert watchdog.check_once() == "ok", "a healthy loop handles the parent itself"
    now[0] = 5.0
    assert watchdog.check_once() == "parent_gone" and exits == [0]


def proc(pid, ppid, name, cmd="", exe="", minutes_ago=30.0, now=None):
    now = now or datetime(2026, 11, 19, 12, 0, tzinfo=timezone.utc)
    return ProcessInfo(pid, ppid, name, cmd, exe, now - timedelta(minutes=minutes_ago))


def test_orphan_plan_follows_the_tree_and_never_touches_editors_or_hub() -> None:
    now = datetime(2026, 11, 19, 12, 0, tzinfo=timezone.utc)
    editor_exe = r"C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"
    shader_exe = r"C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data\Tools\UnityShaderCompiler.exe"
    broker_cmd = r'"C:\Users\u\AppData\Local\UnityMcp\Broker\installations\0.19.1-x\venv\Scripts\python.exe" C:\Users\u\AppData\Local\UnityMcp\Broker\installations\0.19.1-x\source\main.py'
    supervisor_cmd = broker_cmd.replace("main.py", "supervisor.py")
    processes = [
        proc(10, 1, "Unity.exe", f'"{editor_exe}" -projectPath G:\\Game', editor_exe, 120, now),
        proc(11, 10, "UnityShaderCompiler.exe", "", shader_exe, 100, now),
        proc(12, 999, "UnityShaderCompiler.exe", "", shader_exe, 100, now),            # editor gone
        proc(13, 999, "Unity.exe", f'"{editor_exe}" -batchMode -name AssetImportWorker0 -mainProcessId 10', editor_exe, 100, now),
        proc(14, 999, "Unity.exe", f'"{editor_exe}" -batchMode -name AssetImportWorker1 -mainProcessId 777', editor_exe, 100, now),
        proc(15, 50, "UnityShaderCompiler.exe", "", shader_exe, 100, now),             # parent 50 reused PID
        proc(50, 1, "notepad.exe", "", r"C:\Windows\notepad.exe", 5, now),
        proc(20, 1, "Unity Hub.exe", "", r"C:\Program Files\Unity Hub\Unity Hub.exe", 300, now),
        proc(21, 20, "unity.exe", "serve", r"C:\Program Files\Unity Hub\resources\unity.exe", 300, now),
        proc(30, 1, "python.exe", supervisor_cmd, "", 60, now),
        proc(31, 30, "python.exe", broker_cmd, "", 59, now),
        proc(32, 888, "python.exe", broker_cmd, "", 59, now),                           # orphan broker
        proc(33, 1, "Unity.exe", f'"{editor_exe}" -batchmode -projectPath G:\\Other', editor_exe, 300, now),
        proc(40, 999, "UnityShaderCompiler.exe", "", shader_exe, 1, now),               # too young
        proc(41, 999, "UnityShaderCompiler.exe", "", shader_exe, 100, now),             # protected
    ]
    decisions = {decision.pid: decision for decision in plan(processes, now=now, protect_pids=[41])}
    closes = {pid for pid, decision in decisions.items() if decision.action == "close"}
    assert closes == {12, 14, 15, 32}
    assert decisions[11].action == "keep" and decisions[13].action == "keep"
    assert decisions[31].action == "keep" and decisions[40].action == "keep" and decisions[41].action == "keep"
    for never in (10, 20, 21, 30, 33, 50):
        assert never not in closes


def test_prune_expires_old_write_payloads_and_extra_registry_snapshots(tmp_path) -> None:
    store = BrokerStore(tmp_path / "ledger.db")
    old = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
    store.create_request("write", None, "p", "g", "write", "hash", False, False)
    store.update_request("write", "completed", response={"result": "x" * 100})
    store._connection.execute("UPDATE requests SET updated_utc=?", (old,))
    for index in range(5):
        store.save_registry("p", f"hash-{index}", [{"name": f"tool_{index}"}])
        store._connection.execute("UPDATE registry_snapshots SET created_utc=? WHERE registry_hash=?",
                                  (f"2026-11-1{index}T00:00:00+00:00", f"hash-{index}"))
    store._connection.commit()
    kept = store.prune_history(retention_days=7)
    assert kept["expiredResponses"] == 0, "writes are kept unless a write retention is set"
    result = store.prune_history(retention_days=7, write_retention_days=14, registry_snapshots_per_project=2)
    assert result["expiredResponses"] == 1 and result["deletedSnapshots"] == 3
    record = store.get_request("write")
    assert record["state"] == "completed" and "response" not in record and "RESPONSE_EXPIRED" in record["error"]
    assert [row["registry_hash"] for row in store.load_registry_snapshots()] == ["hash-4"]
    store.close()
