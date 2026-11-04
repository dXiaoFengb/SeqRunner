from __future__ import annotations

from dataclasses import dataclass

from .maa_update_evidence import UpdateEvent


@dataclass(frozen=True)
class UpdateRules:
    """Explicit literal signals for a single software's update log."""

    checking: str
    downloading: str
    applying: str
    restarting: str
    failed: str
    recovered: str

    def __post_init__(self) -> None:
        values = (self.checking, self.downloading, self.applying, self.restarting, self.failed, self.recovered)
        if any(not value.strip() or len(value) > 512 for value in values):
            raise ValueError("更新规则必须包含六个非空信号，每项不超过 512 字")
        if len(set(values)) != len(values):
            raise ValueError("更新规则信号不能完全相同")


class UpdateRuleReader:
    """Decode complete incremental lines without retaining whole logs."""

    def __init__(self, rules: UpdateRules) -> None:
        self.rules = rules
        self._pending = ""

    def reset(self) -> None:
        self._pending = ""

    def feed(self, text: str) -> list[UpdateEvent]:
        lines = (self._pending + text).split("\n")
        self._pending = lines.pop()[-65536:]
        events: list[UpdateEvent] = []
        signals = (
            ("update_failed", self.rules.failed, "更新失败"),
            ("update_detected", self.rules.checking, "检查更新"),
            ("downloading", self.rules.downloading, "下载更新"),
            ("applying", self.rules.applying, "应用更新"),
            ("restart_requested", self.rules.restarting, "请求重启"),
            ("update_applied", self.rules.recovered, "更新完成，等待确认程序状态"),
        )
        for line in lines:
            # A failure must not be masked by a success signal in the same line.
            match = next(((kind, detail) for kind, pattern, detail in signals if pattern in line), None)
            if match:
                events.append(UpdateEvent(*match))
        return events
