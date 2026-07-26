from __future__ import annotations

from dataclasses import dataclass
from typing import Any
from uuid import uuid4

PROTOCOL_VERSION = 1


@dataclass(frozen=True)
class BridgeIdentity:
    project_key: str
    project_name: str
    project_path: str
    process_id: int
    process_start_utc: str
    generation_id: str
    package_version: str
    registry_count: int
    registry_hash: str

    @classmethod
    def from_hello(cls, payload: dict[str, Any]) -> "BridgeIdentity":
        required = (
            "projectKey", "projectName", "projectPath", "processId",
            "processStartUtc", "generationId", "packageVersion",
            "registryCount", "registryHash",
        )
        missing = [key for key in required if payload.get(key) is None]
        if missing:
            raise ValueError(f"bridge hello missing: {', '.join(missing)}")
        return cls(
            project_key=str(payload["projectKey"]),
            project_name=str(payload["projectName"]),
            project_path=str(payload["projectPath"]),
            process_id=int(payload["processId"]),
            process_start_utc=str(payload["processStartUtc"]),
            generation_id=str(payload["generationId"]),
            package_version=str(payload["packageVersion"]),
            registry_count=int(payload["registryCount"]),
            registry_hash=str(payload["registryHash"]),
        )


def envelope(
    message_type: str,
    payload: dict[str, Any],
    *,
    identity: BridgeIdentity | None = None,
    correlation_id: str | None = None,
    message_id: str | None = None,
) -> dict[str, Any]:
    return {
        "version": PROTOCOL_VERSION,
        "type": message_type,
        "messageId": message_id or uuid4().hex,
        "correlationId": correlation_id,
        "projectKey": identity.project_key if identity else None,
        "processId": identity.process_id if identity else None,
        "generationId": identity.generation_id if identity else None,
        "payload": payload,
    }


def validate_envelope(message: dict[str, Any]) -> None:
    if message.get("version") != PROTOCOL_VERSION:
        raise ValueError(f"unsupported bridge protocol version: {message.get('version')}")
    if not isinstance(message.get("type"), str):
        raise ValueError("bridge envelope type is required")
    if not isinstance(message.get("messageId"), str):
        raise ValueError("bridge envelope messageId is required")
    if not isinstance(message.get("payload"), dict):
        raise ValueError("bridge envelope payload must be an object")
