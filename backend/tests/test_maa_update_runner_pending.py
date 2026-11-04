"""Controlled update lifecycle regression; never runs real MAA."""

from __future__ import annotations

import asyncio
import os
import sys
import time
from pathlib import Path

import pytest

from app.engine import RunnerEngine
from app.events import EventHub
from app.external_logs import ExternalLogWatcher
from app.models import (
    CompletionSource,
    ItemRecord,
    ItemState,
    LogRule,
    MatchMode,
    RunRecord,
    TaskDefinition,
    UpdateMonitoringConfig,
)
from app.processes import OwnedProcess, capture_owned_process
from app.storage import JsonStore
from app.update_lineage import ProcessSnapshot


@pytest.mark.asyncio
async def test_update_restart_reattaches_and_waits_for_real_task_success(tmp_path: Path, monkeypatch) -> None:
    log = tmp_path / "gui.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(
        str(log), "*.log", "TASK DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
        success_rules=[], failure_rules=[LogRule(pattern="MaaAssistantArknights GUI exited")],
        monitor_success=True,
    )
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    task = TaskDefinition(name="模拟 MAA", preset_id="maa", executable="fixture", log_path=str(log),
                          success_log="TASK DONE", timeout_seconds=5)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    epoch = time.time()
    root, restarted = OwnedProcess(100, epoch - 2), OwnedProcess(102, epoch + 1)
    engine._owned_process = root
    started = time.monotonic()

    def snapshots(_roots):
        if time.monotonic() - started < 0.55:
            return [ProcessSnapshot(100, epoch - 2, 1, "fixture"), ProcessSnapshot(101, epoch + 0.1, 100, "updater")]
        return [ProcessSnapshot(101, epoch + 0.1, 100, "updater"), ProcessSnapshot(102, epoch + 1, 101, "fixture")]

    class Target:
        def exe(self) -> str:
            return "fixture"

        def create_time(self) -> float:
            return restarted.created_at

    async def alive(_owned):
        return True

    monkeypatch.setattr("app.engine.snapshot_processes", snapshots)
    monkeypatch.setattr("app.processes.owns_process", lambda _owned: Target())
    monkeypatch.setattr("app.engine.process_tree_alive", alive)

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(0.2)
            return 0

    async def write_update_log() -> None:
        await asyncio.sleep(0.05)
        with log.open("a", encoding="utf-8") as stream:
            stream.write("[INF][VersionUpdateDialogViewModel] New version found: fixture\n")
            stream.write("[INF][Bootstrapper] Shutdown and restart with custom StartInfo\n")
            stream.write("[INF][Bootstrapper] MaaAssistantArknights GUI exited\n")
            stream.flush()
            await asyncio.sleep(0.65)
            stream.write("[INF][Bootstrapper] Pending update package detected, applying before full startup\n")
            stream.write("[INF][Bootstrapper] Delegated pending update completed successfully\n")
            stream.flush()
            await asyncio.sleep(0.7)
            stream.write("TASK DONE\n")
            stream.flush()

    writer = asyncio.create_task(write_update_log())
    try:
        result = await asyncio.wait_for(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item), timeout=4)  # type: ignore[arg-type]
        assert result[0:2] == ("success", CompletionSource.SUCCESS_LOG)
        assert engine._owned_process == restarted
    finally:
        await writer


@pytest.mark.asyncio
async def test_real_failure_in_same_chunk_is_not_hidden(tmp_path: Path) -> None:
    log = tmp_path / "gui.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[LogRule(pattern="MaaAssistantArknights GUI exited"),
                                                                  LogRule(pattern="REAL FAILURE")], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    task = TaskDefinition(name="模拟 MAA", preset_id="maa", executable="fixture", timeout_seconds=5)
    item = ItemRecord(task_id=task.id, task_name=task.name)

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(2)
            return 0

    async def write_log() -> None:
        await asyncio.sleep(0.05)
        log.write_text("[VersionUpdateDialogViewModel] New version found: fixture\n"
                       "[Bootstrapper] Shutdown and restart with custom StartInfo\n"
                       "[Bootstrapper] MaaAssistantArknights GUI exited\nREAL FAILURE\n", encoding="utf-8")

    writer = asyncio.create_task(write_log())
    try:
        result = await asyncio.wait_for(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item), timeout=2)  # type: ignore[arg-type]
        assert result[0] == "failure" and result[1] == "REAL FAILURE"
    finally:
        await writer


