from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit


OFFICIAL_API_HOSTS = frozenset({"openapi.tripo3d.ai", "openapi.tripo3d.com"})
OFFICIAL_MESHY_API_HOST = "api.meshy.ai"


def _default_data_dir() -> Path:
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "Maicon" / "UnitySceneAnalyzer" / "TripoGateway"
    return Path.home() / ".unity-scene-analyzer" / "tripo-gateway"


def _parse_roots(raw: str | None) -> tuple[Path, ...]:
    if not raw:
        return ()
    roots: list[Path] = []
    for value in raw.split(os.pathsep):
        value = value.strip()
        if value:
            roots.append(Path(value).expanduser().resolve())
    return tuple(roots)


def normalize_api_base_url(value: str, allow_custom: bool = False) -> str:
    parsed = urlsplit((value or "").strip())
    if parsed.scheme != "https":
        raise ValueError("TRIPO_API_BASE_URL must use https")
    if not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("TRIPO_API_BASE_URL must not contain credentials")
    if parsed.query or parsed.fragment:
        raise ValueError("TRIPO_API_BASE_URL must not contain query or fragment")
    if parsed.port not in (None, 443):
        raise ValueError("TRIPO_API_BASE_URL must use the default https port")
    if not allow_custom and parsed.hostname.lower() not in OFFICIAL_API_HOSTS:
        raise ValueError("TRIPO_API_BASE_URL host is not an official Tripo API host")
    path = parsed.path.rstrip("/")
    if not path.endswith("/v3"):
        raise ValueError("TRIPO_API_BASE_URL must end with /v3")
    return f"https://{parsed.hostname.lower()}{path}"


def normalize_meshy_api_base_url(value: str, allow_custom: bool = False) -> str:
    parsed = urlsplit((value or "").strip())
    if parsed.scheme != "https":
        raise ValueError("MESHY_API_BASE_URL must use https")
    if not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("MESHY_API_BASE_URL must not contain credentials")
    if parsed.query or parsed.fragment:
        raise ValueError("MESHY_API_BASE_URL must not contain query or fragment")
    if parsed.port not in (None, 443):
        raise ValueError("MESHY_API_BASE_URL must use the default https port")
    if not allow_custom and parsed.hostname.lower() != OFFICIAL_MESHY_API_HOST:
        raise ValueError("MESHY_API_BASE_URL host is not the official Meshy API host")
    path = parsed.path.rstrip("/")
    if path:
        raise ValueError("MESHY_API_BASE_URL must not contain a path")
    return f"https://{parsed.hostname.lower()}"


