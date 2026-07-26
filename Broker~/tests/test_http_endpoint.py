from __future__ import annotations

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
