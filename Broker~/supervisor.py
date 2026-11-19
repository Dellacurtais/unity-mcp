"""Per-user supervisor that keeps the Unity MCP broker alive and healthy.

Why it exists: the broker used to be a child of one Editor and died with it, so MCP
clients lost their server whenever the Editor closed, crashed or was reopened, and
a hung broker stayed hung until someone closed the Editor by hand.

What it does:

* one supervisor per Windows user (named mutex); a second start exits quietly;
* runs ``main.py`` as a child in persistent mode and restarts it, with backoff,
  when it exits or when ``/health`` stops answering;
* puts itself and the broker in a kill-on-close job object, so nothing it started
  outlives it, while still allowing an Editor launched by the broker to break away;
* runs the orphan cleanup periodically (see orphans.py);
* moves to a newer installation when ``current.json`` changes;
* stops cleanly when ``runtime/supervisor.stop`` appears.

Usage: ``python supervisor.py --detach`` (what Unity runs) or ``python supervisor.py``.
"""
from __future__ import annotations

import argparse
import json
import logging
import logging.handlers
import os
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

SOURCE = Path(__file__).resolve().parent
MUTEX_NAME = "Local\\UnityMcp.Broker.Supervisor.v1"
CREATE_NO_WINDOW = 0x08000000
DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000
ERROR_ALREADY_EXISTS = 183

HEALTH_INTERVAL = 5.0
HEALTH_TIMEOUT = 3.0
HEALTH_FAILURES_BEFORE_RESTART = 4
STARTUP_GRACE = 45.0
BACKOFF = (1, 2, 5, 10, 30, 60)
ORPHAN_INTERVAL = 10 * 60
INSTALL_CHECK_INTERVAL = 30.0


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def broker_root() -> Path:
    override = os.getenv("UNITY_MCP_BROKER_ROOT", "").strip()
    if override:
        return Path(override)
    base = os.getenv("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "UnityMcp" / "Broker"


def atomic_write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{uuid4().hex}.tmp")
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(temporary, path)


def read_json(path: Path) -> dict | None:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def process_start_utc(pid: int) -> str | None:
    if sys.platform != "win32":
        return None
    import ctypes
    from ctypes import wintypes
    from datetime import timedelta

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    handle = kernel32.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel32.GetProcessTimes(handle, *(ctypes.byref(item) for item in times)):
            return None
        ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        return (datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=ticks / 10)).isoformat()
    finally:
        kernel32.CloseHandle(handle)


class SingleInstance:
    def __init__(self, name: str = MUTEX_NAME) -> None:
        self.handle = None
        self.acquired = True
        if sys.platform != "win32":
            return
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateMutexW.restype = ctypes.c_void_p
        self._kernel32 = kernel32
        self.handle = kernel32.CreateMutexW(None, False, name)
        self.acquired = bool(self.handle) and ctypes.get_last_error() != ERROR_ALREADY_EXISTS

    def release(self) -> None:
        if self.handle:
            self._kernel32.CloseHandle(ctypes_handle(self.handle))
            self.handle = None
            self.acquired = False


def ctypes_handle(value):
    import ctypes

    return ctypes.c_void_p(value)


class KillOnCloseJob:
    """Job object holding the supervisor and its children; closing it ends them all."""

    def __init__(self) -> None:
        self.handle = None
        if sys.platform != "win32":
            return
        import ctypes
        from ctypes import wintypes

        class BasicLimit(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class IoCounters(ctypes.Structure):
            _fields_ = [(name, ctypes.c_uint64) for name in (
                "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class ExtendedLimit(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", BasicLimit), ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t),
            ]

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        kernel32.GetCurrentProcess.restype = wintypes.HANDLE
        kernel32.GetCurrentProcess.argtypes = []
        kernel32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        kernel32.SetInformationJobObject.restype = wintypes.BOOL
        kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        job = kernel32.CreateJobObjectW(None, None)
        if not job:
            return
        info = ExtendedLimit()
        # KILL_ON_JOB_CLOSE | BREAKAWAY_OK: children die with us unless they ask to break away.
        info.BasicLimitInformation.LimitFlags = 0x2000 | 0x0800
        if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):
            kernel32.CloseHandle(job)
            return
        if not kernel32.AssignProcessToJobObject(job, kernel32.GetCurrentProcess()):
            kernel32.CloseHandle(job)
            return
        self.handle = job


