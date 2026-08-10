from __future__ import annotations

import hashlib
import json
import os
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urljoin, urlsplit

import httpx

from .models import ArtifactRecord
from .security import safe_filename, validate_download_url


OUTPUT_URL_KEYS = frozenset(
    {
        "model_url",
        "model_urls",
        "rendered_image_url",
        "base_model_url",
        "pbr_model_url",
        "model",
        "base_model",
        "pbr_model",
        "rendered_image",
    }
)
CONTENT_EXTENSIONS = {
    "model/gltf-binary": ".glb",
    "model/gltf+json": ".gltf",
    "application/octet-stream": ".bin",
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/webp": ".webp",
}
ALLOWED_EXTENSIONS = frozenset(
    {".glb", ".gltf", ".fbx", ".obj", ".stl", ".usdz", ".3mf", ".zip", ".png", ".jpg", ".jpeg", ".webp", ".bin"}
)


class ArtifactVault:
    def __init__(
        self,
        root: Path,
        max_download_bytes: int,
        *,
        client: httpx.AsyncClient | None = None,
    ) -> None:
        self.root = Path(root).resolve() / "artifacts"
        self.root.mkdir(parents=True, exist_ok=True)
        self.max_download_bytes = max_download_bytes
        self._owns_client = client is None
        self._client = client or httpx.AsyncClient(
            timeout=httpx.Timeout(120.0), follow_redirects=False
        )

    async def close(self) -> None:
        if self._owns_client:
            await self._client.aclose()

    async def download_outputs(
        self, job_id: str, output: dict[str, Any]
    ) -> list[ArtifactRecord]:
        if not job_id or any(char not in "0123456789abcdef" for char in job_id):
            raise ValueError("ARTIFACT_JOB_ID_INVALID")
        candidates = _collect_urls(output)
        if not candidates:
            raise ValueError("ARTIFACT_OUTPUT_MISSING: task has no approved downloadable output")
        if len(candidates) > 32:
            raise ValueError("ARTIFACT_COUNT_LIMIT: task returned more than 32 artifacts")
        records: list[ArtifactRecord] = []
        for index, (logical_name, url) in enumerate(candidates):
            records.append(await self._download(job_id, index, logical_name, url))
        self._write_manifest(job_id, records)
        return records

    async def _download(
        self, job_id: str, index: int, logical_name: str, initial_url: str
    ) -> ArtifactRecord:
        url = validate_download_url(initial_url)
        response: httpx.Response | None = None
        stream_context = None
        max_redirects = 3
        for redirect_count in range(max_redirects + 1):
            stream_context = self._client.stream("GET", url, headers={"Accept": "*/*"})
            response = await stream_context.__aenter__()
            if response.status_code not in {301, 302, 303, 307, 308}:
                break
            location = response.headers.get("Location")
            await stream_context.__aexit__(None, None, None)
            stream_context = None
            if not location:
                raise ValueError("ARTIFACT_REDIRECT_INVALID: redirect has no Location")
            if redirect_count >= max_redirects:
                raise ValueError("ARTIFACT_REDIRECT_LIMIT")
            url = validate_download_url(urljoin(url, location))
        if response is None or stream_context is None:
            raise ValueError("ARTIFACT_REDIRECT_LIMIT")

        try:
            response.raise_for_status()
            declared = response.headers.get("Content-Length")
            if declared and int(declared) > self.max_download_bytes:
                raise ValueError("ARTIFACT_TOO_LARGE: Content-Length exceeds configured limit")
            content_type = response.headers.get("Content-Type", "").split(";", 1)[0].strip() or None
            suffix = _extension(url, content_type)
            folder = self.root / job_id
            folder.mkdir(parents=True, exist_ok=True)
            name = f"{index:03d}_{safe_filename(logical_name)}{suffix}"
            destination = folder / name
            temporary = folder / f".{name}.{uuid.uuid4().hex}.part"
            digest = hashlib.sha256()
            size = 0
            try:
                with temporary.open("wb") as handle:
                    async for chunk in response.aiter_bytes(1024 * 1024):
                        size += len(chunk)
                        if size > self.max_download_bytes:
                            raise ValueError("ARTIFACT_TOO_LARGE: streamed data exceeds configured limit")
                        digest.update(chunk)
                        handle.write(chunk)
                    handle.flush()
                    os.fsync(handle.fileno())
                os.replace(temporary, destination)
            finally:
                temporary.unlink(missing_ok=True)
            return ArtifactRecord(
                logical_name=logical_name,
                path=str(destination),
                byte_count=size,
                sha256=digest.hexdigest(),
                content_type=content_type,
                source_host=urlsplit(url).hostname or "",
                downloaded_utc=datetime.now(timezone.utc).isoformat(),
            )
        finally:
            await stream_context.__aexit__(None, None, None)

    def _write_manifest(self, job_id: str, records: list[ArtifactRecord]) -> None:
        folder = self.root / job_id
        folder.mkdir(parents=True, exist_ok=True)
        destination = folder / "manifest.json"
        temporary = folder / f".manifest.{uuid.uuid4().hex}.tmp"
        payload = {
            "schemaVersion": 1,
            "jobId": job_id,
            "artifacts": [record.model_dump(mode="json") for record in records],
        }
        try:
            with temporary.open("w", encoding="utf-8", newline="\n") as handle:
                json.dump(payload, handle, ensure_ascii=False, sort_keys=True, indent=2)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temporary, destination)
        finally:
            temporary.unlink(missing_ok=True)


def _collect_urls(output: dict[str, Any]) -> list[tuple[str, str]]:
    candidates: list[tuple[str, str]] = []
    for key, value in output.items():
        if key not in OUTPUT_URL_KEYS:
            continue
        if isinstance(value, str) and value.startswith("https://"):
            candidates.append((key, value))
        elif isinstance(value, list):
            for index, item in enumerate(value):
                if isinstance(item, str) and item.startswith("https://"):
                    candidates.append((f"{key}_{index}", item))
    return candidates


def _extension(url: str, content_type: str | None) -> str:
    suffix = Path(urlsplit(url).path).suffix.lower()
    if suffix in ALLOWED_EXTENSIONS:
        return suffix
    return CONTENT_EXTENSIONS.get(content_type or "", ".bin")
