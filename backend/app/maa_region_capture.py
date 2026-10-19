from __future__ import annotations

import asyncio
import base64
import json
import os
import subprocess
from pathlib import Path

from .models import UiLogRegion
from .processes import OwnedProcess, owns_process
from .ui_log_region import find_owned_region_window


async def run_helper(mode: str, process: OwnedProcess, region: UiLogRegion) -> dict:
    if os.name != "nt":
        raise RuntimeError("当前系统不支持 Windows 界面日志采集")
    candidate = await asyncio.to_thread(find_owned_region_window, process, region)
    script = Path(__file__).with_name("maa_window_helper.ps1")
    script_text = script.read_text(encoding="utf-8-sig")
    # Arguments are fixed modes and validated numeric values, never user-provided PowerShell.
    if mode not in ("ocr", "select", "frame"):
        raise ValueError("无效的界面日志操作")
    command = (f"try {{ & {{ {script_text} }} -Mode '{mode}' -WindowHandle {candidate.handle} "
               f"-TargetPid {process.pid} -Started {process.created_at:.6f} "
               f"-ClientWidth {region.client_width} -ClientHeight {region.client_height} "
               f"-X {region.x} -Y {region.y} -Width {region.width} -Height {region.height} "
               "} catch { @{ error = $_.Exception.Message } | ConvertTo-Json -Compress; exit 1 }")
    encoded = base64.b64encode(command.encode("utf-16-le")).decode("ascii")
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", encoded,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    try:
        stdout, stderr = await asyncio.wait_for(child.communicate(), timeout=120 if mode == "select" else 15)
    except (TimeoutError, asyncio.CancelledError):
        child.kill()
        await child.wait()
        raise
    if not owns_process(process):
        raise RuntimeError("目标进程身份已变化")
    current = await asyncio.to_thread(find_owned_region_window, process, region)
    if current.handle != candidate.handle:
        raise RuntimeError("目标窗口已变化，请重新选择")
    if child.returncode:
        try:
            raw = str(json.loads(stdout.decode("utf-8-sig")).get("error", ""))
        except (ValueError, UnicodeError, AttributeError):
            raw = stderr.decode("utf-8", errors="replace")
        reasons = ("目标窗口不可见或已最小化", "日志区域已失效", "日志区域被遮挡或位于屏幕外",
                   "识别期间日志区域被遮挡", "Windows 中文 OCR 不可用", "目标窗口身份已变化",
                   "窗口客户区尺寸已改变，请重新框选", "识别期间目标窗口位置或尺寸已改变")
        raise RuntimeError(next((reason for reason in reasons if reason in raw), "窗口选择或本地 OCR 失败"))
    try:
        result = json.loads(stdout.decode("utf-8-sig"))
    except (ValueError, UnicodeError) as error:
        raise RuntimeError("窗口选择或本地 OCR 返回无效结果") from error
    if not isinstance(result, dict):
        raise TypeError("窗口选择或本地 OCR 返回无效结果")
    return result


async def read_maa_region(process: OwnedProcess, region: UiLogRegion) -> str:
    result = await run_helper("ocr", process, region)
    if not isinstance(result.get("text"), str):
        raise TypeError("本地 OCR 返回了无效内容")
    return result["text"] + "\n" if result["text"] else ""
