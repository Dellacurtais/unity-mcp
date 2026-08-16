from __future__ import annotations

import ipaddress
import logging
import os
from pathlib import Path

import uvicorn

from tripo_gateway.app import create_app
from tripo_gateway.config import GatewaySettings


def main() -> None:
    host = os.environ.get("TRIPO_GATEWAY_HOST", "127.0.0.1")
    try:
        address = ipaddress.ip_address(host)
    except ValueError as exception:
        raise SystemExit("TRIPO_GATEWAY_HOST must be a loopback IP address") from exception
    if not address.is_loopback:
        raise SystemExit("Refusing non-loopback TRIPO_GATEWAY_HOST")
    port = int(os.environ.get("TRIPO_GATEWAY_PORT", "8771"))
    if port < 1 or port > 65535:
        raise SystemExit("TRIPO_GATEWAY_PORT must be between 1 and 65535")
    settings = GatewaySettings.from_environment()
    log_path = os.environ.get("TRIPO_GATEWAY_LOG")
    run_options: dict[str, object] = {}
    if log_path:
        resolved_log = Path(log_path).expanduser().resolve()
        resolved_log.parent.mkdir(parents=True, exist_ok=True)
        logging.basicConfig(
            level=logging.INFO,
            format="%(asctime)s %(levelname)s %(name)s %(message)s",
            handlers=[logging.FileHandler(resolved_log, encoding="utf-8")],
            force=True,
        )
        run_options["log_config"] = None
    uvicorn.run(
        create_app(settings),
        host=host,
        port=port,
        log_level="info",
        **run_options,
    )


if __name__ == "__main__":
    main()
