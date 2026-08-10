from __future__ import annotations

import hashlib
import hmac
import json
import os
from pathlib import Path
import time
import uuid
from typing import Any


class WebhookVerificationError(ValueError):
    pass


def verify_tripo_signature(
    raw_body: bytes,
    signature_header: str,
    secret: str,
    *,
    now: int | None = None,
    tolerance_seconds: int = 300,
) -> int:
    if not secret or not signature_header:
        raise WebhookVerificationError("webhook signature is required")
    parts: dict[str, str] = {}
    for value in signature_header.split(","):
        key, separator, item = value.strip().partition("=")
        if separator and key and item:
            parts[key] = item
    timestamp_text = parts.get("t", "")
    supplied = parts.get("v1", "")
    try:
        timestamp = int(timestamp_text)
    except ValueError as exception:
        raise WebhookVerificationError("webhook timestamp is invalid") from exception
    current = int(time.time()) if now is None else int(now)
    if abs(current - timestamp) > tolerance_seconds:
        raise WebhookVerificationError("webhook timestamp is outside the replay window")
    signed = str(timestamp).encode("ascii") + b"." + raw_body
    expected = hmac.new(secret.encode("utf-8"), signed, hashlib.sha256).hexdigest()
    if not hmac.compare_digest(expected, supplied):
        raise WebhookVerificationError("webhook signature is invalid")
    return timestamp


class WebhookDeliveryStore:
    def __init__(self, root: Path) -> None:
        self.root = Path(root).resolve() / "webhooks"
        self.root.mkdir(parents=True, exist_ok=True)

    def record(
        self,
        delivery_id: str,
        event_type: str,
        event_id: str,
        created_at: str,
    ) -> bool:
        if not delivery_id or len(delivery_id) > 256:
            raise WebhookVerificationError("webhook delivery id is invalid")
        digest = hashlib.sha256(delivery_id.encode("utf-8")).hexdigest()
        destination = self.root / f"{digest}.json"
        if destination.exists():
            return False
        payload = {
            "schemaVersion": 1,
            "deliveryHash": digest,
            "eventType": event_type,
            "eventId": event_id,
            "createdAt": created_at,
        }
        temporary = destination.with_name(f".{destination.name}.{uuid.uuid4().hex}.tmp")
        try:
            with temporary.open("x", encoding="utf-8", newline="\n") as handle:
                json.dump(payload, handle, ensure_ascii=False, sort_keys=True, indent=2)
                handle.flush()
                os.fsync(handle.fileno())
            try:
                os.link(temporary, destination)
            except FileExistsError:
                return False
            return True
        finally:
            temporary.unlink(missing_ok=True)

    def forget(self, delivery_id: str) -> None:
        if not delivery_id:
            return
        digest = hashlib.sha256(delivery_id.encode("utf-8")).hexdigest()
        (self.root / f"{digest}.json").unlink(missing_ok=True)


def parse_event(raw_body: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw_body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise WebhookVerificationError("webhook body must be valid UTF-8 JSON") from exception
    if not isinstance(value, dict) or set(value) - {"id", "type", "created_at", "data"}:
        raise WebhookVerificationError("webhook event contains unsupported fields")
    if not isinstance(value.get("id"), str) or not value["id"]:
        raise WebhookVerificationError("webhook event id is required")
    if value.get("type") not in {"task.completed", "task.failed", "balance.low"}:
        raise WebhookVerificationError("webhook event type is unsupported")
    if not isinstance(value.get("created_at"), str) or not value["created_at"]:
        raise WebhookVerificationError("webhook created_at is required")
    if not isinstance(value.get("data"), dict):
        raise WebhookVerificationError("webhook data must be an object")
    return value