@pytest.mark.asyncio
async def test_update_error_pauses_until_user_stops(tmp_path: Path) -> None:
    log = tmp_path / "gui.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    task = TaskDefinition(name="fixture", preset_id="maa", executable="fixture", timeout_seconds=5)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id="q", queue_name="fixture", trigger_source="manual", items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(4)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))  # type: ignore[arg-type]
    with log.open("a", encoding="utf-8") as stream:
        stream.write("[VersionUpdateDialogViewModel] New version found: fixture\n"
                     "[Bootstrapper] Shutdown and restart with custom StartInfo\n"
                     "[Bootstrapper] Delegated pending update failed. Reason: fixture\n")
    for _ in range(30):
        if item.state == ItemState.WAITING_UPDATE:
            break
        await asyncio.sleep(0.1)
    assert item.state == ItemState.WAITING_UPDATE
    assert not engine._runner_task.done()
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == "stopped"


@pytest.mark.asyncio
async def test_generic_update_idle_timeout_pauses_and_fail_requires_user_choice(tmp_path: Path) -> None:
    log = tmp_path / "generic.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    config = UpdateMonitoringConfig(enabled=True, checking="CHECK", downloading="DOWNLOAD", applying="APPLY",
                                    restarting="RESTART", failed="ERROR", recovered="READY", idle_seconds=1, total_seconds=10)
    task = TaskDefinition(name="generic", executable="fixture", log_path=str(log), timeout_seconds=5, update_monitor=config)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id="q", queue_name="fixture", trigger_source="manual", items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(0.3)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))  # type: ignore[arg-type]
    log.write_text("CHECK\nDOWNLOAD\nRESTART\n", encoding="utf-8")
    for _ in range(40):
        if item.state == ItemState.WAITING_UPDATE:
            break
        await asyncio.sleep(0.1)
    assert item.state == ItemState.WAITING_UPDATE
    assert not engine._runner_task.done()
    await engine.decide_update(task.id, "fail")
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == "failure"


@pytest.mark.asyncio
async def test_download_progress_renews_idle_without_spending_task_timeout(tmp_path: Path) -> None:
    log = tmp_path / "generic.log"
    log.write_text("", encoding="utf-8")
    watcher = ExternalLogWatcher(str(log), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    config = UpdateMonitoringConfig(enabled=True, checking="CHECK", downloading="DOWNLOAD", applying="APPLY",
                                    restarting="RESTART", failed="ERROR", recovered="READY", idle_seconds=1, total_seconds=8)
    task = TaskDefinition(name="generic", executable="fixture", log_path=str(log), timeout_seconds=1, update_monitor=config)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id="q", queue_name="fixture", trigger_source="manual", items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(6)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))  # type: ignore[arg-type]
    with log.open("a", encoding="utf-8") as stream:
        stream.write("CHECK\nDOWNLOAD 0\n")
        stream.flush()
        for amount in range(1, 4):
            await asyncio.sleep(0.55)
            stream.write(f"DOWNLOAD {amount}\n")
            stream.flush()
        await asyncio.sleep(0.3)
    assert not engine._runner_task.done()
    assert item.state != ItemState.WAITING_UPDATE
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == "stopped"


