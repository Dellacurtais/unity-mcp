from __future__ import annotations

import asyncio
import time
from dataclasses import replace
from datetime import datetime, timedelta, timezone

import pytest

from config import BrokerConfig
from mcp import McpBroker
from protocol import BridgeIdentity, envelope
from registry import RegistryCache
from router import BridgeRouter, BridgeUnavailable
from store import BrokerStore


class Socket:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    async def send_json(self, message: dict) -> None:
        self.sent.append(message)

    @property
    def commands(self) -> list[dict]:
        return [message for message in self.sent if message["type"] == "command"]


def identity(project="a", generation="generation-a") -> BridgeIdentity:
    return BridgeIdentity(project, project, f"D:/{project}", 123, "2026-01-01T00:00:00Z",
                          generation, "0.18.6", 1, "registry")


def descriptor(name="create_empty", *, job=False) -> dict:
    tool = {"name": name, "inputSchema": {"type": "object"},
            "annotations": {"readOnlyHint": False, "idempotentHint": False}}
    if job:
        tool["_meta"] = {"jobLifecycle": {"autoWait": True, "waitTool": "wait_tool_job"}}
    return tool


def request(request_id, name="create_empty", *, arguments=None, meta=None) -> dict:
    return {"jsonrpc": "2.0", "id": request_id, "method": "tools/call",
            "params": {"name": name, "arguments": arguments or {}, "_meta": meta or {}}}


async def until(predicate) -> None:
    async def wait() -> None:
        while not predicate():
            await asyncio.sleep(0)
    await asyncio.wait_for(wait(), timeout=2)


async def respond(router, connection, message, result=None) -> None:
    await router.handle(connection, envelope("result", {"jsonRpc": {
        "jsonrpc": "2.0", "id": message["payload"]["jsonRpc"]["id"],
        "result": result if result is not None else {"ok": True},
    }}, identity=connection.identity, correlation_id=message["correlationId"]))


@pytest.fixture
def broker(tmp_path):
    store = BrokerStore(tmp_path / "broker.sqlite3")
    registry = RegistryCache()
    router = BridgeRouter(store, registry, focus_assist_enabled=False)
    instance = McpBroker(BrokerConfig(database_path=tmp_path / "broker.sqlite3", request_timeout_seconds=2),
                         store, registry, router)
    yield instance
    store.close()


@pytest.mark.asyncio
async def test_twenty_duplicate_writes_join_one_accepted_operation(broker) -> None:
    socket = Socket()
    connection = await broker.router.register(socket, identity(), [descriptor()], "ready")
    tasks = [asyncio.create_task(broker.process(request(i, meta={"idempotencyKey": "one-write"})))
             for i in range(10)]
    await until(lambda: len(socket.commands) == 1)
    command = socket.commands[0]
    ledger_id = command["payload"]["requestId"]
    await broker.router.handle(connection, envelope("accepted", {"requestId": ledger_id},
                               identity=connection.identity, correlation_id=command["correlationId"]))
    tasks.extend(asyncio.create_task(broker.process(request(i, meta={"idempotencyKey": "one-write"})))
                 for i in range(10, 20))
    await until(lambda: len(broker._client_requests) == 20)
    assert len(socket.commands) == 1
    assert broker.store.get_request(ledger_id)["state"] == "accepted_by_unity"
    await respond(broker.router, connection, command)
    responses = await asyncio.gather(*tasks)
    assert [response[1]["id"] for response in responses] == list(range(20))
    assert all(response[1]["result"]["ok"] for response in responses)
    _, replay = await broker.process(request(99, meta={"idempotencyKey": "one-write"}))
    assert replay["id"] == 99
    assert len(socket.commands) == 1


@pytest.mark.asyncio
async def test_sessions_isolate_project_rpc_ids_and_cancellation(broker) -> None:
    a_socket, b_socket = Socket(), Socket()
    a = await broker.router.register(a_socket, identity(), [descriptor()], "ready")
    b = await broker.router.register(b_socket, identity("b"), [descriptor()], "ready")
    session_a, session_b = broker.create_session(), broker.create_session()
    await broker.process(request(8, "broker_activate_project", arguments={"projectKey": "b"}), session_id=session_b)
    assert broker.router.active_project_key == "a"
    calls = [asyncio.create_task(broker.process(request(1), session_id=session))
             for session in (session_a, session_b)]
    await until(lambda: len(a_socket.commands) == len(b_socket.commands) == 1)
    a_command, b_command = a_socket.commands[0], b_socket.commands[0]
    assert a_command["payload"]["jsonRpc"]["id"] != b_command["payload"]["jsonRpc"]["id"]
    await broker.router.activate("b")
    await broker.process({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 1}},
                         session_id=session_a)
    assert a_socket.sent[-1]["type"] == "cancel"
    assert a_socket.sent[-1]["payload"]["requestId"] == a_command["payload"]["jsonRpc"]["id"]
    assert not any(message["type"] == "cancel" for message in b_socket.sent)
    await respond(broker.router, a, a_command)
    await respond(broker.router, b, b_command)
    assert [response[1]["id"] for response in await asyncio.gather(*calls)] == [1, 1]


