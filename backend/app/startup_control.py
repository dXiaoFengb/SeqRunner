from __future__ import annotations

import asyncio
import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path

from .processes import OwnedProcess, owns_process


@dataclass(frozen=True)
class ControlSelector:
    window_title: str
    automation_id: str = ""
    control_name: str = ""

    def __post_init__(self) -> None:
        if not self.window_title.strip() or not (self.automation_id.strip() or self.control_name.strip()):
            raise ValueError("必须指定窗口标题和至少一个控件标识")


@dataclass(frozen=True)
class ControlSnapshot:
    process: OwnedProcess
    window_title: str
    automation_id: str
    control_name: str
    control_type: str
    enabled: bool
    visible: bool
    invoke_supported: bool


def select_startup_control(
    owner: OwnedProcess, selector: ControlSelector, controls: list[ControlSnapshot],
) -> ControlSnapshot:
    """Find one exact user-selected control; selection does not perform an action."""
    matches = [control for control in controls if (
        control.process == owner
        and control.window_title == selector.window_title
        and (not selector.automation_id or control.automation_id == selector.automation_id)
        and (not selector.control_name or control.control_name == selector.control_name)
        and control.control_type == "Button"
    )]
    if len(matches) != 1:
        raise RuntimeError("未能唯一定位已配置的受管控件，需要人工处理")
    control = matches[0]
    if not control.enabled or not control.visible or not control.invoke_supported:
        raise RuntimeError("已配置控件不可用或不支持安全调用，需要人工处理")
    return control


async def query_uia(owner: OwnedProcess, selector: ControlSelector, *, invoke: bool = False) -> dict:
    """Use UIA only for an identity-verified process; selection never executes shell text."""
    if os.name != "nt" or owns_process(owner) is None:
        raise RuntimeError("无法确认受管 Windows 进程")
    request = {"pid": owner.pid, "created_at": owner.created_at, "invoke": invoke,
               "window_title": selector.window_title, "automation_id": selector.automation_id,
               "control_name": selector.control_name}
    arguments = [sys.executable, "--uia-helper"] if getattr(sys, "frozen", False) else [sys.executable, "-m", "app.startup_uia_native"]
    child = await asyncio.create_subprocess_exec(
        *arguments, cwd=str(Path(__file__).resolve().parent.parent), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0),
    )
    try:
        output, _ = await asyncio.wait_for(child.communicate(json.dumps(request, ensure_ascii=False).encode("utf-8")), 8)
    except (TimeoutError, asyncio.CancelledError):
        child.kill()
        await child.wait()
        raise
    if child.returncode or owns_process(owner) is None:
        raise RuntimeError("无法唯一识别或操作受管控件，请人工处理")
    try:
        result = json.loads(output.decode("utf-8-sig"))
    except (UnicodeError, ValueError) as error:
        raise RuntimeError("控件助手返回无效结果") from error
    if not isinstance(result, dict) or (invoke and result.get("invoked") is not True):
        raise RuntimeError("控件助手未确认操作")
    return result
