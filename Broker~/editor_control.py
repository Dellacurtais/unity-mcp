"""Windows helpers the broker uses to look after Unity Editors from the outside.

Everything here works when the Editor cannot answer: listing processes, reading the
dialogs a frozen Editor shows, pressing one named button, launching an Editor that is
not tied to the broker's lifetime and reading the free commit charge. Nothing here
ever terminates an Editor.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

CREATE_NO_WINDOW = 0x08000000
DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000


@dataclass(frozen=True)
class ProcessInfo:
    pid: int
    ppid: int
    name: str
    command_line: str
    executable: str
    created: datetime | None

    @property
    def lower_name(self) -> str:
        return self.name.lower()


def _parse_created(value: Any) -> datetime | None:
    if not value:
        return None
    text = str(value)
    try:
        if text.startswith("/Date("):
            millis = int(text[6:text.index(")")].split("+")[0].split("-")[0])
            return datetime.fromtimestamp(millis / 1000, timezone.utc)
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
        return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)
    except (ValueError, IndexError):
        return None


def snapshot_processes(timeout: float = 30.0) -> list[ProcessInfo]:
    """All processes with command lines (Windows: CIM through PowerShell)."""
    if sys.platform != "win32":
        return []
    script = (
        "$ErrorActionPreference='Stop';"
        "Get-CimInstance Win32_Process | ForEach-Object { [pscustomobject]@{"
        "p=[int]$_.ProcessId; q=[int]$_.ParentProcessId; n=$_.Name; c=$_.CommandLine; e=$_.ExecutablePath;"
        "t=$(if($_.CreationDate){$_.CreationDate.ToUniversalTime().ToString('o')}else{$null}) } }"
        " | ConvertTo-Json -Compress -Depth 2"
    )
    completed = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        capture_output=True, timeout=timeout, creationflags=CREATE_NO_WINDOW, check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError("process snapshot failed: " + completed.stderr.decode("utf-8", "replace")[:300])
    raw = completed.stdout.decode("utf-8", "replace").strip()
    items = json.loads(raw) if raw else []
    if isinstance(items, dict):
        items = [items]
    return [
        ProcessInfo(
            pid=int(item.get("p") or 0), ppid=int(item.get("q") or 0), name=str(item.get("n") or ""),
            command_line=str(item.get("c") or ""), executable=str(item.get("e") or ""),
            created=_parse_created(item.get("t")),
        )
        for item in items if item.get("p")
    ]


def normalize_path(value: str) -> str:
    try:
        return os.path.normcase(os.path.normpath(os.path.abspath(value))).rstrip("\\/")
    except (TypeError, ValueError):
        return ""


def _project_path_argument(command_line: str) -> str | None:
    """The -projectPath value of a Unity command line (quoted or not)."""
    lower = command_line.lower()
    index = lower.find("-projectpath")
    if index < 0:
        return None
    rest = command_line[index + len("-projectpath"):].lstrip()
    if rest.startswith('"'):
        end = rest.find('"', 1)
        return rest[1:end] if end > 0 else rest[1:]
    return rest.split(" ", 1)[0] if rest else None


def is_unity_editor(process: ProcessInfo) -> bool:
    if process.lower_name != "unity.exe":
        return False
    lower_cmd = process.command_line.lower()
    if "assetimportworker" in lower_cmd:
        return False
    return "\\unity hub\\" not in process.executable.lower()


def editors_for_project(project_path: str, processes: list[ProcessInfo]) -> list[ProcessInfo]:
    target = normalize_path(project_path)
    found = []
    for process in processes:
        if not is_unity_editor(process):
            continue
        argument = _project_path_argument(process.command_line)
        if argument and normalize_path(argument) == target:
            found.append(process)
    return found


# --- liveness --------------------------------------------------------------
def process_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if sys.platform != "win32":
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    handle = kernel32.OpenProcess(0x1000 | 0x00100000, False, pid)  # QUERY_LIMITED | SYNCHRONIZE
    if not handle:
        return False
    try:
        return kernel32.WaitForSingleObject(handle, 0) == 0x00000102
    finally:
        kernel32.CloseHandle(handle)


def wait_for_exit(pid: int, timeout: float, poll: float = 0.5) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not process_alive(pid):
            return True
        time.sleep(poll)
    return not process_alive(pid)


def free_commit_bytes() -> int | None:
    """Commit charge still available (what the owner watches as FreeVirtualMemory)."""
    if sys.platform != "win32":
        return None
    import ctypes
    from ctypes import wintypes

    class MemoryStatus(ctypes.Structure):
        _fields_ = [
            ("dwLength", wintypes.DWORD), ("dwMemoryLoad", wintypes.DWORD),
            ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
            ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    status = MemoryStatus()
    status.dwLength = ctypes.sizeof(status)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        return None
    return int(status.ullAvailPageFile)


# --- dialogs ---------------------------------------------------------------
@dataclass
class DialogInfo:
    handle: int
    title: str
    class_name: str
    buttons: list[str] = field(default_factory=list)
    text: list[str] = field(default_factory=list)

    def as_dict(self) -> dict[str, Any]:
        return {"title": self.title, "className": self.class_name, "buttons": self.buttons,
                "text": self.text[:6], "handle": self.handle}


def _window_text(user32, handle) -> str:
    import ctypes

    length = user32.GetWindowTextLengthW(handle)
    buffer = ctypes.create_unicode_buffer(max(1, length + 1))
    user32.GetWindowTextW(handle, buffer, len(buffer))
    return buffer.value


def _class_name(user32, handle) -> str:
    import ctypes

    buffer = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(handle, buffer, 256)
    return buffer.value


def list_dialogs(process_id: int) -> list[DialogInfo]:
    """Visible native dialogs (#32770) owned by a process, with their buttons.

    A modal such as "Script Updating Consent" blocks the Editor's main thread, so no
    tool inside Unity can report it; it is still visible from outside.
    """
    if sys.platform != "win32" or process_id <= 0:
        return []
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    dialogs: list[DialogInfo] = []
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def visit_top(handle, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(handle, ctypes.byref(owner))
        if owner.value != process_id or not user32.IsWindowVisible(handle):
            return True
        class_name = _class_name(user32, handle)
        if class_name != "#32770":
            return True
        dialog = DialogInfo(handle=int(handle), title=_window_text(user32, handle), class_name=class_name)

        def visit_child(child, __):
            child_class = _class_name(user32, child).lower()
            text = _window_text(user32, child).strip()
            if not text or not user32.IsWindowVisible(child):
                return True
            if child_class == "button":
                dialog.buttons.append(text.replace("&", ""))
            elif child_class == "static":
                dialog.text.append(text[:300])
            return True

        user32.EnumChildWindows(handle, enum_proc(visit_child), 0)
        dialogs.append(dialog)
        return True

    user32.EnumWindows(enum_proc(visit_top), 0)
    return dialogs


def press_dialog_button(process_id: int, title: str, button: str) -> dict[str, Any]:
    """Click one button, identified by exact text, on a dialog of exactly this title."""
    if sys.platform != "win32":
        raise RuntimeError("Dialog control is only available on Windows")
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    matches = [dialog for dialog in list_dialogs(process_id) if dialog.title == title]
    if not matches:
        raise LookupError(f"No dialog titled {title!r} is open in process {process_id}")
    dialog = matches[0]
    found = []
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def visit(child, _):
        if _class_name(user32, child).lower() == "button" and _window_text(user32, child).replace("&", "").strip() == button:
            found.append(child)
            return False
        return True

    user32.EnumChildWindows(wintypes.HWND(dialog.handle), enum_proc(visit), 0)
    if not found:
        raise LookupError(f"Dialog {title!r} has no button {button!r}; buttons: {dialog.buttons}")
    BM_CLICK = 0x00F5
    user32.PostMessageW(found[0], BM_CLICK, 0, 0)
    return {"pressed": True, "title": title, "button": button, "processId": process_id}


# --- launching -------------------------------------------------------------
def launch_detached(arguments: list[str], working_directory: str | None = None) -> int:
    """Start a process that is not part of the broker's job object.

    The supervisor keeps the broker in a kill-on-close job; an Editor launched without
    breaking away would die with the next broker restart. Refuse rather than risk it.
    """
    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    if sys.platform == "win32":
        flags |= CREATE_BREAKAWAY_FROM_JOB
    try:
        process = subprocess.Popen(
            arguments, cwd=working_directory, creationflags=flags if sys.platform == "win32" else 0,
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True,
        )
    except OSError as exception:
        if sys.platform == "win32" and getattr(exception, "winerror", None) == 5:
            raise RuntimeError(
                "The broker's job object does not allow breakaway; refusing to start an Editor "
                "that would be closed together with the broker.") from exception
        raise
    return process.pid


def project_looks_valid(project_path: str) -> bool:
    root = Path(project_path)
    return (root / "Assets").is_dir() and (root / "ProjectSettings").is_dir()


def editor_executable_valid(editor_path: str) -> bool:
    path = Path(editor_path)
    return path.is_file() and path.name.lower() == "unity.exe"
