"""Event-loop watchdog for the broker.

The broker is a single asyncio loop. If anything blocks that loop (a synchronous
SQLite call waiting on a lock, a write to a full stdio pipe, a hung native call), the
socket keeps accepting TCP connections but nothing answers, and the parent-exit
watcher, being a coroutine on the same loop, never runs either. This thread runs
outside the loop: it dumps every thread's stack once when the loop stalls (so the
cause lands in the log), and exits the process when the stall lasts long enough that
a restart is better than waiting. The supervisor then starts a fresh broker.
"""
from __future__ import annotations

import asyncio
import faulthandler
import logging
import os
import threading
import time
from collections import deque
from pathlib import Path
from typing import Callable

LOG = logging.getLogger("uvicorn.error").getChild("watchdog")

STALL_EXIT_CODE = 70
PARENT_GONE_EXIT_CODE = 0


class LoopWatchdog:
    def __init__(
        self,
        *,
        dump_after: float = 15.0,
        exit_after: float = 90.0,
        dump_path: Path | None = None,
        parent_alive: Callable[[], bool] | None = None,
        exit_process: Callable[[int], None] = os._exit,
        clock: Callable[[], float] = time.monotonic,
        poll_interval: float = 1.0,
    ) -> None:
        self.dump_after = dump_after
        self.exit_after = exit_after
        self.dump_path = dump_path
        self.parent_alive = parent_alive
        self.exit_process = exit_process
        self.clock = clock
        self.poll_interval = poll_interval
        self.last_pulse = clock()
        self._dumped = False
        self._stopped = threading.Event()
        self._thread: threading.Thread | None = None
        self._recent_lags: deque[tuple[float, float]] = deque(maxlen=600)
        self.stalls = 0

    # --- loop side -------------------------------------------------------
    async def pulse(self, interval: float = 0.5) -> None:
        while True:
            now = self.clock()
            self._recent_lags.append((now, max(0.0, now - self.last_pulse - interval)))
            self.last_pulse = now
            await asyncio.sleep(interval)

    # --- thread side -----------------------------------------------------
    def start(self) -> None:
        if self._thread is not None:
            return
        self.last_pulse = self.clock()
        self._thread = threading.Thread(target=self._run, name="unity-mcp-loop-watchdog", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stopped.set()

    def lag(self) -> float:
        return max(0.0, self.clock() - self.last_pulse)

    def max_recent_lag(self, window_seconds: float = 300.0) -> float:
        cutoff = self.clock() - window_seconds
        return max((lag for at, lag in list(self._recent_lags) if at >= cutoff), default=0.0)

    def _run(self) -> None:
        while not self._stopped.wait(self.poll_interval):
            try:
                self.check_once()
            except Exception:  # never let the watchdog itself die silently
                pass

    def check_once(self) -> str:
        """One watchdog decision; returns what it did (for tests and logs)."""
        lag = self.lag()
        if self.parent_alive is not None and lag > 3.0:
            # The loop's own parent watcher cannot run while the loop is blocked.
            try:
                alive = self.parent_alive()
            except Exception:
                alive = True
            if not alive:
                self._write_line(f"owner process is gone and the loop is blocked for {lag:.1f}s; exiting")
                self.exit_process(PARENT_GONE_EXIT_CODE)
                return "parent_gone"
        if self.dump_after and lag >= self.dump_after and not self._dumped:
            self._dumped = True
            self.stalls += 1
            self._write_line(f"event loop blocked for {lag:.1f}s; dumping all thread stacks")
            self._dump_stacks()
            return "dumped"
        if self.exit_after and lag >= self.exit_after:
            self._write_line(f"event loop blocked for {lag:.1f}s (limit {self.exit_after:.0f}s); exiting so the supervisor restarts the broker")
            self.exit_process(STALL_EXIT_CODE)
            return "exited"
        if lag < self.dump_after and self._dumped:
            self._dumped = False
            self._write_line(f"event loop recovered after a stall")
            return "recovered"
        return "ok"

    def _write_line(self, message: str) -> None:
        # The logging module may be what is blocked; write the evidence file directly.
        line = f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} WATCHDOG {message}\n"
        if self.dump_path is not None:
            try:
                with open(self.dump_path, "a", encoding="utf-8") as stream:
                    stream.write(line)
            except OSError:
                pass
        try:
            LOG.warning(message)
        except Exception:
            pass

    def _dump_stacks(self) -> None:
        if self.dump_path is None:
            return
        try:
            with open(self.dump_path, "a", encoding="utf-8") as stream:
                faulthandler.dump_traceback(file=stream, all_threads=True)
                stream.write("\n")
        except (OSError, ValueError):
            pass
