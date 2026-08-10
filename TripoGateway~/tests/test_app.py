from __future__ import annotations

import hashlib
import hmac
import json
import time
from pathlib import Path

from fastapi.testclient import TestClient

from fakes import FakeArtifactVault, FakeTripoProvider
from tripo_gateway.app import create_app
from tripo_gateway.config import GatewaySettings


AUTH = {"Authorization": "Bearer local-gateway-token"}


def settings(tmp_path: Path, upload_roots: tuple[Path, ...] = ()) -> GatewaySettings:
    return GatewaySettings(
        data_dir=tmp_path / "gateway-data",
        gateway_token="local-gateway-token",
        api_key="provider-api-secret",
        upload_roots=upload_roots,
        poll_interval_seconds=0.01,
        cancelled_poll_interval_seconds=0.01,
    )


def job_body(prompt: str = "synthetic cube") -> dict:
    return {
        "project_key": "project:test",
        "operation": "text_to_model",
        "plan_hash": "a" * 64,
        "payload": {"prompt": prompt, "model": "v3.1-20260211"},
        "budget": {"estimated_credits": 100, "cap_credits": 120},
        "disclosure": {
            "external_send_approved": True,
            "kinds": ["text"],
            "source_hashes": [],
        },
        "auto_download": True,
    }


def wait_for_terminal(client: TestClient, job_id: str, timeout: float = 2.0) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        response = client.get(f"/v1/jobs/{job_id}", headers=AUTH)
        assert response.status_code == 200
        job = response.json()
        if job["status"] in {"completed", "failed", "cancelled", "completed_after_cancel"}:
            return job
        time.sleep(0.01)
    raise AssertionError("job did not become terminal")


def test_health_is_secret_free_and_v1_requires_auth(tmp_path: Path) -> None:
    app = create_app(settings(tmp_path), provider=FakeTripoProvider(), artifact_vault=FakeArtifactVault())
    with TestClient(app) as client:
        response = client.get("/health")
        assert response.status_code == 200
        raw = response.text
        assert "provider-api-secret" not in raw
        assert "local-gateway-token" not in raw
        assert "credentialSource" not in raw
        assert "credentialStoreAvailable" not in raw
        assert response.json()["configured"] is True
        assert client.get("/v1/capabilities").status_code == 401
        assert client.get("/v1/capabilities", headers={"Authorization": "Bearer wrong"}).status_code == 401
        capabilities = client.get("/v1/capabilities", headers=AUTH).json()
        assert capabilities["remoteCancellation"] is False
        assert capabilities["automaticUnityImport"] is False
        assert capabilities["credentialSource"] == "environment"
        assert capabilities["credentialStoreAvailable"] is True
        assert Path(capabilities["artifactVaultRoot"]).resolve() == (
            settings(tmp_path).data_dir / "artifacts"
        ).resolve()


def test_successful_job_is_durable_and_idempotent(tmp_path: Path) -> None:
    config = settings(tmp_path)
    provider = FakeTripoProvider()
    vault = FakeArtifactVault()
    app = create_app(config, provider=provider, artifact_vault=vault)
    request_headers = {**AUTH, "Idempotency-Key": "same-logical-request"}
    with TestClient(app) as client:
        response = client.post("/v1/jobs", headers=request_headers, json=job_body())
        assert response.status_code == 202
        first = response.json()
        assert first["created"] is True
        job_id = first["job"]["job_id"]
        completed = wait_for_terminal(client, job_id)
        assert completed["status"] == "completed"
        assert completed["credits_consumed"] == 100
        assert len(completed["artifacts"]) == 1
        assert provider.created == [
            ("text_to_model", {"prompt": "synthetic cube", "model": "v3.1-20260211"})
        ]
        replay = client.post("/v1/jobs", headers=request_headers, json=job_body())
        assert replay.status_code == 200
        assert replay.json()["created"] is False
        assert replay.json()["job"]["job_id"] == job_id
        summaries = client.get("/v1/jobs", headers=AUTH).json()["jobs"]
        assert summaries[0]["job_id"] == job_id
        assert "safe_input" not in summaries[0]
        assert "provider_result" not in summaries[0]

    second_provider = FakeTripoProvider()
    second_app = create_app(config, provider=second_provider, artifact_vault=FakeArtifactVault())
    with TestClient(second_app) as client:
        replay = client.post("/v1/jobs", headers=request_headers, json=job_body())
        assert replay.status_code == 200
        assert replay.json()["job"]["job_id"] == job_id
        assert second_provider.created == []


