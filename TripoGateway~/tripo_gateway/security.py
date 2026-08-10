from __future__ import annotations

import hashlib
import hmac
import json
import re
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urlsplit


SECRET_KEYS = frozenset(
    {
        "authorization",
        "api_key",
        "apikey",
        "token",
        "gateway_token",
        "tripo_api_key",
        "tripo_gateway_token",
    }
)
SAFE_NAME = re.compile(r"[^A-Za-z0-9._-]+")
ALLOWED_DOWNLOAD_SUFFIXES = (".tripo3d.ai", ".tripo3d.com")


def canonical_json(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def sha256_json(value: Any) -> str:
    return sha256_text(canonical_json(value))


def sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(chunk_size):
            digest.update(chunk)
    return digest.hexdigest()


def constant_time_equal(left: str, right: str) -> bool:
    return hmac.compare_digest((left or "").encode(), (right or "").encode())


def redact(value: Any) -> Any:
    if isinstance(value, dict):
        return {
            key: "[REDACTED]" if key.lower() in SECRET_KEYS else redact(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact(item) for item in value]
    if isinstance(value, tuple):
        return tuple(redact(item) for item in value)
    return value


def resolve_allowed_file(path: str, roots: Iterable[Path]) -> Path:
    candidate = Path(path).expanduser().resolve(strict=True)
    if not candidate.is_file():
        raise ValueError("UPLOAD_SOURCE_NOT_FILE: source must be a regular file")
    for root in roots:
        resolved_root = Path(root).resolve()
        try:
            candidate.relative_to(resolved_root)
            return candidate
        except ValueError:
            continue
    raise ValueError("UPLOAD_SOURCE_OUTSIDE_ALLOWED_ROOTS")


def validate_download_url(url: str) -> str:
    parsed = urlsplit((url or "").strip())
    if parsed.scheme != "https":
        raise ValueError("ARTIFACT_URL_REJECTED: https is required")
    if not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("ARTIFACT_URL_REJECTED: credentials are not allowed")
    if parsed.fragment or parsed.port not in (None, 443):
        raise ValueError("ARTIFACT_URL_REJECTED: fragment or non-default port")
    host = parsed.hostname.lower()
    if not any(host.endswith(suffix) for suffix in ALLOWED_DOWNLOAD_SUFFIXES):
        raise ValueError("ARTIFACT_URL_REJECTED: host is not owned by Tripo")
    return url


def safe_filename(value: str, fallback: str = "artifact") -> str:
    name = SAFE_NAME.sub("_", (value or "").strip()).strip("._")
    return (name or fallback)[:120]

