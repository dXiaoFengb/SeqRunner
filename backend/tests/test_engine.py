from __future__ import annotations

import asyncio
import sys
from pathlib import Path

import pytest

from app.engine import ConfirmRejectedError, RunnerEngine, RunValidationError
from app.events import EventHub
from app.models import (
    AppConfig,
    CompletionSource,
    ItemRecord,
    ItemState,
    LogRule,
    MatchMode,
    QueueDefinition,
    RunRecord,
    TaskDefinition,
)
from app.readable_reports import append_script_events
from app.storage import JsonStore


def build_engine(tmp_path: Path, tasks: list[TaskDefinition]) -> tuple[RunnerEngine, QueueDefinition]:
    queues = [QueueDefinition(name=task.name, task_ids=[task.id]) for task in tasks]
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=queues, tasks=tasks))
    return RunnerEngine(store, EventHub(), cooldown_seconds=0.01, close_grace_seconds=0.05), queues[0]


def python_task(name: str, source: str, *args: str) -> TaskDefinition:
    return TaskDefinition(name=name, executable=sys.executable, arguments=["-c", source, *args])


@pytest.mark.asyncio
async def test_existing_maa_is_reported_without_starting_or_stopping_it(tmp_path: Path, monkeypatch) -> None:
    from app.processes import executable_running

    task = python_task("MAA", "pass")
    task.preset_id = "maa"
    engine, _ = build_engine(tmp_path, [task])
    item = ItemRecord(task_id=task.id, task_name=task.name)
    record = RunRecord(queue_id="q", queue_name="q", trigger_source="manual", items=[item])
    assert executable_running(sys.executable)
    async def forbidden(*args, **kwargs):
        pytest.fail("不应启动或停止已有进程")
    monkeypatch.setattr("app.engine.asyncio.create_subprocess_exec", forbidden)
    await engine._run_item_inner(record, item, task, has_next=False)
    assert "程序已在运行，请先自行关闭后再运行" in (item.error_summary or "")


@pytest.mark.asyncio
async def test_process_exit_runs_tasks_in_strict_order(tmp_path: Path) -> None:
    output = tmp_path / "order.txt"
    source = "from pathlib import Path; import sys; Path(sys.argv[1]).open('a').write(sys.argv[2] + '\\n')"
    first = python_task("第一项", source, str(output), "first")
    second = python_task("第二项", source, str(output), "second")
    engine, _ = build_engine(tmp_path, [first, second])

    await engine.start_all()
    await engine.wait_until_idle()

    assert output.read_text(encoding="utf-8").splitlines() == ["first", "second"]
    run = engine.history.runs[0]
    entries = engine.detail_logs.page(run.run_log_id, 0)["entries"]
    messages = [entry["text"] for entry in entries]
    assert messages[0] == "本轮开始运行"
    assert any("第一项：开始校验任务配置" in message for message in messages)
    assert "全部成功" in messages[-1] and "总耗时" in messages[-1]
    assert [item.completion_source for item in run.items] == [
        CompletionSource.PROCESS_EXIT,
        CompletionSource.PROCESS_EXIT,
    ]


@pytest.mark.asyncio
async def test_report_generation_failure_does_not_block_history_save(tmp_path: Path, monkeypatch) -> None:
    task = python_task("报告回退", "pass")
    engine, queue = build_engine(tmp_path, [task])

    def reject_report(*_args, **_kwargs) -> None:
        raise RuntimeError("受控报告生成失败")

    monkeypatch.setattr("app.engine.local_report_provider.build_run_report", reject_report)
    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert engine.history.runs[0].items[0].state.value == "succeeded"
    assert engine.history.runs[0].readable_report is None