def test_signed_webhook_completes_job_once_and_telemetry_stays_redacted(tmp_path: Path) -> None:
    secret = "whsec_synthetic_test"
    config = GatewaySettings(
        data_dir=tmp_path / "gateway-data",
        gateway_token="local-gateway-token",
        api_key="provider-api-secret",
        webhook_secret=secret,
        poll_interval_seconds=0.01,
        cancelled_poll_interval_seconds=0.01,
    )
    provider = FakeTripoProvider(block_polling=True)
    vault = FakeArtifactVault()
    app = create_app(config, provider=provider, artifact_vault=vault)
    with TestClient(app) as client:
        created = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "webhook-job"},
            json=job_body("private webhook prompt"),
        )
        assert created.status_code == 202
        job_id = created.json()["job"]["job_id"]
        assert provider.provider_task_created.wait(1.0)
        event = {
            "id": "task_fake_001:task.completed",
            "type": "task.completed",
            "created_at": "2026-08-09T00:00:00Z",
            "data": dict(provider.task_result),
        }
        raw = json.dumps(event, separators=(",", ":")).encode("utf-8")
        timestamp = int(time.time())
        signature = hmac.new(
            secret.encode("utf-8"), str(timestamp).encode("ascii") + b"." + raw, hashlib.sha256
        ).hexdigest()
        headers = {
            "Content-Type": "application/json",
            "Tripo-Webhook-Signature": f"t={timestamp},v1={signature}",
            "Tripo-Webhook-Delivery": "delivery-001",
            "Tripo-Webhook-Event": "task.completed",
        }
        response = client.post("/v1/webhooks/tripo", headers=headers, content=raw)
        assert response.status_code == 200
        assert response.json() == {
            "accepted": True,
            "duplicate": False,
            "matchedJobId": job_id,
        }
        duplicate = client.post("/v1/webhooks/tripo", headers=headers, content=raw)
        assert duplicate.status_code == 200
        assert duplicate.json()["duplicate"] is True
        provider.release_polling.set()
        completed = wait_for_terminal(client, job_id)
        assert completed["status"] == "completed"
        assert len(vault.calls) == 1
        telemetry = client.get(
            "/v1/telemetry/summary?project_key=project:test", headers=AUTH
        )
        assert telemetry.status_code == 200
        assert telemetry.json()["creditsConsumed"] == 100
        assert "private webhook prompt" not in telemetry.text
        assert "provider-api-secret" not in telemetry.text


def test_project_quotas_block_new_work_but_never_break_idempotent_replay(tmp_path: Path) -> None:
    config = GatewaySettings(
        data_dir=tmp_path / "gateway-data",
        gateway_token="local-gateway-token",
        api_key="provider-api-secret",
        max_active_jobs_per_project=1,
        max_daily_estimated_credits_per_project=150,
        poll_interval_seconds=0.01,
        cancelled_poll_interval_seconds=0.01,
    )
    provider = FakeTripoProvider(block_polling=True)
    app = create_app(config, provider=provider, artifact_vault=FakeArtifactVault())
    first_headers = {**AUTH, "Idempotency-Key": "quota-first"}
    with TestClient(app) as client:
        first = client.post("/v1/jobs", headers=first_headers, json=job_body())
        assert first.status_code == 202
        replay = client.post("/v1/jobs", headers=first_headers, json=job_body())
        assert replay.status_code == 200
        assert replay.json()["job"]["job_id"] == first.json()["job"]["job_id"]
        blocked_active = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "quota-second"},
            json=job_body("second"),
        )
        assert blocked_active.status_code == 429
        assert blocked_active.json()["error"]["code"] == "PROJECT_CONCURRENCY_QUOTA_EXCEEDED"
        provider.release_polling.set()
        wait_for_terminal(client, first.json()["job"]["job_id"])
        blocked_daily = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "quota-third"},
            json=job_body("third"),
        )
        assert blocked_daily.status_code == 429
        assert blocked_daily.json()["error"]["code"] == "PROJECT_DAILY_CREDIT_QUOTA_EXCEEDED"