@pytest.mark.asyncio
@pytest.mark.skipif(os.name != "nt", reason="Windows subprocess lifecycle fixture")
async def test_real_child_process_chain_can_be_reattached(tmp_path: Path, monkeypatch) -> None:
    log = tmp_path / "lifecycle.log"
    log.write_text("", encoding="utf-8")
    fixture = tmp_path / "lifecycle_fixture.py"
    fixture.write_text(
        "import pathlib, subprocess, sys, time\n"
        "log = pathlib.Path(sys.argv[2])\n"
        "def emit(s):\n"
        "    with log.open('a', encoding='utf-8') as f: f.write(s + '\\n')\n"
        "if sys.argv[1] == 'root':\n"
        "    emit('CHECK'); time.sleep(0.4); emit('RESTART')\n"
        "    command = subprocess.list2cmdline([sys.executable, __file__, 'replacement', str(log)])\n"
        "    subprocess.Popen(['cmd.exe', '/d', '/c', command])\n"
        "    time.sleep(3.0)\n"
        "else:\n"
        "    time.sleep(3.4); emit('APPLY'); emit('READY'); time.sleep(0.8); emit('TASK DONE'); time.sleep(1)\n",
        encoding="utf-8",
    )
    executable = getattr(sys, "_base_executable", sys.executable)
    watcher = ExternalLogWatcher(str(log), "*.log", "TASK DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    config = UpdateMonitoringConfig(enabled=True, checking="CHECK", downloading="DOWNLOAD", applying="APPLY",
                                    restarting="RESTART", failed="ERROR", recovered="READY", idle_seconds=8, total_seconds=20)
    task = TaskDefinition(name="child fixture", executable=executable, log_path=str(log), success_log="TASK DONE",
                          timeout_seconds=10, update_monitor=config)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    observations = []
    from app.update_lineage import UpdateLineage

    observe = UpdateLineage.observe

    def record_observation(lineage, processes, verified_parents=None):
        result = observe(lineage, processes, verified_parents)
        known = {entry.pid for entry in lineage._known}
        observations.append(([(entry.pid, entry.parent_pid, entry.created_at, entry.executable) for entry in processes
                              if entry.pid in known or entry.parent_pid in known], result))
        return result

    monkeypatch.setattr(UpdateLineage, "observe", record_observation)
    process = await asyncio.create_subprocess_exec(executable, str(fixture), "root", str(log),
                                                   creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0))
    engine._owned_process = await capture_owned_process(process.pid)
    try:
        try:
            result = await asyncio.wait_for(engine._wait_for_outcome(process, task, watcher, item), timeout=12)
        except TimeoutError:
            pytest.fail(f"真实子进程接续超时：状态={item.state}，进展={item.update_message}，受管进程={engine._owned_process}，日志={log.read_text(encoding='utf-8')!r}，进程观察={observations!r}")
        assert result[0:2] == ("success", CompletionSource.SUCCESS_LOG)
        assert engine._owned_process and engine._owned_process.pid != process.pid
    finally:
        if process.returncode is None:
            process.terminate()
        await process.wait()

@pytest.mark.asyncio
async def test_update_actions_available_before_timeout_and_manual_completion_is_explicit(tmp_path: Path) -> None:
    log = tmp_path / 'gui.log'
    log.write_text('', encoding='utf-8')
    watcher = ExternalLogWatcher(str(log), '*.log', None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    task = TaskDefinition(name='fixture', preset_id='maa', executable='fixture', timeout_seconds=5,
                          allow_manual_confirmation=True)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id='q', queue_name='fixture', trigger_source='manual', items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(4)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))
    log.write_text('[VersionUpdateDialogViewModel] New version found: fixture\n', encoding='utf-8')
    for _ in range(30):
        if item.update_active:
            break
        await asyncio.sleep(0.1)
    assert item.update_active and item.manual_completion_available
    await engine.decide_update(task.id, 'wait')
    await asyncio.sleep(0.2)
    assert not engine._runner_task.done()
    await engine.decide_update(task.id, 'complete')
    assert (await asyncio.wait_for(engine._runner_task, 2))[0:2] == ('success', CompletionSource.MANUAL)

