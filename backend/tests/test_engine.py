from __future__ import annotations

import asyncio
import sys
from pathlib import Path

import pytest

from app.engine import RunnerEngine
from app.events import EventHub
from app.models import AppConfig, CompletionSource, MatchMode, QueueDefinition, TaskDefinition
from app.storage import JsonStore


def build_engine(tmp_path: Path, tasks: list[TaskDefinition]) -> tuple[RunnerEngine, QueueDefinition]:
    queue = QueueDefinition(name="测试队列", task_ids=[task.id for task in tasks])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=[queue], tasks=tasks))
    return RunnerEngine(store, EventHub(), cooldown_seconds=0.01, close_grace_seconds=0.05), queue


def python_task(name: str, source: str, *args: str) -> TaskDefinition:
    return TaskDefinition(name=name, executable=sys.executable, arguments=["-c", source, *args])


@pytest.mark.asyncio
async def test_process_exit_runs_tasks_in_strict_order(tmp_path: Path) -> None:
    output = tmp_path / "order.txt"
    source = "from pathlib import Path; import sys; Path(sys.argv[1]).open('a').write(sys.argv[2] + '\\n')"
    first = python_task("第一项", source, str(output), "first")
    second = python_task("第二项", source, str(output), "second")
    engine, queue = build_engine(tmp_path, [first, second])

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert output.read_text(encoding="utf-8").splitlines() == ["first", "second"]
    run = engine.history.runs[0]
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
    engine, queue = build_engine(tmp_path, [task])

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    item = engine.history.runs[0].items[0]
    assert item.completion_source == CompletionSource.SUCCESS_LOG
    assert item.log_summary == ["DONE"]


@pytest.mark.asyncio
async def test_manual_confirmation_stops_only_owned_process(tmp_path: Path) -> None:
    task = python_task("人工确认", "import time; time.sleep(10)")
    task.allow_manual_confirmation = True
    task.allow_process_exit = False
    engine, queue = build_engine(tmp_path, [task])

    await engine.start(queue.id, "manual")
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
    engine, queue = build_engine(tmp_path, [task])

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert "命中前退出" in (engine.history.runs[0].items[0].error_summary or "")


@pytest.mark.asyncio
async def test_stop_prevents_next_task(tmp_path: Path) -> None:
    output = tmp_path / "should-not-run.txt"
    first = python_task("长任务", "import time; time.sleep(10)")
    second = python_task("后续任务", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('ran')", str(output))
    engine, queue = build_engine(tmp_path, [first, second])

    await engine.start(queue.id, "manual")
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
    engine, queue = build_engine(tmp_path, [first, second])

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert engine.history.runs[0].items[0].state.value == "failed"
    assert output.read_text(encoding="utf-8") == "ran"


@pytest.mark.asyncio
async def test_timeout_marks_failure_and_continues(tmp_path: Path) -> None:
    output = tmp_path / "after-timeout.txt"
    first = python_task("超时", "import time; time.sleep(10)")
    first.timeout_seconds = 1
    second = python_task("后续任务", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('ran')", str(output))
    engine, queue = build_engine(tmp_path, [first, second])

    await engine.start(queue.id, "manual")
    await engine.wait_until_idle()

    assert "运行超时" in (engine.history.runs[0].items[0].error_summary or "")
    assert output.exists()
