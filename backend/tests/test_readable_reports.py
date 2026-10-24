from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.models import CompletionSource, ItemRecord, ItemState, RunRecord, RunState
from app.readable_reports import append_item_event, build_run_report, note_external_levels


def test_local_report_explains_success_without_changing_terminal_state() -> None:
    item = ItemRecord(task_id="task", task_name="受控程序", state=ItemState.SUCCEEDED, completion_source=CompletionSource.PROCESS_EXIT)
    append_item_event(item, "任务成功，来源：CompletionSource.PROCESS_EXIT", rule_id="completion.process_exit")
    note_external_levels(item, "[ERR] optional startup task failed\n[INF] completed\n")

    assert item.state == ItemState.SUCCEEDED
    assert item.readable_report is not None
    assert item.readable_report.conclusion == "任务已完成"
    assert item.readable_report.completion_basis == "允许的正常退出"
    assert item.readable_report.risks == ["检测到 1 条错误级脚本日志，但未构成终态失败"]
    assert item.readable_events[-1].rule_id == "completion.process_exit"


def test_unknown_external_text_is_counted_without_persisting_text() -> None:
    item = ItemRecord(task_id="task", task_name="受控程序", state=ItemState.MONITORING)
    note_external_levels(item, "未定义格式的一段第三方文本")
    assert item.unrecognized_log_count == 1
    assert item.readable_events == []


def test_run_report_summarises_item_outcomes() -> None:
    successful = ItemRecord(task_id="ok", task_name="成功", state=ItemState.SUCCEEDED)
    failed = ItemRecord(task_id="bad", task_name="失败", state=ItemState.FAILED, error_summary="退出码 1")
    record = RunRecord(queue_id="q", queue_name="队列", trigger_source="manual", state=RunState.COMPLETED, ended_at=datetime.now(UTC), items=[successful, failed])
    report = build_run_report(record)
    assert report.conclusion == "本轮结束，存在失败或未执行项"
    assert report.completion_basis == "请查看任务级报告"


def test_legacy_history_without_report_fields_remains_readable() -> None:
    record = RunRecord.model_validate({
        "queue_id": "legacy", "queue_name": "旧队列", "trigger_source": "manual",
        "items": [{"task_id": "task", "task_name": "旧任务", "state": "succeeded"}],
    })
    assert record.readable_report is None
    assert record.items[0].readable_events == []


@pytest.mark.parametrize("case", json.loads((Path(__file__).parent / "fixtures" / "readable_report_cases.json").read_text(encoding="utf-8")), ids=lambda case: case["name"])
def test_report_fixture_cases_are_deterministic(case: dict[str, object]) -> None:
    item = ItemRecord(
        task_id="fixture",
        task_name="受控夹具",
        state=ItemState(str(case["state"])),
        completion_source=CompletionSource(str(case["completion_source"])) if case.get("completion_source") else None,
        error_summary=str(case["error_summary"]) if case.get("error_summary") else None,
        external_log_status=str(case["external_log_status"]) if case.get("external_log_status") else None,
    )
    append_item_event(item, "开始校验任务配置")
    note_external_levels(item, str(case["raw"]))
    report = item.readable_report
    assert report is not None
    assert report.conclusion == case["conclusion"]
    assert (str(case["risk"]) if case["risk"] else None) in (report.risks or [None])
    assert report.events[-1].event_type == "validation.started"
    assert report.events[-1].rule_id == "lifecycle.validation.started"
    if case.get("unrecognized_count"):
        assert item.unrecognized_log_count == case["unrecognized_count"]
        assert not report.risks
