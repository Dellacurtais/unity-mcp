from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGE_NAME = "com.maicon.scene-analyzer"
RELEASE_VERSION = "0.18.5"
PINNED_REQUIREMENT = re.compile(r"^[A-Za-z0-9_.-]+==[^=<>!~]+$")


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def require(condition: bool, message: str, failures: list[str]) -> None:
    if not condition:
        failures.append(message)


def main() -> int:
    failures: list[str] = []
    package = json.loads(read("package.json"))
    require(package.get("name") == PACKAGE_NAME, f"package name must remain {PACKAGE_NAME}", failures)
    require(package.get("version") == RELEASE_VERSION, f"package version must be {RELEASE_VERSION}", failures)
    require(package.get("displayName") == "Unity MCP Transport", "display name must be Unity MCP Transport", failures)

    version_source = read("Broker~/broker_version.py")
    operational_source = read("Editor/Mcp/OperationalTools.cs")
    require(f'BROKER_VERSION = "{RELEASE_VERSION}"' in version_source, "broker version is not synchronized", failures)
    require(f'PACKAGE_NAME = "{PACKAGE_NAME}"' in version_source, "broker package identity is not synchronized", failures)
    require(f'Fallback = "{RELEASE_VERSION}"' in operational_source, "C# fallback version is not synchronized", failures)
    require(f'"{PACKAGE_NAME}"' in operational_source, "C# package fallback identity is not synchronized", failures)

    expected_asmdefs = (
        "Runtime/com.maicon.scene-analyzer.asmdef",
        "Editor/com.maicon.scene-analyzer.Editor.asmdef",
        "Tests/Editor/com.maicon.scene-analyzer.Editor.Tests.asmdef",
    )
    for relative in expected_asmdefs:
        path = ROOT / relative
        require(path.is_file(), f"compatibility asmdef is missing: {relative}", failures)
        if path.is_file():
            require(
                json.loads(path.read_text(encoding="utf-8")).get("name") == path.stem,
                f"asmdef name drifted: {relative}",
                failures,
            )

    for source in (*ROOT.joinpath("Editor").rglob("*.cs"), *ROOT.joinpath("Runtime").rglob("*.cs")):
        require(
            source.with_suffix(source.suffix + ".meta").is_file(),
            f"Unity C# source is missing its immutable-package meta file: {source.relative_to(ROOT)}",
            failures,
        )

    settings = read("Editor/Settings.cs")
    require("Embedded = 0" in settings, "Embedded must remain enum value zero", failures)
    require(
        "EditorPrefs.GetInt(KTransportMode, (int)McpTransportMode.Embedded)" in settings,
        "Embedded must remain the default transport",
        failures,
    )

    bootstrap = read("Editor/Bootstrap.cs")
    reload_block = bootstrap[bootstrap.index("private static void OnBeforeReload") :]
    reload_block = reload_block[: reload_block.index("private static void OnQuitting")]
    quitting_block = bootstrap[bootstrap.index("private static void OnQuitting") :]
    require("ManagedBrokerProcess.Stop" not in reload_block, "Domain Reload must not stop the external broker", failures)
    require(
        "ManagedBrokerProcess.StopOwnedByCurrentUnity()" in quitting_block,
        "Editor quit must stop the managed broker owned by that Unity process",
        failures,
    )

    for relative in (
        "Broker~/requirements.txt",
        "Broker~/requirements-dev.txt",
        "TripoGateway~/requirements.txt",
        "TripoGateway~/requirements-dev.txt",
    ):
        for line in read(relative).splitlines():
            value = line.strip()
            if not value or value.startswith("-r "):
                continue
            require(
                bool(PINNED_REQUIREMENT.fullmatch(value)),
                f"dependency must be exactly pinned in {relative}: {value}",
                failures,
            )

    tracked_roots = (
        "package.json",
        "README.md",
        "Editor",
        "Runtime",
        "Tests",
        "Broker~",
        "TripoGateway~",
        "Documentation~",
    )
    stale_identity: list[str] = []
    excluded_directories = {".venv", "venv", "env", "__pycache__", ".pytest_cache", "bin", "obj"}
    for root_name in tracked_roots:
        path = ROOT / root_name
        candidates = [path] if path.is_file() else path.rglob("*")
        for candidate in candidates:
            if any(part in excluded_directories for part in candidate.relative_to(ROOT).parts):
                continue
            if not candidate.is_file() or candidate.suffix.lower() in {".png", ".jpg", ".jpeg", ".gif", ".dll"}:
                continue
            try:
                text = candidate.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue
            if "com.maicon.unity-mcp" in text or "extensions/unity-mcp" in text:
                stale_identity.append(str(candidate.relative_to(ROOT)))
    require(
        not stale_identity,
        "stale incompatible package identity: " + ", ".join(stale_identity),
        failures,
    )

    if failures:
        for failure in failures:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 1
    print(
        f"release contract OK: {PACKAGE_NAME}@{RELEASE_VERSION}; "
        "Embedded default; dependencies pinned"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