def test_job_polls_queued_running_success_and_records_provider_failure(tmp_path: Path) -> None:
    provider = FakeTripoProvider()
    provider.task_results = [
        {"task_id": "task_fake_001", "status": "queued", "progress": 0},
        {"task_id": "task_fake_001", "status": "running", "progress": 60},
        provider.task_result,
    ]
    app = create_app(settings(tmp_path), provider=provider, artifact_vault=FakeArtifactVault())
    with TestClient(app) as client:
        created = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "state-sequence"},
            json=job_body(),
        )
        completed = wait_for_terminal(client, created.json()["job"]["job_id"])
        assert completed["status"] == "completed"
        assert len(provider.polled) >= 3

    failing = FakeTripoProvider()
    failing.task_result = {
        "task_id": "task_fake_001",
        "status": "failed",
        "progress": 100,
        "error_code": "synthetic_failure",
        "error_message": "fixture failure",
        "credits_consumed": 0,
    }
    failing_app = create_app(
        settings(tmp_path / "failed"), provider=failing, artifact_vault=FakeArtifactVault()
    )
    with TestClient(failing_app) as client:
        created = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "failed-state"},
            json=job_body(),
        )
        failed = wait_for_terminal(client, created.json()["job"]["job_id"])
        assert failed["status"] == "failed"
        assert failed["error"]["code"] == "synthetic_failure"


def test_idempotency_conflict_is_409(tmp_path: Path) -> None:
    app = create_app(settings(tmp_path), provider=FakeTripoProvider(), artifact_vault=FakeArtifactVault())
    headers = {**AUTH, "Idempotency-Key": "reused-key"}
    with TestClient(app) as client:
        assert client.post("/v1/jobs", headers=headers, json=job_body()).status_code == 202
        conflict = client.post("/v1/jobs", headers=headers, json=job_body("different prompt"))
        assert conflict.status_code == 409
        assert conflict.json()["error"]["code"] == "IDEMPOTENCY_CONFLICT"


def test_insufficient_balance_fails_before_provider_submission(tmp_path: Path) -> None:
    provider = FakeTripoProvider()
    provider.balance = {"balance": 50, "frozen": 0}
    app = create_app(settings(tmp_path), provider=provider, artifact_vault=FakeArtifactVault())
    with TestClient(app) as client:
        created = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "insufficient-balance"},
            json=job_body(),
        )
        failed = wait_for_terminal(client, created.json()["job"]["job_id"])
        assert failed["status"] == "failed"
        assert failed["error"]["code"] == "INSUFFICIENT_CREDITS"
        assert provider.created == []


def test_cancel_after_submit_reconciles_without_downloading(tmp_path: Path) -> None:
    provider = FakeTripoProvider(block_polling=True)
    vault = FakeArtifactVault()
    app = create_app(settings(tmp_path), provider=provider, artifact_vault=vault)
    with TestClient(app) as client:
        response = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "cancel-after-submit"},
            json=job_body(),
        )
        job_id = response.json()["job"]["job_id"]
        assert provider.provider_task_created.wait(timeout=1.0)
        cancelled = client.post(f"/v1/jobs/{job_id}/cancel", headers=AUTH)
        assert cancelled.status_code == 202
        assert cancelled.json()["cancel_requested"] is True
        provider.release_polling.set()
        completed = wait_for_terminal(client, job_id)
        assert completed["status"] == "completed_after_cancel"
        assert completed["artifacts"] == []
        assert vault.calls == []


