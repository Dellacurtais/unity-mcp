from __future__ import annotations

import base64
import json
from pathlib import Path

import httpx
import pytest

from tripo_gateway.artifacts import ArtifactVault
from tripo_gateway.config import GatewaySettings
from tripo_gateway.meshy_provider import MeshyHttpProvider
from tripo_gateway.provider import TripoApiError


def settings(tmp_path: Path) -> GatewaySettings:
    return GatewaySettings(
        data_dir=tmp_path,
        gateway_token="gateway-token",
        meshy_api_key="msy_test-secret-123456",
        max_retries=1,
    )


@pytest.mark.asyncio
async def test_meshy_balance_and_image_smart_topology_payload(tmp_path: Path) -> None:
    image = tmp_path / "reference.png"
    image.write_bytes(b"synthetic-png")
    seen: list[tuple[str, dict]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.headers["Authorization"] == "Bearer msy_test-secret-123456"
        if request.url.path == "/openapi/v1/balance":
            return httpx.Response(200, json={"balance": 77})
        payload = json.loads(request.content)
        seen.append((request.url.path, payload))
        return httpx.Response(200, json={"result": "meshy-task-001"})

    client = httpx.AsyncClient(
        base_url="https://api.meshy.ai", transport=httpx.MockTransport(handler)
    )
    provider = MeshyHttpProvider(settings(tmp_path), client=client)
    try:
        assert await provider.get_balance() == {"balance": 77, "frozen": 0}
        handle = await provider.upload_file(image)
        assert handle.startswith("meshy-local:")
        assert "synthetic-png" not in handle
        task_id = await provider.create_task(
            "image_to_model",
            {
                "input": handle,
                "model": "latest",
                "smart_low_poly": True,
                "face_limit": 20_000,
                "texture": True,
                "pbr": True,
            },
        )
        assert task_id == "meshy-task-001"
        path, payload = seen[0]
        assert path == "/openapi/v1/image-to-3d"
        assert payload["model_type"] == "smart-topology"
        assert payload["ai_model"] == "meshy-t2"
        assert payload["target_polycount"] == 15_000
        assert payload["target_formats"] == ["fbx", "glb"]
        assert payload["image_url"].startswith("data:image/png;base64,")
        assert base64.b64decode(payload["image_url"].split(",", 1)[1]) == b"synthetic-png"
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_meshy_polling_normalizes_rig_outputs_and_usage(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/openapi/v1/rigging/rig-task-001"
        return httpx.Response(
            200,
            json={
                "id": "rig-task-001",
                "type": "rig",
                "status": "SUCCEEDED",
                "progress": 100,
                "consumed_credits": 5,
                "result": {
                    "rigged_character_fbx_url": "https://assets.meshy.ai/task/character.fbx",
                    "rigged_character_glb_url": "https://assets.meshy.ai/task/character.glb",
                    "basic_animations": {
                        "walking_fbx_url": "https://assets.meshy.ai/task/walk.fbx"
                    },
                },
            },
        )

    client = httpx.AsyncClient(
        base_url="https://api.meshy.ai", transport=httpx.MockTransport(handler)
    )
    provider = MeshyHttpProvider(settings(tmp_path), client=client)
    try:
        task = await provider.get_task("rig-task-001", "rig")
        assert task["status"] == "success"
        assert task["credits_consumed"] == 5
        assert task["output"]["rigged_character_fbx_url"].endswith("character.fbx")
        assert task["output"]["basic_animations"]["walking_fbx_url"].endswith("walk.fbx")
        with pytest.raises(TripoApiError) as missing_operation:
            await provider.get_task("rig-task-001")
        assert missing_operation.value.code == "MESHY_OPERATION_REQUIRED"
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_meshy_animation_and_resize_payloads_match_contract(tmp_path: Path) -> None:
    payloads: dict[str, dict] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        payloads[request.url.path] = json.loads(request.content)
        return httpx.Response(200, json={"result": "task-001"})

    client = httpx.AsyncClient(
        base_url="https://api.meshy.ai", transport=httpx.MockTransport(handler)
    )
    provider = MeshyHttpProvider(settings(tmp_path), client=client)
    try:
        await provider.create_task(
            "animate", {"input": "rig-task", "action_id": 92, "fps": 24}
        )
        await provider.create_task(
            "resize", {"input": "model-task", "resize_height": 1.8, "origin_at": "center"}
        )
        assert payloads["/openapi/v1/animations"] == {
            "rig_task_id": "rig-task",
            "action_id": 92,
            "post_process": {"operation_type": "change_fps", "fps": 24},
        }
        assert payloads["/openapi/v1/resize"] == {
            "input_task_id": "model-task",
            "resize_height": 1.8,
            "origin_at": "center",
        }
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_meshy_nested_rig_artifacts_are_downloaded_from_approved_host(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.host == "assets.meshy.ai"
        return httpx.Response(200, headers={"Content-Type": "application/octet-stream"}, content=b"fbx")

    client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    vault = ArtifactVault(tmp_path, 1024, client=client)
    try:
        records = await vault.download_outputs(
            "e" * 32,
            {
                "rigged_character_fbx_url": "https://assets.meshy.ai/task/character.fbx",
                "basic_animations": {
                    "walking_fbx_url": "https://assets.meshy.ai/task/walk.fbx"
                },
            },
        )
        assert {Path(record.path).suffix for record in records} == {".fbx"}
        assert len(records) == 2
    finally:
        await client.aclose()
