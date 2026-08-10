from __future__ import annotations

import asyncio
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from tripo_gateway.models import ArtifactRecord


class FakeTripoProvider:
    def __init__(self, *, block_polling: bool = False) -> None:
        self.balance = {"balance": 10_000, "frozen": 0}
        self.usage = [
            {
                "task_id": "task_old",
                "type": "text_to_model",
                "credits_consumed": 100,
                "created_at": "2026-01-01T00:00:00Z",
            }
        ]
        self.created: list[tuple[str, dict[str, Any]]] = []
        self.uploaded: list[Path] = []
        self.polled: list[str] = []
        self.closed = False
        self.block_polling = block_polling
        self.provider_task_created = threading.Event()
        self.release_polling = threading.Event()
        if not block_polling:
            self.release_polling.set()
        self.task_result: dict[str, Any] = {
            "task_id": "task_fake_001",
            "type": "text_to_model",
            "status": "success",
            "progress": 100,
            "output": {"model_url": "https://cdn.tripo3d.ai/output/model.glb"},
            "credits_consumed": 100,
        }
        self.task_results: list[dict[str, Any]] = []

    @property
    def configured(self) -> bool:
        return True

    async def get_balance(self) -> dict[str, Any]:
        return dict(self.balance)

    async def get_usage(self) -> list[dict[str, Any]]:
        return [dict(item) for item in self.usage]

    async def upload_file(self, path: Path) -> str:
        self.uploaded.append(path)
        return "file_fake_001"

    async def create_task(self, operation: str, payload: dict[str, Any]) -> str:
        self.created.append((operation, dict(payload)))
        self.provider_task_created.set()
        return "task_fake_001"

    async def get_task(self, task_id: str) -> dict[str, Any]:
        self.polled.append(task_id)
        while not self.release_polling.is_set():
            await asyncio.sleep(0.005)
        if self.task_results:
            return dict(self.task_results.pop(0))
        return dict(self.task_result)

    async def close(self) -> None:
        self.closed = True


class FakeArtifactVault:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.closed = False

    async def download_outputs(
        self, job_id: str, output: dict[str, Any]
    ) -> list[ArtifactRecord]:
        self.calls.append((job_id, dict(output)))
        return [
            ArtifactRecord(
                logical_name="model_url",
                path=f"C:/vault/{job_id}/model.glb",
                byte_count=4,
                sha256="0" * 64,
                content_type="model/gltf-binary",
                source_host="cdn.tripo3d.ai",
                downloaded_utc=datetime.now(timezone.utc).isoformat(),
            )
        ]

    async def close(self) -> None:
        self.closed = True
