from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.engine import RunnerEngine
from app.events import EventHub
from app.models import AppConfig, PlanTask, SchedulePlan, TimeSlot
from app.scheduler import QueueScheduler
from app.storage import JsonStore


@pytest.mark.asyncio
async def test_scheduler_deduplicates_multiple_slots_and_persists(tmp_path: Path) -> None:
    plan = SchedulePlan(name="定时", items=[PlanTask(script_id="one"), PlanTask(script_id="two")], time_slots=[TimeSlot(time="09:00", days=[0]), TimeSlot(time="09:00", days=[0])])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[plan]))
    engine = RunnerEngine(store, EventHub(), cooldown_seconds=0.01)
    scheduler = QueueScheduler(engine)
    started: list[str] = []

    async def capture(plan_id: str, source: str, due_at=None):
        started.append(f"{plan_id}:{source}")

    engine.start_plan = capture  # type: ignore[method-assign]
    moment = datetime(2026, 10, 12, 9, 0, tzinfo=UTC)
    await scheduler.tick(moment)
    await scheduler.tick(moment)

    assert started == [f"{plan.id}:scheduled"]
    assert len(engine.config.fired_slots) == 2


@pytest.mark.asyncio
async def test_busy_scheduler_marks_slot_without_queueing(tmp_path: Path) -> None:
    plan = SchedulePlan(name="定时", items=[PlanTask(script_id="one"), PlanTask(script_id="two")], time_slots=[TimeSlot(time="09:00", days=[0])])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[plan]))
    engine = RunnerEngine(store, EventHub())
    scheduler = QueueScheduler(engine)
    class BusyTask:
        def done(self) -> bool:
            return False

    engine._runner_task = BusyTask()  # type: ignore[assignment]

    await scheduler.tick(datetime(2026, 10, 12, 9, 0, tzinfo=UTC))

    assert len(engine.config.fired_slots) == 1


@pytest.mark.asyncio
async def test_scheduler_uses_due_time_not_plan_display_order(tmp_path: Path) -> None:
    late_plan = SchedulePlan(name="显示在前", items=[PlanTask(script_id="one")], time_slots=[TimeSlot(time="09:05", days=[0])])
    early_plan = SchedulePlan(name="显示在后", items=[PlanTask(script_id="two")], time_slots=[TimeSlot(time="09:00", days=[0])])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[late_plan, early_plan]))
    engine = RunnerEngine(store, EventHub(), cooldown_seconds=0.01)
    scheduler = QueueScheduler(engine)
    started: list[tuple[str, datetime]] = []

    async def capture(plan_id: str, source: str, due_at: datetime | None = None) -> None:
        assert source == "scheduled"
        assert due_at is not None
        started.append((plan_id, due_at))

    engine.start_plan = capture  # type: ignore[method-assign]
    await scheduler.tick(datetime(2026, 10, 12, 9, 6, tzinfo=UTC))

    assert started == [
        (early_plan.id, datetime(2026, 10, 12, 9, 0, tzinfo=UTC)),
        (late_plan.id, datetime(2026, 10, 12, 9, 5, tzinfo=UTC)),
    ]
    assert len(engine.config.fired_slots) == 2


@pytest.mark.asyncio
async def test_empty_due_plan_creates_a_safe_skip_record(tmp_path: Path) -> None:
    plan = SchedulePlan(name="未完成计划", time_slots=[TimeSlot(time="09:00", days=[0])])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[plan]))
    engine = RunnerEngine(store, EventHub())
    scheduler = QueueScheduler(engine)

    await scheduler.tick(datetime(2026, 10, 12, 9, 0, tzinfo=UTC))

    assert len(engine.config.fired_slots) == 1
    assert not engine.is_busy
    assert engine.history.runs[0].message == "计划尚未添加软件脚本，已跳过本次执行"