@pytest.mark.asyncio
async def test_success_log_locks_success_before_process_exit(tmp_path: Path) -> None:
    log = tmp_path / "target.log"
    source = "from pathlib import Path; import sys,time; Path(sys.argv[1]).write_text('DONE\\n'); time.sleep(10)"
    task = python_task("日志成功", source, str(log))
    task.log_path = str(log)
    task.success_log = "DONE"
    engine, _ = build_engine(tmp_path, [task])

    await engine.start_all()
    await engine.wait_until_idle()

    item = engine.history.runs[0].items[0]
    assert item.completion_source == CompletionSource.SUCCESS_LOG
    assert item.log_summary == ["DONE"]


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", [False, True])
async def test_split_log_signal_from_owned_process(tmp_path: Path, failure: bool) -> None:
    log = tmp_path / "split.log"
    source = (
        "from pathlib import Path; import sys,time; p=Path(sys.argv[1]); "
        "p.write_text(sys.argv[2]); time.sleep(.8); "
        "p.open('a').write(sys.argv[3]); time.sleep(10)"
    )
    task = python_task("分片日志", source, str(log), "FA" if failure else "DO", "IL" if failure else "NE")
    task.log_path = str(log)
    task.success_log = "DONE"
    task.failure_log = "FAIL"
    task.allow_manual_confirmation = failure
    engine, _ = build_engine(tmp_path, [task])
    await engine.start_all()
    await engine.wait_until_idle()
    item = engine.history.runs[0].items[0]
    if failure:
        assert item.state.value == "failed" and "FAIL" in (item.error_summary or "")
    else:
        assert item.completion_source == CompletionSource.SUCCESS_LOG


@pytest.mark.asyncio
async def test_manual_confirmation_stops_only_owned_process(tmp_path: Path) -> None:
    task = python_task("人工确认", "import time; time.sleep(10)")
    task.allow_manual_confirmation = True
    task.allow_process_exit = False
    engine, _ = build_engine(tmp_path, [task])

    await engine.start_all()
    await asyncio.sleep(0.2)
    await engine.confirm(task.id)
    await engine.wait_until_idle()

    item = engine.history.runs[0].items[0]
    assert item.completion_source == CompletionSource.MANUAL
    assert engine._owned_process is None


@pytest.mark.asyncio
async def test_runtime_snapshot_restores_summary_before_manual_confirmation(tmp_path: Path) -> None:
    task = python_task("人工确认", "import time; time.sleep(10)")
    task.allow_manual_confirmation = True
    task.allow_process_exit = False
    engine, queue = build_engine(tmp_path, [task])

    await engine.start(queue.id, "manual")
    for _ in range(30):
        snapshot = engine.snapshot()
        if any(entry["phase"] == "waiting" for entry in snapshot["liveLogs"]):
            break
        await asyncio.sleep(0.05)
    else:
        await engine.stop()
        await engine.wait_until_idle()
        pytest.fail("等待人工确认前没有写入摘要日志快照")

    entries = snapshot["liveLogs"]
    assert [entry["sequence"] for entry in entries] == list(range(1, len(entries) + 1))
    assert entries[0]["message"] == "本轮开始运行"
    assert any(entry["phase"] == "validation" for entry in entries)
    assert any(entry["phase"] == "running" for entry in entries)
    assert any(entry["phase"] == "waiting" for entry in entries)
    assert {entry["runId"] for entry in entries} == {snapshot["run"]["id"]}
    assert snapshot["runCenter"]["readableProgress"] == "正在等待人工确认"
    # 新记录的脚本可读事件仅来自已授权的外部脚本日志；运行器生命周期
    # 仍在 liveLogs 中提供，不伪装成脚本转译结果。
    assert snapshot["run"]["items"][0]["readable_report"]["events"] == []

    await engine.confirm(task.id)
    await engine.wait_until_idle()
    assert engine.history.runs[0].readable_report is not None
    assert engine.history.runs[0].readable_report.generator_version == "local-rules-v2"


@pytest.mark.asyncio
async def test_live_summary_keeps_recent_entries_and_survives_detail_log_failure(tmp_path: Path, monkeypatch) -> None:
    task = python_task("摘要", "pass")
    engine, _ = build_engine(tmp_path, [task])
    record = RunRecord(queue_id="q", queue_name="q", trigger_source="manual", run_log_id="00000000-0000-0000-0000-000000000001")
    engine.current_run = record
    engine._reset_live_summary(record)

    def reject_persist(*_args, **_kwargs) -> None:
        raise OSError("受控写入失败")

    monkeypatch.setattr(engine.detail_logs, "append", reject_persist)
    subscription = engine.events.subscribe()
    first_event = asyncio.create_task(anext(subscription))
    await asyncio.sleep(0)
    for sequence in range(45):
        await engine._run_log(record, f"摘要 {sequence}", phase="info")

    event = await first_event
    assert event["type"] == "log"
    assert event["data"]["message"] == "摘要 0"
    await subscription.aclose()

    snapshot = engine.snapshot()
    assert record.run_log_status and record.run_log_status.startswith("本轮日志保存失败")
    assert len(snapshot["liveLogs"]) == 40
    assert [entry["sequence"] for entry in snapshot["liveLogs"]] == list(range(6, 46))
    assert snapshot["liveLogs"][0]["message"] == "摘要 5"

    next_record = RunRecord(queue_id="next", queue_name="下一轮", trigger_source="manual")
    engine.current_run = next_record
    engine._reset_live_summary(next_record)
    assert engine.snapshot()["liveLogs"] == []


