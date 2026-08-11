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
    "animate",
    "resize",
    "uv_unwrap",
]
ProviderName = Literal["tripo", "meshy"]


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
    provider: ProviderName = "tripo"
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
            if not isinstance(inputs, list) or not 1 <= len(inputs) <= 4:
                raise ValueError("multiview_to_model requires 1-4 inputs")
            if "image" not in self.disclosure.kinds and "task_reference" not in self.disclosure.kinds:
                raise ValueError("multiview_to_model disclosure must include image or task_reference")
        model_input_operations = {
            "texture", "convert", "segment", "complete", "retopology", "rig_check", "rig",
            "retarget", "animate", "resize", "uv_unwrap"
        }
        if self.operation in model_input_operations:
            if not _non_empty_string(self.payload.get("input")):
                raise ValueError(f"{self.operation} requires input")
            if not ({"mesh", "task_reference", "url"} & set(self.disclosure.kinds)):
                raise ValueError(
                    f"{self.operation} disclosure must include mesh, task_reference, or url"
                )
        if "mesh" in self.disclosure.kinds and not self.disclosure.source_hashes:
            raise ValueError("mesh disclosure requires at least one reviewed source SHA-256")
        if self.operation == "convert":
            output_format = self.payload.get("format")
            if not isinstance(output_format, str) or output_format.upper() not in {
                "GLTF", "FBX", "USDZ", "OBJ", "STL", "3MF"
            }:
                raise ValueError("convert requires a supported format")
        if self.provider == "meshy":
            supported = {
                "text_to_model", "image_to_model", "multiview_to_model", "texture", "convert",
                "retopology", "rig", "animate", "resize", "uv_unwrap"
            }
            if self.operation not in supported:
                raise ValueError(f"Meshy does not support operation {self.operation}")
            if self.operation == "text_to_model" and len(str(self.payload["prompt"])) > 600:
                raise ValueError("Meshy text_to_model prompt cannot exceed 600 characters")
            if self.operation == "retopology":
                face_limit = self.payload.get("face_limit", 30_000)
                if not isinstance(face_limit, int) or not 100 <= face_limit <= 300_000:
                    raise ValueError("Meshy retopology face_limit must be between 100 and 300000")
                if not isinstance(self.payload.get("quad", False), bool):
                    raise ValueError("Meshy retopology quad must be boolean")
            if self.operation == "rig":
                height = self.payload.get("height_meters", 1.7)
                if not isinstance(height, (int, float)) or height <= 0:
                    raise ValueError("Meshy rig height_meters must be positive")
            if self.operation == "animate":
                action_id = self.payload.get("action_id")
                if not isinstance(action_id, int) or action_id <= 0:
                    raise ValueError("Meshy animate requires a positive action_id")
                fps = self.payload.get("fps", 30)
                if fps not in {24, 25, 30, 60}:
                    raise ValueError("Meshy animate fps must be 24, 25, 30 or 60")
            if self.operation == "resize":
                height = self.payload.get("resize_height", self.payload.get("height_meters", 1.0))
                if not isinstance(height, (int, float)) or height <= 0:
                    raise ValueError("Meshy resize height must be positive")
                if self.payload.get("origin_at", "bottom") not in {"bottom", "center"}:
                    raise ValueError("Meshy resize origin_at must be bottom or center")
            return self
        if self.operation == "retopology":
            model = self.payload.get("model", "v2.0")
            if model not in {"v1.0", "v2.0"}:
                raise ValueError("retopology model must be v1.0 or v2.0")
            quad = self.payload.get("quad", False)
            if not isinstance(quad, bool):
                raise ValueError("retopology quad must be boolean")
            face_limit = self.payload.get("face_limit")
            if model == "v1.0" and not isinstance(face_limit, int):
                raise ValueError("retopology v1.0 requires face_limit")
            if face_limit is not None:
                maximum = 10_000 if model == "v2.0" and quad else 20_000 if model == "v2.0" else 150_000 if quad else 2_000_000
                if not isinstance(face_limit, int) or not 500 <= face_limit <= maximum:
                    raise ValueError(f"retopology face_limit must be between 500 and {maximum}")
            if model == "v1.0" and ({"bake", "part_names"} & set(self.payload)):
                raise ValueError("retopology v1.0 does not support bake or part_names")
            _string_list(self.payload.get("part_names"), "retopology part_names", 64, optional=True)
        if self.operation == "segment":
            model = self.payload.get("model", "v1.0-20250506")
            if model not in {"v1.0-20250506", "v2.0-20260430"}:
                raise ValueError("segment model is unsupported")
            if model == "v1.0-20250506" and ({"segmentation_granularity", "split_by_connectivity", "ref_image"} & set(self.payload)):
                raise ValueError("segment v1 does not support semantic v2 parameters")
            if model == "v2.0-20260430":
                granularity = self.payload.get("segmentation_granularity", "balanced")
                if granularity not in {"simple", "balanced", "detailed"}:
                    raise ValueError("segment granularity must be simple, balanced or detailed")
                if not isinstance(self.payload.get("split_by_connectivity", True), bool):
                    raise ValueError("split_by_connectivity must be boolean")
        if self.operation == "complete":
            if self.payload.get("completion_mode", "ai_completion") not in {"ai_completion", "quick_cap"}:
                raise ValueError("completion_mode must be ai_completion or quick_cap")
            _string_list(self.payload.get("part_names"), "complete part_names", 64, optional=True)
        if self.operation == "rig_check" and set(self.payload) != {"input"}:
            raise ValueError("rig_check payload accepts only input")
        if self.operation == "rig":
            if self.payload.get("rig_type", "biped") not in {
                "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic"
            }:
                raise ValueError("rig_type is unsupported")
            if self.payload.get("spec", "tripo") not in {"tripo", "mixamo"}:
                raise ValueError("rig spec must be tripo or mixamo")
            if self.payload.get("out_format", "glb") not in {"glb", "fbx"}:
                raise ValueError("rig out_format must be glb or fbx")
        if self.operation == "retarget":
            single = _non_empty_string(self.payload.get("animation"))
            multiple = self.payload.get("animations")
            has_multiple = isinstance(multiple, list) and bool(multiple)
            if single == has_multiple:
                raise ValueError("retarget requires exactly one of animation or animations")
            if single and not str(self.payload["animation"]).startswith("preset:"):
                raise ValueError("retarget animation must be a typed preset identifier")
            if has_multiple:
                _string_list(multiple, "retarget animations", 5)
                if any(not value.startswith("preset:") for value in multiple):
                    raise ValueError("retarget animations must be typed preset identifiers")
            if self.payload.get("out_format", "glb") not in {"glb", "fbx"}:
                raise ValueError("retarget out_format must be glb or fbx")
            for key in ("bake_animation", "export_with_geometry", "animate_in_place"):
                if key in self.payload and not isinstance(self.payload[key], bool):
                    raise ValueError(f"retarget {key} must be boolean")
        return self


class UploadRequest(StrictModel):
    project_key: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")
    provider: ProviderName = "tripo"
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
    provider: ProviderName = "tripo"
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


def _string_list(
    value: Any, label: str, maximum: int, *, optional: bool = False
) -> list[str]:
    if value is None and optional:
        return []
    if not isinstance(value, list) or not value or len(value) > maximum:
        raise ValueError(f"{label} must contain 1-{maximum} values")
    if any(not isinstance(item, str) or not item.strip() or len(item) > 128 for item in value):
        raise ValueError(f"{label} contains an invalid value")
    return value
