"""经证据核验的预设元数据；仅转录事实，不包含上游代码或资产。"""
from __future__ import annotations

from pathlib import Path

from .models import LogRule, MatchMode, TaskDefinition


def _rule(pattern: str) -> LogRule:
    return LogRule(pattern=pattern, mode=MatchMode.KEYWORD)


PRESETS: dict[str, dict] = {
    "maa": {
        "label": "MAA",
        "executables": ["MAA.exe"],
        "log": "debug/gui.log",
        "success": [_rule("任务已全部完成！")],
        "failure": [_rule(value) for value in ("任务已完成，但出现错误！", "任务已结束，以下任务出现错误", "未选择任务", "任务出错: 开始唤醒", "已停止", "MaaAssistantArknights GUI exited")],
        "manual": True,
        "evidence": "AUTO-MAS/app/task/MAA/AutoProxy.py（本地只读检索，2026-10-12）",
        "note": "日志成功；成功标记前退出视为失败。",
    },
    "zzz_onedragon": {
        "label": "绝区零一条龙",
        "executables": ["OneDragon-RuntimeLauncher.exe", "OneDragon-Launcher.exe"],
        "arguments": ["--onedragon"],
        "log": ".log/log.txt",
        "success": [_rule("指令[ 一条龙 ] 执行成功")],
        "failure": [_rule(value) for value in ("未找到有效的实例", "请先结束其他运行中的功能 再启动", "运行应用 one_dragon 失败", "指令[ 一条龙 ] 执行失败")],
        "manual": True,
        "evidence": "AUTO-MAS/app/task/ZzzOd/AutoProxy.py（本地只读检索，2026-10-12）",
        "note": "优先 RuntimeLauncher；日志成功，成功标记前退出视为失败。",
    },
    "maaend": {
        "label": "MaaEnd",
        "executables": ["MaaEnd.exe"],
        "manual": True,
        "evidence": "AUTO-MAS/app/task/MaaEnd/AutoProxy.py；https://github.com/MaaEnd/MaaEnd（访问 2026-10-12）",
        "note": "未找到安全的整项会话终态契约，需人工确认。",
    },
    "march7th": {
        "label": "三月七小助手",
        "executables": ["March7th Assistant.exe"],
        "manual": True,
        "evidence": "AUTO-MAS/app/task/HSR/manager.py；https://github.com/moesnow/March7thAssistant/blob/main/app/log_interface.py#L283-L285（访问 2026-10-12）",
        "note": "官方内部退出码不是外部稳定整项契约，需人工确认。",
    },
    "bettergi": {
        "label": "BetterGI",
        "executables": ["BetterGI.exe"],
        "arguments": ["startOneDragon"],
        "manual": True,
        "evidence": "https://www.bettergi.com/feats/command/introduce.html（访问 2026-10-12）",
        "note": "命令启动当前一条龙配置；未声明全局终态，需人工确认。",
    },
}


def public_presets() -> list[dict]:
    return [
        {"id": key, "label": data["label"], "executables": data["executables"], "note": data["note"], "evidence": data["evidence"], "manualRequired": bool(data["manual"])}
        for key, data in PRESETS.items()
    ]


def apply_preset(preset_id: str, executable: str, *, task_id: str, name: str) -> TaskDefinition:
    try:
        preset = PRESETS[preset_id]
    except KeyError as error:
        raise ValueError("未知预设") from error
    selected = Path(executable)
    allowed = {item.casefold() for item in preset["executables"]}
    if selected.name.casefold() not in allowed:
        raise ValueError("所选程序与预设不匹配，请切换为通用程序后再保存")
    log_path = str(selected.parent / preset["log"]) if preset.get("log") else None
    return TaskDefinition(
        id=task_id,
        name=name or preset["label"],
        executable=str(selected),
        arguments=list(preset.get("arguments", [])),
        working_directory=str(selected.parent),
        allow_manual_confirmation=bool(preset["manual"]),
        # 预设无稳定完成日志时禁止把正常退出伪装为成功。
        allow_process_exit=False,
        log_path=log_path,
        preset_id=preset_id,
        preset_evidence=preset["evidence"],
        success_log_rules=list(preset.get("success", [])),
        failure_log_rules=list(preset.get("failure", [])),
    )
