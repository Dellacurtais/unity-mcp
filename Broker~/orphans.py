"""Orphan cleanup for Unity helper processes and stale Unity MCP brokers.

Decisions come from the process tree, never from a name alone:

* An Editor (Unity.exe that is not an AssetImportWorker) is never closed.
* Unity Hub and everything under it (and the licensing client) is never closed.
* A Unity helper (AssetImportWorker, UnityShaderCompiler, bee_backend, ILPP, any
  program inside an Editor's ``Editor\\Data`` folder, ...) is closed only when no live
  Editor is above it in the tree. For an AssetImportWorker the owner named by
  ``-mainProcessId`` must also be gone.
* A broker ``main.py`` is closed only when nothing live is above it (its Editor or
  its supervisor died and its own parent watcher did not get to run).
* The broker that is serving, its supervisor and their children are protected.
* A parent counts only if it was created before the child (Windows reuses PIDs).
* Nothing younger than ``min_age_seconds`` is touched.

``python orphans.py`` only lists; ``python orphans.py --apply`` closes. The owner's
scheduled task "Limpeza de orfaos (Codex e Claude)" protects every command line with
Unity or UnityMcp in it, so the two never act on the same process.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Iterable

if str(Path(__file__).resolve().parent) not in sys.path:
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from editor_control import ProcessInfo, snapshot_processes  # noqa: E402

HELPER_NAMES = {
    "unityshadercompiler.exe", "unitypackagemanager.exe", "bee_backend.exe", "unity.ilpp.runner.exe",
    "unity.ilpp.trigger.exe", "unityautoquitter.exe", "unitycrashhandler64.exe", "unity.compiler.client.exe",
}
NEVER_NAMES = {"unity hub.exe", "unity.licensing.client.exe", "unitylicensingclient.exe"}
PYTHON_NAMES = {"python.exe", "pythonw.exe"}
BROKER_MARKER = "\\unitymcp\\broker\\installations\\"
MAIN_PROCESS_ID = re.compile(r"-mainprocessid\s+(\d+)", re.IGNORECASE)


@dataclass
class Decision:
    pid: int
    name: str
    action: str  # "close" or "keep"
    reason: str
    command_line: str
    created: str | None

    def short(self) -> str:
        command = self.command_line if len(self.command_line) <= 220 else self.command_line[:217] + "..."
        return f"{self.action:5} {self.pid:>6} {self.name}: {self.reason} | {command}"


def _lower(value: str) -> str:
    return (value or "").lower()


def is_hub(process: ProcessInfo) -> bool:
    return process.lower_name in NEVER_NAMES or "\\unity hub\\" in _lower(process.executable)


def is_editor(process: ProcessInfo) -> bool:
    return (process.lower_name == "unity.exe" and "assetimportworker" not in _lower(process.command_line)
            and not is_hub(process))


def is_asset_worker(process: ProcessInfo) -> bool:
    return process.lower_name == "unity.exe" and "assetimportworker" in _lower(process.command_line)


def is_unity_helper(process: ProcessInfo) -> bool:
    if is_hub(process) or is_editor(process):
        return False
    if is_asset_worker(process) or process.lower_name in HELPER_NAMES:
        return True
    executable = _lower(process.executable)
    return "\\editor\\data\\" in executable and ("\\unity\\" in executable or "unity" in executable)


def is_broker_main(process: ProcessInfo) -> bool:
    command = _lower(process.command_line)
    return process.lower_name in PYTHON_NAMES and BROKER_MARKER in command and "main.py" in command


def is_broker_supervisor(process: ProcessInfo) -> bool:
    command = _lower(process.command_line)
    return process.lower_name in PYTHON_NAMES and BROKER_MARKER in command and "supervisor.py" in command


class Tree:
    def __init__(self, processes: Iterable[ProcessInfo]) -> None:
        self.by_pid = {process.pid: process for process in processes}

    def parent(self, process: ProcessInfo) -> ProcessInfo | None:
        if process.ppid <= 0 or process.ppid == process.pid:
            return None
        parent = self.by_pid.get(process.ppid)
        if parent is None:
            return None
        # A "parent" created after the child is an unrelated process that reused the PID.
        if parent.created and process.created and parent.created > process.created:
            return None
        return parent

    def ancestors(self, process: ProcessInfo, limit: int = 64) -> list[ProcessInfo]:
        chain, seen, current = [], {process.pid}, process
        for _ in range(limit):
            parent = self.parent(current)
            if parent is None or parent.pid in seen:
                break
            chain.append(parent)
            seen.add(parent.pid)
            current = parent
        return chain

    def descendants(self, pid: int) -> set[int]:
        children: dict[int, list[int]] = {}
        for process in self.by_pid.values():
            parent = self.parent(process)
            if parent is not None:
                children.setdefault(parent.pid, []).append(process.pid)
        found, stack = set(), [pid]
        while stack:
            for child in children.get(stack.pop(), []):
                if child not in found:
                    found.add(child)
                    stack.append(child)
        return found


def protected_set(tree: Tree, roots: Iterable[int]) -> set[int]:
    protected: set[int] = set()
    for pid in roots:
        process = tree.by_pid.get(pid)
        if process is None:
            continue
        protected.add(pid)
        protected.update(tree.descendants(pid))
        protected.update(ancestor.pid for ancestor in tree.ancestors(process))
    return protected


def plan(
    processes: list[ProcessInfo],
    *,
    now: datetime | None = None,
    protect_pids: Iterable[int] = (),
    min_age_seconds: float = 120.0,
) -> list[Decision]:
    now = now or datetime.now(timezone.utc)
    tree = Tree(processes)
    protected = protected_set(tree, protect_pids)
    decisions: list[Decision] = []

    def decide(process: ProcessInfo, action: str, reason: str) -> None:
        decisions.append(Decision(process.pid, process.name, action, reason, process.command_line,
                                  process.created.isoformat() if process.created else None))

    for process in processes:
        helper = is_unity_helper(process)
        broker = is_broker_main(process)
        if not helper and not broker:
            continue
        if process.pid in protected:
            decide(process, "keep", "protected: serving broker, its supervisor or their children")
            continue
        if process.created is None or (now - process.created).total_seconds() < min_age_seconds:
            decide(process, "keep", "too young to judge")
            continue
        ancestors = tree.ancestors(process)
        if any(is_hub(ancestor) for ancestor in ancestors):
            decide(process, "keep", "under Unity Hub")
            continue
        if helper:
            owner_editor = next((ancestor for ancestor in ancestors if is_editor(ancestor)), None)
            if owner_editor is not None:
                decide(process, "keep", f"in use by Editor {owner_editor.pid}")
                continue
            match = MAIN_PROCESS_ID.search(process.command_line)
            if match:
                owner = tree.by_pid.get(int(match.group(1)))
                if owner is not None and is_editor(owner) and (
                        not owner.created or not process.created or owner.created <= process.created):
                    decide(process, "keep", f"owner Editor {owner.pid} is alive")
                    continue
            decide(process, "close", "Unity helper whose Editor is gone")
            continue
        # broker main.py
        live_owner = next((ancestor for ancestor in ancestors
                           if is_editor(ancestor) or is_broker_supervisor(ancestor)), None)
        if live_owner is not None:
            decide(process, "keep", f"owned by {live_owner.name} {live_owner.pid}")
            continue
        if any(ancestor.lower_name in PYTHON_NAMES and BROKER_MARKER in _lower(ancestor.command_line)
               for ancestor in ancestors):
            # The venv launcher of a broker that is itself still owned: judged at its level.
            decide(process, "keep", "child of a broker launcher")
            continue
        decide(process, "close", "broker whose Editor or supervisor is gone")
    return decisions


# --- installations -----------------------------------------------------------
def plan_installations(root: Path, processes: list[ProcessInfo], keep: int = 2,
                       min_age: timedelta = timedelta(hours=1)) -> list[tuple[Path, str]]:
    """Old broker installations that nothing references and nothing runs from."""
    installations = root / "installations"
    if not installations.is_dir():
        return []
    protected: set[str] = set()
    for state in ("current.json", "runtime/broker-process.json", "runtime/supervisor.json"):
        try:
            payload = json.loads((root / state).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        value = payload.get("installPath") if isinstance(payload, dict) else None
        if isinstance(value, str) and value:
            protected.add(os.path.normcase(os.path.abspath(value)))
    in_use = " ".join(_lower(p.command_line) + " " + _lower(p.executable) for p in processes)
    candidates = sorted((path for path in installations.iterdir() if path.is_dir()),
                        key=lambda path: path.stat().st_mtime, reverse=True)
    results = []
    for index, path in enumerate(candidates):
        normalized = os.path.normcase(os.path.abspath(path))
        if normalized in protected:
            results.append((path, "keep: current installation"))
        elif index < keep:
            results.append((path, "keep: recent"))
        elif normalized.lower() in in_use:
            results.append((path, "keep: a process runs from it"))
        elif datetime.now(timezone.utc) - datetime.fromtimestamp(path.stat().st_mtime, timezone.utc) < min_age:
            results.append((path, "keep: too new"))
        else:
            results.append((path, "remove"))
    return results


# --- applying ------------------------------------------------------------------
def terminate_verified(pid: int, created: datetime | None) -> str:
    """Terminate one process after checking it is still the same one (PID + start time)."""
    if sys.platform != "win32":
        return "skipped: not Windows"
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    handle = kernel32.OpenProcess(0x0001 | 0x1000, False, pid)  # TERMINATE | QUERY_LIMITED_INFORMATION
    if not handle:
        return "gone"
    try:
        creation, exit_time, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not kernel32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time),
                                        ctypes.byref(kernel), ctypes.byref(user)):
            return "skipped: cannot read start time"
        ticks = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        started = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=ticks / 10)
        if created is not None and abs((started - created).total_seconds()) > 2:
            return "skipped: PID now belongs to another process"
        if not kernel32.TerminateProcess(handle, 1):
            return f"failed: error {ctypes.get_last_error()}"
        return "closed"
    finally:
        kernel32.CloseHandle(handle)


def broker_root() -> Path:
    base = os.getenv("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "UnityMcp" / "Broker"


def serving_pids(root: Path) -> list[int]:
    pids = []
    for state in ("runtime/broker-process.json", "runtime/supervisor.json"):
        try:
            payload = json.loads((root / state).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        for key in ("pid", "childPid"):
            value = payload.get(key) if isinstance(payload, dict) else None
            if isinstance(value, int) and value > 0:
                pids.append(value)
    return pids


def run_cleanup(*, apply: bool, root: Path | None = None, extra_protect: Iterable[int] = (),
                log_path: Path | None = None, include_installations: bool = True,
                processes: list[ProcessInfo] | None = None) -> dict[str, object]:
    root = root or broker_root()
    processes = processes if processes is not None else snapshot_processes()
    protect = [os.getpid(), *serving_pids(root), *extra_protect]
    decisions = plan(processes, protect_pids=protect)
    results = []
    for decision in decisions:
        outcome = "listed"
        if decision.action == "close" and apply:
            created = datetime.fromisoformat(decision.created) if decision.created else None
            outcome = terminate_verified(decision.pid, created)
        results.append({**asdict(decision), "outcome": outcome})
    installs = []
    if include_installations:
        for path, verdict in plan_installations(root, processes):
            outcome = "listed"
            if verdict == "remove" and apply:
                try:
                    shutil.rmtree(path)
                    outcome = "removed"
                except OSError as exception:
                    outcome = f"failed: {exception}"
            installs.append({"path": str(path), "verdict": verdict, "outcome": outcome})
    summary = {
        "mode": "apply" if apply else "report",
        "at": datetime.now(timezone.utc).isoformat(),
        "closed": sum(1 for item in results if item["outcome"] == "closed"),
        "wouldClose": sum(1 for item in results if item["action"] == "close"),
        "kept": sum(1 for item in results if item["action"] == "keep"),
        "processes": results,
        "installations": installs,
    }
    if log_path is not None:
        try:
            log_path.parent.mkdir(parents=True, exist_ok=True)
            with open(log_path, "a", encoding="utf-8") as stream:
                stream.write(f"{summary['at']} [{summary['mode']}] close={summary['wouldClose']} "
                             f"closed={summary['closed']} kept={summary['kept']}\n")
                for item in results:
                    if item["action"] == "close":
                        command = item["command_line"][:300]
                        stream.write(f"  {item['outcome']:>10} {item['pid']} {item['name']}: {item['reason']} | {command}\n")
                for item in installs:
                    if item["verdict"] == "remove":
                        stream.write(f"  {item['outcome']:>10} installation {item['path']}\n")
            if log_path.stat().st_size > 2 * 1024 * 1024:
                lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()[-4000:]
                log_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        except OSError:
            pass
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Unity MCP orphan cleanup (lists unless --apply).")
    parser.add_argument("--apply", action="store_true", help="close the orphans and remove stale installations")
    parser.add_argument("--json", action="store_true", help="print the full JSON result")
    parser.add_argument("--all", action="store_true", help="also print the processes that stay")
    arguments = parser.parse_args()
    root = broker_root()
    summary = run_cleanup(apply=arguments.apply, root=root, log_path=root / "logs" / "orphans.log")
    if arguments.json:
        print(json.dumps(summary, indent=2))
        return 0
    for item in summary["processes"]:
        if item["action"] == "close" or arguments.all:
            print(Decision(**{key: item[key] for key in ("pid", "name", "action", "reason", "command_line", "created")}).short()
                  + ("" if item["outcome"] == "listed" else f"  -> {item['outcome']}"))
    for item in summary["installations"]:
        if item["verdict"] == "remove" or arguments.all:
            print(f"install {item['verdict']}: {item['path']}" + ("" if item["outcome"] == "listed" else f"  -> {item['outcome']}"))
    print(f"[{summary['mode']}] would close {summary['wouldClose']}, closed {summary['closed']}, kept {summary['kept']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