@pytest.mark.asyncio
async def test_script_log_snapshot_is_source_scoped_and_bounded(tmp_path: Path) -> None:
    task = python_task("来源日志", "pass")
    engine, _ = build_engine(tmp_path, [task])
    record = RunRecord(queue_id="q", queue_name="q", trigger_source="manual")
    engine.current_run = record
    engine._reset_live_summary(record)

    await engine._publish_script_log(record, task.id, "external-a", "first.log", "external", "first\n")
    await engine._publish_script_log(record, task.id, "window-log", "界面读取", "window", "window\n")
    await engine._publish_script_log(record, task.id, "external-a", "first.log", "external", "second\n")

    streams = {entry["sourceId"]: entry for entry in engine.snapshot()["scriptLogs"]}
    assert streams["external-a"]["text"] == "first\nsecond\n"
    assert streams["external-a"]["sequence"] == 2
    assert streams["window-log"]["sourceType"] == "window"
    assert engine.snapshot(include_script_logs=False)["scriptLogs"] == []


@pytest.mark.asyncio
async def test_script_translation_events_are_globally_ordered_and_recoverable_from_snapshot(tmp_path: Path) -> None:
    task = python_task("转译日志", "pass")
    engine, _ = build_engine(tmp_path, [task])
    record = RunRecord(queue_id="q", queue_name="q", trigger_source="manual")
    item = ItemRecord(task_id=task.id, task_name=task.name, state=ItemState.MONITORING)
    record.items = [item]
    engine.current_run = record
    engine._reset_live_summary(record)

    events = append_script_events(
        item,
        "[2026-10-25 12:00:00.000][INF][ConfigurationHelper] saved\n"
        "[2026-10-25 12:00:01.000][ERR][Worker] failed\n",
    )
    subscription = engine.events.subscribe()
    first_event = asyncio.create_task(anext(subscription))
    await asyncio.sleep(0)
    await engine._publish_script_events(record, item, events)
    emitted = await first_event
    await subscription.aclose()

    assert emitted["type"] == "script_event"
    snapshot = engine.snapshot()
    assert [entry["sequence"] for entry in snapshot["scriptEvents"]] == [1, 2]
    assert [entry["event"]["source"] for entry in snapshot["scriptEvents"]] == ["script", "script"]
    assert "saved" not in str(snapshot["scriptEvents"])
    assert snapshot["scriptEvents"][0]["event"]["message"] == "脚本正在更新配置。"

    next_record = RunRecord(queue_id="next", queue_name="next", trigger_source="manual")
    engine.current_run = next_record
    engine._reset_live_summary(next_record)
    assert engine.snapshot()["scriptEvents"] == []


@pytest.mark.asyncio
async def test_folded_script_event_updates_its_existing_realtime_sequence(tmp_path: Path) -> None:
    task = python_task("转译日志", "pass")
    engine, _ = build_engine(tmp_path, [task])
    record = RunRecord(queue_id="q", queue_name="q", trigger_source="manual")
    item = ItemRecord(task_id=task.id, task_name=task.name, state=ItemState.MONITORING)
    record.items = [item]
    engine.current_run = record
    engine._reset_live_summary(record)

    first = append_script_events(item, "[2026-10-26 12:00:00.000][INF][Bootstrapper] one\n")
    await engine._publish_script_events(record, item, first)
    updated = append_script_events(item, "[2026-10-26 12:00:01.000][INF][Bootstrapper] two\n")
    await engine._publish_script_events(record, item, updated)

    snapshot = engine.snapshot()["scriptEvents"]
    assert len(snapshot) == 1
    assert snapshot[0]["sequence"] == 1
    assert snapshot[0]["replace"] is True
    assert snapshot[0]["event"]["occurrence_count"] == 2


