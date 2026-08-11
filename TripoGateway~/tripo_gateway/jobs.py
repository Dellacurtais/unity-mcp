from __future__ import annotations

import asyncio
import uuid
from typing import Any, Mapping, Protocol

from .config import GatewaySettings
from .models import (
    ArtifactRecord,
    JobCreateRequest,
    JobError,
    JobRecord,
    JobStatus,
    TERMINAL_JOB_STATUSES,
    utc_now,
)
from .provider import TripoApiError, TripoProvider
from .security import redact, sha256_json, sha256_text
from .store import DurableJobStore, IdempotencyConflictError


class ArtifactSink(Protocol):
    async def download_outputs(
        self, job_id: str, output: dict[str, Any]
    ) -> list[ArtifactRecord]: ...


class GatewayPolicyError(RuntimeError):
    def __init__(self, code: str, message: str, status_code: int = 400):
        super().__init__(message)
        self.code = code
        self.status_code = status_code


class TripoJobManager:
    def __init__(
        self,
        settings: GatewaySettings,
        store: DurableJobStore,
        provider: TripoProvider | Mapping[str, TripoProvider],
        artifacts: ArtifactSink,
    ) -> None:
        self.settings = settings
        self.store = store
        self.providers = (
            dict(provider)
            if isinstance(provider, Mapping)
            else {getattr(provider, "provider_id", "tripo"): provider}
        )
        self.provider = self.providers.get("tripo") or next(iter(self.providers.values()))
        self.artifacts = artifacts
        self._tasks: dict[str, asyncio.Task[None]] = {}
        self._task_lock = asyncio.Lock()

    async def start(self) -> None:
        for record in self.store.list_non_terminal():
            if record.status in {JobStatus.SUBMITTING, JobStatus.INTERRUPTED} and not record.provider_task_id:
                self._fail(
                    record.job_id,
                    "INTERRUPTED_BEFORE_PROVIDER_TASK",
                    "Submission outcome is unknown after restart; refusing to resubmit and risk duplicate billing.",
                )
                continue
            await self._schedule(record.job_id)

    async def stop(self) -> None:
        async with self._task_lock:
            tasks = list(self._tasks.values())
            self._tasks.clear()
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

    async def submit(
        self, request: JobCreateRequest, idempotency_key: str
    ) -> tuple[JobRecord, bool]:
        selected_provider = self.providers.get(request.provider)
        if selected_provider is None:
            raise GatewayPolicyError(
                "PROVIDER_NOT_AVAILABLE", f"AI 3D provider {request.provider} is not available"
            )
        if not selected_provider.configured:
            raise GatewayPolicyError(
                "PROVIDER_NOT_CONFIGURED", f"AI 3D provider {request.provider} is not configured"
            )
        operations = getattr(selected_provider, "operations", frozenset())
        if operations and request.operation not in operations:
            raise GatewayPolicyError(
                "PROVIDER_OPERATION_UNSUPPORTED",
                f"provider {request.provider} does not support {request.operation}",
            )
        if not request.disclosure.external_send_approved:
            raise GatewayPolicyError(
                "EXTERNAL_SEND_APPROVAL_REQUIRED",
                "external_send_approved must be true before a billable provider request",
            )
        if request.budget.cap_credits > self.settings.max_credits_per_job:
            raise GatewayPolicyError(
                "CREDIT_CAP_EXCEEDED",
                f"job cap {request.budget.cap_credits} exceeds gateway cap {self.settings.max_credits_per_job}",
            )
        if not idempotency_key or len(idempotency_key) > 256:
            raise GatewayPolicyError(
                "IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 1-256 characters"
            )
        request_data = request.model_dump(mode="json")
        request_hash = sha256_json(request_data)
        key_hash = sha256_text(idempotency_key)
        existing = self.store.find_idempotent(key_hash)
        if existing is not None:
            if existing.request_hash != request_hash:
                raise IdempotencyConflictError(
                    "IDEMPOTENCY_CONFLICT: key already belongs to another request"
                )
            return existing, False
        self._enforce_project_quotas(request)
        now = utc_now()
        record = JobRecord(
            job_id=uuid.uuid4().hex,
            project_key=request.project_key,
            provider=request.provider,
            operation=request.operation,
            plan_hash=request.plan_hash,
            request_hash=request_hash,
            idempotency_key_hash=key_hash,
            estimated_credits=request.budget.estimated_credits,
            budget_cap_credits=request.budget.cap_credits,
            safe_input=redact(request_data),
            auto_download=request.auto_download,
            created_utc=now,
            updated_utc=now,
        )
        stored, created = self.store.create_idempotent(record)
        if created:
            await self._schedule(stored.job_id)
        return stored, created

    def _enforce_project_quotas(self, request: JobCreateRequest) -> None:
        records = self.store.list_project(request.project_key)
        active = [record for record in records if not record.terminal]
        if len(active) >= self.settings.max_active_jobs_per_project:
            raise GatewayPolicyError(
                "PROJECT_CONCURRENCY_QUOTA_EXCEEDED",
                f"project already has {len(active)} active AI 3D jobs",
                429,
            )
        daily_limit = self.settings.max_daily_estimated_credits_per_project
        if daily_limit <= 0:
            return
        today = utc_now()[:10]
        estimated_today = sum(
            record.estimated_credits
            for record in records
            if record.created_utc[:10] == today
        )
        if estimated_today + request.budget.estimated_credits > daily_limit:
            raise GatewayPolicyError(
                "PROJECT_DAILY_CREDIT_QUOTA_EXCEEDED",
                f"project daily estimate would exceed {daily_limit} credits",
                429,
            )

    async def reconcile_webhook(self, event_data: dict[str, Any]) -> str | None:
        task_id = event_data.get("task_id")
        if not isinstance(task_id, str) or not task_id:
            raise GatewayPolicyError("WEBHOOK_TASK_ID_MISSING", "task webhook has no task_id")
        record = next(
            (
                value for value in self.store.list(500)
                if value.provider == "tripo" and value.provider_task_id == task_id
            ),
            None,
        )
        if record is None:
            return None
        if record.terminal:
            return record.job_id
        status = str(event_data.get("status", "")).lower()
        if status == "success":
            await self._handle_success(record.job_id, event_data)
        elif status in {"failed", "cancelled"}:
            error = event_data.get("error") if isinstance(event_data.get("error"), dict) else {}
            self._fail(
                record.job_id,
                str(error.get("code") or "PROVIDER_TASK_FAILED"),
                str(error.get("message") or f"provider task {status}"),
            )
        else:
            raise GatewayPolicyError(
                "WEBHOOK_TASK_STATUS_INVALID", "task webhook status must be success, failed or cancelled"
            )
        return record.job_id

    def telemetry_summary(self, project_key: str | None = None) -> dict[str, Any]:
        records = self.store.list(500)
        if project_key:
            records = [record for record in records if record.project_key == project_key]
        statuses: dict[str, int] = {}
        operations: dict[str, int] = {}
        providers: dict[str, int] = {}
        estimated = 0
        consumed = 0
        completed_with_usage = 0
        for record in records:
            statuses[record.status.value] = statuses.get(record.status.value, 0) + 1
            operations[record.operation] = operations.get(record.operation, 0) + 1
            providers[record.provider] = providers.get(record.provider, 0) + 1
            estimated += record.estimated_credits
            if record.credits_consumed is not None:
                consumed += record.credits_consumed
                completed_with_usage += 1
        return {
            "schemaVersion": 1,
            "projectKey": project_key,
            "jobCount": len(records),
            "activeJobCount": sum(1 for record in records if not record.terminal),
            "estimatedCredits": estimated,
            "creditsConsumed": consumed,
            "jobsWithProviderUsage": completed_with_usage,
            "statuses": dict(sorted(statuses.items())),
            "operations": dict(sorted(operations.items())),
            "providers": dict(sorted(providers.items())),
            "containsPrompts": False,
            "containsCredentials": False,
        }

    def get(self, job_id: str) -> JobRecord | None:
        return self.store.get(job_id)

    def list(self, limit: int) -> list[JobRecord]:
        return self.store.list(limit)

    def cancel(self, job_id: str) -> JobRecord:
        record = self.store.get(job_id)
        if record is None:
            raise GatewayPolicyError("JOB_NOT_FOUND", "job not found", 404)
        if record.terminal:
            return record

        def mutate(value: JobRecord) -> None:
            value.cancel_requested = True
            value.status = JobStatus.CANCEL_REQUESTED
            value.message = (
                "cancel requested; provider task will still be reconciled"
                if value.provider_task_id
                else "cancel requested before provider submission"
            )
            value.updated_utc = utc_now()

        return self.store.update(job_id, mutate)

    async def wait(self, job_id: str, timeout: float = 10.0) -> JobRecord | None:
        deadline = asyncio.get_running_loop().time() + timeout
        while True:
            record = self.store.get(job_id)
            if record is None or record.terminal:
                return record
            if asyncio.get_running_loop().time() >= deadline:
                return record
            await asyncio.sleep(0.01)

    async def _schedule(self, job_id: str) -> None:
        async with self._task_lock:
            existing = self._tasks.get(job_id)
            if existing and not existing.done():
                return
            task = asyncio.create_task(self._run(job_id), name=f"ai3d-job-{job_id}")
            self._tasks[job_id] = task
            task.add_done_callback(lambda _: self._tasks.pop(job_id, None))

    async def _run(self, job_id: str) -> None:
        try:
            record = self.store.get(job_id)
            if record is None or record.terminal:
                return
            if record.cancel_requested and not record.provider_task_id:
                self._complete_cancelled(job_id)
                return
            if not record.provider_task_id:
                await self._submit_provider_task(record)
                record = self.store.get(job_id)
                if record is None or record.terminal:
                    return
            await self._poll_provider_task(record)
        except asyncio.CancelledError:
            raise
        except TripoApiError as exception:
            self._fail(job_id, exception.code, str(exception), exception.request_id)
        except GatewayPolicyError as exception:
            self._fail(job_id, exception.code, str(exception))
        except Exception as exception:
            self._fail(job_id, "GATEWAY_JOB_FAILED", _safe_exception_message(exception))

    async def _submit_provider_task(self, record: JobRecord) -> None:
        provider = self._provider_for(record)
        self._transition(record.job_id, JobStatus.SUBMITTING, 0.02, "checking balance")
        balance = await provider.get_balance()
        available = int(balance.get("balance", 0))
        if available < record.estimated_credits:
            raise GatewayPolicyError(
                "INSUFFICIENT_CREDITS",
                f"available balance {available} is below estimate {record.estimated_credits}",
            )
        current = self.store.get(record.job_id)
        if current is None:
            return
        if current.cancel_requested:
            self._complete_cancelled(record.job_id)
            return
        payload = current.safe_input.get("payload")
        if not isinstance(payload, dict):
            raise GatewayPolicyError("JOB_INPUT_INVALID", "persisted payload is invalid")
        task_id = await provider.create_task(current.operation, payload)

        def mutate(value: JobRecord) -> None:
            value.provider_task_id = task_id
            value.status = JobStatus.POLLING
            value.progress = 0.05
            value.message = "provider task submitted"
            value.updated_utc = utc_now()

        self.store.update(record.job_id, mutate)

    async def _poll_provider_task(self, record: JobRecord) -> None:
        provider = self._provider_for(record)
        while True:
            current = self.store.get(record.job_id)
            if current is None or current.terminal:
                return
            if not current.provider_task_id:
                raise GatewayPolicyError("PROVIDER_TASK_ID_MISSING", "job cannot be reconciled")
            if getattr(provider, "provider_id", current.provider) == "meshy":
                task = await provider.get_task(current.provider_task_id, current.operation)
            else:
                task = await provider.get_task(current.provider_task_id)
            refreshed_after_wait = self.store.get(current.job_id)
            if refreshed_after_wait is None or refreshed_after_wait.terminal:
                return
            provider_status = str(task.get("status", "")).lower()
            provider_progress = max(0.0, min(float(task.get("progress", 0)) / 100.0, 1.0))
            self._record_poll(current.job_id, task, provider_progress)
            if provider_status == "success":
                await self._handle_success(current.job_id, task)
                return
            if provider_status == "cancelled":
                self._complete_cancelled(current.job_id, task)
                return
            if provider_status in {"failed", "banned", "expired"}:
                self._fail(
                    current.job_id,
                    str(task.get("error_code") or "PROVIDER_TASK_FAILED"),
                    str(task.get("error_message") or f"provider task ended as {provider_status}"),
                    provider_result=task,
                )
                return
            refreshed = self.store.get(current.job_id)
            delay = (
                self.settings.cancelled_poll_interval_seconds
                if refreshed and refreshed.cancel_requested
                else self.settings.poll_interval_seconds
            )
            await asyncio.sleep(delay)

    def _provider_for(self, record: JobRecord) -> TripoProvider:
        provider = self.providers.get(record.provider)
        if provider is None:
            raise GatewayPolicyError(
                "PROVIDER_NOT_AVAILABLE",
                f"AI 3D provider {record.provider} is not available after restart",
            )
        return provider

    async def _handle_success(self, job_id: str, task: dict[str, Any]) -> None:
        current = self.store.get(job_id)
        if current is None:
            return
        credits = _optional_int(task.get("credits_consumed"))
        if current.cancel_requested:
            self._finish(
                job_id,
                JobStatus.COMPLETED_AFTER_CANCEL,
                "provider completed after local cancellation; artifacts were not downloaded",
                task,
                credits,
                [],
            )
            return
        output = task.get("output") if isinstance(task.get("output"), dict) else {}
        artifacts: list[ArtifactRecord] = []
        if current.auto_download and current.operation != "rig_check":
            if not output:
                raise GatewayPolicyError(
                    "ARTIFACT_OUTPUT_MISSING", "successful task returned no artifact output"
                )
            self._transition(job_id, JobStatus.DOWNLOADING, 0.98, "downloading artifacts")
            artifacts = await self.artifacts.download_outputs(job_id, output)
        self._finish(
            job_id,
            JobStatus.COMPLETED,
            "completed",
            task,
            credits,
            artifacts,
        )

    def _record_poll(self, job_id: str, task: dict[str, Any], progress: float) -> None:
        def mutate(value: JobRecord) -> None:
            value.status = (
                JobStatus.CANCEL_REQUESTED if value.cancel_requested else JobStatus.POLLING
            )
            value.progress = min(0.97, max(value.progress, 0.05 + progress * 0.9))
            value.message = (
                "cancel requested; reconciling provider task"
                if value.cancel_requested
                else f"provider task {task.get('status', 'unknown')}"
            )
            value.provider_result = redact(task)
            value.credits_consumed = _optional_int(task.get("credits_consumed"))
            value.updated_utc = utc_now()

        self.store.update(job_id, mutate)

    def _transition(
        self, job_id: str, status: JobStatus, progress: float, message: str
    ) -> None:
        def mutate(value: JobRecord) -> None:
            value.status = status
            value.progress = progress
            value.message = message
            value.updated_utc = utc_now()

        self.store.update(job_id, mutate)

    def _finish(
        self,
        job_id: str,
        status: JobStatus,
        message: str,
        provider_result: dict[str, Any],
        credits: int | None,
        artifacts: list[ArtifactRecord],
    ) -> None:
        def mutate(value: JobRecord) -> None:
            value.status = status
            value.progress = 1.0
            value.message = message
            value.provider_result = redact(provider_result)
            value.credits_consumed = credits
            value.artifacts = artifacts
            value.error = None
            value.updated_utc = utc_now()
            value.completed_utc = value.updated_utc

        self.store.update(job_id, mutate)

    def _complete_cancelled(
        self, job_id: str, provider_result: dict[str, Any] | None = None
    ) -> None:
        self._finish(
            job_id,
            JobStatus.CANCELLED,
            "cancelled",
            provider_result or {},
            _optional_int((provider_result or {}).get("credits_consumed")),
            [],
        )

    def _fail(
        self,
        job_id: str,
        code: str,
        message: str,
        request_id: str | None = None,
        provider_result: dict[str, Any] | None = None,
    ) -> None:
        def mutate(value: JobRecord) -> None:
            value.status = JobStatus.FAILED
            value.progress = 1.0
            value.message = "failed"
            value.error = JobError(code=code, message=message, request_id=request_id)
            if provider_result is not None:
                value.provider_result = redact(provider_result)
            value.updated_utc = utc_now()
            value.completed_utc = value.updated_utc

        try:
            self.store.update(job_id, mutate)
        except KeyError:
            pass


def _optional_int(value: Any) -> int | None:
    try:
        return int(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def _safe_exception_message(exception: Exception) -> str:
    message = str(exception).replace("\r", " ").replace("\n", " ")
    return message[:500] or exception.__class__.__name__
