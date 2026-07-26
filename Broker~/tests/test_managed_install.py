from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

import installer
import main


def make_source(root: Path) -> Path:
    source = root / "source"
    source.mkdir()
    (source / "main.py").write_text("print('broker')\n", encoding="utf-8")
    (source / "requirements.txt").write_text("fastapi>=0.110\n", encoding="utf-8")
    (source / "module.py").write_text("VALUE = 1\n", encoding="utf-8")
    (source / "tests").mkdir()
    (source / "tests" / "test_ignored.py").write_text("raise AssertionError\n", encoding="utf-8")
    (source / "__pycache__").mkdir()
    (source / "__pycache__" / "module.pyc").write_bytes(b"cache")
    return source


def fake_create_venv(_: Path, venv_path: Path, __) -> None:
    python_path = installer.venv_python_path(venv_path)
    python_path.parent.mkdir(parents=True)
    python_path.write_text("managed-python", encoding="utf-8")


def fake_install_requirements(python_path: Path, requirements_path: Path, __) -> None:
    assert python_path.read_text(encoding="utf-8") == "managed-python"
    assert requirements_path.read_text(encoding="utf-8") == "fastapi>=0.110\n"


def test_install_persists_versioned_runtime_and_excludes_development_files(tmp_path: Path) -> None:
    source = make_source(tmp_path)
    root = tmp_path / "managed"

    manifest = installer.perform_install(
        source,
        root,
        "1.2.3 beta",
        base_python=Path(sys.executable),
        installation_id="test-install",
        create_venv_fn=fake_create_venv,
        install_requirements_fn=fake_install_requirements,
    )

    install_path = Path(str(manifest["installPath"]))
    persisted = json.loads((root / "current.json").read_text(encoding="utf-8"))
    status = json.loads((root / "runtime" / "install-status.json").read_text(encoding="utf-8"))

    assert install_path.name == "1.2.3-beta-test-install"
    assert persisted == manifest
    assert Path(str(persisted["pythonPath"])).is_file()
    assert Path(str(persisted["mainPath"])).read_text(encoding="utf-8") == "print('broker')\n"
    assert not (install_path / "source" / "tests").exists()
    assert not (install_path / "source" / "__pycache__").exists()
    assert status["state"] == "completed"
    assert status["installPath"] == str(install_path)


def test_install_rejects_overlapping_installer_before_writing_shared_state(tmp_path: Path) -> None:
    source = make_source(tmp_path)
    root = tmp_path / "managed"
    install_lock = installer.acquire_install_lock(root)

    try:
        with pytest.raises(RuntimeError, match="already running"):
            installer.perform_install(
                source,
                root,
                "1.2.3",
                base_python=Path(sys.executable),
                installation_id="overlap",
                create_venv_fn=fake_create_venv,
                install_requirements_fn=fake_install_requirements,
            )
    finally:
        installer.release_install_lock(install_lock)

    assert not (root / "runtime" / "install-status.json").exists()
    assert not (root / "current.json").exists()


def test_failed_repair_keeps_previous_current_installation(tmp_path: Path) -> None:
    source = make_source(tmp_path)
    root = tmp_path / "managed"
    first = installer.perform_install(
        source,
        root,
        "1.0.0",
        base_python=Path(sys.executable),
        installation_id="working",
        create_venv_fn=fake_create_venv,
        install_requirements_fn=fake_install_requirements,
    )

    def fail_install(*_) -> None:
        raise RuntimeError("dependency install failed")

    with pytest.raises(RuntimeError, match="dependency install failed"):
        installer.perform_install(
            source,
            root,
            "1.0.1",
            repair=True,
            base_python=Path(sys.executable),
            installation_id="broken",
            create_venv_fn=fake_create_venv,
            install_requirements_fn=fail_install,
        )

    current = json.loads((root / "current.json").read_text(encoding="utf-8"))
    status = json.loads((root / "runtime" / "install-status.json").read_text(encoding="utf-8"))
    assert current == first
    assert status["state"] == "failed"
    assert status["mode"] == "repair"
    assert status["error"] == "dependency install failed"
    assert not (root / "installations" / "1.0.1-broken").exists()


