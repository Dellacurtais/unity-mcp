from __future__ import annotations

import asyncio
import time
from pathlib import Path
from typing import Any, Awaitable, Callable, Protocol

import httpx

from .config import GatewaySettings
from .secrets import MemoryCredentialStore, TripoCredentialManager


OPERATION_ENDPOINTS: dict[str, str] = {
    "text_to_model": "/generation/text-to-model",
    "image_to_model": "/generation/image-to-model",
    "multiview_to_model": "/generation/multiview-to-model",
    "texture": "/models/texture",
    "convert": "/models/convert",
    "segment": "/mesh/segment",
    "complete": "/mesh/complete",
    "retopology": "/mesh/decimate",
    "rig_check": "/animations/rig-check",
    "rig": "/animations/rig",
    "retarget": "/animations/retarget",
}


class TripoApiError(RuntimeError):
    def __init__(
        self,
        code: str,
        message: str,
        *,
        status_code: int | None = None,
        request_id: str | None = None,
    ) -> None:
        super().__init__(message)
        self.code = code
        self.status_code = status_code
        self.request_id = request_id


class TripoProvider(Protocol):
    provider_id: str
    operations: frozenset[str]

    @property
    def configured(self) -> bool: ...

    async def get_balance(self) -> dict[str, Any]: ...

    async def get_usage(self) -> list[dict[str, Any]]: ...

    async def upload_file(self, path: Path) -> str: ...

    async def create_task(self, operation: str, payload: dict[str, Any]) -> str: ...

    async def get_task(self, task_id: str, operation: str | None = None) -> dict[str, Any]: ...

    async def close(self) -> None: ...


class TripoHttpProvider:
    provider_id = "tripo"
    operations = frozenset(OPERATION_ENDPOINTS)

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
            settings.api_key, MemoryCredentialStore()
        )
        self._owns_client = client is None
        self._client = client or httpx.AsyncClient(
            base_url=settings.api_base_url,
            timeout=httpx.Timeout(settings.request_timeout_seconds),
            follow_redirects=False,
        )
        self._sleep = sleep

    @property
    def configured(self) -> bool:
        return self.credentials.configured

    async def get_balance(self) -> dict[str, Any]:
        data = await self._request_json("GET", "/account/balance")
        if not isinstance(data, dict):
            raise TripoApiError("TRIPO_RESPONSE_INVALID", "balance data must be an object")
        return data

    async def get_usage(self) -> list[dict[str, Any]]:
        data = await self._request_json("GET", "/account/usage")
        if not isinstance(data, list):
            raise TripoApiError("TRIPO_RESPONSE_INVALID", "usage data must be an array")
        return [item for item in data if isinstance(item, dict)]

    async def upload_file(self, path: Path) -> str:
        self._ensure_configured()
        with path.open("rb") as handle:
            data = await self._request_json(
                "POST",
                "/files",
                files={"file": (path.name, handle, "application/octet-stream")},
            )
        token = data.get("file_token") if isinstance(data, dict) else None
        if not isinstance(token, str) or not token:
            raise TripoApiError("TRIPO_RESPONSE_INVALID", "upload returned no file_token")
        return token

    async def create_task(self, operation: str, payload: dict[str, Any]) -> str:
        endpoint = OPERATION_ENDPOINTS.get(operation)
        if endpoint is None:
            raise TripoApiError("TRIPO_OPERATION_NOT_ALLOWED", f"unsupported operation: {operation}")
        data = await self._request_json("POST", endpoint, json=payload)
        task_id = data.get("task_id") if isinstance(data, dict) else None
        if not isinstance(task_id, str) or not task_id.startswith("task_"):
            raise TripoApiError("TRIPO_RESPONSE_INVALID", "task creation returned no task_id")
        return task_id

    async def get_task(self, task_id: str, operation: str | None = None) -> dict[str, Any]:
        if not task_id.startswith("task_") or len(task_id) > 256:
            raise TripoApiError("TRIPO_TASK_ID_INVALID", "invalid Tripo task id")
        data = await self._request_json("GET", f"/tasks/{task_id}")
        if not isinstance(data, dict):
            raise TripoApiError("TRIPO_RESPONSE_INVALID", "task data must be an object")
        return data

    async def close(self) -> None:
        if self._owns_client:
            await self._client.aclose()

    async def _request_json(
        self,
        method: str,
        path: str,
        *,
        json: dict[str, Any] | None = None,
        files: dict[str, Any] | None = None,
    ) -> Any:
        self._ensure_configured()
        api_key = self.credentials.api_key
        if not api_key:
            raise TripoApiError("TRIPO_NOT_CONFIGURED", "Tripo API key is not configured")
        headers = {"Authorization": f"Bearer {api_key}"}
        if json is not None:
            headers["Content-Type"] = "application/json"
        response: httpx.Response | None = None
        for attempt in range(self.settings.max_retries + 1):
            if attempt and files:
                for file_value in files.values():
                    if isinstance(file_value, tuple) and len(file_value) >= 2:
                        handle = file_value[1]
                        if hasattr(handle, "seek"):
                            handle.seek(0)
            try:
                response = await self._client.request(
                    method, path, headers=headers, json=json, files=files
                )
            except httpx.TimeoutException as exception:
                raise TripoApiError("TRIPO_TIMEOUT", "Tripo request timed out") from exception
            except httpx.HTTPError as exception:
                raise TripoApiError("TRIPO_NETWORK_ERROR", "Tripo network request failed") from exception

            if response.status_code != 429 or attempt >= self.settings.max_retries:
                break
            await self._sleep(self._retry_delay(response, attempt))

        assert response is not None
        payload = self._safe_response_json(response)
        if response.status_code >= 400:
            code = str(payload.get("code") or f"HTTP_{response.status_code}")
            message = self._redact_message(
                str(payload.get("message") or "Tripo request failed"), api_key
            )
            raise TripoApiError(
                code,
                message,
                status_code=response.status_code,
                request_id=_optional_string(payload.get("request_id")),
            )
        code = payload.get("code")
        if code not in (0, "0", None):
            raise TripoApiError(
                str(code),
                self._redact_message(
                    str(payload.get("message") or "Tripo returned an error"), api_key
                ),
                status_code=response.status_code,
                request_id=_optional_string(payload.get("request_id")),
            )
        if "data" not in payload:
            raise TripoApiError("TRIPO_RESPONSE_INVALID", "Tripo response has no data field")
        return payload["data"]

    def _redact_message(self, message: str, api_key: str | None = None) -> str:
        if api_key:
            message = message.replace(api_key, "[REDACTED]")
        return message.replace("\r", " ").replace("\n", " ")[:500]

    def _ensure_configured(self) -> None:
        if not self.credentials.configured:
            raise TripoApiError("TRIPO_NOT_CONFIGURED", "Tripo API key is not configured")

    @staticmethod
    def _safe_response_json(response: httpx.Response) -> dict[str, Any]:
        try:
            value = response.json()
        except ValueError as exception:
            raise TripoApiError(
                "TRIPO_RESPONSE_INVALID",
                "Tripo returned a non-JSON response",
                status_code=response.status_code,
            ) from exception
        if not isinstance(value, dict):
            raise TripoApiError(
                "TRIPO_RESPONSE_INVALID",
                "Tripo response root must be an object",
                status_code=response.status_code,
            )
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


def _optional_string(value: Any) -> str | None:
    return value if isinstance(value, str) and value else None
