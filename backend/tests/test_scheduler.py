from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.engine import RunnerEngine
from app.events import EventHub
from app.models import AppConfig, SchedulePlan, TimeSlot
from app.scheduler import QueueScheduler
from app.storage import JsonStore


@pytest.mark.asyncio
async def test_scheduler_deduplicates_multiple_slots_and_persists(tmp_path: Path) -> None:
    plan = SchedulePlan(name="定时", script_ids=["one", "two"], time_slots=[TimeSlot(time="09:00", days=[0]), TimeSlot(time="09:00", days=[0])])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[plan]))
    engine = RunnerEngine(store, EventHub(), cooldown_seconds=0.01)
    scheduler = QueueScheduler(engine)
    started: list[str] = []

    async def capture(plan_id: str, source: str):
        started.append(f"{plan_id}:{source}")

    engine.start_plan = capture  # type: ignore[method-assign]
    moment = datetime(2026, 10, 12, 9, 0, tzinfo=UTC)
    await scheduler.tick(moment)
    await scheduler.tick(moment)

    assert started == [f"{plan.id}:scheduled"]
    assert len(engine.config.fired_slots) == 2


@pytest.mark.asyncio
async def test_busy_scheduler_marks_slot_without_queueing(tmp_path: Path) -> None:
    plan = SchedulePlan(name="定时", script_ids=["one", "two"], time_slots=[TimeSlot(time="09:00", days=[0])])
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