def test_post_activation_status_failure_rolls_back_current_manifest(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    source = make_source(tmp_path)
    root = tmp_path / "managed"
    first = installer.perform_install(
        source,
        root,
        "1.0.0",
        base_python=Path(sys.executable),
        installation_id="working",
        create_venv_fn=fake_create_venv,
        install_requirements_fn=fake_install_requirements,
    )
    original_write = installer.atomic_write_json

    def fail_completed_status(path: Path, payload: dict[str, object]) -> None:
        if path.name == "install-status.json" and payload.get("state") == "completed":
            raise OSError("status commit failed")
        original_write(path, payload)

    monkeypatch.setattr(installer, "atomic_write_json", fail_completed_status)
    with pytest.raises(OSError, match="status commit failed"):
        installer.perform_install(
            source,
            root,
            "1.0.1",
            repair=True,
            base_python=Path(sys.executable),
            installation_id="rollback",
            create_venv_fn=fake_create_venv,
            install_requirements_fn=fake_install_requirements,
        )

    current = json.loads((root / "current.json").read_text(encoding="utf-8"))
    status = json.loads((root / "runtime" / "install-status.json").read_text(encoding="utf-8"))
    assert current == first
    assert status["state"] == "failed"
    assert status["error"] == "status commit failed"
    assert not (root / "installations" / "1.0.1-rollback").exists()


def test_retention_failure_is_recorded_without_invalidating_install(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    source = make_source(tmp_path)
    root = tmp_path / "managed"

    def fail_prune(*_) -> list[str]:
        raise RuntimeError("retention failed")

    monkeypatch.setattr(installer, "prune_installations", fail_prune)
    manifest = installer.perform_install(
        source,
        root,
        "1.0.1",
        base_python=Path(sys.executable),
        installation_id="warning",
        create_venv_fn=fake_create_venv,
        install_requirements_fn=fake_install_requirements,
    )

    current = json.loads((root / "current.json").read_text(encoding="utf-8"))
    status = json.loads((root / "runtime" / "install-status.json").read_text(encoding="utf-8"))
    assert current == manifest
    assert status["state"] == "completed"
    assert status["cleanupWarning"] == "retention failed"
    assert Path(str(manifest["installPath"])).is_dir()


def test_prune_installations_retains_current_active_and_newest(tmp_path: Path) -> None:
    root = tmp_path / "gestão com espaços"
    installations = root / "installations"
    names = ["current", "active", "old-a", "old-b", "newest"]
    paths = {}
    for index, name in enumerate(names):
        path = installations / name
        path.mkdir(parents=True)
        os.utime(path, (100 + index, 100 + index))
        paths[name] = path.resolve()
    installer.atomic_write_json(root / "current.json", {"installPath": str(paths["current"])})
    installer.atomic_write_json(
        root / "runtime" / "broker-process.json",
        {"state": "running", "installPath": str(paths["active"])},
    )

    removed = {Path(path).name for path in installer.prune_installations(root, keep=1)}

    assert removed == {"old-a", "old-b"}
    assert paths["current"].is_dir()
    assert paths["active"].is_dir()
    assert paths["newest"].is_dir()


def test_rotate_log_keeps_bounded_backups(tmp_path: Path) -> None:
    log = tmp_path / "logs" / "install.log"
    log.parent.mkdir(parents=True)
    log.write_text("current", encoding="utf-8")
    log.with_name("install.log.1").write_text("previous", encoding="utf-8")
    log.with_name("install.log.2").write_text("oldest", encoding="utf-8")

    installer.rotate_log(log, max_bytes=1, backups=2)

    assert not log.exists()
    assert log.with_name("install.log.1").read_text(encoding="utf-8") == "current"
    assert log.with_name("install.log.2").read_text(encoding="utf-8") == "previous"


def test_install_requirements_is_non_interactive_and_bounded(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    captured = {}

    def fake_run(arguments, **kwargs):
        captured["arguments"] = arguments
        captured["kwargs"] = kwargs
        return subprocess.CompletedProcess(arguments, 0)

    monkeypatch.setattr(installer.subprocess, "run", fake_run)
    requirements = tmp_path / "requirements.txt"
    requirements.write_text("fastapi==0.140.0\n", encoding="utf-8")

    installer.install_requirements(Path(sys.executable), requirements, None)

    arguments = captured["arguments"]
    assert "--no-input" in arguments
    assert arguments[arguments.index("--retries") + 1] == "2"
    assert arguments[arguments.index("--timeout") + 1] == "30"
    assert captured["kwargs"]["check"] is True


def test_managed_state_update_preserves_process_identity(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    state_path = tmp_path / "runtime" / "broker-process.json"
    state_path.parent.mkdir()
    state_path.write_text(
        json.dumps({
            "state": "starting",
            "instanceId": "expected",
            "pid": 42,
            "processStartUtc": "2026-01-01T00:00:00+00:00",
        }),
        encoding="utf-8",
    )
    monkeypatch.setenv("UNITY_MCP_BROKER_STATE", str(state_path))
    monkeypatch.setenv("UNITY_MCP_BROKER_INSTANCE_ID", "expected")

    main.update_managed_state("running")

    running = json.loads(state_path.read_text(encoding="utf-8"))
    assert running["state"] == "running"
    assert running["pid"] == 42
    assert running["instanceId"] == "expected"
    assert "readyUtc" in running
    assert "updatedUtc" in running

    monkeypatch.setenv("UNITY_MCP_BROKER_INSTANCE_ID", "other")
    main.update_managed_state("failed", "must not overwrite")
    assert json.loads(state_path.read_text(encoding="utf-8")) == running


def test_managed_logging_uses_only_rotating_file_handler(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    log_path = tmp_path / "logs" / "broker.log"
    monkeypatch.setenv("UNITY_MCP_BROKER_LOG", str(log_path))

    config = main.logging_config()

    handler = config["handlers"]["managed_file"]
    assert handler["class"] == "logging.handlers.RotatingFileHandler"
    assert handler["filename"] == str(log_path)
    for logger_name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        assert config["loggers"][logger_name]["handlers"] == ["managed_file"]
