from __future__ import annotations

import asyncio
import hashlib
import json
from pathlib import Path

import httpx
import pytest

from tripo_gateway.artifacts import ArtifactVault
from tripo_gateway.config import GatewaySettings
from tripo_gateway.provider import TripoApiError, TripoHttpProvider
from tripo_gateway.security import validate_download_url
from tripo_gateway.secrets import MemoryCredentialStore, TripoCredentialManager


def settings(tmp_path: Path, *, retries: int = 2) -> GatewaySettings:
    return GatewaySettings(
        data_dir=tmp_path,
        gateway_token="gateway-token",
        api_key="provider-secret",
        max_retries=retries,
    )


@pytest.mark.asyncio
async def test_provider_retries_429_and_honors_retry_after(tmp_path: Path) -> None:
    attempts = 0
    sleeps: list[float] = []

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal attempts
        attempts += 1
        assert request.headers["Authorization"] == "Bearer provider-secret"
        assert request.url.path == "/v3/account/balance"
        if attempts == 1:
            return httpx.Response(
                429,
                headers={"Retry-After": "0.25"},
                json={"code": 429, "message": "rate limited"},
            )
        return httpx.Response(200, json={"code": 0, "data": {"balance": 500, "frozen": 0}})

    async def fake_sleep(delay: float) -> None:
        sleeps.append(delay)

    client = httpx.AsyncClient(
        base_url="https://openapi.tripo3d.ai/v3", transport=httpx.MockTransport(handler)
    )
    provider = TripoHttpProvider(settings(tmp_path), client=client, sleep=fake_sleep)
    try:
        assert await provider.get_balance() == {"balance": 500, "frozen": 0}
        assert attempts == 2
        assert sleeps == [0.25]
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_provider_uses_runtime_secure_credential_without_restart(tmp_path: Path) -> None:
    credentials = TripoCredentialManager(None, MemoryCredentialStore())

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.headers["Authorization"] == "Bearer runtime-secret-123456"
        return httpx.Response(200, json={"code": 0, "data": {"balance": 321, "frozen": 0}})

    client = httpx.AsyncClient(
        base_url="https://openapi.tripo3d.ai/v3", transport=httpx.MockTransport(handler)
    )
    provider = TripoHttpProvider(
        GatewaySettings(
            data_dir=tmp_path,
            gateway_token="gateway-token",
            api_key=None,
        ),
        client=client,
        credentials=credentials,
    )
    try:
        with pytest.raises(TripoApiError) as missing:
            await provider.get_balance()
        assert missing.value.code == "TRIPO_NOT_CONFIGURED"

        credentials.replace("runtime-secret-123456")
        assert provider.configured is True
        assert await provider.get_balance() == {"balance": 321, "frozen": 0}

        credentials.clear()
        assert provider.configured is False
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_provider_uses_allowlisted_endpoint_and_rejects_unknown_operation(tmp_path: Path) -> None:
    seen_paths: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen_paths.append(request.url.path)
        return httpx.Response(200, json={"code": 0, "data": {"task_id": "task_abc123"}})

    client = httpx.AsyncClient(
        base_url="https://openapi.tripo3d.ai/v3", transport=httpx.MockTransport(handler)
    )
    provider = TripoHttpProvider(settings(tmp_path), client=client)
    try:
        assert await provider.create_task("segment", {"input": "task_source"}) == "task_abc123"
        assert seen_paths == ["/v3/mesh/segment"]
        with pytest.raises(TripoApiError, match="unsupported operation") as raised:
            await provider.create_task("raw_endpoint", {})
        assert raised.value.code == "TRIPO_OPERATION_NOT_ALLOWED"
        assert seen_paths == ["/v3/mesh/segment"]
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_provider_redacts_api_key_from_remote_error(tmp_path: Path) -> None:
    def handler(_: httpx.Request) -> httpx.Response:
        return httpx.Response(
            400,
            json={"code": "bad_request", "message": "echo provider-secret should not escape"},
        )

    client = httpx.AsyncClient(
        base_url="https://openapi.tripo3d.ai/v3", transport=httpx.MockTransport(handler)
    )
    provider = TripoHttpProvider(settings(tmp_path), client=client)
    try:
        with pytest.raises(TripoApiError) as raised:
            await provider.get_balance()
        assert "provider-secret" not in str(raised.value)
        assert "[REDACTED]" in str(raised.value)
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_upload_retry_rewinds_file(tmp_path: Path) -> None:
    source = tmp_path / "input.glb"
    source.write_bytes(b"0123456789")
    bodies: list[bytes] = []

    def handler(request: httpx.Request) -> httpx.Response:
        bodies.append(request.content)
        if len(bodies) == 1:
            return httpx.Response(429, headers={"Retry-After": "0"}, json={"code": 429})
        return httpx.Response(200, json={"code": 0, "data": {"file_token": "file_retry_ok"}})

    async def no_sleep(_: float) -> None:
        return None

    client = httpx.AsyncClient(
        base_url="https://openapi.tripo3d.ai/v3", transport=httpx.MockTransport(handler)
    )
    provider = TripoHttpProvider(settings(tmp_path), client=client, sleep=no_sleep)
    try:
        assert await provider.upload_file(source) == "file_retry_ok"
        assert len(bodies) == 2
        assert b"0123456789" in bodies[0]
        assert b"0123456789" in bodies[1]
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_artifact_vault_streams_hashes_and_writes_manifest(tmp_path: Path) -> None:
    content = b"synthetic-glb-content"

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.host == "cdn.tripo3d.ai"
        assert "Authorization" not in request.headers
        return httpx.Response(
            200,
            headers={"Content-Type": "model/gltf-binary", "Content-Length": str(len(content))},
            content=content,
        )

    client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    vault = ArtifactVault(tmp_path, 1024, client=client)
    try:
        records = await vault.download_outputs(
            "a" * 32, {"model_url": "https://cdn.tripo3d.ai/output/generated.glb"}
        )
        assert len(records) == 1
        record = records[0]
        assert Path(record.path).read_bytes() == content
        assert record.sha256 == hashlib.sha256(content).hexdigest()
        manifest = json.loads((tmp_path / "artifacts" / ("a" * 32) / "manifest.json").read_text("utf-8"))
        assert manifest["artifacts"][0]["sha256"] == record.sha256
        assert list((tmp_path / "artifacts" / ("a" * 32)).glob("*.part")) == []
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_artifact_vault_rejects_foreign_hosts_redirects_and_oversize(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="host is not an approved AI 3D provider"):
        validate_download_url("https://example.com/model.glb")

    def redirect_handler(_: httpx.Request) -> httpx.Response:
        return httpx.Response(302, headers={"Location": "https://evil.example/model.glb"})

    redirect_client = httpx.AsyncClient(transport=httpx.MockTransport(redirect_handler))
    redirect_vault = ArtifactVault(tmp_path / "redirect", 1024, client=redirect_client)
    try:
        with pytest.raises(ValueError, match="host is not an approved AI 3D provider"):
            await redirect_vault.download_outputs(
                "b" * 32, {"model_url": "https://cdn.tripo3d.ai/output/model.glb"}
            )
    finally:
        await redirect_client.aclose()

    def oversize_handler(_: httpx.Request) -> httpx.Response:
        return httpx.Response(200, headers={"Content-Length": "4096"}, content=b"x")

    oversize_client = httpx.AsyncClient(transport=httpx.MockTransport(oversize_handler))
    oversize_vault = ArtifactVault(tmp_path / "oversize", 1024, client=oversize_client)
    try:
        with pytest.raises(ValueError, match="ARTIFACT_TOO_LARGE"):
            await oversize_vault.download_outputs(
                "c" * 32, {"model_url": "https://cdn.tripo3d.ai/output/model.glb"}
            )
    finally:
        await oversize_client.aclose()


@pytest.mark.asyncio
async def test_artifact_vault_requires_downloadable_output(tmp_path: Path) -> None:
    client = httpx.AsyncClient(transport=httpx.MockTransport(lambda _: httpx.Response(500)))
    vault = ArtifactVault(tmp_path, 1024, client=client)
    try:
        with pytest.raises(ValueError, match="ARTIFACT_OUTPUT_MISSING"):
            await vault.download_outputs("d" * 32, {"riggable": True})
    finally:
        await client.aclose()
