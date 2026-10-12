from __future__ import annotations

import asyncio
import os
import time
from dataclasses import dataclass

import psutil


@dataclass(frozen=True)
class OwnedProcess:
    pid: int
    created_at: float


def owns_process(process: OwnedProcess) -> psutil.Process | None:
    try:
        candidate = psutil.Process(process.pid)
        if abs(candidate.create_time() - process.created_at) < 1:
            return candidate
    except (psutil.AccessDenied, psutil.NoSuchProcess):
        pass
    return None


async def request_graceful_close(process: OwnedProcess) -> bool:
    """只对已验证 PID 发关闭请求；Windows 优先发送 WM_CLOSE。"""
    target = owns_process(process)
    if target is None:
        return False
    if os.name == "nt":
        from .windows import close_windows_for_pid

        if await asyncio.to_thread(close_windows_for_pid, process.pid):
            return True
    with __import__("contextlib").suppress(psutil.NoSuchProcess, psutil.AccessDenied):
        target.terminate()
        return True
    return False


async def process_tree_alive(process: OwnedProcess) -> bool:
    return await asyncio.to_thread(lambda: owns_process(process) is not None)


async def force_stop_process_tree(process: OwnedProcess, wait_seconds: float = 3) -> None:
    """终止创建时间仍匹配的受管 PID 及其子树，绝不按名称搜寻。"""
    target = await asyncio.to_thread(owns_process, process)
    if target is None:
        return
    try:
        targets = target.children(recursive=True) + [target]
    except (psutil.AccessDenied, psutil.NoSuchProcess):
        targets = [target]
    for item in reversed(targets):
        with __import__("contextlib").suppress(psutil.NoSuchProcess, psutil.AccessDenied):
            item.terminate()
    _, living = await asyncio.to_thread(psutil.wait_procs, targets, wait_seconds)
    for item in living:
        with __import__("contextlib").suppress(psutil.NoSuchProcess, psutil.AccessDenied):
            item.kill()
    if living:
        await asyncio.to_thread(psutil.wait_procs, living, wait_seconds)


async def capture_owned_process(pid: int) -> OwnedProcess:
    deadline = time.monotonic() + 2
    while True:
        try:
            target = psutil.Process(pid)
            return OwnedProcess(pid=pid, created_at=target.create_time())
        except psutil.NoSuchProcess:
            if time.monotonic() >= deadline:
                raise RuntimeError("启动后未能取得受管进程")
            await asyncio.sleep(0.02)
