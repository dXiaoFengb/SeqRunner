from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime


@dataclass(frozen=True)
class UpdateEvent:
    kind: str
    detail: str
    logged_at: float | None = None


class MaaUpdateEvidence:
    """Interpret only new, complete MAA GUI log lines; no process ownership is implied."""

    def __init__(self) -> None:
        self._pending = ""
        self.update_observed = False
        self.restart_expected = False
        self.applying = False

    def reset(self) -> None:
        """Start a fresh observation after an independently verified handoff."""
        self._pending = ""
        self.update_observed = False
        self.restart_expected = False
        self.applying = False

    def clear_partial(self) -> None:
        """Discard an incomplete line at rotation while preserving update context."""
        self._pending = ""

    def feed(self, text: str) -> list[UpdateEvent]:
        self._pending += text
        lines = self._pending.split("\n")
        self._pending = lines.pop()
        # A corrupt or non-line-oriented source must not grow memory indefinitely.
        if len(self._pending) > 65536:
            self._pending = self._pending[-65536:]
        events: list[UpdateEvent] = []
        for line in lines:
            event = self._classify(line.rstrip("\r"))
            if event:
                stamp = re.match(r"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?)\]", line)
                if stamp:
                    try:
                        event = UpdateEvent(event.kind, event.detail, datetime.fromisoformat(stamp.group(1)).timestamp())
                    except (ValueError, OverflowError, OSError):
                        pass
                events.append(event)
        return events

    def _classify(self, line: str) -> UpdateEvent | None:
        # Restrict interpretation to the observed logger and exact message: other
        # applications or task/resource updates can use similar words.
        if "[VersionUpdateDialogViewModel]" in line and "New version found:" in line:
            self.update_observed = True
            return UpdateEvent("update_detected", "MAA 检测到软件更新")
        if self.update_observed and ("Delegated pending update failed." in line or "Failed to apply pending update package:" in line):
            return UpdateEvent("update_failed", "MAA 更新失败，需要人工检查")
        if "[Bootstrapper]" in line and "Shutdown and restart with custom StartInfo" in line and self.update_observed:
            self.restart_expected = True
            return UpdateEvent("restart_requested", "MAA 请求更新重启")
        if "[Bootstrapper]" in line and "Pending update package detected, applying before full startup" in line and self.restart_expected:
            self.applying = True
            return UpdateEvent("applying", "MAA 正在应用待处理更新")
        if "[PendingUpdateApplier]" in line and "Delegating pending update apply to external updater" in line and self.restart_expected:
            self.applying = True
            return UpdateEvent("applying", "MAA 更新器已接管")
        if "[Bootstrapper]" in line and "Delegated pending update completed successfully" in line and self.restart_expected:
            self.applying = False
            return UpdateEvent("update_applied", "MAA 更新器报告完成，仍需确认程序状态")
        if "[Bootstrapper]" in line and "MaaAssistantArknights GUI exited" in line:
            return UpdateEvent("expected_exit" if self.restart_expected else "unexpected_exit", "MAA 主界面退出")
        if self.restart_expected and "[Bootstrapper]" in line and "MaaAssistantArknights GUI started" in line:
            return UpdateEvent("gui_started", "检测到 MAA 界面启动，仍需核对更新结果和进程身份")
        if self.restart_expected and "Startup auto-run will be skipped due to --skip-startup-auto-run" in line:
            return UpdateEvent("startup_skipped", "更新重启后软件跳过自动运行，仍需确认任务已启动")
        return None


class MaaUpdaterLogEvidence:
    """Parse updater output as observations only; file contents do not identify a writer."""

    def __init__(self) -> None:
        self._pending = ""

    def clear_partial(self) -> None:
        self._pending = ""

    def feed(self, text: str) -> list[UpdateEvent]:
        lines = (self._pending + text).split("\n")
        self._pending = lines.pop()[-65536:]
        events = []
        for line in lines:
            if "Update failed:" in line:
                events.append(UpdateEvent("updater_failure_observed", "更新器日志出现失败信息，归属待确认，请检查"))
            elif "Wrote success status file:" in line:
                events.append(UpdateEvent("updater_success_observed", "更新器日志出现成功信息，归属待确认"))
            elif "Relaunch succeeded." in line:
                events.append(UpdateEvent("updater_relaunch_observed", "更新器日志报告重新启动，仍需确认进程身份"))
        return events
