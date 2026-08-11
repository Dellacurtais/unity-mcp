from __future__ import annotations

import asyncio
import base64
import hashlib
import os
import re
import shutil
import time
import uuid
from pathlib import Path
from typing import Any, Awaitable, Callable

import httpx

from .config import GatewaySettings
from .provider import TripoApiError
from .secrets import MemoryCredentialStore, TripoCredentialManager


MESHY_OPERATION_ENDPOINTS: dict[str, str] = {
    "text_to_model": "/openapi/v2/text-to-3d",
    "image_to_model": "/openapi/v1/image-to-3d",
    "multiview_to_model": "/openapi/v1/multi-image-to-3d",
    "texture": "/openapi/v2/text-to-3d",
    "convert": "/openapi/v1/convert",
    "retopology": "/openapi/v1/remesh",
    "resize": "/openapi/v1/resize",
    "uv_unwrap": "/openapi/v1/uv-unwrap",
    "rig": "/openapi/v1/rigging",
    "animate": "/openapi/v1/animations",
}
_HANDLE = re.compile(r"^meshy-local:([0-9a-f]{64}):(\.[a-z0-9]{1,8})$")
_IMAGE_MIME = {".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png"}


class MeshyHttpProvider:
    provider_id = "meshy"
    operations = frozenset(MESHY_OPERATION_ENDPOINTS)

    def __init__(
        self,
        settings: GatewaySettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
        credentials: TripoCredentialManager | None = None,
    ) -> None:
        self.settings = settings
        self.credentials = credentials or TripoCredentialManager(
            settings.meshy_api_key, MemoryCredentialStore()
        )
        self._owns_client = client is None
        self._client = client or httpx.AsyncClient(
            base_url=settings.meshy_api_base_url,
            timeout=httpx.Timeout(settings.request_timeout_seconds),
            follow_redirects=False,
        )
        self._sleep = sleep
        self._uploads = settings.data_dir / "meshy_uploads"
        self._uploads.mkdir(parents=True, exist_ok=True)

    @property
    def configured(self) -> bool:
        return self.credentials.configured

    async def get_balance(self) -> dict[str, Any]:
        value = await self._request_json("GET", "/openapi/v1/balance")
        if not isinstance(value, dict) or not isinstance(value.get("balance"), (int, float)):
            raise TripoApiError("MESHY_RESPONSE_INVALID", "Meshy balance response is invalid")
        return {"balance": int(value["balance"]), "frozen": 0}

    async def get_usage(self) -> list[dict[str, Any]]:
        # Meshy currently exposes balance and per-task consumed_credits, but no
        # account-wide usage endpoint. Durable local telemetry remains authoritative.
        return []

    async def upload_file(self, path: Path) -> str:
        source = Path(path).resolve(strict=True)
        digest = hashlib.sha256()
        with source.open("rb") as handle:
            while chunk := handle.read(1024 * 1024):
                digest.update(chunk)
        suffix = source.suffix.lower()
        destination = self._uploads / f"{digest.hexdigest()}{suffix}"
        if not destination.exists():
            temporary = destination.with_name(f".{destination.name}.{uuid.uuid4().hex}.tmp")
            try:
                shutil.copyfile(source, temporary)
                os.replace(temporary, destination)
            finally:
                temporary.unlink(missing_ok=True)
        return f"meshy-local:{digest.hexdigest()}:{suffix}"

    async def create_task(self, operation: str, payload: dict[str, Any]) -> str:
        endpoint = MESHY_OPERATION_ENDPOINTS.get(operation)
        if endpoint is None:
            raise TripoApiError(
                "MESHY_OPERATION_NOT_ALLOWED", f"unsupported Meshy operation: {operation}"
            )
        request = self._translate_payload(operation, payload)
        value = await self._request_json("POST", endpoint, json=request)
        task_id = value.get("result") if isinstance(value, dict) else None
        if not isinstance(task_id, str) or not task_id or len(task_id) > 256:
            raise TripoApiError(
                "MESHY_RESPONSE_INVALID", "Meshy task creation returned no task id"
            )
        return task_id

    async def get_task(self, task_id: str, operation: str | None = None) -> dict[str, Any]:
        if not operation or operation not in MESHY_OPERATION_ENDPOINTS:
            raise TripoApiError(
                "MESHY_OPERATION_REQUIRED", "Meshy task polling requires its operation"
            )
        if not task_id or len(task_id) > 256 or any(char in task_id for char in "/?#"):
            raise TripoApiError("MESHY_TASK_ID_INVALID", "invalid Meshy task id")
        value = await self._request_json(
            "GET", f"{MESHY_OPERATION_ENDPOINTS[operation]}/{task_id}"
        )
        if not isinstance(value, dict):
            raise TripoApiError("MESHY_RESPONSE_INVALID", "Meshy task response is invalid")
        return self._normalize_task(value)

    async def close(self) -> None:
        if self._owns_client:
            await self._client.aclose()

    def _translate_payload(self, operation: str, payload: dict[str, Any]) -> dict[str, Any]:
        source = payload.get("input")
        if operation == "text_to_model":
            smart = bool(payload.get("smart_low_poly"))
            request: dict[str, Any] = {
                "mode": "preview",
                "prompt": str(payload.get("prompt") or ""),
                "model_type": "lowpoly" if smart else "standard",
                "ai_model": self._generation_model(payload.get("model")),
                "pose_mode": str(payload.get("pose_mode") or ""),
                "target_formats": ["fbx", "glb"],
            }
            if not smart:
                request["should_remesh"] = False
            return request
        if operation == "image_to_model":
            smart = bool(payload.get("smart_low_poly"))
            request = {
                "image_url": self._data_input(str(source or ""), image=True),
                "model_type": "smart-topology" if smart else "standard",
                "ai_model": "meshy-t2" if smart else self._generation_model(payload.get("model")),
                "should_texture": bool(payload.get("texture", True)),
                "enable_pbr": bool(payload.get("pbr", True)),
                "target_formats": ["fbx", "glb"],
            }
            if smart:
                request["target_polycount"] = max(
                    100, min(int(payload.get("face_limit") or 4_000), 15_000)
                )
            return request
        if operation == "multiview_to_model":
            inputs = payload.get("inputs") if isinstance(payload.get("inputs"), list) else []
            return {
                "image_urls": [self._data_input(str(item), image=True) for item in inputs],
                "ai_model": self._generation_model(payload.get("model")),
                "should_texture": bool(payload.get("texture", True)),
                "enable_pbr": bool(payload.get("pbr", True)),
                "target_formats": ["fbx", "glb"],
            }
        if operation == "texture":
            return {
                "mode": "refine",
                "preview_task_id": str(source or ""),
                "enable_pbr": bool(payload.get("pbr", True)),
                "texture_resolution": str(payload.get("texture_resolution") or "2k"),
                "target_formats": ["fbx", "glb"],
            }
        if operation == "convert":
            request = self._model_input(str(source or ""))
            request["target_formats"] = [str(payload.get("format") or "FBX").lower()]
            return request
        if operation == "retopology":
            request = self._model_input(str(source or ""))
            request.update(
                {
                    "target_formats": ["fbx", "glb"],
                    "topology": "quad" if bool(payload.get("quad")) else "triangle",
                    "target_polycount": max(
                        100, min(int(payload.get("face_limit") or 30_000), 300_000)
                    ),
                }
            )
            return request
        if operation == "rig":
            request = self._model_input(str(source or ""))
            request["height_meters"] = float(payload.get("height_meters") or 1.7)
            return request
        if operation == "animate":
            return {
                "rig_task_id": str(source or ""),
                "action_id": int(payload.get("action_id")),
                **(
                    {
                        "post_process": {
                            "operation_type": "change_fps",
                            "fps": int(payload["fps"]),
                        }
                    }
                    if payload.get("fps") in {24, 25, 30, 60}
                    else {}
                ),
            }
        request = self._model_input(str(source or ""))
        if operation == "resize":
            request["resize_height"] = float(
                payload.get("resize_height")
                or payload.get("height_meters")
                or 1.0
            )
            request["origin_at"] = str(payload.get("origin_at") or "bottom")
        return request

    def _model_input(self, value: str) -> dict[str, str]:
        if value.startswith(("https://", "data:")) or _HANDLE.match(value):
            return {"model_url": self._data_input(value, image=False)}
        return {"input_task_id": value}

    def _data_input(self, value: str, *, image: bool) -> str:
        match = _HANDLE.match(value)
        if not match:
            if value.startswith(("https://", "data:")):
                return value
            raise TripoApiError("MESHY_INPUT_INVALID", "Meshy input is not an approved upload")
        digest, suffix = match.groups()
        path = self._uploads / f"{digest}{suffix}"
        try:
            content = path.read_bytes()
        except OSError as exception:
            raise TripoApiError("MESHY_UPLOAD_MISSING", "staged Meshy upload is missing") from exception
        if hashlib.sha256(content).hexdigest() != digest:
            raise TripoApiError("MESHY_UPLOAD_HASH_MISMATCH", "staged Meshy upload changed")
        mime = _IMAGE_MIME.get(suffix) if image else "application/octet-stream"
        if not mime:
            raise TripoApiError("MESHY_IMAGE_FORMAT_INVALID", "Meshy image must be PNG or JPEG")
        return f"data:{mime};base64,{base64.b64encode(content).decode('ascii')}"

    @staticmethod
    def _generation_model(value: Any) -> str:
        model = str(value or "latest").lower()
        return model if model in {"meshy-5", "meshy-6", "latest"} else "latest"

    @staticmethod
    def _normalize_task(value: dict[str, Any]) -> dict[str, Any]:
        raw_status = str(value.get("status") or "").upper()
        status = {
            "SUCCEEDED": "success",
            "FAILED": "failed",
            "CANCELED": "cancelled",
            "CANCELLED": "cancelled",
            "PENDING": "pending",
            "IN_PROGRESS": "running",
        }.get(raw_status, raw_status.lower())
        output: dict[str, Any] = {}
        if isinstance(value.get("result"), dict):
            output.update(value["result"])
        for key in (
            "model_urls", "thumbnail_url", "alpha_thumbnail_url", "texture_urls",
            "animation_glb_url", "animation_fbx_url",
        ):
            if value.get(key):
                output[key] = value[key]
        error = value.get("task_error") if isinstance(value.get("task_error"), dict) else {}
        return {
            "task_id": str(value.get("id") or ""),
            "type": str(value.get("type") or ""),
            "status": status,
            "progress": int(value.get("progress") or (100 if status == "success" else 0)),
            "output": output,
            "credits_consumed": value.get("consumed_credits"),
            "error_code": error.get("code"),
            "error_message": error.get("message"),
        }

    async def _request_json(
        self, method: str, path: str, *, json: dict[str, Any] | None = None
    ) -> Any:
        if not self.credentials.configured:
            raise TripoApiError("MESHY_NOT_CONFIGURED", "Meshy API key is not configured")
        api_key = self.credentials.api_key
        headers = {"Authorization": f"Bearer {api_key}"}
        if json is not None:
            headers["Content-Type"] = "application/json"
        response: httpx.Response | None = None
        for attempt in range(self.settings.max_retries + 1):
            try:
                response = await self._client.request(method, path, headers=headers, json=json)
            except httpx.TimeoutException as exception:
                raise TripoApiError("MESHY_TIMEOUT", "Meshy request timed out") from exception
            except httpx.HTTPError as exception:
                raise TripoApiError(
                    "MESHY_NETWORK_ERROR", "Meshy network request failed"
                ) from exception
            if response.status_code != 429 or attempt >= self.settings.max_retries:
                break
            await self._sleep(self._retry_delay(response, attempt))
        assert response is not None
        try:
            value = response.json()
        except ValueError as exception:
            raise TripoApiError(
                "MESHY_RESPONSE_INVALID", "Meshy returned a non-JSON response",
                status_code=response.status_code,
            ) from exception
        if not isinstance(value, dict):
            raise TripoApiError(
                "MESHY_RESPONSE_INVALID", "Meshy response root must be an object",
                status_code=response.status_code,
            )
        if response.status_code >= 400:
            error = value.get("task_error") if isinstance(value.get("task_error"), dict) else value
            code = str(error.get("code") or value.get("code") or f"HTTP_{response.status_code}")
            message = str(error.get("message") or value.get("message") or "Meshy request failed")
            if api_key:
                message = message.replace(api_key, "[REDACTED]")
            raise TripoApiError(code, message[:500], status_code=response.status_code)
        return value

    @staticmethod
    def _retry_delay(response: httpx.Response, attempt: int) -> float:
        retry_after = response.headers.get("Retry-After")
        if retry_after:
            try:
                return max(0.0, min(float(retry_after), 60.0))
            except ValueError:
                pass
        reset = response.headers.get("X-RateLimit-Reset")
        if reset:
            try:
                return max(0.0, min(float(reset) - time.time(), 60.0))
            except ValueError:
                pass
        return float(min(2**attempt, 32))