@pytest.mark.asyncio
async def test_user_update_confirmation_without_verified_process_keeps_waiting(tmp_path: Path) -> None:
    log = tmp_path / 'gui.log'
    log.write_text('', encoding='utf-8')
    watcher = ExternalLogWatcher(str(log), '*.log', None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    task = TaskDefinition(name='fixture', preset_id='maa', executable='fixture', timeout_seconds=5)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id='q', queue_name='fixture', trigger_source='manual', items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(0.4)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))
    log.write_text('[VersionUpdateDialogViewModel] New version found: fixture\n'
                   '[Bootstrapper] Shutdown and restart with custom StartInfo\n', encoding='utf-8')
    for _ in range(30):
        if item.update_active:
            break
        await asyncio.sleep(0.1)
    await asyncio.sleep(0.5)
    await engine.decide_update(task.id, 'handled')
    for _ in range(30):
        if item.state == ItemState.WAITING_UPDATE:
            break
        await asyncio.sleep(0.1)
    assert item.state == ItemState.WAITING_UPDATE
    assert '归属可验证' in (item.update_message or '')
    assert not engine._runner_task.done()
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == 'stopped'

@pytest.mark.asyncio
async def test_unattributed_updater_success_does_not_resume_task(tmp_path: Path) -> None:
    from app.logwatch import LogWatcher

    gui = tmp_path / 'gui.log'
    updater = tmp_path / 'pending-update-applier.log'
    gui.write_text('', encoding='utf-8')
    updater.write_text('Wrote success status file: old\n', encoding='utf-8')
    watcher = ExternalLogWatcher(str(gui), '*.log', None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    watcher.maa_updater_log = LogWatcher(str(updater))
    await watcher.maa_updater_log.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    task = TaskDefinition(name='fixture', preset_id='maa', executable='fixture', timeout_seconds=10)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id='q', queue_name='fixture', trigger_source='manual', items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(4)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))
    gui.write_text('[VersionUpdateDialogViewModel] New version found: fixture\n'
                   '[Bootstrapper] Shutdown and restart with custom StartInfo\n', encoding='utf-8')
    for _ in range(30):
        if item.update_active:
            break
        await asyncio.sleep(0.1)
    with updater.open('a', encoding='utf-8') as stream:
        stream.write('Wrote success status file: fixture\nRelaunch succeeded.\n')
    for _ in range(30):
        if '更新器日志' in (item.update_message or ''):
            break
        await asyncio.sleep(0.1)
    assert '更新器日志' in (item.update_message or '')
    assert not engine._runner_task.done()
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == 'stopped'

@pytest.mark.asyncio
async def test_unattributed_updater_error_pauses_for_inspection(tmp_path: Path) -> None:
    from app.logwatch import LogWatcher

    gui = tmp_path / 'gui.log'
    updater = tmp_path / 'pending-update-applier.log'
    gui.write_text('', encoding='utf-8')
    updater.write_text('', encoding='utf-8')
    watcher = ExternalLogWatcher(str(gui), '*.log', None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    watcher.maa_updater_log = LogWatcher(str(updater))
    await watcher.maa_updater_log.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    task = TaskDefinition(name='fixture', preset_id='maa', executable='fixture', timeout_seconds=10)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id='q', queue_name='fixture', trigger_source='manual', items=[item])
    engine._active_task_id = task.id

    class SimulatedProcess:
        async def wait(self) -> int:
            await asyncio.sleep(4)
            return 0

    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))
    gui.write_text('[VersionUpdateDialogViewModel] New version found: fixture\n'
                   '[Bootstrapper] Shutdown and restart with custom StartInfo\n', encoding='utf-8')
    for _ in range(30):
        if item.update_active:
            break
        await asyncio.sleep(0.1)
    updater.write_text('Update failed: fixture\n', encoding='utf-8')
    for _ in range(30):
        if item.state == ItemState.WAITING_UPDATE:
            break
        await asyncio.sleep(0.1)
    assert item.state == ItemState.WAITING_UPDATE
    assert '归属待确认' in (item.update_message or '')
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == 'stopped'

