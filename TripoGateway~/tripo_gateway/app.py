from __future__ import annotations

from contextlib import asynccontextmanager
import json
import os
from pathlib import Path
import uuid
from typing import Any, AsyncIterator

from fastapi import Depends, FastAPI, Header, HTTPException, Query, Request
from fastapi.responses import JSONResponse
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from .artifacts import ArtifactVault
from .config import GatewaySettings
from .jobs import GatewayPolicyError, TripoJobManager
from .models import JobCreateRequest, UploadRequest
from .provider import OPERATION_ENDPOINTS, TripoApiError, TripoHttpProvider, TripoProvider
from .secrets import (
    CredentialStoreError,
    MemoryCredentialStore,
    TripoCredentialManager,
    create_platform_credential_store,
)
from .security import constant_time_equal, resolve_allowed_file, sha256_file, sha256_text
from .store import DurableJobStore, IdempotencyConflictError
from .version import GATEWAY_VERSION
from .webhooks import (
    WebhookDeliveryStore,
    WebhookVerificationError,
    parse_event,
    verify_tripo_signature,
)


IMAGE_EXTENSIONS = frozenset({".jpg", ".jpeg", ".png"})
MODEL_EXTENSIONS = frozenset({".glb", ".gltf", ".fbx", ".obj", ".stl"})
IMAGE_UPLOAD_LIMIT = 20 * 1024 * 1024
MODEL_UPLOAD_LIMIT = 150 * 1024 * 1024


class RequestBodyLimitMiddleware:
    def __init__(self, app: ASGIApp, max_bytes: int) -> None:
        self.app = app
        self.max_bytes = max_bytes

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        headers = {key.lower(): value for key, value in scope.get("headers", [])}
        declared = headers.get(b"content-length")
        if declared is not None:
            try:
                if int(declared) > self.max_bytes:
                    await self._reject(scope, receive, send)
                    return
            except ValueError:
                await self._reject(scope, receive, send, 400, "Content-Length must be an integer")
                return
        received = 0
        buffered: list[Message] = []
        while True:
            message = await receive()
            if message["type"] == "http.request":
                received += len(message.get("body", b""))
                if received > self.max_bytes:
                    await self._reject(scope, receive, send)
                    return
                buffered.append(message)
                if not message.get("more_body", False):
                    break
            elif message["type"] == "http.disconnect":
                buffered.append(message)
                break

        async def replay_receive() -> Message:
            if buffered:
                return buffered.pop(0)
            return await receive()

        await self.app(scope, replay_receive, send)

    @staticmethod
    async def _reject(
        scope: Scope,
        receive: Receive,
        send: Send,
        status_code: int = 413,
        detail: str = "request body exceeds the configured limit",
    ) -> None:
        response = JSONResponse(status_code=status_code, content={"detail": detail})
        await response(scope, receive, send)


