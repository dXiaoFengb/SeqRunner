from __future__ import annotations

import ctypes


def close_windows_for_pid(pid: int) -> bool:
    """向属于指定 PID 的顶层窗口发送 WM_CLOSE。"""
    user32 = ctypes.windll.user32
    found = False
    WM_CLOSE = 0x0010

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def callback(hwnd, _lparam):
        nonlocal found
        owner_pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner_pid))
        if owner_pid.value == pid and user32.IsWindowVisible(hwnd):
            user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
            found = True
        return True

    user32.EnumWindows(callback, 0)
    return found