def kill_tree(pid: int) -> None:
    """Terminate a process and its descendants (the venv launcher spawns the interpreter)."""
    if pid <= 0:
        return
    if sys.platform != "win32":
        try:
            os.kill(pid, 9)
        except OSError:
            pass
        return
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True,
                   creationflags=CREATE_NO_WINDOW, check=False, timeout=30)


def health(host: str, port: int, timeout: float = HEALTH_TIMEOUT) -> dict | None:
    url = f"http://{'[' + host + ']' if ':' in host else host}:{port}/health"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, method="GET"), timeout=timeout) as response:
            if response.status != 200:
                return None
            payload = json.loads(response.read().decode("utf-8"))
            return payload if payload.get("service") == "unity-mcp-broker" else None
    except (urllib.error.URLError, OSError, ValueError):
        return None


class Supervisor:
    def __init__(self, root: Path, logger: logging.Logger) -> None:
        self.root = root
        self.log = logger
        self.runtime = root / "runtime"
        self.state_path = self.runtime / "supervisor.json"
        self.process_state_path = self.runtime / "broker-process.json"
        self.stop_path = self.runtime / "supervisor.stop"
        self.host = os.getenv("UNITY_MCP_BROKER_HOST", "127.0.0.1")
        self.port = int(os.getenv("UNITY_MCP_BROKER_PORT", "8765"))
        self.child: subprocess.Popen | None = None
        self.child_started = 0.0
        self.child_answered = False
        self.restarts = 0
        self.last_reason = None
        self.started_utc = utc_now()
        self.install_path = str(SOURCE.parent)
        manifest = read_json(root / "current.json") or {}
        self.manifest_install = manifest.get("installPath")
        self.stopping = threading.Event()

    # --- state file -------------------------------------------------------
    def write_state(self, state: str) -> None:
        atomic_write_json(self.state_path, {
            "schemaVersion": 1,
            "state": state,
            "pid": os.getpid(),
            "processStartUtc": process_start_utc(os.getpid()),
            "installPath": self.install_path,
            "host": self.host,
            "port": self.port,
            "childPid": self.child.pid if self.child and self.child.poll() is None else None,
            "restarts": self.restarts,
            "lastRestartReason": self.last_reason,
            "startedUtc": self.started_utc,
            "updatedUtc": utc_now(),
        })

    # --- child --------------------------------------------------------------
    def child_environment(self, instance_id: str) -> dict[str, str]:
        environment = dict(os.environ)
        environment.update({
            "UNITY_MCP_BROKER_HOST": self.host,
            "UNITY_MCP_BROKER_PORT": str(self.port),
            "UNITY_MCP_BROKER_DB": environment.get("UNITY_MCP_BROKER_DB") or str(self.root / "data" / "broker.sqlite3"),
            "UNITY_MCP_BROKER_LOG": environment.get("UNITY_MCP_BROKER_LOG") or str(self.root / "logs" / "broker.log"),
            "UNITY_MCP_BROKER_STATE": str(self.process_state_path),
            "UNITY_MCP_BROKER_INSTANCE_ID": instance_id,
            "UNITY_MCP_BROKER_PARENT_PID": str(os.getpid()),
            "UNITY_MCP_BROKER_PERSISTENT": "1",
        })
        # The token the broker launches with: the user variable wins, so clients and broker agree.
        try:
            from auth import read_user_environment_token

            user_token = read_user_environment_token("UNITY_MCP_BROKER_TOKEN")
        except Exception:
            user_token = None
        if user_token:
            environment["UNITY_MCP_BROKER_TOKEN"] = user_token
        environment.pop("UNITY_MCP_BROKER_PARENT_START_UTC", None)
        return environment

    def start_child(self) -> None:
        instance_id = uuid4().hex
        atomic_write_json(self.process_state_path, {
            "schemaVersion": 1, "state": "starting", "instanceId": instance_id,
            "host": self.host, "port": self.port, "installPath": self.install_path,
            "logPath": str(self.root / "logs" / "broker.log"),
            "ownerPid": os.getpid(), "ownerStartUtc": process_start_utc(os.getpid()),
            "supervised": True, "startedUtc": utc_now(), "updatedUtc": utc_now(),
        })
        stderr_path = self.root / "logs" / "broker.stderr.log"
        stderr_path.parent.mkdir(parents=True, exist_ok=True)
        stream = open(stderr_path, "a", encoding="utf-8")
        try:
            self.child = subprocess.Popen(
                [sys.executable, str(SOURCE / "main.py")], cwd=str(SOURCE),
                env=self.child_environment(instance_id),
                stdin=subprocess.DEVNULL, stdout=stream, stderr=stream,
                creationflags=CREATE_NO_WINDOW if sys.platform == "win32" else 0,
            )
        finally:
            stream.close()
        self.child_started = time.monotonic()
        self.child_answered = False
        state = read_json(self.process_state_path) or {}
        if state.get("instanceId") == instance_id:
            state["pid"] = self.child.pid
            state["processStartUtc"] = process_start_utc(self.child.pid)
            state["updatedUtc"] = utc_now()
            atomic_write_json(self.process_state_path, state)
        self.log.info("broker started pid=%s instance=%s", self.child.pid, instance_id[:8])
        self.write_state("running")

    def stop_child(self, reason: str) -> None:
        if self.child is None:
            return
        if self.child.poll() is None:
            self.log.info("stopping broker pid=%s: %s", self.child.pid, reason)
            kill_tree(self.child.pid)
            try:
                self.child.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.log.warning("broker pid=%s did not exit after kill", self.child.pid)
        self.child = None

    # --- loops ----------------------------------------------------------------
    def orphan_loop(self) -> None:
        mode = os.getenv("UNITY_MCP_BROKER_ORPHAN_CLEANUP", "apply").strip().lower()
        if mode == "off":
            return
        if self.stopping.wait(60):
            return
        while not self.stopping.is_set():
            try:
                import orphans

                protect = [os.getpid()]
                if self.child is not None:
                    protect.append(self.child.pid)
                summary = orphans.run_cleanup(apply=mode == "apply", root=self.root, extra_protect=protect,
                                              log_path=self.root / "logs" / "orphans.log")
                if summary["wouldClose"]:
                    self.log.info("orphan cleanup (%s): would close %s, closed %s",
                                  mode, summary["wouldClose"], summary["closed"])
            except Exception as exception:  # cleanup must never take the supervisor down
                self.log.warning("orphan cleanup failed: %s", exception)
            if self.stopping.wait(ORPHAN_INTERVAL):
                break

    def newer_installation(self) -> str | None:
        manifest = read_json(self.root / "current.json") or {}
        install = manifest.get("installPath")
        if not isinstance(install, str) or not install:
            return None
        if os.path.normcase(os.path.abspath(install)) == os.path.normcase(os.path.abspath(self.install_path)):
            return None
        supervisor = Path(install) / "source" / "supervisor.py"
        python = manifest.get("pythonPath")
        return install if supervisor.is_file() and isinstance(python, str) and Path(python).is_file() else None

    def run(self) -> int:
        # A stop request left behind by an earlier supervisor must not stop this one.
        try:
            self.stop_path.unlink()
        except OSError:
            pass
        self.write_state("starting")
        threading.Thread(target=self.orphan_loop, name="orphans", daemon=True).start()
        failures = 0
        backoff_index = 0
        next_health = time.monotonic() + HEALTH_INTERVAL
        next_install_check = time.monotonic() + INSTALL_CHECK_INTERVAL
        existing = health(self.host, self.port, timeout=1.0)
        if existing and existing.get("pid") and existing.get("supervisorPid") not in (None, os.getpid()):
            self.log.info("a broker (pid %s) already answers on %s:%s; waiting for it to go away",
                          existing.get("pid"), self.host, self.port)
        while not self.stopping.is_set():
            if self.stop_path.exists():
                try:
                    self.stop_path.unlink()
                except OSError:
                    pass
                self.log.info("stop requested")
                break
            now = time.monotonic()
            if now >= next_install_check:
                next_install_check = now + INSTALL_CHECK_INTERVAL
                newer = self.newer_installation()
                if newer:
                    self.log.info("a newer installation is current (%s); handing over", newer)
                    self.stop_child("handover to a newer installation")
                    self.write_state("handover")
                    return 75
            if self.child is None or self.child.poll() is not None:
                if self.child is not None:
                    code = self.child.returncode
                    self.last_reason = f"broker exited with code {code}"
                    self.log.warning("%s", self.last_reason)
                    self.child = None
                    self.restarts += 1
                    delay = BACKOFF[min(backoff_index, len(BACKOFF) - 1)]
                    backoff_index += 1
                    self.write_state("restarting")
                    if self.stopping.wait(delay):
                        break
                foreign = health(self.host, self.port, timeout=1.0)
                if foreign and foreign.get("supervisorPid") != os.getpid():
                    # Another broker (an old Editor-owned one) holds the port; do not fight it.
                    self.write_state("waiting_for_port")
                    if self.stopping.wait(10):
                        break
                    continue
                try:
                    self.start_child()
                except OSError as exception:
                    self.last_reason = f"could not start broker: {exception}"
                    self.log.error("%s", self.last_reason)
                    if self.stopping.wait(10):
                        break
                    continue
                failures = 0
                next_health = time.monotonic() + HEALTH_INTERVAL
                continue
            if now >= next_health:
                next_health = now + HEALTH_INTERVAL
                answer = health(self.host, self.port)
                # The venv launcher runs the interpreter as its own child, so match the
                # broker by the supervisor PID it reports rather than by its own PID.
                if answer is not None and answer.get("supervisorPid") == os.getpid():
                    failures = 0
                    self.child_answered = True
                    if now - self.child_started > 300:
                        backoff_index = 0
                # The start-up grace only covers a broker that has not answered yet.
                elif self.child_answered or now - self.child_started > STARTUP_GRACE:
                    failures += 1
                    if failures >= HEALTH_FAILURES_BEFORE_RESTART:
                        self.last_reason = f"health check failed {failures} times in a row"
                        self.log.warning("%s; restarting broker pid=%s", self.last_reason, self.child.pid)
                        self.stop_child(self.last_reason)
                        self.restarts += 1
                        failures = 0
                        continue
            self.stopping.wait(1.0)
        self.stop_child("supervisor stopping")
        self.write_state("stopped")
        return 0