@pytest.mark.asyncio
async def test_refresh_cancellation_targets_job_owner_and_waits_for_ack(broker) -> None:
    a_socket, b_socket = Socket(), Socket()
    a = await broker.router.register(a_socket, identity(), [], "ready")
    await broker.router.register(b_socket, identity("b"), [], "ready")
    job = broker.store.create_job("refresh_compile", "a")
    broker.store.update_job(job["job_id"], "running")
    await broker.router.activate("b")
    _, response = await broker.process(request(1, "broker_cancel_job", arguments={"jobId": job["job_id"]}))
    assert response["result"]["structuredContent"]["state"] == "cancel_requested"
    assert a_socket.commands[-1]["payload"]["control"] == "cancel_job"
    assert b_socket.commands == []
    await broker.router.handle(a, envelope("event", {
        "event": "refresh_cancelled", "state": "ready", "jobId": job["job_id"],
    }, identity=a.identity))
    assert broker.store.get_job(job["job_id"])["state"] == "cancelled"


@pytest.mark.asyncio
async def test_job_poll_stays_on_original_project_after_global_route_switch(broker) -> None:
    a_socket, b_socket = Socket(), Socket()
    a = await broker.router.register(a_socket, identity(), [descriptor("run_tests", job=True)], "ready")
    await broker.router.register(b_socket, identity("b"), [descriptor("run_tests", job=True)], "ready")
    call = asyncio.create_task(broker.process(request(7, "run_tests")))
    await until(lambda: len(a_socket.commands) == 1)
    await broker.router.activate("b")
    await respond(broker.router, a, a_socket.commands[0], {
        "isError": False, "structuredContent": {"jobId": "job-a", "kind": "test", "status": "Running"},
    })
    await until(lambda: len(a_socket.commands) == 2)
    assert a_socket.commands[1]["payload"]["jsonRpc"]["params"]["name"] == "wait_tool_job"
    assert b_socket.commands == []
    await respond(broker.router, a, a_socket.commands[1], {
        "isError": False, "structuredContent": {"jobId": "job-a", "kind": "test", "status": "Completed"},
    })
    _, response = await call
    assert response["id"] == 7
    assert response["result"]["structuredContent"]["status"] == "Completed"


@pytest.mark.asyncio
async def test_reconnect_requires_success_evidence_and_never_completes_other_job_types(broker) -> None:
    jobs = {}
    for label, kind, result in (
        ("proven", "refresh_compile", {"event": "compilation_finished", "generationId": "old", "messages": []}),
        ("unproven", "refresh_compile", {}),
        ("test", "test", {"status": "Running"}),
        ("error", "refresh_compile", {"event": "compilation_finished", "generationId": "old", "messages": [{"severity": "Error"}]}),
    ):
        job = broker.store.create_job(kind, "a")
        broker.store.update_job(job["job_id"], "waiting_for_reconnect", result=result)
        jobs[label] = job["job_id"]
    connection = await broker.router.register(Socket(), identity(), [], "compile_failed")
    assert all(broker.store.get_job(job_id)["state"] == "waiting_for_reconnect" for job_id in jobs.values())
    await broker.router.unregister(connection)
    await broker.router.register(Socket(), replace(identity(), generation_id="new"), [], "ready")
    assert broker.store.get_job(jobs["proven"])["state"] == "completed"
    assert all(broker.store.get_job(jobs[label])["state"] == "waiting_for_reconnect"
               for label in ("unproven", "test", "error"))