def test_upload_requires_allowed_root_approval_and_hash(tmp_path: Path) -> None:
    approved = tmp_path / "approved"
    approved.mkdir()
    source = approved / "concept.png"
    source.write_bytes(b"synthetic-png")
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    provider = FakeTripoProvider()
    app = create_app(
        settings(tmp_path, (approved,)), provider=provider, artifact_vault=FakeArtifactVault()
    )
    body = {
        "project_key": "project:test",
        "source_path": str(source),
        "expected_sha256": digest,
        "external_send_approved": True,
    }
    with TestClient(app) as client:
        uploaded = client.post("/v1/uploads", headers=AUTH, json=body)
        assert uploaded.status_code == 201
        assert uploaded.json()["fileToken"] == "file_fake_001"
        assert uploaded.json()["sha256"] == digest
        audits = list((settings(tmp_path, (approved,)).data_dir / "uploads").glob("*.json"))
        assert len(audits) == 1
        audit_text = audits[0].read_text("utf-8")
        assert "file_fake_001" not in audit_text
        assert str(source) not in audit_text

        rejected = dict(body, expected_sha256="0" * 64)
        response = client.post("/v1/uploads", headers=AUTH, json=rejected)
        assert response.status_code == 400
        assert response.json()["error"]["code"] == "UPLOAD_HASH_MISMATCH"

        outside = tmp_path / "outside.png"
        outside.write_bytes(b"outside")
        outside_body = dict(body, source_path=str(outside))
        outside_body["expected_sha256"] = hashlib.sha256(outside.read_bytes()).hexdigest()
        response = client.post("/v1/uploads", headers=AUTH, json=outside_body)
        assert response.status_code == 400
        assert response.json()["error"]["code"] == "UPLOAD_SOURCE_REJECTED"


def test_rig_check_completes_as_metadata_without_artifact_download(tmp_path: Path) -> None:
    provider = FakeTripoProvider()
    provider.task_result = {
        "task_id": "task_fake_001",
        "type": "rig_check",
        "status": "success",
        "progress": 100,
        "output": {"riggable": True, "rig_type": "biped"},
        "credits_consumed": 0,
    }
    vault = FakeArtifactVault()
    app = create_app(settings(tmp_path), provider=provider, artifact_vault=vault)
    body = {
        "project_key": "project:test",
        "operation": "rig_check",
        "plan_hash": "b" * 64,
        "payload": {"input": "task_source"},
        "budget": {"estimated_credits": 0, "cap_credits": 0},
        "disclosure": {
            "external_send_approved": True,
            "kinds": ["task_reference"],
            "source_hashes": [],
        },
        "auto_download": True,
    }
    with TestClient(app) as client:
        created = client.post(
            "/v1/jobs", headers={**AUTH, "Idempotency-Key": "rig-check"}, json=body
        )
        completed = wait_for_terminal(client, created.json()["job"]["job_id"])
        assert completed["status"] == "completed"
        assert completed["provider_result"]["output"]["riggable"] is True
        assert completed["artifacts"] == []
        assert vault.calls == []


def test_request_limit_and_job_payload_never_persist_api_key(tmp_path: Path) -> None:
    config = GatewaySettings(
        data_dir=tmp_path / "data",
        gateway_token="local-gateway-token",
        api_key="provider-api-secret",
        max_request_bytes=64 * 1024,
        poll_interval_seconds=0.01,
        cancelled_poll_interval_seconds=0.01,
    )
    app = create_app(config, provider=FakeTripoProvider(), artifact_vault=FakeArtifactVault())
    with TestClient(app) as client:
        response = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "too-big", "Content-Length": str(70 * 1024)},
            content=b"{}",
        )
        assert response.status_code == 413

        created = client.post(
            "/v1/jobs",
            headers={**AUTH, "Idempotency-Key": "secret-scan"},
            json=job_body(),
        )
        assert created.status_code == 202
        wait_for_terminal(client, created.json()["job"]["job_id"])
    persisted = "\n".join(path.read_text("utf-8") for path in config.data_dir.rglob("*.json"))
    assert "provider-api-secret" not in persisted
    assert "local-gateway-token" not in persisted
