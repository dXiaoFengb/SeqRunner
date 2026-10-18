import asyncio
import ctypes
import os
import subprocess
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.models import TaskDefinition, UiLogRegion
from app.processes import OwnedProcess, capture_owned_process
from app.ui_log_region import (
    WindowCandidate,
    check_region_window,
    find_owned_region_window,
    same_window_class,
    select_owned_candidate,
)


def test_wpf_window_guid_changes_only_within_same_class():
    first = "HwndWrapper[MaaWpfGui.exe;Main;12345678-1234-1234-1234-123456789abc]"
    second = "HwndWrapper[MaaWpfGui.exe;Main;aaaaaaaa-1234-1234-1234-123456789abc]"
    other = "HwndWrapper[Other.exe;Main;aaaaaaaa-1234-1234-1234-123456789abc]"
    assert same_window_class(first, second)
    assert not same_window_class(first, other)


def region(**overrides):
    value = {"executable": r"C:\MAA\MAA.exe", "window_class": "MaaWindow", "client_width": 1200,
             "client_height": 800, "x": 800, "y": 40, "width": 350, "height": 700}
    value.update(overrides)
    return UiLogRegion(**value)


def test_region_round_trip_and_legacy_task():
    old = TaskDefinition(name="旧任务")
    assert old.ui_log_region is None
    task = TaskDefinition(name="MAA", ui_log_region=region())
    assert TaskDefinition.model_validate_json(task.model_dump_json()).ui_log_region == task.ui_log_region


@pytest.mark.parametrize("changes", [{"x": -1}, {"width": 0}, {"x": 1000, "width": 300}, {"y": 790, "height": 11}])
def test_region_rejects_outside_client_area(changes):
    with pytest.raises(ValidationError):
        region(**changes)


def test_region_requires_matching_owned_window():
    fields = {"executable": r"c:\maa\maa.exe", "window_class": "MaaWindow", "client_width": 1200,
              "client_height": 800, "process_identity_verified": True}
    assert check_region_window(region(), **fields) is None
    for field, value in [("executable", r"C:\Other\MAA.exe"), ("window_class", "Other"),
                         ("client_width", 1199), ("process_identity_verified", False)]:
        changed = {**fields, field: value}
        assert check_region_window(region(), **changed) is not None


def test_owned_window_selection_rejects_missing_duplicate_minimized_and_resize():
    candidate = WindowCandidate(10, "MaaWindow", 1200, 800)
    executable = r"C:\MAA\MAA.exe"
    assert select_owned_candidate(region(), executable, [candidate]) == candidate
    for candidates in ([], [candidate, candidate], [WindowCandidate(10, "MaaWindow", 1200, 800, True)],
                       [WindowCandidate(10, "MaaWindow", 1199, 800)]):
        with pytest.raises(RuntimeError):
            select_owned_candidate(region(), executable, candidates)


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows 受控窗口")
@pytest.mark.asyncio
async def test_finds_controlled_window_only_for_current_process():
    fixture = Path(__file__).parent / "fixtures" / "ui_log_window.ps1"
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-STA", "-Command", fixture.read_text(encoding="utf-8"),
        stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        owned = await capture_owned_process(child.pid)
        from ctypes import wintypes

        import psutil

        process = psutil.Process(child.pid)
        handles = []
        callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        @callback_type
        def visit(hwnd, _):
            pid = wintypes.DWORD()
            ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == child.pid and ctypes.windll.user32.IsWindowVisible(hwnd):
                handles.append(hwnd)
            return True

        for _ in range(30):
            handles.clear()
            ctypes.windll.user32.EnumWindows(visit, 0)
            if handles:
                break
            await asyncio.sleep(0.2)
        assert len(handles) == 1
        handle = handles[0]
        name = ctypes.create_unicode_buffer(256)
        assert ctypes.windll.user32.GetClassNameW(handle, name, len(name))
        rect = wintypes.RECT()
        assert ctypes.windll.user32.GetClientRect(handle, ctypes.byref(rect))
        configured = region(executable=process.exe(), window_class=name.value,
                            client_width=rect.right, client_height=rect.bottom, x=0, y=0, width=1, height=1)
        assert find_owned_region_window(owned, configured).handle == handle
        with pytest.raises(RuntimeError, match="受管程序"):
            find_owned_region_window(OwnedProcess(owned.pid, owned.created_at - 10), configured)
    finally:
        if child.returncode is None:
            child.terminate()
        await child.wait()
