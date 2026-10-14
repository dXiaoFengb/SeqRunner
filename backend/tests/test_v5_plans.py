from __future__ import annotations

import sys
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.engine import RunnerEngine
from app.events import EventHub
from app.models import (
    AppConfig,
    CompletionAction,
    ItemState,
    PlanTask,
    QueueDefinition,
    RunState,
    SchedulePlan,
    StartupMode,
    TaskDefinition,
    TimeSlot,
)
from app.scheduler import QueueScheduler
from app.storage import JsonStore


def make_plan(tmp_path: Path, *, days: list[int]) -> tuple[RunnerEngine, SchedulePlan]:
    first = TaskDefinition(name="一", executable=sys.executable)
    second = TaskDefinition(name="二", executable=sys.executable)
    one = QueueDefinition(name="软件一", task_ids=[first.id])
    two = QueueDefinition(name="软件二", task_ids=[second.id])
    plan = SchedulePlan(name="计划", items=[PlanTask(script_id=one.id, days=days), PlanTask(script_id=two.id, days=days)])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(queues=[one, two], tasks=[first, second], plans=[plan]))
    return RunnerEngine(store, EventHub(), cooldown_seconds=0.01), plan


@pytest.mark.asyncio
async def test_plan_with_no_tasks_for_today_records_a_terminal_history(tmp_path: Path) -> None:
    engine, plan = make_plan(tmp_path, days=[0])

    record = await engine.start_plan(plan.id, "scheduled", datetime(2026, 10, 13, 9, 0, tzinfo=UTC))  # 周二

    assert record.state.value == "completed"
    assert record.message == "当天没有匹配“运行周几”的软件任务"
    assert engine.history.runs[0].id == record.id


@pytest.mark.asyncio
async def test_disabled_scheduled_plan_does_not_attempt_a_trigger(tmp_path: Path) -> None:
    engine, plan = make_plan(tmp_path, days=list(range(7)))
    plan.scheduled_enabled = False
    plan.time_slots = [TimeSlot(time="09:00", days=[0])]
    engine.config.plans = [plan]
    scheduler = QueueScheduler(engine)
    attempted: list[str] = []

    async def capture(*args, **kwargs):
        attempted.append("started")

    engine.start_plan = capture  # type: ignore[method-assign]
    await scheduler.tick(datetime(2026, 10, 12, 9, 0, tzinfo=UTC))

    assert attempted == []
    assert engine.config.fired_slots == []


@pytest.mark.asyncio
async def test_daily_first_startup_uses_persisted_local_date(tmp_path: Path) -> None:
    engine, plan = make_plan(tmp_path, days=list(range(7)))
    plan.startup_mode = StartupMode.DAILY_FIRST
    engine.config.plans = [plan]
    scheduler = QueueScheduler(engine)
    started: list[str] = []

    async def capture(plan_id: str, source: str, due_at=None):
        started.append(f"{plan_id}:{source}")

    engine.start_plan = capture  # type: ignore[method-assign]
    await scheduler.run_startup_once()
    assert started == [f"{plan.id}:startup"]
    assert engine.config.startup_fired_dates[plan.id] == datetime.now().astimezone().date().isoformat()


@pytest.mark.asyncio
async def test_completion_action_uses_dispatcher_only_after_success(tmp_path: Path) -> None:
    class Dispatcher:
        def __init__(self) -> None:
            self.actions: list[CompletionAction] = []

        async def execute(self, action: CompletionAction) -> None:
            self.actions.append(action)

    engine, plan = make_plan(tmp_path, days=list(range(7)))
    dispatcher = Dispatcher()
    engine._completion_dispatcher = dispatcher  # type: ignore[assignment]
    plan.completion_action = CompletionAction.SLEEP
    record = await engine.start_plan(plan.id, "scheduled", datetime(2026, 10, 12, 9, 0, tzinfo=UTC))
    record.state = RunState.COMPLETED
    for item in record.items:
        item.state = ItemState.SUCCEEDED
    engine.history.runs = [record]

    async def immediate(seconds: int, status: str) -> None:
        assert engine._pending_completion is not None
        engine._pending_completion["status"] = status
        engine._pending_completion["remainingSeconds"] = 0

    engine._completion_countdown = immediate  # type: ignore[method-assign]
    await engine._wait_then_complete_action(record, plan)

    assert dispatcher.actions == [CompletionAction.SLEEP]
    assert record.completion_action_status == "已执行"