@pytest.mark.asyncio
async def test_external_log_increment_reaches_translation_snapshot_websocket_and_report(tmp_path: Path) -> None:
    """The real watcher callback must fan out without retaining its raw source text."""
    task = python_task("受控外部日志", "pass")
    engine, _ = build_engine(tmp_path, [task])
    record = RunRecord(queue_id="q", queue_name="q", trigger_source="manual")
    item = ItemRecord(task_id=task.id, task_name=task.name, state=ItemState.MONITORING)
    record.items = [item]
    engine.current_run = record
    engine._reset_live_summary(record)

    subscription = engine.events.subscribe()
    next_event = asyncio.create_task(anext(subscription))
    await asyncio.sleep(0)
    raw = "[2026-10-26 12:00:00.000][INF][ConfigurationHelper] saved\n"
    await engine._record_external_log(record, item, task.id, tmp_path / "authorized.log", raw)
    emitted = await next_event
    await subscription.aclose()

    assert emitted["type"] == "script_event"
    assert emitted["data"]["sequence"] == 1
    assert emitted["data"]["event"]["message"] == "脚本正在更新配置。"
    snapshot = engine.snapshot()
    assert snapshot["scriptEvents"][0]["event"]["message"] == "脚本正在更新配置。"
    assert snapshot["scriptLogs"][0]["text"] == raw
    assert record.readable_report is not None
    assert item.readable_report is not None
    assert raw not in item.model_dump_json()
    assert raw not in record.model_dump_json()


