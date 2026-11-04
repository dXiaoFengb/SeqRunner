from __future__ import annotations

import asyncio
from pathlib import Path

import pytest

from app.engine import RunnerEngine
from app.events import EventHub
from app.external_logs import ExternalLogWatcher
from app.models import (
    CompletionSource,
    ItemRecord,
    ItemState,
    MatchMode,
    RunRecord,
    StartupControlConfig,
    StartupFallbackConfig,
    TaskDefinition,
)
from app.processes import OwnedProcess
from app.storage import JsonStore


@pytest.mark.asyncio
@pytest.mark.parametrize("auto_started", [False, True])
async def test_startup_fallback_does_not_double_start(tmp_path: Path, monkeypatch, auto_started: bool) -> None:
    log = tmp_path / "task.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", "TASK DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    control = StartupControlConfig(window_title="fixture", automation_id="start")
    task = TaskDefinition(name="fixture", executable="fixture", log_path=str(log), success_log="TASK DONE",
                          timeout_seconds=6, startup_fallback=StartupFallbackConfig(enabled=True, started_signal="STARTED",
                                                                                   wait_seconds=1, start=control))
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    engine._owned_process = OwnedProcess(1, 1)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    calls: list[bool] = []
    invoked = asyncio.Event()

    async def query(_owner, _selector, *, invoke=False):
        calls.append(invoke)
        if invoke:
            invoked.set()
        return {"invoked": True}

    monkeypatch.setattr("app.engine.query_uia", query)
    monkeypatch.setattr("app.engine.snapshot_processes", lambda _roots: [])

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(5)
            return 0

    async def writer() -> None:
        if auto_started:
            await asyncio.sleep(0.1)
        else:
            await asyncio.wait_for(invoked.wait(), 3)
        with log.open("a", encoding="utf-8") as stream:
            stream.write("STARTED\n")
            stream.flush()
            await asyncio.sleep(0.25)
            stream.write("TASK DONE\n")
            stream.flush()

    write_task = asyncio.create_task(writer())
    try:
        result = await asyncio.wait_for(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item), timeout=4)  # type: ignore[arg-type]
        assert result[0:2] == ("success", CompletionSource.SUCCESS_LOG)
        assert calls == ([] if auto_started else [True])
    finally:
        await write_task


@pytest.mark.asyncio
async def test_failed_start_action_is_not_repeated_automatically(tmp_path: Path, monkeypatch) -> None:
    log = tmp_path / "task.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    control = StartupControlConfig(window_title="fixture", automation_id="start")
    task = TaskDefinition(name="fixture", executable="fixture", log_path=str(log), timeout_seconds=10,
                          startup_fallback=StartupFallbackConfig(enabled=True, started_signal="STARTED", wait_seconds=1, start=control))
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    engine._owned_process = OwnedProcess(1, 1)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id="q", queue_name="fixture", trigger_source="manual", items=[item])
    engine._active_task_id = task.id
    calls = 0

    async def query(_owner, _selector, *, invoke=False):
        nonlocal calls
        calls += 1
        raise RuntimeError("fixture: operation result unknown")

    monkeypatch.setattr("app.engine.query_uia", query)
    monkeypatch.setattr("app.engine.snapshot_processes", lambda _roots: [])

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(9)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))  # type: ignore[arg-type]
    for _ in range(40):
        if item.state == ItemState.WAITING_UPDATE:
            break
        await asyncio.sleep(0.1)
    assert item.state == ItemState.WAITING_UPDATE and calls == 1
    await engine.decide_update(task.id, "wait")
    await asyncio.sleep(1.4)
    assert item.state == ItemState.WAITING_UPDATE and calls == 1
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == "stopped"


@pytest.mark.asyncio
async def test_announcement_closes_and_auto_start_does_not_click_start(tmp_path: Path, monkeypatch) -> None:
    log = tmp_path / "task.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", "TASK DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    announcement = StartupControlConfig(window_title="notice", automation_id="close")
    start = StartupControlConfig(window_title="main", automation_id="start")
    task = TaskDefinition(name="fixture", executable="fixture", log_path=str(log), success_log="TASK DONE",
                          timeout_seconds=6, startup_fallback=StartupFallbackConfig(enabled=True, started_signal="STARTED",
                              wait_seconds=1, announcement=announcement, start=start))
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    engine._owned_process = OwnedProcess(1, 1)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    closed = asyncio.Event()
    calls: list[tuple[str, bool]] = []

    async def query(_owner, selector, *, invoke=False):
        calls.append((selector.automation_id, invoke))
        if invoke and selector.automation_id == "close":
            closed.set()
        return {"window_found": True, "invoked": invoke}

    monkeypatch.setattr("app.engine.query_uia", query)
    monkeypatch.setattr("app.engine.snapshot_processes", lambda _roots: [])

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(5)
            return 0

    async def writer() -> None:
        await asyncio.wait_for(closed.wait(), 3)
        with log.open("a", encoding="utf-8") as stream:
            stream.write("STARTED\n")
            stream.flush()
            await asyncio.sleep(0.25)
            stream.write("TASK DONE\n")

    write_task = asyncio.create_task(writer())
    try:
        result = await asyncio.wait_for(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item), timeout=4)  # type: ignore[arg-type]
        assert result[0:2] == ("success", CompletionSource.SUCCESS_LOG)
        assert calls == [("close", False), ("close", True)]
    finally:
        await write_task


@pytest.mark.asyncio
async def test_start_signal_during_inspection_prevents_announcement_click(tmp_path: Path, monkeypatch) -> None:
    log = tmp_path / "task.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", "TASK DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    announcement = StartupControlConfig(window_title="notice", automation_id="close")
    start = StartupControlConfig(window_title="main", automation_id="start")
    task = TaskDefinition(name="fixture", executable="fixture", log_path=str(log), success_log="TASK DONE",
                          timeout_seconds=6, startup_fallback=StartupFallbackConfig(enabled=True, started_signal="STARTED",
                              wait_seconds=1, announcement=announcement, start=start))
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    engine._owned_process = OwnedProcess(1, 1)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    calls: list[bool] = []

    async def query(_owner, _selector, *, invoke=False):
        calls.append(invoke)
        if not invoke:
            with log.open("a", encoding="utf-8") as stream:
                stream.write("STARTED\n")
            await asyncio.sleep(0.4)
            with log.open("a", encoding="utf-8") as stream:
                stream.write("TASK DONE\n")
        return {"window_found": True}

    monkeypatch.setattr("app.engine.query_uia", query)
    monkeypatch.setattr("app.engine.snapshot_processes", lambda _roots: [])

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(5)
            return 0

    result = await asyncio.wait_for(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item), timeout=4)  # type: ignore[arg-type]
    assert result[0:2] == ("success", CompletionSource.SUCCESS_LOG)
    assert calls == [False]