@pytest.mark.asyncio
async def test_reused_pid_with_subsecond_creation_change_is_not_reattached(tmp_path: Path, monkeypatch) -> None:
    log = tmp_path / 'gui.log'
    log.write_text('', encoding='utf-8')
    watcher = ExternalLogWatcher(str(log), '*.log', None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    task = TaskDefinition(name='fixture', preset_id='maa', executable='fixture', timeout_seconds=5)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    engine.current_run = RunRecord(queue_id='q', queue_name='fixture', trigger_source='manual', items=[item])
    engine._active_task_id = task.id
    epoch = time.time()
    root = OwnedProcess(100, epoch - 2)
    engine._owned_process = root

    def snapshots(_roots):
        return [ProcessSnapshot(100, epoch - 2, 1, 'fixture'), ProcessSnapshot(101, epoch + 1, 100, 'fixture')]

    class ReusedTarget:
        def exe(self): return 'fixture'
        def create_time(self): return epoch + 1.2

    class SimulatedProcess:
        async def wait(self):
            await asyncio.sleep(0.3)
            return 0

    monkeypatch.setattr('app.engine.snapshot_processes', snapshots)
    monkeypatch.setattr('app.processes.owns_process', lambda _owned: ReusedTarget())
    engine._runner_task = asyncio.create_task(engine._wait_for_outcome(SimulatedProcess(), task, watcher, item))
    log.write_text('[VersionUpdateDialogViewModel] New version found: fixture\n'
                   '[Bootstrapper] Shutdown and restart with custom StartInfo\n'
                   '[Bootstrapper] Delegated pending update completed successfully\n', encoding='utf-8')
    for _ in range(40):
        if item.state == ItemState.WAITING_UPDATE:
            break
        await asyncio.sleep(0.1)
    assert item.state == ItemState.WAITING_UPDATE
    assert engine._owned_process == root
    await engine.stop()
    assert (await asyncio.wait_for(engine._runner_task, 2))[0] == 'stopped'

@pytest.mark.asyncio
async def test_delayed_gui_log_retains_reattachment_candidate(tmp_path: Path, monkeypatch) -> None:
    from datetime import UTC, datetime

    log = tmp_path / 'gui.log'
    log.write_text('', encoding='utf-8')
    watcher = ExternalLogWatcher(str(log), '*.log', 'TASK DONE', MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[], monitor_success=True)
    await watcher.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    now = time.time()
    root = OwnedProcess(100, now - 10)
    restarted = OwnedProcess(101, now - 0.5)
    engine._owned_process = root
    task = TaskDefinition(name='fixture', preset_id='maa', executable='fixture', timeout_seconds=5)
    item = ItemRecord(task_id=task.id, task_name=task.name)

    class Target:
        def exe(self): return 'fixture'
        def create_time(self): return restarted.created_at

    class Process:
        async def wait(self):
            await asyncio.sleep(0.3)
            return 0

    async def alive(_owned): return True
    monkeypatch.setattr('app.engine.snapshot_processes', lambda _: [
        ProcessSnapshot(100, root.created_at, 1, 'fixture'),
        ProcessSnapshot(101, restarted.created_at, 100, 'fixture')])
    monkeypatch.setattr('app.processes.owns_process', lambda _: Target())
    monkeypatch.setattr('app.engine.process_tree_alive', alive)
    stamp = datetime.fromtimestamp(now - 1, UTC).astimezone().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]

    async def writer():
        await asyncio.sleep(0.05)
        with log.open('a', encoding='utf-8') as stream:
            stream.write(f'[{stamp}][VersionUpdateDialogViewModel] New version found: fixture\n'
                         '[Bootstrapper] Shutdown and restart with custom StartInfo\n'
                         '[Bootstrapper] Delegated pending update completed successfully\n')
            stream.flush()
            await asyncio.sleep(0.8)
            stream.write('TASK DONE\n')

    writing = asyncio.create_task(writer())
    try:
        result = await asyncio.wait_for(engine._wait_for_outcome(Process(), task, watcher, item), timeout=3)
        assert result[0:2] == ('success', CompletionSource.SUCCESS_LOG)
        assert engine._owned_process == restarted
    finally:
        await writing

