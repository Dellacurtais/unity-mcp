from __future__ import annotations

from pathlib import Path

import pytest

from fakes import FakeArtifactVault, FakeTripoProvider
from tripo_gateway.config import GatewaySettings
from tripo_gateway.jobs import TripoJobManager
from tripo_gateway.models import JobRecord, JobStatus, utc_now
from tripo_gateway.store import DurableJobStore


def settings(tmp_path: Path) -> GatewaySettings:
    return GatewaySettings(
        data_dir=tmp_path,
        gateway_token="gateway-token",
        api_key="provider-key",
        poll_interval_seconds=0.01,
        cancelled_poll_interval_seconds=0.01,
    )


def interrupted_record() -> JobRecord:
    now = utc_now()
    return JobRecord(
        job_id="a" * 32,
        project_key="project:test",
        operation="text_to_model",
        plan_hash="b" * 64,
        request_hash="c" * 64,
        idempotency_key_hash="d" * 64,
        status=JobStatus.SUBMITTING,
        progress=0.02,
        message="submitting",
        estimated_credits=100,
        budget_cap_credits=120,
        safe_input={"payload": {"prompt": "synthetic"}},
        created_utc=now,
        updated_utc=now,
    )


@pytest.mark.asyncio
async def test_restart_does_not_resubmit_unknown_provider_outcome(tmp_path: Path) -> None:
    config = settings(tmp_path)
    store = DurableJobStore(config.data_dir)
    store.save(interrupted_record())
    provider = FakeTripoProvider()
    manager = TripoJobManager(config, store, provider, FakeArtifactVault())
    await manager.start()
    try:
        record = store.get("a" * 32)
        assert record is not None
        assert record.status == JobStatus.FAILED
        assert record.error is not None
        assert record.error.code == "INTERRUPTED_BEFORE_PROVIDER_TASK"
        assert provider.created == []
    finally:
        await manager.stop()


@pytest.mark.asyncio
async def test_restart_resumes_polling_when_provider_task_id_is_durable(tmp_path: Path) -> None:
    config = settings(tmp_path)
    store = DurableJobStore(config.data_dir)
    record = interrupted_record().model_copy(
        update={
            "status": JobStatus.POLLING,
            "provider_task_id": "task_durable_001",
            "message": "provider task submitted",
        }
    )
    store.save(record)
    provider = FakeTripoProvider()
    vault = FakeArtifactVault()
    manager = TripoJobManager(config, store, provider, vault)
    await manager.start()
    try:
        completed = await manager.wait(record.job_id, timeout=1.0)
        assert completed is not None
        assert completed.status == JobStatus.COMPLETED
        assert provider.created == []
        assert provider.polled == ["task_durable_001"]
        assert len(vault.calls) == 1
    finally:
        await manager.stop()


def test_idempotency_index_is_recovered_from_job_file(tmp_path: Path) -> None:
    store = DurableJobStore(tmp_path)
    record = interrupted_record()
    store.save(record)
    stored, created = store.create_idempotent(record.model_copy(update={"job_id": "e" * 32}))
    assert created is False
    assert stored.job_id == record.job_id
    index = tmp_path / "idempotency" / f"{record.idempotency_key_hash}.json"
    assert index.exists()
