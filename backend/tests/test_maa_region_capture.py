import asyncio
import os
import subprocess
from pathlib import Path

import pytest

from app.detail_logs import DetailLogs
from app.maa_region_capture import read_maa_region, run_helper
from app.models import ItemRecord, UiLogRegion
from app.processes import capture_owned_process
from app.ui_log_region import list_windows_for_executable
from app.ui_logs import WindowLogCapture, ocr_appended


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows 受控窗口")
@pytest.mark.asyncio
async def test_select_region_on_controlled_window(monkeypatch):
    import ctypes
    from ctypes import wintypes

    import psutil

    source = (Path(__file__).parent / "fixtures" / "ui_log_window.ps1").read_text(encoding="utf-8")
    source = source.replace("$window.Left = -2000", "$window.Left = 120").replace("$window.Top = -2000", "$window.Top = 120")
    create = asyncio.create_subprocess_exec
    target = await create("powershell.exe", "-NoProfile", "-STA", "-Command", source,
                          stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    helper = []
    try:
        owned = await capture_owned_process(target.pid)
        for _ in range(30):
            candidates = await asyncio.to_thread(list_windows_for_executable, psutil.Process(target.pid).exe())
            candidates = [candidate for candidate in candidates if candidate.pid == target.pid]
            if candidates:
                break
            await asyncio.sleep(0.2)
        assert len(candidates) == 1
        candidate = candidates[0]
        region = UiLogRegion(executable=psutil.Process(target.pid).exe(), window_class=candidate.window_class,
                             client_width=candidate.client_width, client_height=candidate.client_height,
                             x=0, y=0, width=candidate.client_width, height=candidate.client_height)

        async def capture_child(*args, **kwargs):
            process = await create(*args, **kwargs)
            helper.append(process)
            return process

        monkeypatch.setattr(asyncio, "create_subprocess_exec", capture_child)
        task = asyncio.create_task(run_helper("select", owned, region))
        for _ in range(40):
            if helper:
                break
            await asyncio.sleep(0.1)
        assert helper
        user32 = ctypes.windll.user32
        callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        handles = []

        @callback_type
        def visit(hwnd, _):
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == helper[0].pid and user32.IsWindowVisible(hwnd):
                handles.append(hwnd)
            return True

        for _ in range(50):
            handles.clear()
            user32.EnumWindows(visit, 0)
            if handles:
                break
            await asyncio.sleep(0.1)
        assert len(handles) == 1
        hwnd = handles[0]
        point = wintypes.POINT(20, 20)
        previous = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(previous))
        user32.ClientToScreen(hwnd, ctypes.byref(point))
        user32.SetCursorPos(point.x, point.y)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        await asyncio.sleep(0.2)
        user32.SetCursorPos(point.x + 100, point.y + 50)
        await asyncio.sleep(0.2)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        user32.SetCursorPos(previous.x, previous.y)
        result = await asyncio.wait_for(task, 10)
        assert result["width"] > 0 and result["height"] > 0
    finally:
        for process in helper + [target]:
            if process.returncode is None:
                process.terminate()
            await process.wait()


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows OCR 和可见的受控窗口")
@pytest.mark.asyncio
async def test_ocr_reads_controlled_window_only(tmp_path):
    source = (Path(__file__).parent / "fixtures" / "ui_log_window.ps1").read_text(encoding="utf-8")
    trigger = (tmp_path / "append-now").as_posix().replace("'", "''")
    source = source.replace("$window.Left = -2000", "$window.Left = 120").replace(
        "$window.Top = -2000", "$window.Top = 120\n$window.Topmost = $true"
    ).replace('$log.Text = "baseline`n"', '$log.FontSize = 32; $log.Text = "baseline`n"').replace(
        "FromSeconds(5)", "FromMilliseconds(200)"
    ).replace(
        '$timer.Add_Tick({ $log.AppendText("new line`n"); $timer.Stop() })',
        f'$timer.Add_Tick({{ if (Test-Path -LiteralPath \'{trigger}\') {{ $log.AppendText("new line`n"); $timer.Stop() }} }})',
    )
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-STA", "-Command", source,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        owned = await capture_owned_process(child.pid)
        import psutil

        for _ in range(30):
            candidates = await asyncio.to_thread(list_windows_for_executable, psutil.Process(child.pid).exe())
            candidates = [candidate for candidate in candidates if candidate.pid == child.pid]
            if candidates:
                break
            await asyncio.sleep(0.2)
        assert len(candidates) == 1
        candidate = candidates[0]
        region = UiLogRegion(executable=psutil.Process(child.pid).exe(), window_class=candidate.window_class,
                             client_width=candidate.client_width, client_height=candidate.client_height,
                             x=0, y=0, width=candidate.client_width, height=candidate.client_height)
        item = ItemRecord(task_id="controlled", task_name="受控窗口")
        output = []

        async def emit(value):
            output.append(value)

        async def reader(process):
            return await read_maa_region(process, region)

        store = DetailLogs(tmp_path)
        capture = WindowLogCapture(store, item, owned, emit, reader, source="画面识别（可能有错字或遗漏）",
                                   matcher=ocr_appended)
        await capture._poll()
        assert "baseline" in (capture.previous or "").lower()
        initial = capture.previous
        assert "newline" not in initial.lower().replace(" ", "")
        trigger_path = tmp_path / "append-now"
        trigger_path.write_text("go", encoding="utf-8")
        await asyncio.sleep(1)
        await capture._poll()
        assert "newline" in "".join(output).lower().replace(" ", ""), (initial, capture.previous, item.ui_log_status)
        assert "baseline" not in "".join(output).lower()
        assert "画面识别" in item.ui_log_status
        assert "newline" in store.page(item.ui_log_id, 0)["entries"][0]["text"].lower().replace(" ", "")
    finally:
        if child.returncode is None:
            child.terminate()
        await child.wait()
