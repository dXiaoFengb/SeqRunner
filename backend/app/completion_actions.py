from __future__ import annotations

import asyncio
import os

from .models import CompletionAction


class CompletionActionDispatcher:
    """Windows 完成后操作的最小受控入口；测试可注入替身，绝不拼接用户输入。"""

    async def execute(self, action: CompletionAction) -> None:
        if action in {CompletionAction.NONE, CompletionAction.EXIT_APP}:
            return
        if os.name != "nt":
            raise RuntimeError("完成后系统操作仅支持 Windows")
        command = {
            CompletionAction.SHUTDOWN: ("shutdown.exe", "/s", "/t", "0"),
            CompletionAction.FORCE_SHUTDOWN: ("shutdown.exe", "/s", "/f", "/t", "0"),
            CompletionAction.RESTART: ("shutdown.exe", "/r", "/t", "0"),
            CompletionAction.HIBERNATE: ("shutdown.exe", "/h"),
            CompletionAction.SLEEP: ("rundll32.exe", "powrprof.dll,SetSuspendState", "0,1,0"),
            CompletionAction.LOGOFF: ("shutdown.exe", "/l"),
        }.get(action)
        if command is None:
            raise RuntimeError(f"未知完成后操作：{action.value}")
        process = await asyncio.create_subprocess_exec(*command)
        code = await process.wait()
        if code != 0:
            raise RuntimeError(f"系统操作退出码 {code}")
