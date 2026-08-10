from __future__ import annotations

from datetime import datetime, timezone
from enum import Enum
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


OperationName = Literal[
    "text_to_model",
    "image_to_model",
    "multiview_to_model",
    "texture",
    "convert",
    "segment",
    "complete",
    "retopology",
    "rig_check",
    "rig",
    "retarget",
]


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class JobStatus(str, Enum):
    QUEUED = "queued"
    SUBMITTING = "submitting"
    POLLING = "polling"
    DOWNLOADING = "downloading"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCEL_REQUESTED = "cancel_requested"
    CANCELLED = "cancelled"
    COMPLETED_AFTER_CANCEL = "completed_after_cancel"
    INTERRUPTED = "interrupted"


TERMINAL_JOB_STATUSES = frozenset(
    {
        JobStatus.COMPLETED,
        JobStatus.FAILED,
        JobStatus.CANCELLED,
        JobStatus.COMPLETED_AFTER_CANCEL,
    }
)


class BudgetSpec(StrictModel):
    estimated_credits: int = Field(ge=0, le=1_000_000)
    cap_credits: int = Field(ge=0, le=1_000_000)

    @model_validator(mode="after")
    def estimate_must_fit_cap(self) -> "BudgetSpec":
        if self.estimated_credits > self.cap_credits:
            raise ValueError("estimated_credits cannot exceed cap_credits")
        return self


class DataDisclosure(StrictModel):
    external_send_approved: bool
    kinds: list[Literal["text", "image", "mesh", "task_reference", "url"]] = Field(
        default_factory=list, max_length=8
    )
    source_hashes: list[str] = Field(default_factory=list, max_length=32)

    @field_validator("source_hashes")
    @classmethod
    def hashes_are_sha256(cls, values: list[str]) -> list[str]:
        for value in values:
            if len(value) != 64 or any(char not in "0123456789abcdefABCDEF" for char in value):
                raise ValueError("source_hashes must contain SHA-256 hex values")
        return [value.lower() for value in values]


class JobCreateRequest(StrictModel):
    project_key: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")
    operation: OperationName
    plan_hash: str = Field(pattern=r"^[0-9a-fA-F]{64}$")
    payload: dict[str, Any]
    budget: BudgetSpec
    disclosure: DataDisclosure
    auto_download: bool = True

    @field_validator("plan_hash")
    @classmethod
    def normalize_plan_hash(cls, value: str) -> str:
        return value.lower()

    @model_validator(mode="after")
    def validate_operation_budget_and_minimum_payload(self) -> "JobCreateRequest":
        if self.operation != "rig_check" and self.budget.estimated_credits <= 0:
            raise ValueError("estimated_credits must be positive for billable operations")
        if self.operation == "text_to_model":
            prompt = self.payload.get("prompt")
            if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 1024:
                raise ValueError("text_to_model requires a prompt containing 1-1024 characters")
            if "text" not in self.disclosure.kinds:
                raise ValueError("text_to_model disclosure must include text")
        if self.operation in {"text_to_model", "image_to_model", "multiview_to_model"}:
            model = self.payload.get("model")
            if not isinstance(model, str) or not model.strip():
                raise ValueError(f"{self.operation} requires a model version")
        if self.operation == "image_to_model":
            if not _non_empty_string(self.payload.get("input")):
                raise ValueError("image_to_model requires input")
            if "image" not in self.disclosure.kinds:
                raise ValueError("image_to_model disclosure must include image")
        if self.operation == "multiview_to_model":
            inputs = self.payload.get("inputs")
            if not isinstance(inputs, list) or not inputs:
                raise ValueError("multiview_to_model requires a non-empty inputs array")
            if "image" not in self.disclosure.kinds and "task_reference" not in self.disclosure.kinds:
                raise ValueError("multiview_to_model disclosure must include image or task_reference")
        model_input_operations = {
            "texture", "convert", "segment", "complete", "retopology", "rig_check", "rig", "retarget"
        }
        if self.operation in model_input_operations:
            if not _non_empty_string(self.payload.get("input")):
                raise ValueError(f"{self.operation} requires input")
            if not ({"mesh", "task_reference", "url"} & set(self.disclosure.kinds)):
                raise ValueError(
                    f"{self.operation} disclosure must include mesh, task_reference, or url"
                )
        if self.operation == "convert":
            output_format = self.payload.get("format")
            if not isinstance(output_format, str) or output_format.upper() not in {
                "GLTF", "FBX", "USDZ", "OBJ", "STL", "3MF"
            }:
                raise ValueError("convert requires a supported format")
        if self.operation == "retarget":
            single = _non_empty_string(self.payload.get("animation"))
            multiple = self.payload.get("animations")
            has_multiple = isinstance(multiple, list) and bool(multiple)
            if single == has_multiple:
                raise ValueError("retarget requires exactly one of animation or animations")
        return self


class UploadRequest(StrictModel):
    project_key: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")
    source_path: str = Field(min_length=1, max_length=4096)
    expected_sha256: str = Field(pattern=r"^[0-9a-fA-F]{64}$")
    external_send_approved: bool

    @field_validator("expected_sha256")
    @classmethod
    def normalize_expected_hash(cls, value: str) -> str:
        return value.lower()


class JobError(StrictModel):
    code: str
    message: str
    request_id: str | None = None


class ArtifactRecord(StrictModel):
    logical_name: str
    path: str
    byte_count: int = Field(ge=0)
    sha256: str
    content_type: str | None = None
    source_host: str
    downloaded_utc: str


class JobRecord(StrictModel):
    schema_version: int = 1
    job_id: str
    project_key: str
    operation: OperationName
    plan_hash: str
    request_hash: str
    idempotency_key_hash: str
    status: JobStatus = JobStatus.QUEUED
    progress: float = Field(default=0.0, ge=0.0, le=1.0)
    message: str = "queued"
    provider_task_id: str | None = None
    estimated_credits: int = 0
    budget_cap_credits: int = 0
    credits_consumed: int | None = None
    safe_input: dict[str, Any]
    provider_result: dict[str, Any] | None = None
    artifacts: list[ArtifactRecord] = Field(default_factory=list)
    auto_download: bool = True
    cancel_requested: bool = False
    error: JobError | None = None
    created_utc: str
    updated_utc: str
    completed_utc: str | None = None

    @property
    def terminal(self) -> bool:
        return self.status in TERMINAL_JOB_STATUSES


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _non_empty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())
