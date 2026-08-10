from __future__ import annotations

from pathlib import Path

from fastapi.testclient import TestClient

from fakes import FakeArtifactVault, FakeTripoProvider
from tripo_gateway.app import create_app
from tripo_gateway.config import GatewaySettings
from tripo_gateway.secrets import MemoryCredentialStore, TripoCredentialManager


AUTH = {"Authorization": "Bearer local-gateway-token"}


def settings(tmp_path: Path) -> GatewaySettings:
    return GatewaySettings(
        data_dir=tmp_path / "gateway-data",
        gateway_token="local-gateway-token",
        api_key=None,
        poll_interval_seconds=0.01,
        cancelled_poll_interval_seconds=0.01,
    )


def test_credential_manager_prefers_secure_store_and_clear_reveals_environment() -> None:
    store = MemoryCredentialStore()
    manager = TripoCredentialManager("environment-secret", store)
    assert manager.api_key == "environment-secret"
    assert manager.status().source == "environment"

    status = manager.replace("stored-secret-123456")
    assert status.configured is True
    assert status.source == "memory"
    assert manager.api_key == "stored-secret-123456"

    status = manager.clear()
    assert status.configured is True
    assert status.source == "environment"
    assert manager.api_key == "environment-secret"


def test_authenticated_credential_api_never_returns_or_echoes_key(tmp_path: Path) -> None:
    secret = "tripo-secret-value-123456"
    manager = TripoCredentialManager(None, MemoryCredentialStore())
    app = create_app(
        settings(tmp_path),
        provider=FakeTripoProvider(),
        artifact_vault=FakeArtifactVault(),
        credential_manager=manager,
    )
    with TestClient(app) as client:
        assert client.get("/v1/credentials/tripo").status_code == 401
        saved = client.put(
            "/v1/credentials/tripo", headers=AUTH, json={"apiKey": secret}
        )
        assert saved.status_code == 200
        assert saved.json()["configured"] is True
        assert saved.json()["source"] == "memory"
        assert secret not in saved.text

        status = client.get("/v1/credentials/tripo", headers=AUTH)
        assert status.status_code == 200
        assert secret not in status.text

        rejected_value = "invalid secret with spaces"
        rejected = client.put(
            "/v1/credentials/tripo",
            headers=AUTH,
            json={"apiKey": rejected_value},
        )
        assert rejected.status_code == 400
        assert rejected.json()["error"]["code"] == "TRIPO_API_KEY_INVALID"
        assert rejected_value not in rejected.text

        removed = client.delete("/v1/credentials/tripo", headers=AUTH)
        assert removed.status_code == 200
        assert removed.json()["configured"] is False
        assert manager.api_key is None
