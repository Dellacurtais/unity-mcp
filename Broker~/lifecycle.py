"""Editor lifecycle tools that live in the broker, outside any Editor.

Closing asks the Editor itself to quit (tool ``editor_quit``), which refuses while a
scene is dirty, a build or compilation runs, or Play Mode is on. The broker never
terminates an Editor. Opening launches the Editor that this project last connected
with, only when no Editor already has the project open. Both are two-step: without
``confirm: true`` they only report what they would do.
"""
from __future__ import annotations

import asyncio
import logging
import os
from typing import TYPE_CHECKING, Any
from uuid import uuid4

import editor_control
import orphans

if TYPE_CHECKING:
    from mcp import McpBroker

LOG = logging.getLogger("uvicorn.error").getChild("lifecycle")
GIB = 1024 ** 3
MIN_FREE_COMMIT_BYTES = 3 * GIB
OPEN_TIMEOUT_SECONDS = 15 * 60


class LifecycleError(RuntimeError):
    def __init__(self, code: str, message: str, data: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.data = data


class EditorLifecycle:
    def __init__(self, broker: "McpBroker") -> None:
        self.broker = broker
        self.router = broker.router
        self.store = broker.store
        self._tasks: set[asyncio.Task[Any]] = set()

    # --- helpers ---------------------------------------------------------------
    def _spawn(self, coroutine) -> None:
        task = asyncio.create_task(coroutine)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    def launch_info(self, project_key: str | None) -> dict[str, Any]:
        if not project_key:
            return {}
        info = dict(self.store.get_meta(f"launch:{project_key}") or {})
        for project in self.store.list_projects():
            if project.get("project_key") == project_key:
                info.setdefault("projectPath", project.get("project_path"))
                info.setdefault("projectName", project.get("project_name"))
        return info

    def resolve_project(self, project_key: str | None, project_path: str | None) -> str | None:
        if project_key:
            return project_key
        if project_path:
            target = editor_control.normalize_path(project_path)
            for project in self.store.list_projects():
                if editor_control.normalize_path(str(project.get("project_path") or "")) == target:
                    return str(project["project_key"])
            return None
        return self.router.active_project_key

    async def call_unity(self, project_key: str, tool: str, arguments: dict[str, Any], timeout: float = 30.0) -> dict[str, Any]:
        request_id = f"lifecycle:{uuid4().hex}"
        response = await self.router.dispatch(request_id, {
            "jsonrpc": "2.0", "id": request_id, "method": "tools/call",
            "params": {"name": tool, "arguments": arguments},
        }, timeout=timeout, project_key=project_key)
        if "error" in response:
            error = response["error"] or {}
            raise LifecycleError("UNITY_TOOL_ERROR", str(error.get("message") or error), error.get("data"))
        result = response.get("result") or {}
        structured = result.get("structuredContent") if isinstance(result.get("structuredContent"), dict) else {}
        if result.get("isError"):
            code = str(structured.get("code") or structured.get("error") or "UNITY_TOOL_ERROR")
            message = str(structured.get("message") or next(
                (item.get("text") for item in result.get("content") or [] if isinstance(item, dict)), "Unity refused"))
            raise LifecycleError(code, message, structured)
        return structured

    def _update(self, job_id: str, state: str, *, progress: float | None = None, message: str | None = None,
                result: Any = None, error: str | None = None) -> None:
        self.store.update_job(job_id, state, progress=progress, message=message, result=result, error=error)
        self.router.notify_job_changed(job_id)

    async def _short_wait(self, job_id: str, seconds: float) -> dict[str, Any]:
        deadline = asyncio.get_running_loop().time() + seconds
        job = self.store.get_job(job_id) or {}
        while job.get("state") not in {"completed", "failed", "cancelled"}:
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                break
            job = await self.router.wait_job(job_id, remaining) or job
        return job

    # --- status ------------------------------------------------------------------
    async def status(self, project_key: str | None = None) -> dict[str, Any]:
        processes: list[editor_control.ProcessInfo] = []
        snapshot_error = None
        try:
            processes = await asyncio.to_thread(editor_control.snapshot_processes)
        except Exception as exception:  # a status call must still answer
            snapshot_error = str(exception)
        projects = []
        for project in self.store.list_projects():
            key = str(project.get("project_key"))
            if project_key and key != project_key:
                continue
            info = self.launch_info(key)
            connection = self.router.connections.get(key)
            liveness = self.router.connection_status(key)
            path = str(info.get("projectPath") or project.get("project_path") or "")
            editors = editor_control.editors_for_project(path, processes) if path else []
            pids = sorted({process.pid for process in editors} | ({connection.identity.process_id} if connection else set()))
            dialogs = []
            for pid in pids:
                dialogs.extend(dialog.as_dict() | {"processId": pid} for dialog in editor_control.list_dialogs(pid))
            projects.append({
                "projectKey": key,
                "projectName": project.get("project_name"),
                "projectPath": path,
                "active": key == self.router.active_project_key,
                "connected": connection is not None,
                **liveness,
                "editorProcessIds": pids,
                "editorRunning": bool(pids),
                "editorPath": info.get("editorPath"),
                "unityVersion": info.get("unityVersion"),
                "dialogs": dialogs,
                "lastSeenUtc": project.get("last_seen_utc"),
                "canOpen": not pids and bool(info.get("editorPath")),
            })
        free = editor_control.free_commit_bytes()
        return {
            "projects": projects,
            "freeCommitGb": round(free / GIB, 1) if free is not None else None,
            "processSnapshotError": snapshot_error,
            "hint": "A dialog listed here blocks the Editor's main thread; broker_press_dialog_button presses one named button (ask the owner first).",
        }

    # --- close ---------------------------------------------------------------------
    async def close(self, project_key: str | None, *, confirm: bool, save_dirty_scenes: bool,
                    timeout_seconds: float) -> dict[str, Any]:
        key = self.resolve_project(project_key, None)
        if not key:
            raise LifecycleError("PROJECT_NOT_FOUND", "No Unity project is selected or known.")
        try:
            connection = self.router.connection_for(key)
        except Exception as exception:
            status = await self.status(key)
            raise LifecycleError("UNITY_OFFLINE", f"The Editor cannot be asked to quit: {exception}", status)
        pid = connection.identity.process_id
        arguments = {"dryRun": True, "saveDirtyScenes": save_dirty_scenes}
        try:
            preview = await self.call_unity(key, "editor_quit", arguments)
        except LifecycleError as exception:
            if exception.code in {"TOOL_NOT_FOUND", "UNITY_TOOL_ERROR"} and "editor_quit" in str(exception):
                raise LifecycleError("UNSUPPORTED", "This Editor's package has no editor_quit tool; update the package.")
            raise
        if not confirm:
            return {"confirmRequired": True, "preview": preview, "projectKey": key, "processId": pid,
                    "message": "Nothing was closed. Call again with confirm=true to quit this Editor."}
        if not preview.get("canQuit", False):
            raise LifecycleError("QUIT_REFUSED", str(preview.get("reason") or "The Editor cannot quit now."), preview)
        result = await self.call_unity(key, "editor_quit", {"confirm": True, "saveDirtyScenes": save_dirty_scenes})
        job = self.store.create_job("editor_close", key, "waiting for the Editor to exit")
        job_id = job["job_id"]
        self._update(job_id, "running", progress=0.3, message="Editor is quitting", result={"unity": result, "processId": pid})
        LOG.info("editor close requested project=%s pid=%s", key, pid)

        async def watch() -> None:
            exited = await asyncio.to_thread(editor_control.wait_for_exit, pid, timeout_seconds)
            if exited:
                self._update(job_id, "completed", progress=1, message="Editor exited",
                             result={"unity": result, "processId": pid, "exited": True})
            else:
                dialogs = [dialog.as_dict() for dialog in editor_control.list_dialogs(pid)]
                self._update(job_id, "failed", progress=1, message="Editor did not exit in time",
                             error=f"The Editor (pid {pid}) is still running after {timeout_seconds:.0f} s.",
                             result={"unity": result, "processId": pid, "exited": False, "dialogs": dialogs})

        self._spawn(watch())
        return {"job": await self._short_wait(job_id, min(30.0, self.broker.config.sync_wait_seconds - 5)),
                "jobId": job_id, "next": {"tool": "broker_wait_job", "arguments": {"jobId": job_id}}}

    # --- open ------------------------------------------------------------------------
    async def open(self, project_key: str | None, project_path: str | None, *, confirm: bool) -> dict[str, Any]:
        key = self.resolve_project(project_key, project_path)
        if not key:
            raise LifecycleError("PROJECT_NOT_FOUND",
                                 "Only a project that connected to this broker before can be opened.")
        info = self.launch_info(key)
        editor_path = str(info.get("editorPath") or "")
        path = str(info.get("projectPath") or "")
        if not editor_control.editor_executable_valid(editor_path):
            raise LifecycleError("EDITOR_UNKNOWN", "The Editor executable for this project is unknown or missing; "
                                 "open the project once with an updated package.", {"editorPath": editor_path})
        if not editor_control.project_looks_valid(path):
            raise LifecycleError("PROJECT_MISSING", f"{path} does not look like a Unity project.")
        processes = await asyncio.to_thread(editor_control.snapshot_processes)
        running = editor_control.editors_for_project(path, processes)
        if running:
            return {"alreadyRunning": True, "projectKey": key, "processIds": [item.pid for item in running]}
        free = editor_control.free_commit_bytes()
        preview = {"projectKey": key, "projectPath": path, "editorPath": editor_path,
                   "unityVersion": info.get("unityVersion"),
                   "freeCommitGb": round(free / GIB, 1) if free is not None else None}
        if not confirm:
            return {"confirmRequired": True, "preview": preview,
                    "message": "Nothing was started. Call again with confirm=true to open the Editor."}
        if free is not None and free < MIN_FREE_COMMIT_BYTES:
            raise LifecycleError("LOW_MEMORY", f"Only {free / GIB:.1f} GB of commit charge is free; "
                                 "close something before opening the Editor.", preview)
        pid = await asyncio.to_thread(editor_control.launch_detached, [editor_path, "-projectPath", path], path)
        job = self.store.create_job("editor_open", key, "Editor starting")
        job_id = job["job_id"]
        self._update(job_id, "running", progress=0.1, message="Editor starting", result={**preview, "processId": pid})
        LOG.info("editor open requested project=%s pid=%s", key, pid)
        self._spawn(self._watch_open(job_id, key, pid, preview))
        return {"job": await self._short_wait(job_id, 10.0), "jobId": job_id,
                "next": {"tool": "broker_wait_job", "arguments": {"jobId": job_id}}}

    async def _watch_open(self, job_id: str, key: str, pid: int, preview: dict[str, Any]) -> None:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + OPEN_TIMEOUT_SECONDS
        while loop.time() < deadline:
            connection = await self.router.wait_for_connection(key, 5.0)
            if connection is not None:
                self._update(job_id, "completed", progress=1, message="Editor connected",
                             result={**preview, "processId": connection.identity.process_id,
                                     "generationId": connection.identity.generation_id})
                return
            if not editor_control.process_alive(pid):
                # The launcher may hand over to another process (Hub, license); look by path.
                processes = await asyncio.to_thread(editor_control.snapshot_processes)
                if not editor_control.editors_for_project(str(preview.get("projectPath")), processes):
                    self._update(job_id, "failed", progress=1, message="Editor exited before connecting",
                                 error="The Editor process ended before the MCP bridge connected.")
                    return
            dialogs = [dialog.as_dict() for dialog in editor_control.list_dialogs(pid)]
            self._update(job_id, "running", progress=0.5, message="waiting for the MCP bridge",
                         result={**preview, "processId": pid, "dialogs": dialogs})
        self._update(job_id, "failed", progress=1, message="Editor did not connect in time",
                     error=f"No MCP bridge after {OPEN_TIMEOUT_SECONDS // 60} minutes.")

    # --- restart -------------------------------------------------------------------
    async def restart(self, project_key: str | None, *, confirm: bool, save_dirty_scenes: bool) -> dict[str, Any]:
        key = self.resolve_project(project_key, None)
        closing = await self.close(key, confirm=confirm, save_dirty_scenes=save_dirty_scenes, timeout_seconds=180)
        if not confirm:
            return {"confirmRequired": True, "close": closing,
                    "message": "Nothing was restarted. Call again with confirm=true."}
        close_job = closing["jobId"]
        job = self.store.create_job("editor_restart", key, "closing the Editor")
        job_id = job["job_id"]
        self._update(job_id, "running", progress=0.1, message="closing the Editor", result={"closeJobId": close_job})

        async def run() -> None:
            closed = self.store.get_job(close_job) or {}
            while closed.get("state") not in {"completed", "failed", "cancelled"}:
                closed = await self.router.wait_job(close_job, 30) or closed
            if closed.get("state") != "completed":
                self._update(job_id, "failed", progress=1, message="the Editor did not close",
                             error=str(closed.get("error") or "close failed"), result={"close": closed})
                return
            self._update(job_id, "running", progress=0.5, message="opening the Editor")
            await asyncio.sleep(3)
            try:
                opening = await self.open(key, None, confirm=True)
            except LifecycleError as exception:
                self._update(job_id, "failed", progress=1, message="the Editor did not reopen",
                             error=f"{exception.code}: {exception}", result={"close": closed, "data": exception.data})
                return
            open_job = opening.get("jobId")
            if not open_job:
                self._update(job_id, "completed", progress=1, message="Editor already running", result=opening)
                return
            opened = self.store.get_job(open_job) or {}
            while opened.get("state") not in {"completed", "failed", "cancelled"}:
                opened = await self.router.wait_job(open_job, 30) or opened
            self._update(job_id, "completed" if opened.get("state") == "completed" else "failed", progress=1,
                         message=str(opened.get("message")), error=opened.get("error"),
                         result={"close": closed, "open": opened})

        self._spawn(run())
        return {"jobId": job_id, "job": self.store.get_job(job_id),
                "next": {"tool": "broker_wait_job", "arguments": {"jobId": job_id}}}

    # --- dialogs ---------------------------------------------------------------------
    async def press_dialog_button(self, project_key: str | None, title: str, button: str, *, confirm: bool) -> dict[str, Any]:
        key = self.resolve_project(project_key, None)
        status = await self.status(key)
        project = next(iter(status["projects"]), None)
        if project is None:
            raise LifecycleError("PROJECT_NOT_FOUND", "No such Unity project.")
        dialog = next((item for item in project["dialogs"] if item["title"] == title), None)
        if dialog is None:
            raise LifecycleError("DIALOG_NOT_FOUND", f"No dialog titled {title!r} is open.", project["dialogs"])
        if button not in dialog["buttons"]:
            raise LifecycleError("BUTTON_NOT_FOUND", f"The dialog has no button {button!r}.", dialog)
        if not confirm:
            return {"confirmRequired": True, "dialog": dialog,
                    "message": "Nothing was pressed. Call again with confirm=true (only with the owner's answer)."}
        LOG.warning("pressing dialog button project=%s title=%r button=%r", key, title, button)
        return await asyncio.to_thread(editor_control.press_dialog_button, int(dialog["processId"]), title, button)

    # --- cleanup ---------------------------------------------------------------------
    async def cleanup(self, *, apply: bool, verbose: bool) -> dict[str, Any]:
        root = orphans.broker_root()
        summary = await asyncio.to_thread(
            orphans.run_cleanup, apply=apply, root=root,
            extra_protect=[os.getpid(), os.getppid(), int(os.getenv("UNITY_MCP_BROKER_PARENT_PID", "0") or 0)],
            log_path=root / "logs" / "orphans.log")
        if not verbose:
            summary["processes"] = [item for item in summary["processes"] if item["action"] == "close"]
            summary["installations"] = [item for item in summary["installations"] if item["verdict"] == "remove"]
        return summary