def create_app(
    settings: GatewaySettings | None = None,
    *,
    provider: TripoProvider | None = None,
    artifact_vault: ArtifactVault | None = None,
    credential_manager: TripoCredentialManager | None = None,
) -> FastAPI:
    config = settings or GatewaySettings.from_environment()
    store = DurableJobStore(config.data_dir)
    credentials = credential_manager or TripoCredentialManager(
        config.api_key,
        create_platform_credential_store(config.api_base_url)
        if provider is None
        else MemoryCredentialStore(),
    )
    provider_instance = provider or TripoHttpProvider(config, credentials=credentials)
    vault = artifact_vault or ArtifactVault(config.data_dir, config.max_download_bytes)
    manager = TripoJobManager(config, store, provider_instance, vault)
    webhook_deliveries = WebhookDeliveryStore(config.data_dir)

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        await manager.start()
        try:
            yield
        finally:
            await manager.stop()
            await provider_instance.close()
            close_vault = getattr(vault, "close", None)
            if close_vault is not None:
                await close_vault()

    app = FastAPI(title="unity-tripo-gateway", version=GATEWAY_VERSION, lifespan=lifespan)
    app.add_middleware(RequestBodyLimitMiddleware, max_bytes=config.max_request_bytes)
    app.state.settings = config
    app.state.store = store
    app.state.provider = provider_instance
    app.state.credentials = credentials
    app.state.artifact_vault = vault
    app.state.job_manager = manager
    app.state.webhook_deliveries = webhook_deliveries

    async def require_auth(authorization: str | None = Header(default=None)) -> None:
        if not authorization or not authorization.lower().startswith("bearer "):
            raise HTTPException(status_code=401, detail="Bearer token is required")
        supplied = authorization[7:].strip()
        if not constant_time_equal(supplied, config.gateway_token):
            raise HTTPException(status_code=401, detail="Bearer token is invalid")

    protected = Depends(require_auth)

    @app.exception_handler(TripoApiError)
    async def tripo_error(_: Request, exception: TripoApiError) -> JSONResponse:
        status = exception.status_code if exception.status_code and 400 <= exception.status_code < 600 else 502
        return _error_response(status, exception.code, str(exception), exception.request_id)

    @app.exception_handler(GatewayPolicyError)
    async def policy_error(_: Request, exception: GatewayPolicyError) -> JSONResponse:
        return _error_response(exception.status_code, exception.code, str(exception))

    @app.exception_handler(IdempotencyConflictError)
    async def idempotency_error(_: Request, exception: IdempotencyConflictError) -> JSONResponse:
        return _error_response(409, "IDEMPOTENCY_CONFLICT", str(exception))

    @app.exception_handler(CredentialStoreError)
    async def credential_error(_: Request, exception: CredentialStoreError) -> JSONResponse:
        status = 503 if exception.code == "CREDENTIAL_STORE_UNAVAILABLE" else 400
        return _error_response(status, exception.code, str(exception))

    def public_summary(include_credential_metadata: bool = False) -> dict[str, object]:
        result = config.public_summary()
        status = credentials.status()
        result["configured"] = bool(provider_instance.configured)
        if include_credential_metadata:
            result["credentialSource"] = status.source
            result["credentialStoreAvailable"] = status.store_available
            result["credentialPersistent"] = status.persistent
        return result

    @app.get("/health")
    async def health() -> dict[str, Any]:
        return {
            "service": "unity-tripo-gateway",
            "version": GATEWAY_VERSION,
            "schemaVersion": 1,
            "ready": True,
            **public_summary(),
        }

    @app.get("/v1/capabilities", dependencies=[protected])
    async def capabilities() -> dict[str, Any]:
        return {
            "contractVersion": "1.2-draft",
            "operations": sorted(OPERATION_ENDPOINTS),
            "remoteCancellation": False,
            "durableJobs": True,
            "artifactVault": True,
            "artifactVaultRoot": str(
                Path(getattr(vault, "root", config.data_dir / "artifacts")).resolve()
            ),
            "automaticUnityImport": False,
            "uploadExtensions": sorted(IMAGE_EXTENSIONS | MODEL_EXTENSIONS),
            **public_summary(True),
        }

    @app.get("/v1/credentials/tripo", dependencies=[protected])
    async def get_tripo_credential() -> dict[str, object]:
        return credentials.status().public_dict()

    @app.put("/v1/credentials/tripo", dependencies=[protected])
    async def replace_tripo_credential(request: Request) -> dict[str, object]:
        try:
            body = await request.json()
        except (UnicodeDecodeError, ValueError) as exception:
            raise GatewayPolicyError(
                "CREDENTIAL_REQUEST_INVALID", "credential request must be valid JSON"
            ) from exception
        if not isinstance(body, dict) or set(body) != {"apiKey"}:
            raise GatewayPolicyError(
                "CREDENTIAL_REQUEST_INVALID", "credential request must contain only apiKey"
            )
        value = body.get("apiKey")
        if not isinstance(value, str):
            raise CredentialStoreError(
                "TRIPO_API_KEY_INVALID", "Tripo API key must be a string."
            )
        return credentials.replace(value).public_dict()

    @app.delete("/v1/credentials/tripo", dependencies=[protected])
    async def delete_tripo_credential() -> dict[str, object]:
        return credentials.clear().public_dict()

    @app.get("/v1/account/balance", dependencies=[protected])
    async def balance() -> dict[str, Any]:
        return await provider_instance.get_balance()

    @app.get("/v1/account/usage", dependencies=[protected])
    async def usage(
        limit: int = Query(default=100, ge=1, le=500),
    ) -> list[dict[str, Any]]:
        return (await provider_instance.get_usage())[:limit]

    @app.get("/v1/telemetry/summary", dependencies=[protected])
    async def telemetry_summary(
        project_key: str | None = Query(default=None, min_length=1, max_length=128),
    ) -> dict[str, Any]:
        return manager.telemetry_summary(project_key)

    @app.post("/v1/webhooks/tripo")
    async def tripo_webhook(
        request: Request,
        signature: str | None = Header(default=None, alias="Tripo-Webhook-Signature"),
        delivery_id: str | None = Header(default=None, alias="Tripo-Webhook-Delivery"),
        event_header: str | None = Header(default=None, alias="Tripo-Webhook-Event"),
    ) -> dict[str, Any]:
        if not config.webhook_secret:
            raise HTTPException(status_code=503, detail="Tripo webhook is not configured")
        raw_body = await request.body()
        try:
            verify_tripo_signature(
                raw_body,
                signature or "",
                config.webhook_secret,
                tolerance_seconds=config.webhook_tolerance_seconds,
            )
            event = parse_event(raw_body)
            if event_header != event["type"]:
                raise WebhookVerificationError("webhook event header does not match body")
            if not delivery_id:
                raise WebhookVerificationError("webhook delivery header is required")
            created = webhook_deliveries.record(
                delivery_id, event["type"], event["id"], event["created_at"]
            )
            if not created:
                return {"accepted": True, "duplicate": True, "matchedJobId": None}
            try:
                matched = None
                if event["type"].startswith("task."):
                    matched = await manager.reconcile_webhook(event["data"])
                return {"accepted": True, "duplicate": False, "matchedJobId": matched}
            except Exception:
                webhook_deliveries.forget(delivery_id)
                raise
        except WebhookVerificationError as exception:
            raise HTTPException(status_code=400, detail=str(exception)) from exception

    @app.post("/v1/uploads", dependencies=[protected], status_code=201)
    async def upload(request: UploadRequest) -> dict[str, Any]:
        if not request.external_send_approved:
            raise GatewayPolicyError(
                "EXTERNAL_SEND_APPROVAL_REQUIRED",
                "external_send_approved must be true before uploading a local file",
            )
        if not config.upload_roots:
            raise GatewayPolicyError(
                "UPLOAD_ROOTS_NOT_CONFIGURED", "no upload roots are configured"
            )
        try:
            source = resolve_allowed_file(request.source_path, config.upload_roots)
        except (OSError, ValueError) as exception:
            raise GatewayPolicyError("UPLOAD_SOURCE_REJECTED", str(exception)) from exception
        _validate_upload_file(source)
        actual_hash = sha256_file(source)
        if actual_hash != request.expected_sha256:
            raise GatewayPolicyError(
                "UPLOAD_HASH_MISMATCH", "source SHA-256 differs from expected_sha256"
            )
        token = await provider_instance.upload_file(source)
        result = {
            "projectKey": request.project_key,
            "fileToken": token,
            "sourceName": source.name,
            "byteCount": source.stat().st_size,
            "sha256": actual_hash,
        }
        _write_upload_audit(config.data_dir, result)
        return result

    @app.post("/v1/jobs", dependencies=[protected], status_code=202)
    async def create_job(
        request: JobCreateRequest,
        idempotency_key: str | None = Header(default=None, alias="Idempotency-Key"),
    ) -> JSONResponse:
        record, created = await manager.submit(request, idempotency_key or "")
        return JSONResponse(
            status_code=202 if created else 200,
            content={"created": created, "job": record.model_dump(mode="json")},
        )

    @app.get("/v1/jobs", dependencies=[protected])
    async def list_jobs(
        limit: int = Query(default=100, ge=1, le=500),
    ) -> dict[str, Any]:
        records = manager.list(limit)
        return {"jobs": [_job_summary(record) for record in records]}

    @app.get("/v1/jobs/{job_id}", dependencies=[protected])
    async def get_job(job_id: str) -> dict[str, Any]:
        record = _get_job(manager, job_id)
        return record.model_dump(mode="json")

    @app.post("/v1/jobs/{job_id}/cancel", dependencies=[protected], status_code=202)
    async def cancel_job(job_id: str) -> dict[str, Any]:
        _get_job(manager, job_id)
        return manager.cancel(job_id).model_dump(mode="json")

    return app


