from __future__ import annotations

import ntpath
import os
import re
from dataclasses import dataclass

import psutil

from .models import UiLogRegion
from .processes import OwnedProcess, owns_process


@dataclass(frozen=True)
class WindowCandidate:
    handle: int
    window_class: str
    client_width: int
    client_height: int
    minimized: bool = False


@dataclass(frozen=True)
class SelectableWindow:
    handle: int
    pid: int
    created_at: float
    title: str
    window_class: str
    client_width: int
    client_height: int


def same_window_class(left: str, right: str) -> bool:
    if left == right:
        return True
    # WPF assigns a new GUID on each launch; retain both stable class components.
    pattern = r"^(HwndWrapper\[[^;\]]*;[^;\]]*;)[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\]$"
    a, b = re.fullmatch(pattern, left), re.fullmatch(pattern, right)
    return a is not None and b is not None and a.group(1) == b.group(1)


def system_dpi_client_rect(user32, hwnd, rect) -> bool:
    """Use the same system-DPI context as the selection and OCR helper."""
    import ctypes
    from ctypes import wintypes

    setter = getattr(user32, "SetThreadDpiAwarenessContext", None)
    if setter is None:
        return bool(user32.GetClientRect(hwnd, ctypes.byref(rect)))
    setter.argtypes = (wintypes.HANDLE,)
    setter.restype = wintypes.HANDLE
    previous = setter(wintypes.HANDLE(-2))
    if not previous:
        return False
    try:
        return bool(user32.GetClientRect(hwnd, ctypes.byref(rect)))
    finally:
        setter(previous)


def list_windows_for_executable(executable: str) -> list[SelectableWindow]:
    """只返回完整可执行路径匹配的可见窗口，供用户自行选择。"""
    if os.name != "nt":
        raise RuntimeError("当前系统不支持 Windows 窗口选择")
    if not executable or not ntpath.isabs(executable):
        raise RuntimeError("请先保存软件的完整程序路径")
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = (callback_type, wintypes.LPARAM)
    user32.IsWindowVisible.argtypes = (wintypes.HWND,)
    user32.IsIconic.argtypes = (wintypes.HWND,)
    user32.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
    user32.GetClientRect.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.RECT))
    user32.GetClassNameW.argtypes = (wintypes.HWND, wintypes.LPWSTR, ctypes.c_int)
    user32.GetWindowTextW.argtypes = (wintypes.HWND, wintypes.LPWSTR, ctypes.c_int)
    wanted = ntpath.normcase(ntpath.normpath(executable))
    windows: list[SelectableWindow] = []

    @callback_type
    def visit(hwnd, _):
        if not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        try:
            process = psutil.Process(pid.value)
            if ntpath.normcase(ntpath.normpath(process.exe())) != wanted:
                return True
            created = process.create_time()
        except (psutil.Error, OSError):
            return True
        rect, name, title = wintypes.RECT(), ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(512)
        if not system_dpi_client_rect(user32, hwnd, rect) or not user32.GetClassNameW(hwnd, name, len(name)):
            return True
        user32.GetWindowTextW(hwnd, title, len(title))
        if rect.right > 0 and rect.bottom > 0 and title.value:
            windows.append(SelectableWindow(int(hwnd), pid.value, created, title.value, name.value,
                                            rect.right, rect.bottom))
        return True

    if not user32.EnumWindows(visit, 0):
        raise RuntimeError("无法枚举软件窗口")
    return windows


def select_owned_candidate(region: UiLogRegion, executable: str, candidates: list[WindowCandidate]) -> WindowCandidate:
    matches = [candidate for candidate in candidates if same_window_class(candidate.window_class, region.window_class)]
    if len(matches) != 1:
        raise RuntimeError("未能唯一定位本轮受管程序窗口，请重新框选界面日志区域")
    candidate = matches[0]
    if candidate.minimized:
        raise RuntimeError("目标窗口已最小化，界面日志读取已暂停")
    reason = check_region_window(
        region, executable=executable, window_class=candidate.window_class,
        client_width=candidate.client_width, client_height=candidate.client_height,
        process_identity_verified=True,
    )
    if reason:
        raise RuntimeError(reason)
    return candidate


def find_owned_region_window(process: OwnedProcess, region: UiLogRegion) -> WindowCandidate:
    """只枚举创建时间仍匹配的受管 PID 所拥有的可见顶层窗口。"""
    if os.name != "nt":
        raise RuntimeError("当前系统不支持 Windows 界面日志采集")
    target = owns_process(process)
    if target is None:
        raise RuntimeError("无法确认窗口属于本轮受管程序")
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
    user32.IsWindowVisible.argtypes = (wintypes.HWND,)
    user32.IsIconic.argtypes = (wintypes.HWND,)
    user32.GetClientRect.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.RECT))
    user32.GetClassNameW.argtypes = (wintypes.HWND, wintypes.LPWSTR, ctypes.c_int)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = (callback_type, wintypes.LPARAM)
    candidates: list[WindowCandidate] = []

    @callback_type
    def collect(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != process.pid or not user32.IsWindowVisible(hwnd):
            return True
        rect = wintypes.RECT()
        class_name = ctypes.create_unicode_buffer(256)
        if system_dpi_client_rect(user32, hwnd, rect) and user32.GetClassNameW(hwnd, class_name, len(class_name)):
            candidates.append(WindowCandidate(int(hwnd), class_name.value, rect.right - rect.left,
                                              rect.bottom - rect.top, bool(user32.IsIconic(hwnd))))
        return True

    if not user32.EnumWindows(collect, 0):
        raise RuntimeError("无法枚举受管窗口")
    try:
        executable = target.exe()
    except Exception as error:
        raise RuntimeError("无法验证受管程序路径") from error
    if owns_process(process) is None:
        raise RuntimeError("受管进程身份已变化")
    return select_owned_candidate(region, executable, candidates)


def check_region_window(
    region: UiLogRegion,
    *,
    executable: str,
    window_class: str,
    client_width: int,
    client_height: int,
    process_identity_verified: bool,
) -> str | None:
    """检查窗口是否仍与用户确认的区域一致；不根据进程名称寻找窗口。"""
    if not process_identity_verified:
        return "无法确认窗口属于本轮受管程序"
    if ntpath.normcase(ntpath.normpath(executable)) != ntpath.normcase(ntpath.normpath(region.executable)):
        return "程序路径已改变，请重新框选界面日志区域"
    if not same_window_class(window_class, region.window_class):
        return "窗口类型已改变，请重新框选界面日志区域"
    if (client_width, client_height) != (region.client_width, region.client_height):
        return "窗口客户区尺寸已改变，请重新框选界面日志区域"
    return None
