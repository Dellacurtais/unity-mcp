from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

MINIMUM_PYTHON = (3, 10)
RETAINED_INSTALLATIONS = 3
MAX_INSTALL_LOG_BYTES = 5 * 1024 * 1024
INSTALL_LOG_BACKUPS = 3
EXCLUDED_DIRECTORIES = {
    ".git",
    ".mypy_cache",
    ".pytest_cache",
    ".ruff_cache",
    ".venv",
    "__pycache__",
    "env",
    "htmlcov",
    "tests",
    "venv",
}
EXCLUDED_SUFFIXES = {".pyc", ".pyo"}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def safe_version_segment(version: str) -> str:
    value = "".join(character if character.isalnum() or character in {".", "-", "_"} else "-" for character in version)
    value = value.strip(".-_")
    return value or "unknown"


def venv_python_path(venv_path: Path) -> Path:
    return venv_path / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def build_install_path(root: Path, version: str, installation_id: str | None = None) -> Path:
    identifier = installation_id or f"{datetime.now(timezone.utc):%Y%m%d%H%M%S}-{uuid4().hex[:8]}"
    return root / "installations" / f"{safe_version_segment(version)}-{identifier}"


def atomic_write_json(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{uuid4().hex}.tmp")
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(temporary, path)


def read_json(path: Path) -> dict[str, object] | None:
    if not path.is_file():
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, TypeError):
        return None
    return payload if isinstance(payload, dict) else None


def rotate_log(path: Path, max_bytes: int = MAX_INSTALL_LOG_BYTES, backups: int = INSTALL_LOG_BACKUPS) -> None:
    if max_bytes <= 0 or backups < 1 or not path.is_file() or path.stat().st_size < max_bytes:
        return
    oldest = path.with_name(f"{path.name}.{backups}")
    oldest.unlink(missing_ok=True)
    for index in range(backups - 1, 0, -1):
        source = path.with_name(f"{path.name}.{index}")
        if source.exists():
            os.replace(source, path.with_name(f"{path.name}.{index + 1}"))
    os.replace(path, path.with_name(f"{path.name}.1"))


def protected_install_paths(root: Path) -> set[Path]:
    paths: set[Path] = set()
    for state_path in (root / "current.json", root / "runtime" / "broker-process.json"):
        payload = read_json(state_path)
        value = payload.get("installPath") if payload else None
        if isinstance(value, str) and value.strip():
            paths.add(Path(value).expanduser().resolve())
    return paths


def prune_installations(root: Path, keep: int = RETAINED_INSTALLATIONS) -> list[str]:
    installations = root / "installations"
    if keep < 1 or not installations.is_dir():
        return []
    protected = protected_install_paths(root)
    candidates = [path.resolve() for path in installations.iterdir() if path.is_dir()]
    candidates.sort(key=lambda path: path.stat().st_mtime_ns, reverse=True)
    retained = set(candidates[:keep]) | protected
    removed: list[str] = []
    for path in candidates:
        if path in retained:
            continue
        try:
            shutil.rmtree(path)
        except OSError:
            continue
        removed.append(str(path))
    return removed


def _ignore_copy_entries(_: str, names: list[str]) -> set[str]:
    ignored: set[str] = set()
    for name in names:
        if name in EXCLUDED_DIRECTORIES or Path(name).suffix.lower() in EXCLUDED_SUFFIXES:
            ignored.add(name)
    return ignored


def copy_broker_source(source: Path, destination: Path) -> None:
    shutil.copytree(source, destination, ignore=_ignore_copy_entries)


def requirements_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def create_venv(base_python: Path, venv_path: Path, log) -> None:
    subprocess.run(
        [str(base_python), "-m", "venv", str(venv_path)],
        check=True,
        stdout=log,
        stderr=subprocess.STDOUT,
        text=True,
    )


def install_requirements(python_path: Path, requirements_path: Path, log) -> None:
    subprocess.run(
        [
            str(python_path),
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--no-input",
            "--retries",
            "2",
            "--timeout",
            "30",
            "--requirement",
            str(requirements_path),
        ],
        check=True,
        stdout=log,
        stderr=subprocess.STDOUT,
        text=True,
    )