def configure_logging(root: Path) -> logging.Logger:
    logger = logging.getLogger("unity-mcp-supervisor")
    logger.setLevel(logging.INFO)
    path = root / "logs" / "supervisor.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    handler = logging.handlers.RotatingFileHandler(path, maxBytes=2 * 1024 * 1024, backupCount=2, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)
    return logger


def detach() -> int:
    """Relaunch outside the caller's console, process group and (when allowed) job."""
    arguments = [sys.executable, str(Path(__file__).resolve())]
    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    for extra in (CREATE_BREAKAWAY_FROM_JOB, 0):
        try:
            subprocess.Popen(arguments, cwd=str(SOURCE), creationflags=flags | extra,
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             close_fds=True)
            return 0
        except OSError:
            continue
    return 1


def redirect_stdio(root: Path) -> None:
    path = root / "logs" / "supervisor.stderr.log"
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        stream = open(path, "a", encoding="utf-8", buffering=1)
        os.dup2(stream.fileno(), 1)
        os.dup2(stream.fileno(), 2)
        sys.stdout = sys.stderr = stream
    except OSError:
        pass


def main() -> int:
    parser = argparse.ArgumentParser(description="Unity MCP broker supervisor")
    parser.add_argument("--detach", action="store_true", help="start a detached supervisor and return")
    arguments = parser.parse_args()
    if arguments.detach and sys.platform == "win32":
        return detach()
    if str(SOURCE) not in sys.path:
        sys.path.insert(0, str(SOURCE))
    root = broker_root()
    redirect_stdio(root)
    instance = SingleInstance(os.getenv("UNITY_MCP_BROKER_SUPERVISOR_MUTEX", "").strip() or MUTEX_NAME)
    if not instance.acquired:
        return 0
    logger = configure_logging(root)
    job = KillOnCloseJob()
    logger.info("supervisor started pid=%s install=%s job=%s", os.getpid(), SOURCE.parent, bool(job.handle))
    supervisor = Supervisor(root, logger)
    code = supervisor.run()
    if code == 75:
        newer = supervisor.newer_installation()
        manifest = read_json(root / "current.json") or {}
        instance.release()
        if newer and manifest.get("pythonPath"):
            # Break away from our kill-on-close job, or the successor dies when we exit.
            flags = (DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB) if sys.platform == "win32" else 0
            subprocess.Popen([manifest["pythonPath"], str(Path(newer) / "source" / "supervisor.py")],
                             cwd=str(Path(newer) / "source"), creationflags=flags,
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            logger.info("handed over to %s", newer)
        return 0
    logger.info("supervisor stopped")
    return code


if __name__ == "__main__":
    sys.exit(main())
