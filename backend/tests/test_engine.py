from __future__ import annotations

import asyncio
import sys
from pathlib import Path

import pytest

from app.engine import ConfirmRejectedError, RunnerEngine, RunValidationError
from app.events import EventHub
from app.models import AppConfig, CompletionSource, MatchMode, QueueDefinition, TaskDefinition
from app.storage import JsonStore


def build_engine(tmp_path: Path, tasks: list[TaskDefinition]) -> tuple[RunnerEngine, QueueDefinition]:
    queues = [QueueDefinition(name=task.name, task_ids=[task.id]) for task in tasks]
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=queues, tasks=tasks))
    return RunnerEngine(store, EventHub(), cooldown_seconds=0.01, close_grace_seconds=0.05), queues[0]


def python_task(name: str, source: str, *args: str) -> TaskDefinition:
    return TaskDefinition(name=name, executable=sys.executable, arguments=["-c", source, *args])


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
