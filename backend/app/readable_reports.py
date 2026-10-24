"""Deterministic, local-only summaries for a runner's own lifecycle facts.

This module deliberately never receives full external log bodies.  External
readers may report bounded severity counters, while the original text remains
at its authorised source.
"""

from __future__ import annotations

from collections import Counter
from datetime import UTC
from typing import Protocol

from .models import (
    CompletionSource,
    ItemRecord,
    ItemState,
    ReadableEvent,
    ReadableReport,
    RunRecord,
    RunState,
)

REPORT_GENERATOR_VERSION = "local-rules-v1"


class ReadableReportProvider(Protocol):
    """Replaceable, local report boundary; providers never decide run state."""

    generator_version: str

    def append_item_event(self, item: ItemRecord, message: str, *, rule_id: str | None = None) -> None: ...

    def note_external_levels(self, item: ItemRecord, text: str) -> None: ...

    def build_item_report(self, item: ItemRecord) -> ReadableReport: ...

    def build_run_report(self, record: RunRecord) -> ReadableReport: ...


def event_severity(state: ItemState, message: str) -> str:
    if state in {ItemState.FAILED, ItemState.STOPPED} or "失败" in message or "超时" in message:
        return "error"
    if "未配置" in message or "未发现" in message or "仍等待" in message:
        return "warning"
    return "info"


def event_category(state: ItemState) -> str:
    return {
        ItemState.VALIDATING: "validation",
        ItemState.STARTING: "start",
        ItemState.MONITORING: "monitoring",
        ItemState.WAITING_MANUAL: "confirmation",
        ItemState.CLOSING: "cleanup",
        ItemState.FORCE_CLOSING: "cleanup",
        ItemState.COOLING_DOWN: "cooldown",
        ItemState.SUCCEEDED: "completed",
        ItemState.FAILED: "failed",
        ItemState.STOPPED: "stopped",
    }.get(state, "info")


def event_type(state: ItemState, message: str) -> str:
    if "开始校验" in message:
        return "validation.started"
    if "启动" in message:
        return "launch.started"
    if "人工确认" in message:
        return "manual.confirmation"
    if "静默" in message or "超时" in message:
        return "watchdog.timeout"
    if "冷却" in message:
        return "cooldown"
    if "停止" in message:
        return "run.stopped"
    if state == ItemState.SUCCEEDED or "成功" in message or "完成" in message:
        return "completion"
    if state == ItemState.FAILED or "失败" in message:
        return "failure"
    return f"{event_category(state)}.progress"


def append_item_event(item: ItemRecord, message: str, *, rule_id: str | None = None) -> None:
    """Add a concise event produced by the runner, never an external log line."""
    item.readable_events.append(
        ReadableEvent(
            task_id=item.task_id,
            task_name=item.task_name,
            phase=event_category(item.state),
            severity=event_severity(item.state, message),
            event_type=event_type(item.state, message),
            message=message,
            rule_id=rule_id or f"lifecycle.{event_type(item.state, message)}",
        )
    )
    item.readable_events = item.readable_events[-80:]
    item.readable_report = build_item_report(item)


def count_external_levels(text: str) -> dict[str, int]:
    """Count common level tokens without retaining or interpreting raw content."""
    upper = text.upper()
    markers = {
        "error": ("[ERR]", "[ERROR]"),
        "warning": ("[WRN]", "[WARN]", "[WARNING]"),
        "info": ("[INF]", "[INFO]"),
    }
    return {level: sum(upper.count(marker) for marker in values) for level, values in markers.items()}


def note_external_levels(item: ItemRecord, text: str) -> None:
    counts = count_external_levels(text)
    if not any(counts.values()):
        item.unrecognized_log_count += 1
        return
    for level, count in counts.items():
        if count:
            item.external_log_level_counts[level] = item.external_log_level_counts.get(level, 0) + count
    item.readable_report = build_item_report(item)


def completion_basis(item: ItemRecord) -> str:
    if item.completion_source == CompletionSource.MANUAL:
        return "人工确认"
    if item.completion_source == CompletionSource.SUCCESS_LOG:
        return "成功规则命中"
    if item.completion_source == CompletionSource.PROCESS_EXIT:
        return "允许的正常退出"
    if item.error_summary:
        return item.error_summary
    return "等待运行结果"


def item_conclusion(item: ItemRecord) -> str:
    if item.state == ItemState.SUCCEEDED:
        return "任务已完成"
    if item.state == ItemState.FAILED:
        return "任务未完成"
    if item.state == ItemState.STOPPED:
        return "任务已停止"
    if item.state == ItemState.WAITING_MANUAL:
        return "正在等待人工确认"
    if item.state == ItemState.VALIDATING:
        return "正在校验任务配置"
    if item.state == ItemState.STARTING:
        return "正在启动程序"
    if item.state == ItemState.COOLING_DOWN:
        return "正在冷却，稍后继续"
    return "正在监控程序运行"


def build_item_report(item: ItemRecord) -> ReadableReport:
    errors = item.external_log_level_counts.get("error", 0)
    warnings = item.external_log_level_counts.get("warning", 0)
    risks: list[str] = []
    if errors:
        if item.state == ItemState.SUCCEEDED:
            risks.append(f"检测到 {errors} 条错误级脚本日志，但未构成终态失败")
        else:
            risks.append(f"检测到 {errors} 条错误级脚本日志")
    if warnings:
        risks.append(f"检测到 {warnings} 条警告级脚本日志")
    if item.external_log_status and "未发现" in item.external_log_status:
        risks.append("运行期间未发现匹配的脚本内部日志")
    return ReadableReport(
        generator_version=REPORT_GENERATOR_VERSION,
        conclusion=item_conclusion(item),
        completion_basis=completion_basis(item),
        risk_count=len(risks),
        risks=risks,
        events=list(item.readable_events),
    )


def build_run_report(record: RunRecord) -> ReadableReport:
    states = Counter(item.state for item in record.items)
    if record.state == RunState.STOPPED:
        conclusion = "本轮已停止"
        basis = "用户停止请求"
    elif record.state == RunState.COMPLETED and record.items and states[ItemState.SUCCEEDED] == len(record.items):
        conclusion = "本轮全部完成"
        basis = "所有任务均获得完成依据"
    elif record.state == RunState.COMPLETED:
        conclusion = "本轮结束，存在失败或未执行项"
        basis = "请查看任务级报告"
    else:
        conclusion = "本轮正在运行"
        basis = "等待终态仲裁"
    duration = None
    if record.ended_at:
        # RunRecord predates UTC defaults and may contain a local naive start.
        duration = max(0, int((record.ended_at - record.started_at.astimezone(UTC)).total_seconds()))
    risks = [risk for item in record.items for risk in (item.readable_report.risks if item.readable_report else [])]
    return ReadableReport(
        generator_version=REPORT_GENERATOR_VERSION,
        conclusion=conclusion,
        completion_basis=basis,
        duration_seconds=duration,
        risk_count=len(risks),
        risks=risks[:12],
        events=[event for item in record.items for event in item.readable_events][-160:],
    )


class LocalRuleReportProvider:
    """The sole enabled provider: deterministic rules and no network access."""

    generator_version = REPORT_GENERATOR_VERSION

    append_item_event = staticmethod(append_item_event)
    note_external_levels = staticmethod(note_external_levels)
    build_item_report = staticmethod(build_item_report)
    build_run_report = staticmethod(build_run_report)


local_report_provider: ReadableReportProvider = LocalRuleReportProvider()
