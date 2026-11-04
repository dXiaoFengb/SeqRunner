from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from .maa_update_evidence import UpdateEvent
from .processes import OwnedProcess

UpdateStage = Literal["normal", "checking", "downloading", "restarting", "applying", "awaiting_process", "needs_attention", "reattached"]


@dataclass(frozen=True)
class UpdateStatus:
    stage: UpdateStage
    message: str


class UpdateSupervisor:
    """Track explicit update evidence; never infer completion from process exit."""

    def __init__(self, *, idle_seconds: float = 600, total_seconds: float = 3600) -> None:
        if idle_seconds <= 0 or total_seconds <= 0:
            raise ValueError("更新等待时间必须大于零")
        self.idle_seconds = idle_seconds
        self.total_seconds = total_seconds
        self.stage: UpdateStage = "normal"
        self.message = ""
        self.started_at: float | None = None
        self.progress_at: float | None = None
        self.update_applied = False

    def observe_event(self, event: UpdateEvent, now: float) -> UpdateStatus:
        if self.stage == "needs_attention":
            return self.status()
        if event.kind == "update_detected":
            if self.started_at is None or self.stage == "reattached":
                self.started_at = now
                self.update_applied = False
            self.stage = "checking"
            self.message = event.detail
        elif event.kind == "update_failed" and self.started_at is not None:
            return self.attention(event.detail)
        elif event.kind == "downloading" and self.started_at is not None:
            self.stage = "downloading"
            self.message = event.detail
        elif event.kind == "restart_requested":
            if self.started_at is None:
                return self.status()
            self.stage = "restarting"
            self.message = event.detail
        elif event.kind == "applying" and self.started_at is not None:
            self.stage = "applying"
            self.message = event.detail
        elif event.kind == "update_applied" and self.started_at is not None:
            self.update_applied = True
            self.stage = "awaiting_process"
            self.message = "更新器报告完成，等待确认重启程序"
        elif event.kind == "expected_exit" and self.started_at is not None:
            self.message = "程序因更新退出，等待更新和重启"
        elif event.kind == "gui_started" and self.started_at is not None:
            self.message = event.detail
        else:
            return self.status()
        self.progress_at = now
        return self.status()

    def progress(self, now: float) -> None:
        if self.started_at is not None and self.stage not in ("needs_attention", "reattached"):
            self.progress_at = now

    def check_deadlines(self, now: float) -> UpdateStatus:
        if self.started_at is None or self.stage in ("needs_attention", "reattached"):
            return self.status()
        if now - self.started_at >= self.total_seconds:
            return self.attention("更新等待超过总时限，请检查后选择继续等待或停止")
        if self.progress_at is not None and now - self.progress_at >= self.idle_seconds:
            return self.attention("更新长时间没有可靠进展，请检查后选择继续等待或停止")
        return self.status()

    def attention(self, message: str) -> UpdateStatus:
        self.stage = "needs_attention"
        self.message = message
        return self.status()

    def continue_waiting(self, now: float) -> UpdateStatus:
        if self.stage != "needs_attention":
            raise ValueError("当前无需人工继续等待")
        self.started_at = self.progress_at = now
        self.stage = "awaiting_process" if self.update_applied else "restarting"
        self.message = "继续等待更新和程序重启"
        return self.status()

    def choose_candidate(self, candidates: list[OwnedProcess]) -> OwnedProcess | None:
        if self.stage != "awaiting_process" or self.started_at is None or not self.update_applied:
            return None
        if len(candidates) > 1:
            self.attention("找到多个可能的重启进程，无法安全确认归属")
            return None
        if len(candidates) != 1:
            return None
        self.stage = "reattached"
        self.message = "已确认重启程序，继续监控任务"
        return candidates[0]

    def status(self) -> UpdateStatus:
        return UpdateStatus(self.stage, self.message)