def perform_install(
    source: Path,
    root: Path,
    version: str,
    *,
    repair: bool = False,
    base_python: Path | None = None,
    installation_id: str | None = None,
    create_venv_fn=None,
    install_requirements_fn=None,
) -> dict[str, object]:
    source = source.resolve()
    root = root.resolve()
    base_python = (base_python or Path(sys.executable)).resolve()
    create_venv_fn = create_venv_fn or create_venv
    install_requirements_fn = install_requirements_fn or install_requirements

    if sys.version_info[:2] < MINIMUM_PYTHON:
        raise RuntimeError(
            f"Python {MINIMUM_PYTHON[0]}.{MINIMUM_PYTHON[1]} or newer is required; "
            f"found {sys.version_info.major}.{sys.version_info.minor}."
        )
    if not (source / "main.py").is_file() or not (source / "requirements.txt").is_file():
        raise RuntimeError(f"Broker source is incomplete: {source}")

    runtime_path = root / "runtime"
    status_path = runtime_path / "install-status.json"
    log_path = root / "logs" / "install.log"
    install_path = build_install_path(root, version, installation_id)
    source_copy = install_path / "source"
    venv_path = install_path / "venv"
    previous_manifest = read_json(root / "current.json")
    current_activated = False
    started_utc = utc_now()

    status: dict[str, object] = {
        "schemaVersion": 1,
        "state": "installing",
        "mode": "repair" if repair else "install",
        "pid": os.getpid(),
        "version": version,
        "startedUtc": started_utc,
        "updatedUtc": started_utc,
        "message": "Preparing managed broker installation",
        "logPath": str(log_path),
    }
    atomic_write_json(status_path, status)

    try:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        rotate_log(log_path)
        with log_path.open("a", encoding="utf-8", buffering=1) as log:
            log.write(f"\n[{utc_now()}] {'Repairing' if repair else 'Installing'} Unity MCP broker {version}\n")
            log.write(f"Base Python: {base_python}\n")
            log.write(f"Installation: {install_path}\n")

            install_path.parent.mkdir(parents=True, exist_ok=True)
            status.update(message="Copying broker source", updatedUtc=utc_now())
            atomic_write_json(status_path, status)
            copy_broker_source(source, source_copy)

            status.update(message="Creating isolated Python environment", updatedUtc=utc_now())
            atomic_write_json(status_path, status)
            create_venv_fn(base_python, venv_path, log)

            managed_python = venv_python_path(venv_path)
            if not managed_python.is_file():
                raise RuntimeError(f"Virtual environment did not create {managed_python}")

            status.update(message="Installing broker dependencies", updatedUtc=utc_now())
            atomic_write_json(status_path, status)
            managed_requirements = source_copy / "requirements.txt"
            install_requirements_fn(managed_python, managed_requirements, log)

            manifest: dict[str, object] = {
                "schemaVersion": 1,
                "version": version,
                "installPath": str(install_path),
                "pythonPath": str(managed_python),
                "mainPath": str(source_copy / "main.py"),
                "requirementsHash": requirements_hash(managed_requirements),
                "installedUtc": utc_now(),
            }
            atomic_write_json(install_path / "install.json", manifest)
            atomic_write_json(root / "current.json", manifest)
            current_activated = True

            status.update(
                state="completed",
                message="Managed broker installation is ready",
                updatedUtc=utc_now(),
                completedUtc=utc_now(),
                installPath=str(install_path),
                pythonPath=str(managed_python),
                removedInstallations=[],
            )
            atomic_write_json(status_path, status)
            current_activated = False

            try:
                removed_installations = prune_installations(root)
                if removed_installations:
                    status.update(
                        removedInstallations=removed_installations,
                        updatedUtc=utc_now(),
                    )
                    atomic_write_json(status_path, status)
            except Exception as cleanup_exception:
                status.update(
                    cleanupWarning=str(cleanup_exception),
                    updatedUtc=utc_now(),
                )
                try:
                    atomic_write_json(status_path, status)
                except OSError:
                    pass
                try:
                    log.write(f"[{utc_now()}] Installation cleanup warning: {cleanup_exception}\n")
                except OSError:
                    pass
            try:
                log.write(f"[{utc_now()}] Installation completed\n")
            except OSError:
                pass
            return manifest
    except Exception as exception:
        rollback_error = None
        if current_activated:
            try:
                if previous_manifest is None:
                    (root / "current.json").unlink(missing_ok=True)
                else:
                    atomic_write_json(root / "current.json", previous_manifest)
            except Exception as rollback_exception:
                rollback_error = str(rollback_exception)
        error = str(exception)
        if rollback_error:
            error += f"; current installation rollback failed: {rollback_error}"
        status.update(
            state="failed",
            message="Managed broker installation failed",
            error=error,
            updatedUtc=utc_now(),
            completedUtc=utc_now(),
        )
        status_error = None
        try:
            atomic_write_json(status_path, status)
        except Exception as status_exception:
            status_error = str(status_exception)
        try:
            with log_path.open("a", encoding="utf-8") as log:
                traceback.print_exc(file=log)
                if status_error:
                    log.write(f"Could not persist failed install status: {status_error}\n")
        except Exception:
            pass
        finally:
            if rollback_error is None:
                shutil.rmtree(install_path, ignore_errors=True)
        raise


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Install the Unity MCP broker into a user-owned location.")
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--repair", action="store_true")
    return parser.parse_args()


def main() -> int:
    arguments = parse_args()
    try:
        perform_install(
            arguments.source,
            arguments.root,
            arguments.version,
            repair=arguments.repair,
        )
        return 0
    except Exception as exception:
        print(f"Unity MCP broker installation failed: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
