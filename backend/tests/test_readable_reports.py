from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.models import CompletionSource, ItemRecord, ItemState, RunRecord, RunState
from app.readable_reports import (
    SCRIPT_TRANSLATION_VERSION,
    append_item_event,
    append_script_events,
    build_run_report,
    note_external_levels,
)


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


def test_translation_reports_unrecognised_script_text_without_persisting_it() -> None:
    item = ItemRecord(task_id="task", task_name="受控程序", state=ItemState.MONITORING)
    note_external_levels(item, "未定义格式的一段第三方文本")
    append_script_events(item, "未定义格式的一段第三方文本")

    assert item.readable_report is not None
    assert item.readable_report.risks == ["检测到 1 条未识别脚本日志；请查看脚本内部日志确认原文"]
    assert item.readable_report.events == []


def test_unknown_info_line_is_counted_but_never_given_a_guessed_chinese_meaning() -> None:
    item = ItemRecord(task_id="task", task_name="受控程序", state=ItemState.MONITORING)
    note_external_levels(item, "[INF][ThirdPartyWorker] opaque business message")
    events = append_script_events(item, "[INF][ThirdPartyWorker] opaque business message")

    assert events == []
    assert item.unrecognized_log_count == 1
    assert item.readable_report is not None
    assert item.readable_report.events == []
    assert "opaque business message" not in str(item.readable_report)


def test_external_script_lines_become_chinese_events_without_storing_raw_text() -> None:
    item = ItemRecord(task_id="task", task_name="受控程序", state=ItemState.MONITORING)
    raw = "[2026-10-25 12:00:00.000][INF][ConfigurationHelper] config saved\n[2026-10-25 12:00:01.000][ERR][Worker] failed\n"
    events = append_script_events(item, raw)
    assert item.script_translation_version == SCRIPT_TRANSLATION_VERSION
    assert [event.severity for event in events] == ["info", "error"]
    assert events[0].message == "脚本正在更新配置。"
    assert "config saved" not in events[0].message
    assert "failed" not in events[1].message
    assert item.readable_report is not None
    assert item.readable_report.events == events


def test_new_translation_enabled_record_keeps_lifecycle_events_out_of_script_stream() -> None:
    item = ItemRecord(task_id="task", task_name="受控程序", state=ItemState.MONITORING, script_translation_version=SCRIPT_TRANSLATION_VERSION)
    append_item_event(item, "开始校验任务配置")
    assert item.readable_events
    assert item.readable_report is not None
    assert item.readable_report.events == []


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


def test_consecutive_script_translations_are_folded_without_retaining_raw_text() -> None:
    item = ItemRecord(task_id="fixture", task_name="受控夹具", state=ItemState.MONITORING)
    changed = append_script_events(
        item,
        "[2026-10-26 12:00:00.000][INF][Bootstrapper] one\n"
        "[2026-10-26 12:00:01.000][INF][Bootstrapper] two\n"
        "[2026-10-26 12:00:02.000][INF][Bootstrapper] three\n"
        "[2026-10-26 12:00:03.000][ERR][Worker] fail\n"
        "[2026-10-26 12:00:04.000][INF][Bootstrapper] after-break\n",
    )

    assert len(item.script_readable_events) == 3
    first, error, final = item.script_readable_events
    assert first.message == "脚本启动器正在初始化。"
    assert first.occurrence_count == 3
    assert first.last_timestamp is not None
    assert first.sequence == 1
    assert error.sequence == 2
    assert final.sequence == 3
    assert len(changed) == 3
    persisted = "\n".join(event.model_dump_json() for event in item.script_readable_events)
    assert not any(raw in persisted for raw in ("one", "two", "three", "after-break"))


def test_consecutive_translation_folding_crosses_polling_blocks_but_unknown_text_breaks_it() -> None:
    item = ItemRecord(task_id="fixture", task_name="受控夹具", state=ItemState.MONITORING)
    append_script_events(item, "[2026-10-26 12:00:00.000][INF][Bootstrapper] first\n")
    updated = append_script_events(item, "[2026-10-26 12:00:01.000][INF][Bootstrapper] second\n")
    assert len(item.script_readable_events) == 1
    assert updated[0].sequence == 1
    assert updated[0].occurrence_count == 2
    append_script_events(item, "unrecognised separator\n")
    append_script_events(item, "[2026-10-26 12:00:02.000][INF][Bootstrapper] third\n")
    assert len(item.script_readable_events) == 2


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