def _validate_upload_file(path: Path) -> None:
    extension = path.suffix.lower()
    size = path.stat().st_size
    if extension in IMAGE_EXTENSIONS:
        limit = IMAGE_UPLOAD_LIMIT
    elif extension in MODEL_EXTENSIONS:
        limit = MODEL_UPLOAD_LIMIT
    else:
        raise GatewayPolicyError("UPLOAD_EXTENSION_REJECTED", f"unsupported upload extension: {extension}")
    if size <= 0:
        raise GatewayPolicyError("UPLOAD_EMPTY", "empty files cannot be uploaded")
    if size > limit:
        raise GatewayPolicyError("UPLOAD_TOO_LARGE", f"upload exceeds the {limit}-byte type limit")


def _get_job(manager: TripoJobManager, job_id: str):
    try:
        record = manager.get(job_id)
    except ValueError as exception:
        raise GatewayPolicyError("JOB_ID_INVALID", "job id is invalid", 400) from exception
    if record is None:
        raise GatewayPolicyError("JOB_NOT_FOUND", "job not found", 404)
    return record


def _job_summary(record) -> dict[str, Any]:
    return {
        "job_id": record.job_id,
        "project_key": record.project_key,
        "operation": record.operation,
        "status": record.status.value,
        "progress": record.progress,
        "message": record.message,
        "provider_task_id": record.provider_task_id,
        "estimated_credits": record.estimated_credits,
        "credits_consumed": record.credits_consumed,
        "artifact_count": len(record.artifacts),
        "cancel_requested": record.cancel_requested,
        "created_utc": record.created_utc,
        "updated_utc": record.updated_utc,
        "completed_utc": record.completed_utc,
        "error": record.error.model_dump(mode="json") if record.error else None,
    }


def _error_response(
    status_code: int, code: str, message: str, request_id: str | None = None
) -> JSONResponse:
    content: dict[str, Any] = {"error": {"code": code, "message": message}}
    if request_id:
        content["error"]["requestId"] = request_id
    return JSONResponse(status_code=status_code, content=content)


def _write_upload_audit(data_dir: Path, result: dict[str, Any]) -> None:
    audit_dir = data_dir / "uploads"
    audit_dir.mkdir(parents=True, exist_ok=True)
    audit_id = uuid.uuid4().hex
    destination = audit_dir / f"{audit_id}.json"
    temporary = audit_dir / f".{audit_id}.{uuid.uuid4().hex}.tmp"
    from .models import utc_now

    payload = {
        "schemaVersion": 1,
        "uploadId": audit_id,
        "projectKey": result["projectKey"],
        "sourceName": result["sourceName"],
        "byteCount": result["byteCount"],
        "sha256": result["sha256"],
        "fileTokenHash": sha256_text(str(result["fileToken"])),
        "uploadedUtc": utc_now(),
    }
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, ensure_ascii=False, sort_keys=True, indent=2)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
