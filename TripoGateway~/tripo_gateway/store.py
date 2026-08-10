from __future__ import annotations

import json
import os
import threading
import uuid
from pathlib import Path
from typing import Callable

from .models import JobRecord
from .models import TERMINAL_JOB_STATUSES


class IdempotencyConflictError(RuntimeError):
    pass


class DurableJobStore:
    def __init__(self, root: Path):
        self.root = Path(root).resolve()
        self.jobs_dir = self.root / "jobs"
        self.idempotency_dir = self.root / "idempotency"
        self.jobs_dir.mkdir(parents=True, exist_ok=True)
        self.idempotency_dir.mkdir(parents=True, exist_ok=True)
        self._lock = threading.RLock()

    def get(self, job_id: str) -> JobRecord | None:
        path = self._job_path(job_id)
        if not path.exists():
            return None
        with self._lock:
            return JobRecord.model_validate_json(path.read_text(encoding="utf-8"))

    def list(self, limit: int = 100) -> list[JobRecord]:
        with self._lock:
            records: list[JobRecord] = []
            for path in self.jobs_dir.glob("*.json"):
                try:
                    records.append(JobRecord.model_validate_json(path.read_text("utf-8")))
                except (OSError, ValueError, json.JSONDecodeError):
                    continue
            records.sort(key=lambda item: item.created_utc, reverse=True)
            return records[: max(1, min(limit, 500))]

    def list_non_terminal(self) -> list[JobRecord]:
        with self._lock:
            records: list[JobRecord] = []
            for path in self.jobs_dir.glob("*.json"):
                try:
                    record = JobRecord.model_validate_json(path.read_text("utf-8"))
                except (OSError, ValueError, json.JSONDecodeError):
                    continue
                if record.status not in TERMINAL_JOB_STATUSES:
                    records.append(record)
            records.sort(key=lambda item: item.created_utc)
            return records

    def create_idempotent(self, record: JobRecord) -> tuple[JobRecord, bool]:
        with self._lock:
            mapping_path = self._idempotency_path(record.idempotency_key_hash)
            if mapping_path.exists():
                mapping = json.loads(mapping_path.read_text("utf-8"))
                if mapping.get("requestHash") != record.request_hash:
                    raise IdempotencyConflictError(
                        "IDEMPOTENCY_CONFLICT: key already belongs to another request"
                    )
                existing = self.get(str(mapping.get("jobId", "")))
                if existing is None:
                    raise RuntimeError("IDEMPOTENCY_INDEX_CORRUPT")
                return existing, False
            recovered = self._find_by_idempotency(record.idempotency_key_hash)
            if recovered is not None:
                if recovered.request_hash != record.request_hash:
                    raise IdempotencyConflictError(
                        "IDEMPOTENCY_CONFLICT: key already belongs to another request"
                    )
                self._atomic_json(
                    mapping_path,
                    {"requestHash": recovered.request_hash, "jobId": recovered.job_id},
                )
                return recovered, False
            self._atomic_json(self._job_path(record.job_id), record.model_dump(mode="json"))
            self._atomic_json(
                mapping_path,
                {"requestHash": record.request_hash, "jobId": record.job_id},
            )
            return record, True

    def _find_by_idempotency(self, key_hash: str) -> JobRecord | None:
        for path in self.jobs_dir.glob("*.json"):
            try:
                candidate = JobRecord.model_validate_json(path.read_text("utf-8"))
            except (OSError, ValueError, json.JSONDecodeError):
                continue
            if candidate.idempotency_key_hash == key_hash:
                return candidate
        return None

    def save(self, record: JobRecord) -> JobRecord:
        with self._lock:
            self._atomic_json(self._job_path(record.job_id), record.model_dump(mode="json"))
            return record

    def update(self, job_id: str, mutate: Callable[[JobRecord], None]) -> JobRecord:
        with self._lock:
            record = self.get(job_id)
            if record is None:
                raise KeyError(job_id)
            mutate(record)
            self._atomic_json(self._job_path(job_id), record.model_dump(mode="json"))
            return record

    def _job_path(self, job_id: str) -> Path:
        if not job_id or any(char not in "0123456789abcdef" for char in job_id):
            raise ValueError("INVALID_JOB_ID")
        return self.jobs_dir / f"{job_id}.json"

    def _idempotency_path(self, key_hash: str) -> Path:
        if len(key_hash) != 64 or any(char not in "0123456789abcdef" for char in key_hash):
            raise ValueError("INVALID_IDEMPOTENCY_HASH")
        return self.idempotency_dir / f"{key_hash}.json"

    @staticmethod
    def _atomic_json(path: Path, value: object) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
        payload = json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2)
        try:
            with temporary.open("w", encoding="utf-8", newline="\n") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temporary, path)
        finally:
            if temporary.exists():
                temporary.unlink(missing_ok=True)
