from __future__ import annotations

import asyncio
import base64
import json
import os
from collections.abc import Awaitable, Callable
from difflib import SequenceMatcher
from uuid import uuid4

from .detail_logs import DetailLogs
from .models import ItemRecord
from .processes import OwnedProcess, owns_process

# 三月七官方源码为日志页面和悬浮日志正文分别设置对象名。仅在 UIA
# 公开标识、元素唯一且属于本轮受管进程时读取。
_QUERY = r'''
param([int]$TargetPid, [double]$Started)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = Get-Process -Id $TargetPid -ErrorAction Stop
$actual = ([DateTimeOffset]$p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
if ([Math]::Abs($actual - $Started) -ge 1) { throw '受管进程身份已变化' }
$cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'logInterface')
if ($p.MainWindowHandle -ne 0) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $pages = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($pages.Count -gt 1) { throw '日志页面不唯一' }
    if ($pages.Count -eq 1) {
        if ($pages.Item(0).Current.ProcessId -ne $TargetPid) { throw '日志页面不属于本轮受管进程' }
        $editCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        $edits = $pages.Item(0).FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
        if ($edits.Count -ne 1 -or $edits.Item(0).Current.ProcessId -ne $TargetPid) { throw '日志控件不唯一或不属于本轮受管进程' }
        $pattern = $edits.Item(0).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if (-not $pattern.Current.IsReadOnly) { throw '脚本日志控件并非只读' }
        @{text = $pattern.Current.Value} | ConvertTo-Json -Compress
        return
    }
}
$pidCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$overlayCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'gameLogOverlayBody')
$matches = @()
foreach ($window in $windows) {
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $overlayCond)
    foreach ($element in $elements) { if ($element.Current.ProcessId -eq $TargetPid) { $matches += $element } }
}
if ($matches.Count -ne 1) { throw '未能唯一定位本轮受管日志悬浮窗' }
if ($matches[0].Current.ControlType -ne [System.Windows.Automation.ControlType]::Text) { throw '悬浮日志正文不是文本控件' }
@{text = $matches[0].Current.Name} | ConvertTo-Json -Compress
'''


async def read_march_window(process: OwnedProcess) -> str:
    if os.name != "nt" or not owns_process(process):
        raise RuntimeError("受管窗口不可用")
    # 参数经数值转换后拼接，不包含用户提供的脚本路径或文本。
    code = f"& {{ {_QUERY} }} -TargetPid {int(process.pid)} -Started {float(process.created_at):.6f}"
    encoded = base64.b64encode(code.encode("utf-16-le")).decode("ascii")
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0),
    )
    try:
        output, _ = await asyncio.wait_for(child.communicate(), 5)
    except (TimeoutError, asyncio.CancelledError):
        child.kill()
        await child.wait()
        raise
    if child.returncode or not owns_process(process):
        raise RuntimeError("未能唯一读取本轮受管窗口的日志控件")
    value = json.loads(output.decode("utf-8-sig"))
    if not isinstance(value, dict) or not isinstance(value.get("text"), str):
        raise TypeError("脚本日志控件返回了无效内容")
    return value["text"]


def appended(previous: str, current: str) -> str | None:
    """仅在快照的重叠部分可确认时返回新增内容。"""
    if current.startswith(previous):
        return current[len(previous):]
    if not current:
        return ""
    # 控件可能自动丢弃旧行；仅使用完整行的重叠。
    old_lines, new_lines = previous.splitlines(keepends=True), current.splitlines(keepends=True)
    for size in range(min(len(old_lines), len(new_lines)), 0, -1):
        if old_lines[-size:] == new_lines[:size]:
            return "".join(new_lines[size:])
    return None