@pytest.mark.asyncio
@pytest.mark.parametrize("allow_exit", [False, True])
async def test_manual_waits_after_success_log_and_zero_exit(tmp_path: Path, allow_exit: bool) -> None:
    log = tmp_path / "success.log"
    marker = tmp_path / "next.txt"
    first = python_task("人工确认", "from pathlib import Path; import sys,time; Path(sys.argv[1]).write_text('DONE\\n'); time.sleep(.3)", str(log))
    first.log_path = str(log)
    first.success_log = "DONE"
    first.allow_manual_confirmation = True
    first.allow_process_exit = allow_exit
    second = python_task("下一项", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('ran')", str(marker))
    engine, _ = build_engine(tmp_path, [first, second])
    await engine.start_all()
    for _ in range(100):
        if engine.current_run and engine.current_run.run_log_id and any(
            "仍等待人工确认" in entry["text"]
            for entry in engine.detail_logs.page(engine.current_run.run_log_id, 0)["entries"]
        ):
            break
        await asyncio.sleep(.05)
    else:
        await engine.stop()
        await engine.wait_until_idle()
        pytest.fail("未观察到程序正常退出后等待人工确认")
    assert engine.is_busy and not marker.exists()
    await engine.confirm(first.id)
    await engine.wait_until_idle()
    assert marker.exists()
    assert engine.history.runs[0].items[0].completion_source == CompletionSource.MANUAL


@pytest.mark.asyncio
async def test_manual_keeps_monitoring_failure_after_success(tmp_path: Path) -> None:
    log = tmp_path / "signals.log"
    source = "from pathlib import Path; import sys,time; p=Path(sys.argv[1]); p.write_text('DONE\\n'); time.sleep(.4); p.open('a').write('FAIL\\n'); time.sleep(2)"
    task = python_task("人工确认", source, str(log))
    task.log_path = str(log)
    task.success_log = "DONE"
    task.failure_log = "FAIL"
    task.success_log_rules = [LogRule(pattern="PRESET_DONE")]
    task.failure_log_rules = [LogRule(pattern="PRESET_FAIL")]
    task.allow_manual_confirmation = True
    engine, _ = build_engine(tmp_path, [task])
    await engine.start_all()
    await engine.wait_until_idle()
    assert "FAIL" in (engine.history.runs[0].items[0].error_summary or "")


@pytest.mark.asyncio
@pytest.mark.parametrize("action", ["stop", "timeout", "nonzero"])
async def test_manual_exit_still_obeys_failure_stop_timeout(tmp_path: Path, action: str) -> None:
    task = python_task("人工确认", "import sys,time; time.sleep(.2); sys.exit(int(sys.argv[1]))", "1" if action == "nonzero" else "0")
    task.allow_manual_confirmation = True
    task.timeout_seconds = 1
    engine, _ = build_engine(tmp_path, [task])
    await engine.start_all()
    if action == "stop":
        await asyncio.sleep(.5)
        await engine.stop()
    await engine.wait_until_idle()
    item = engine.history.runs[0].items[0]
    if action == "stop":
        assert item.state.value == "stopped"
    else:
        assert ("退出码 1" if action == "nonzero" else "超时") in (item.error_summary or "")


@pytest.mark.asyncio
async def test_zero_exit_without_enabled_completion_does_not_succeed(tmp_path: Path) -> None:
    task = python_task("不启用退出", "import time; time.sleep(.2)")
    task.allow_process_exit = False
    engine, _ = build_engine(tmp_path, [task])
    await engine.start_all()
    await engine.wait_until_idle()
    assert "未获得完成信号" in (engine.history.runs[0].items[0].error_summary or "")


@pytest.mark.asyncio
async def test_configured_success_log_and_early_exit_is_failure(tmp_path: Path) -> None:
    log = tmp_path / "target.log"
    log.write_text("historic DONE\n", encoding="utf-8")
    task = python_task("提前退出", "pass")
    task.log_path = str(log)
    task.success_log = "DONE"
    engine, _ = build_engine(tmp_path, [task])

    await engine.start_all()
    await engine.wait_until_idle()

    assert "命中前退出" in (engine.history.runs[0].items[0].error_summary or "")


@pytest.mark.asyncio
async def test_stop_prevents_next_task(tmp_path: Path) -> None:
    output = tmp_path / "should-not-run.txt"
    first = python_task("长任务", "import time; time.sleep(10)")
    second = python_task("后续任务", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('ran')", str(output))
    engine, _ = build_engine(tmp_path, [first, second])

    await engine.start_all()
    await asyncio.sleep(0.2)
    await engine.stop()
    await engine.wait_until_idle()

    assert not output.exists()
    assert engine.history.runs[0].state.value == "stopped"


@pytest.mark.asyncio
async def test_failure_log_regex_cleans_up_then_continues(tmp_path: Path) -> None:
    log = tmp_path / "failure.log"
    output = tmp_path / "following.txt"
    first = python_task(
        "日志失败",
        "from pathlib import Path; import sys,time; Path(sys.argv[1]).write_text('ERROR 42\\n'); time.sleep(10)",
        str(log),
    )
    first.log_path = str(log)
    first.failure_log = r"ERROR\s+\d+"
    first.failure_log_mode = MatchMode.REGEX
    second = python_task("后续任务", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('ran')", str(output))
    engine, _ = build_engine(tmp_path, [first, second])

    await engine.start_all()
    await engine.wait_until_idle()

    assert engine.history.runs[0].items[0].state.value == "failed"
    assert output.read_text(encoding="utf-8") == "ran"


@pytest.mark.asyncio
async def test_timeout_marks_failure_and_continues(tmp_path: Path) -> None:
    output = tmp_path / "after-timeout.txt"
    first = python_task("超时", "import time; time.sleep(10)")
    first.timeout_seconds = 1
    second = python_task("后续任务", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('ran')", str(output))
    engine, _ = build_engine(tmp_path, [first, second])

    await engine.start_all()
    await engine.wait_until_idle()

    assert "运行超时" in (engine.history.runs[0].items[0].error_summary or "")
    assert output.exists()


@pytest.mark.asyncio
async def test_log_idle_timeout_fails_only_for_an_existing_automatic_external_log(tmp_path: Path) -> None:
    log = tmp_path / "idle.log"
    log.write_text("baseline\n", encoding="utf-8")
    task = python_task("无日志推进", "import time; time.sleep(10)")
    task.log_path = str(log)
    task.log_idle_timeout_seconds = 1
    task.timeout_seconds = 5
    task.allow_manual_confirmation = False
    engine, _ = build_engine(tmp_path, [task])

    await engine.start_all()
    await engine.wait_until_idle()

    item = engine.history.runs[0].items[0]
    assert "日志超过 1 秒无新增" in (item.error_summary or "")


@pytest.mark.asyncio
async def test_manual_confirmation_is_accepted_only_while_the_active_task_waits(tmp_path: Path) -> None:
    task = python_task("人工确认", "import time; time.sleep(10)")
    task.allow_manual_confirmation = True
    task.allow_process_exit = False
    engine, queue = build_engine(tmp_path, [task])

    with pytest.raises(ConfirmRejectedError):
        await engine.confirm(task.id)
    await engine.start(queue.id, "manual")
    await asyncio.sleep(0.2)
    await engine.confirm(task.id)
    with pytest.raises(ConfirmRejectedError):
        await engine.confirm("not-active")
    await engine.wait_until_idle()

    assert engine.history.runs[0].items[0].completion_source == CompletionSource.MANUAL


@pytest.mark.asyncio
async def test_start_all_expands_scripts_in_persisted_order_and_records_origin(tmp_path: Path) -> None:
    output = tmp_path / "all-order.txt"
    source = "from pathlib import Path; import sys; Path(sys.argv[1]).open('a').write(sys.argv[2] + '\\n')"
    first = python_task("第一程序", source, str(output), "one")
    second = python_task("第二程序", source, str(output), "two")
    first_script = QueueDefinition(name="第一脚本", task_ids=[first.id])
    second_script = QueueDefinition(name="第二脚本", task_ids=[second.id])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=[first_script, second_script], tasks=[first, second]))
    engine = RunnerEngine(store, EventHub(), cooldown_seconds=0.01, close_grace_seconds=0.05)

    record = await engine.start_all()
    assert record.run_mode == "all_scripts"
    await engine.wait_until_idle()

    completed = engine.history.runs[0]
    assert output.read_text(encoding="utf-8").splitlines() == ["one", "two"]
    assert [(item.script_id, item.script_name) for item in completed.items] == [
        (first_script.id, "第一脚本"),
        (second_script.id, "第二脚本"),
    ]


@pytest.mark.asyncio
async def test_start_all_blocks_everything_when_a_script_is_incomplete(tmp_path: Path) -> None:
    complete = python_task("已配置", "pass")
    incomplete = TaskDefinition(name="未配置")
    first_script = QueueDefinition(name="完整脚本", task_ids=[complete.id])
    second_script = QueueDefinition(name="不完整脚本", task_ids=[incomplete.id])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=[first_script, second_script], tasks=[complete, incomplete]))
    engine = RunnerEngine(store, EventHub(), cooldown_seconds=0.01)

    with pytest.raises(RunValidationError) as error:
        await engine.start_all()
    assert "不完整脚本" in error.value.problems[0]
    assert not engine.is_busy