@pytest.mark.asyncio
@pytest.mark.skipif(os.name != 'nt', reason='Windows controlled update fixture')
async def test_real_maa_style_chain_uses_gui_evidence_and_waits_for_task(tmp_path: Path) -> None:
    from app.logwatch import LogWatcher

    gui = tmp_path / 'gui.log'
    updater_log = tmp_path / 'pending-update-applier.log'
    fixture = tmp_path / 'maa_lifecycle_fixture.py'
    gui.write_text('', encoding='utf-8')
    updater_log.write_text('Wrote success status file: old\n', encoding='utf-8')
    fixture.write_text(
        'import pathlib, subprocess, sys, time\n'
        'gui, up = pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])\n'
        'def emit(line):\n'
        '    with gui.open("a", encoding="utf-8") as f: f.write(line + "\\n")\n'
        'if sys.argv[1] == "root":\n'
        '    emit("[VersionUpdateDialogViewModel] New version found: fixture")\n'
        '    emit("[Bootstrapper] Shutdown and restart with custom StartInfo")\n'
        '    time.sleep(0.7)\n'
        '    command = "echo Wrote success status file: fixture >> " + subprocess.list2cmdline([str(up)]) + " & " + subprocess.list2cmdline([sys.executable, __file__, "target", str(gui), str(up)])\n'
        '    subprocess.Popen(["cmd.exe", "/d", "/c", command])\n'
        '    time.sleep(3)\n'
        '    emit("[Bootstrapper] MaaAssistantArknights GUI exited")\n'
        'else:\n'
        '    time.sleep(4)\n'
        '    emit("[Bootstrapper] Delegated pending update completed successfully")\n'
        '    time.sleep(0.8)\n'
        '    emit("TASK DONE")\n'
        '    time.sleep(0.5)\n', encoding='utf-8')
    executable = getattr(sys, '_base_executable', sys.executable)
    watcher = ExternalLogWatcher(str(gui), '*.log', 'TASK DONE', MatchMode.KEYWORD, None, MatchMode.KEYWORD,
                                 success_rules=[], failure_rules=[LogRule(pattern='MaaAssistantArknights GUI exited')], monitor_success=True)
    await watcher.start_at_end()
    watcher.maa_updater_log = LogWatcher(str(updater_log))
    await watcher.maa_updater_log.start_at_end()
    engine = RunnerEngine(JsonStore(tmp_path / 'data'), EventHub())
    task = TaskDefinition(name='MAA fixture', preset_id='maa', executable=executable, log_path=str(gui),
                          success_log='TASK DONE', timeout_seconds=12)
    item = ItemRecord(task_id=task.id, task_name=task.name)
    process = await asyncio.create_subprocess_exec(executable, str(fixture), 'root', str(gui), str(updater_log),
                                                   creationflags=getattr(__import__('subprocess'), 'CREATE_NO_WINDOW', 0))
    engine._owned_process = await capture_owned_process(process.pid)
    try:
        result = await asyncio.wait_for(engine._wait_for_outcome(process, task, watcher, item), timeout=12)
        assert result[0:2] == ('success', CompletionSource.SUCCESS_LOG)
        assert engine._owned_process is not None and engine._owned_process.pid != process.pid
        assert 'Wrote success status file: fixture' in updater_log.read_text(encoding='utf-8')
    finally:
        if process.returncode is None:
            process.terminate()
        await process.wait()