@dataclass(frozen=True)
class GatewaySettings:
    data_dir: Path
    gateway_token: str
    api_key: str | None = None
    api_base_url: str = "https://openapi.tripo3d.ai/v3"
    meshy_api_key: str | None = None
    meshy_api_base_url: str = "https://api.meshy.ai"
    upload_roots: tuple[Path, ...] = ()
    max_request_bytes: int = 2 * 1024 * 1024
    max_download_bytes: int = 1024 * 1024 * 1024
    max_credits_per_job: int = 1000
    max_daily_estimated_credits_per_project: int = 1000
    max_active_jobs_per_project: int = 3
    webhook_secret: str | None = None
    webhook_tolerance_seconds: int = 300
    request_timeout_seconds: float = 60.0
    poll_interval_seconds: float = 2.0
    cancelled_poll_interval_seconds: float = 10.0
    max_retries: int = 5
    allow_custom_api_base_url: bool = False

    def __post_init__(self) -> None:
        object.__setattr__(self, "data_dir", Path(self.data_dir).expanduser().resolve())
        object.__setattr__(
            self,
            "upload_roots",
            tuple(Path(root).expanduser().resolve() for root in self.upload_roots),
        )
        object.__setattr__(
            self,
            "api_base_url",
            normalize_api_base_url(self.api_base_url, self.allow_custom_api_base_url),
        )
        object.__setattr__(
            self,
            "meshy_api_base_url",
            normalize_meshy_api_base_url(
                self.meshy_api_base_url, self.allow_custom_api_base_url
            ),
        )
        if not self.gateway_token:
            raise ValueError("TRIPO_GATEWAY_TOKEN is required")
        if self.max_request_bytes < 64 * 1024:
            raise ValueError("max_request_bytes must be at least 65536")
        if self.max_download_bytes < 1024:
            raise ValueError("max_download_bytes must be positive")
        if self.max_credits_per_job < 0:
            raise ValueError("max_credits_per_job must be non-negative")
        if self.max_daily_estimated_credits_per_project < 0:
            raise ValueError("max_daily_estimated_credits_per_project must be non-negative")
        if self.max_active_jobs_per_project < 1:
            raise ValueError("max_active_jobs_per_project must be positive")
        if self.webhook_secret is not None and not self.webhook_secret.startswith("whsec_"):
            raise ValueError("TRIPO_WEBHOOK_SECRET must start with whsec_")
        if self.webhook_tolerance_seconds < 30 or self.webhook_tolerance_seconds > 3600:
            raise ValueError("webhook_tolerance_seconds must be between 30 and 3600")
        if self.poll_interval_seconds <= 0 or self.cancelled_poll_interval_seconds <= 0:
            raise ValueError("poll intervals must be positive")

    @classmethod
    def from_environment(cls) -> "GatewaySettings":
        allow_custom = os.environ.get("TRIPO_GATEWAY_ALLOW_CUSTOM_BASE_URL") == "1"
        return cls(
            data_dir=Path(os.environ.get("TRIPO_GATEWAY_DATA_DIR") or _default_data_dir()),
            gateway_token=os.environ.get("TRIPO_GATEWAY_TOKEN", ""),
            api_key=os.environ.get("TRIPO_API_KEY") or None,
            api_base_url=os.environ.get(
                "TRIPO_API_BASE_URL", "https://openapi.tripo3d.ai/v3"
            ),
            meshy_api_key=os.environ.get("MESHY_API_KEY") or None,
            meshy_api_base_url=os.environ.get(
                "MESHY_API_BASE_URL", "https://api.meshy.ai"
            ),
            upload_roots=_parse_roots(os.environ.get("TRIPO_GATEWAY_UPLOAD_ROOTS")),
            max_request_bytes=int(
                os.environ.get("TRIPO_GATEWAY_MAX_REQUEST_BYTES", 2 * 1024 * 1024)
            ),
            max_download_bytes=int(
                os.environ.get("TRIPO_GATEWAY_MAX_DOWNLOAD_BYTES", 1024 * 1024 * 1024)
            ),
            max_credits_per_job=int(
                os.environ.get("TRIPO_GATEWAY_MAX_CREDITS_PER_JOB", 1000)
            ),
            max_daily_estimated_credits_per_project=int(
                os.environ.get("TRIPO_GATEWAY_MAX_DAILY_ESTIMATED_CREDITS", 1000)
            ),
            max_active_jobs_per_project=int(
                os.environ.get("TRIPO_GATEWAY_MAX_ACTIVE_JOBS_PER_PROJECT", 3)
            ),
            webhook_secret=os.environ.get("TRIPO_WEBHOOK_SECRET") or None,
            webhook_tolerance_seconds=int(
                os.environ.get("TRIPO_WEBHOOK_TOLERANCE_SECONDS", 300)
            ),
            request_timeout_seconds=float(
                os.environ.get("TRIPO_GATEWAY_REQUEST_TIMEOUT_SECONDS", 60)
            ),
            poll_interval_seconds=float(
                os.environ.get("TRIPO_GATEWAY_POLL_INTERVAL_SECONDS", 2)
            ),
            cancelled_poll_interval_seconds=float(
                os.environ.get("TRIPO_GATEWAY_CANCELLED_POLL_INTERVAL_SECONDS", 10)
            ),
            max_retries=int(os.environ.get("TRIPO_GATEWAY_MAX_RETRIES", 5)),
            allow_custom_api_base_url=allow_custom,
        )

    @property
    def configured(self) -> bool:
        return bool(self.api_key)

    def public_summary(self) -> dict[str, object]:
        return {
            "configured": self.configured,
            "apiRegion": "cn" if self.api_base_url.endswith(".com/v3") else "global",
            "meshyConfiguredFromEnvironment": bool(self.meshy_api_key),
            "uploadRootCount": len(self.upload_roots),
            "maxRequestBytes": self.max_request_bytes,
            "maxDownloadBytes": self.max_download_bytes,
            "maxCreditsPerJob": self.max_credits_per_job,
            "maxDailyEstimatedCreditsPerProject": self.max_daily_estimated_credits_per_project,
            "maxActiveJobsPerProject": self.max_active_jobs_per_project,
            "webhookConfigured": bool(self.webhook_secret),
        }
