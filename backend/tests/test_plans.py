from __future__ import annotations

import asyncio
import sys
from pathlib import Path

import pytest

from app.engine import RunnerEngine, RunValidationError
from app.events import EventHub
from app.main import validate_schedule_plans
from app.models import (
    AppConfig,
    ItemRecord,
    MAAAdapterConfig,
    MaaEndAdapterConfig,
    PlanTask,
    QueueDefinition,
    SchedulePlan,
    StartupMode,
    TaskDefinition,
    TimeSlot,
)
from app.storage import JsonStore


def task(name: str, source: str = "pass") -> TaskDefinition:
    return TaskDefinition(name=name, executable=sys.executable, arguments=["-c", source])


@pytest.mark.asyncio
async def test_plan_runs_two_scripts_in_saved_order(tmp_path: Path) -> None:
    output = tmp_path / "order.txt"
    first = task("第一", "from pathlib import Path; import sys; Path(sys.argv[1]).open('a').write('1')")
    first.arguments.append(str(output))
    second = task("第二", "from pathlib import Path; import sys; Path(sys.argv[1]).open('a').write('2')")
    second.arguments.append(str(output))
    one, two = QueueDefinition(name="一", task_ids=[first.id]), QueueDefinition(name="二", task_ids=[second.id])
    plan = SchedulePlan(name="早班", items=[PlanTask(script_id=one.id), PlanTask(script_id=two.id)])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=[one, two], tasks=[first, second], plans=[plan]))
    engine = RunnerEngine(store, EventHub(), cooldown_seconds=0.01, close_grace_seconds=0.05)

    record = await engine.start_plan(plan.id, "scheduled")
    await engine.wait_until_idle()

    assert record.run_mode == "plan"
    assert output.read_text(encoding="utf-8") == "12"
    assert [item.script_name for item in engine.history.runs[0].items] == ["一", "二"]


def test_plan_validation_rejects_overlap_and_multiple_startup() -> None:
    one, two, three = QueueDefinition(name="一", task_ids=["a"]), QueueDefinition(name="二", task_ids=["b"]), QueueDefinition(name="三", task_ids=["c"])
    first = SchedulePlan(name="计划甲", items=[PlanTask(script_id=one.id), PlanTask(script_id=two.id)], startup_mode=StartupMode.ALWAYS, time_slots=[TimeSlot(time="09:00", days=[0, 1])])
    second = SchedulePlan(name="计划乙", items=[PlanTask(script_id=two.id), PlanTask(script_id=three.id)], startup_mode=StartupMode.ALWAYS, time_slots=[TimeSlot(time="09:00", days=[1])])
    with pytest.raises(ValueError) as error:
        validate_schedule_plans(AppConfig(queues=[one, two, three], plans=[first, second]))
    assert "仅允许一个" in str(error.value)
    assert "同时触发" in str(error.value)


@pytest.mark.asyncio
async def test_incomplete_plan_is_a_saveable_draft_but_cannot_start(tmp_path: Path) -> None:
    draft = SchedulePlan(name="待配置草稿")
    validate_schedule_plans(AppConfig(plans=[draft]))
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[draft]))
    engine = RunnerEngine(store, EventHub())

    with pytest.raises(RunValidationError, match="至少需要两个不同的软件脚本"):
        await engine.start_plan(draft.id, "manual")


@pytest.mark.asyncio
async def test_busy_plan_is_recorded_as_skipped_in_current_run(tmp_path: Path) -> None:
    task_one = task("运行中", "import time; time.sleep(10)")
    task_two, task_three = task("二"), task("三")
    one = QueueDefinition(name="一", task_ids=[task_one.id])
    two, three = QueueDefinition(name="二", task_ids=[task_two.id]), QueueDefinition(name="三", task_ids=[task_three.id])
    plan = SchedulePlan(name="待跳过", items=[PlanTask(script_id=two.id), PlanTask(script_id=three.id)])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=[one, two, three], tasks=[task_one, task_two, task_three], plans=[plan]))
    engine = RunnerEngine(store, EventHub(), close_grace_seconds=0.05)
    await engine.start(one.id, "manual")
    await asyncio.sleep(0.1)
    await engine.record_schedule_skip(plan, __import__("datetime").datetime.now())
    assert engine.current_run and engine.current_run.scheduler_skips
    await engine.stop()
    await engine.wait_until_idle()
    assert engine.history.runs[0].scheduler_skips


@pytest.mark.asyncio
async def test_maa_adb_preflight_failure_is_warning_only(tmp_path: Path) -> None:
    store = JsonStore(tmp_path / "data")
    engine = RunnerEngine(store, EventHub())
    target = TaskDefinition(name="MAA", executable=sys.executable, preset_id="maa", maa_adapter=MAAAdapterConfig(adb_executable="missing-adb.exe", adb_address="127.0.0.1:5555"))
    item = ItemRecord(task_id=target.id, task_name=target.name)
    await engine._maa_adb_preflight(item, target)
    assert item.state.value == "queued"


@pytest.mark.asyncio
async def test_maaend_game_process_is_owned_and_only_closed_when_requested(tmp_path: Path) -> None:
    store = JsonStore(tmp_path / "data")
    engine = RunnerEngine(store, EventHub(), close_grace_seconds=0.05)
    target = TaskDefinition(
        name="MaaEnd", executable=sys.executable, preset_id="maaend",
        maaend_adapter=MaaEndAdapterConfig(game_executable=sys.executable, game_arguments=["-c", "import time; time.sleep(10)"], game_wait_seconds=0, close_game_on_finish=True),
    )
    item = ItemRecord(task_id=target.id, task_name=target.name)
    assert await engine._start_maaend_game(item, target)
    assert engine._auxiliary_owned_processes
    await engine._cleanup_auxiliary(item, target)
    assert not engine._auxiliary_owned_processes