def ocr_appended(previous: str, current: str) -> str | None:
    if current.startswith(previous):
        return current[len(previous):]
    if not current:
        return ""
    old_lines = previous.splitlines()
    new_lines = current.splitlines(keepends=True)
    candidates = []
    for size in range(1, min(len(old_lines), len(new_lines)) + 1):
        pairs = zip(old_lines[-size:], new_lines[:size], strict=True)
        if all(len(a.strip()) >= 6 and len(b.strip()) >= 6 and
               SequenceMatcher(None, a.strip(), b.strip(), autojunk=False).ratio() >= 0.9 for a, b in pairs):
            candidates.append(size)
    if len(candidates) != 1:
        return None
    size = candidates[0]
    overlap = old_lines[-size:]
    if sum(old_lines[index:index + size] == overlap for index in range(len(old_lines) - size + 1)) != 1:
        return None
    return "".join(new_lines[size:])


class WindowLogCapture:
    def __init__(self, store: DetailLogs, item: ItemRecord, process: OwnedProcess,
                 emit: Callable[[str], Awaitable[None]],
                 reader: Callable[[OwnedProcess], Awaitable[str]] = read_march_window,
                 notify: Callable[[], Awaitable[None]] | None = None,
                 source: str = "脚本界面日志",
                 matcher: Callable[[str, str], str | None] = appended) -> None:
        self.store, self.item, self.process, self.emit, self.reader = store, item, process, emit, reader
        self.notify = notify
        self.source = source
        self.matcher = matcher
        self.previous: str | None = None
        self.interrupted = False
        self.discontinuous = False
        self.stopped = asyncio.Event()
        self.task: asyncio.Task | None = None

    def start(self) -> None:
        self.item.ui_log_status = f"正在定位{self.source}，首次读取前的内容可能遗漏"
        self.task = asyncio.create_task(self._loop())

    async def _poll(self) -> None:
        old_status = self.item.ui_log_status
        try:
            current = await self.reader(self.process)
            if self.previous is None:
                self.previous = current
                if self.interrupted:
                    self.item.ui_log_status = f"已重新连接{self.source}；读取中断期间的内容可能遗漏"
                elif self.discontinuous:
                    self.item.ui_log_status = f"已连接{self.source}；日志改写期间的内容可能遗漏"
                else:
                    self.item.ui_log_status = f"已连接{self.source}；首次读取前的内容可能遗漏"
                return
            if self.previous and not current:
                self.previous = None
                self.discontinuous = True
                self.item.ui_log_status = f"{self.source}已清空，清空期间的内容可能遗漏"
                return
            delta = self.matcher(self.previous, current)
            approximate = self.matcher is ocr_appended and appended(self.previous, current) is None and delta is not None
            self.previous = current
            if delta is None:
                self.discontinuous = True
                self.item.ui_log_status = f"{self.source}发生清空或改写，无法确认期间的内容"
                return
            if approximate:
                self.discontinuous = True
                self.item.ui_log_status = f"{self.source}采用近似对齐，可能有错字、重复或遗漏"
            if delta:
                if not self.item.ui_log_id:
                    self.item.ui_log_id = str(uuid4())
                for offset in range(0, len(delta), 4096):
                    await asyncio.to_thread(self.store.append, self.item.ui_log_id, delta[offset:offset + 4096])
                await self.emit(delta)
        except (OSError, ValueError, TypeError, RuntimeError, TimeoutError) as error:
            if self.previous is not None:
                self.interrupted = True
            detail = str(error) if isinstance(error, RuntimeError) and str(error) else "读取失败"
            self.item.ui_log_status = f"{self.source}读取暂停：{detail}；界面日志可能不完整"
            self.previous = None
        finally:
            if self.notify and self.item.ui_log_status != old_status:
                await self.notify()

    async def _loop(self) -> None:
        while not self.stopped.is_set():
            await self._poll()
            try:
                await asyncio.wait_for(self.stopped.wait(), 1)
            except TimeoutError:
                pass

    async def finish(self) -> None:
        self.stopped.set()
        if self.task:
            self.task.cancel()
            with __import__("contextlib").suppress(asyncio.CancelledError):
                await self.task