@pytest.mark.asyncio
@pytest.mark.parametrize("hold_lock", [False, True])
async def test_dispatch_deadline_covers_blocked_send_and_send_queue(broker, hold_lock) -> None:
    class BlockedSocket:
        async def send_json(self, message):
            await asyncio.Event().wait()

    connection = await broker.router.register(BlockedSocket(), identity(), [], "ready")
    if hold_lock:
        await connection.send_lock.acquire()
    try:
        operation = asyncio.create_task(broker.router.dispatch("deadline", request(1), timeout=0.03, project_key="a"))
        done, pending = await asyncio.wait({operation}, timeout=0.5)
        assert not pending, "dispatch exceeded its deadline while sending or waiting for the send lock"
        with pytest.raises(asyncio.TimeoutError):
            await operation
        assert not connection.pending
    finally:
        if hold_lock:
            connection.send_lock.release()


@pytest.mark.asyncio
async def test_explicit_project_and_generation_guard_override_legacy_route(broker) -> None:
    a_socket, b_socket = Socket(), Socket()
    a = await broker.router.register(a_socket, identity(), [descriptor()], "ready")
    await broker.router.register(b_socket, identity("b"), [descriptor()], "ready")
    await broker.router.activate("b")
    _, stale = await broker.process(request(1, meta={"projectKey": "a", "expectedGenerationId": "old"}))
    assert stale["result"]["structuredContent"]["code"] == "STALE_GENERATION"
    assert not a_socket.commands and not b_socket.commands
    call = asyncio.create_task(broker.process(request(2, meta={"projectKey": "a", "expectedGenerationId": "generation-a"})))
    await until(lambda: len(a_socket.commands) == 1)
    await respond(broker.router, a, a_socket.commands[0])
    assert (await call)[1]["id"] == 2
    assert not b_socket.commands


@pytest.mark.asyncio
@pytest.mark.parametrize("read_only", [False, True])
async def test_disconnect_after_send_without_ack_never_replays_a_write(broker, read_only) -> None:
    class DroppedSocket(Socket):
        async def send_json(self, message):
            self.sent.append(message)
            raise OSError("Connection lost before the accepted acknowledgement")

    socket = DroppedSocket()
    tool = descriptor()
    tool["annotations"]["readOnlyHint"] = read_only
    await broker.router.register(socket, identity(), [tool], "ready")
    _, response = await broker.process(request(1))
    code = response["result"]["structuredContent"]["code"]
    assert code == ("UNITY_RECONNECT_REQUIRED" if read_only else "OUTCOME_UNKNOWN")
    assert response["result"]["structuredContent"]["data"]["retrySafe"] is read_only
    assert len(socket.commands) == 1


@pytest.mark.asyncio
async def test_heartbeat_distinguishes_blocked_main_thread_from_compilation_and_dead_transport(broker) -> None:
    socket = Socket()
    connection = await broker.router.register(socket, identity(), [descriptor()], "ready")
    old_tick = (datetime.now(timezone.utc) - timedelta(seconds=40)).isoformat()
    await broker.router.handle(connection, envelope("heartbeat", {
        "state": "ready", "mainThreadLastTickUtc": old_tick,
        "dispatcherQueueDepth": 3, "inFlightRequests": 2,
    }, identity=connection.identity))
    status = broker.router.connection_status("a")
    assert status["bridgeState"] == "blocked"
    assert status["dispatcherQueueDepth"] == 3 and status["inFlightRequests"] == 2
    with pytest.raises(BridgeUnavailable, match="blocked"):
        broker.router.connection_for("a")
    await broker.router.handle(connection, envelope("heartbeat", {"state": "compiling"}, identity=connection.identity))
    assert broker.router.connection_status("a")["routeReady"] is True
    connection.last_heartbeat = time.monotonic() - 40
    assert broker.router.connection_status("a")["bridgeState"] == "unresponsive"
    await broker.router.handle(connection, envelope("heartbeat", {
        "state": "ready", "mainThreadLastTickUtc": datetime.now(timezone.utc).isoformat(),
    }, identity=connection.identity))
    assert broker.router.connection_status("a")["routeReady"] is True


@pytest.mark.asyncio
async def test_capacity_rejection_does_not_enqueue_or_create_another_request(broker) -> None:
    broker.MAX_INFLIGHT_REQUESTS = 1
    socket = Socket()
    connection = await broker.router.register(socket, identity(), [descriptor()], "ready")
    call = asyncio.create_task(broker.process(request(1)))
    await until(lambda: len(socket.commands) == 1)
    _, busy = await broker.process(request(2))
    assert busy["result"]["structuredContent"]["code"] == "BROKER_BUSY"
    assert len(socket.commands) == 1
    await respond(broker.router, connection, socket.commands[0])
    await call
