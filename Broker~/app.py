from __future__ import annotations

import hmac
import json
import asyncio
import logging
from contextlib import asynccontextmanager
from urllib.parse import urlparse

from fastapi import FastAPI, Header, HTTPException, Request, Response, WebSocket, WebSocketDisconnect

from broker_version import BROKER_VERSION
from config import BrokerConfig
from mcp import McpBroker
from protocol import BridgeIdentity, validate_envelope
from registry import RegistryCache
from router import BridgeRouter
from store import BrokerStore


def create_app(config: BrokerConfig | None = None) -> FastAPI:
    settings = config or BrokerConfig.from_env()
    store = BrokerStore(settings.database_path, response_inline_bytes=settings.response_inline_bytes)
    store.reconcile_startup()
    registry = RegistryCache()
    for snapshot in store.load_registry_snapshots():
        registry.update(
            str(snapshot["project_key"]),
            str(snapshot["registry_hash"]),
            snapshot["tools"],
        )
    router = BridgeRouter(
        store,
        registry,
        focus_assist_enabled=settings.focus_assist_enabled,
        focus_assist_delay_seconds=settings.focus_assist_delay_seconds,
    )
    broker = McpBroker(settings, store, registry, router)

    async def maintain_history() -> None:
        while True:
            await asyncio.sleep(settings.history_maintenance_seconds)
            maintenance = asyncio.create_task(asyncio.to_thread(
                store.prune_history,
                retention_days=settings.history_retention_days,
                max_response_bytes=settings.history_max_response_bytes,
            ))
            try:
                await asyncio.shield(maintenance)
            except asyncio.CancelledError:
                # SQLite must remain open until the worker thread has released it.
                await asyncio.gather(maintenance, return_exceptions=True)
                raise
            except Exception:
                logging.getLogger(__name__).exception("Broker history maintenance failed")

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        maintenance_task = asyncio.create_task(maintain_history())
        try:
            yield
        finally:
            maintenance_task.cancel()
            await asyncio.gather(maintenance_task, return_exceptions=True)
            await broker.close()
            store.close()

    app = FastAPI(title="unity-mcp-broker", version=BROKER_VERSION, lifespan=lifespan)
    app.state.config = settings
    app.state.store = store
    app.state.registry = registry
    app.state.router = router
    app.state.broker = broker

    def authorized(value: str | None) -> bool:
        if not settings.token:
            return False
        if not value or not value.lower().startswith("bearer "):
            return False
        return hmac.compare_digest(value[7:].strip(), settings.token)

    def allowed_origin(value: str | None) -> bool:
        if not value:
            return True
        try:
            parsed = urlparse(value)
        except ValueError:
            return False
        return parsed.scheme in {"http", "https"} and parsed.hostname in {
            "127.0.0.1", "localhost", "::1",
        }

    @app.get("/health")
    async def health() -> dict[str, object]:
        active = router.active_project_key
        connection = router.connections.get(active or "")
        liveness = router.connection_status(active)
        registry_snapshot = registry.get(active)
        return {
            "service": "unity-mcp-broker",
            "version": BROKER_VERSION,
            "schemaVersion": store.schema_version(),
            "activeProjectKey": active,
            "activeProjectName": connection.identity.project_name if connection else None,
            "activeProjectPath": connection.identity.project_path if connection else None,
            "bridgeConnected": connection is not None,
            **liveness,
            "connectedProjects": len(router.connections),
            "registryCount": len(registry_snapshot.tools) if registry_snapshot else 0,
            "focusAssistEnabled": settings.focus_assist_enabled,
            "focusAssistDelaySeconds": settings.focus_assist_delay_seconds,
            "autoWaitJobs": settings.auto_wait_jobs,
        }

    @app.post("/mcp/")
    async def mcp_endpoint(
        request: Request,
        authorization: str | None = Header(default=None),
        mcp_protocol_version: str | None = Header(default=None, alias="MCP-Protocol-Version"),
        mcp_session_id: str | None = Header(default=None, alias="Mcp-Session-Id"),
    ) -> Response:
        if not allowed_origin(request.headers.get("origin")):
            raise HTTPException(status_code=403, detail="Origin is not an allowed loopback origin")
        if not authorized(authorization):
            raise HTTPException(status_code=401, detail="Bearer token is required")
        if mcp_session_id is not None and not broker.has_session(mcp_session_id):
            raise HTTPException(status_code=404, detail="MCP session expired; initialize a new session")
        content_type = request.headers.get("content-type", "")
        if not content_type.lower().startswith("application/json"):
            raise HTTPException(status_code=415, detail="Content-Type must be application/json")
        accept = request.headers.get("accept", "")
        if accept and "application/json" not in accept and "*/*" not in accept:
            raise HTTPException(status_code=406, detail="Accept must include application/json or */*")
        if mcp_protocol_version and mcp_protocol_version not in {
            "2024-11-05", "2025-03-26", "2025-06-18",
        }:
            raise HTTPException(status_code=400, detail="Unsupported MCP-Protocol-Version")
        content_length = request.headers.get("content-length")
        try:
            declared_length = int(content_length) if content_length else None
        except ValueError:
            raise HTTPException(status_code=400, detail="Content-Length must be an integer")
        if declared_length is not None and declared_length > settings.max_request_bytes:
            raise HTTPException(status_code=413, detail="MCP request body exceeds the configured limit")
        chunks: list[bytes] = []
        received = 0
        async for chunk in request.stream():
            received += len(chunk)
            if received > settings.max_request_bytes:
                raise HTTPException(status_code=413, detail="MCP request body exceeds the configured limit")
            chunks.append(chunk)
        body = b"".join(chunks)
        try:
            payload = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            status, result = 200, {"jsonrpc": "2.0", "error": {"code": -32700, "message": "Parse error"}}
        else:
            if (mcp_session_id is None and isinstance(payload, dict)
                    and payload.get("jsonrpc") == "2.0" and payload.get("method") == "initialize"
                    and "id" in payload):
                try:
                    mcp_session_id = broker.create_session()
                except ValueError as exception:
                    raise HTTPException(status_code=503, detail=str(exception))
            status, result = await broker.process(payload, mcp_protocol_version, mcp_session_id)
        session_headers = {"Mcp-Session-Id": mcp_session_id} if mcp_session_id else {}
        if result is None:
            return Response(status_code=status, headers=session_headers)
        return Response(
            content=json.dumps(result, separators=(",", ":")),
            status_code=status,
            media_type="application/json",
            headers=session_headers,
        )

    @app.delete("/mcp/")
    async def delete_session(
        request: Request,
        authorization: str | None = Header(default=None),
        mcp_session_id: str | None = Header(default=None, alias="Mcp-Session-Id"),
    ) -> Response:
        if not allowed_origin(request.headers.get("origin")):
            raise HTTPException(status_code=403, detail="Origin is not an allowed loopback origin")
        if not authorized(authorization):
            raise HTTPException(status_code=401, detail="Bearer token is required")
        if not mcp_session_id or not broker.has_session(mcp_session_id):
            raise HTTPException(status_code=404, detail="MCP session was not found")
        broker.drop_session(mcp_session_id)
        return Response(status_code=204)

    @app.websocket("/bridge")
    async def bridge_endpoint(websocket: WebSocket) -> None:
        if not allowed_origin(websocket.headers.get("origin")):
            await websocket.close(code=4403, reason="Origin is not an allowed loopback origin")
            return
        if not authorized(websocket.headers.get("authorization")):
            await websocket.close(code=4401, reason="Bearer token is required")
            return
        await websocket.accept()
        connection = None
        try:
            hello = await websocket.receive_json()
            validate_envelope(hello)
            if hello["type"] != "hello":
                raise ValueError("first bridge message must be hello")
            payload = hello["payload"]
            identity = BridgeIdentity.from_hello(payload)
            tools = payload.get("tools")
            if not isinstance(tools, list):
                raise ValueError("bridge hello tools must be an array")
            connection = await router.register(
                websocket, identity, tools, str(payload.get("state", "ready"))
            )
            await websocket.send_json({
                "version": 1,
                "type": "hello_ack",
                "messageId": hello["messageId"],
                "correlationId": hello["messageId"],
                "projectKey": identity.project_key,
                "processId": identity.process_id,
                "generationId": identity.generation_id,
                "payload": {"active": router.active_project_key == identity.project_key},
            })
            while True:
                await router.handle(connection, await websocket.receive_json())
        except WebSocketDisconnect:
            pass
        except ValueError as exception:
            await websocket.close(code=4400, reason=str(exception)[:120])
        finally:
            if connection:
                await router.unregister(connection)

    return app
