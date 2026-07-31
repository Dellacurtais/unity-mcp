from __future__ import annotations

import sys
from dataclasses import dataclass


@dataclass(frozen=True)
class FocusActivation:
    attempted: bool = False
    activated: bool = False
    target_window: int | None = None
    previous_window: int | None = None
    reason: str = "disabled"


class ProcessFocusAssist:
    """Best-effort foreground activation for a stalled local Unity process."""

    def activate(self, process_id: int) -> FocusActivation:
        if sys.platform != "win32":
            return FocusActivation(reason="unsupported_platform")
        if process_id <= 0:
            return FocusActivation(reason="invalid_process")

        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        candidates: list[tuple[int, bool]] = []
        enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        @enum_proc
        def collect(hwnd, _lparam):
            if not user32.IsWindowVisible(hwnd):
                return True
            owner_pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner_pid))
            if owner_pid.value != process_id:
                return True
            length = user32.GetWindowTextLengthW(hwnd)
            title = ctypes.create_unicode_buffer(max(1, length + 1))
            user32.GetWindowTextW(hwnd, title, len(title))
            candidates.append((int(hwnd), "unity" in title.value.lower()))
            return True

        user32.EnumWindows(collect, 0)
        if not candidates:
            return FocusActivation(attempted=True, reason="window_not_found")

        candidates.sort(key=lambda item: item[1], reverse=True)
        target = candidates[0][0]
        previous = int(user32.GetForegroundWindow() or 0) or None
        if previous == target:
            return FocusActivation(True, True, target, previous, "already_foreground")

        SW_RESTORE = 9
        user32.ShowWindowAsync(target, SW_RESTORE)
        user32.BringWindowToTop(target)
        activated = bool(user32.SetForegroundWindow(target))
        if int(user32.GetForegroundWindow() or 0) == target:
            activated = True
        return FocusActivation(True, activated, target, previous, "activated" if activated else "activation_denied")

    def restore(self, activation: FocusActivation | None) -> bool:
        if sys.platform != "win32" or activation is None:
            return False
        if not activation.target_window or not activation.previous_window:
            return False
        if activation.target_window == activation.previous_window:
            return False

        import ctypes

        user32 = ctypes.windll.user32
        if int(user32.GetForegroundWindow() or 0) != activation.target_window:
            return False
        if not user32.IsWindow(activation.previous_window):
            return False
        user32.ShowWindowAsync(activation.previous_window, 9)
        return bool(user32.SetForegroundWindow(activation.previous_window))