@pytest.mark.asyncio
async def test_snapshot_exposes_cooldown_deadline_and_cancels_it_on_stop(tmp_path: Path) -> None:
    first = python_task("第一项", "pass")
    second = python_task("第二项", "import time; time.sleep(10)")
    engine, _ = build_engine(tmp_path, [first, second])
    engine.cooldown_seconds = 1
    await engine.start_all()
    for _ in range(20):
        if engine.snapshot()["cooldownUntil"]:
            break
        await asyncio.sleep(0.02)
    assert engine.snapshot()["cooldownUntil"]
    assert engine.snapshot()["runCenter"]["currentState"] == "cooling_down"
    await engine.stop()
    await engine.wait_until_idle()
    assert engine.snapshot()["cooldownUntil"] is None


@pytest.mark.asyncio
async def test_final_success_does_not_enter_cooldown(tmp_path: Path) -> None:
    task = python_task("最后成功", "pass")
    engine, queue = build_engine(tmp_path, [task])
    engine.cooldown_seconds = 1

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert engine.history.runs[0].state.value == "completed"
    assert engine.history.runs[0].items[0].state.value == "succeeded"
    assert engine.snapshot()["cooldownUntil"] is None


@pytest.mark.asyncio
async def test_final_failure_does_not_enter_cooldown(tmp_path: Path) -> None:
    task = python_task("最后失败", "import sys; sys.exit(3)")
    engine, queue = build_engine(tmp_path, [task])
    engine.cooldown_seconds = 1

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert engine.history.runs[0].state.value == "completed"
    assert engine.history.runs[0].items[0].state.value == "failed"
    assert engine.snapshot()["cooldownUntil"] is None
