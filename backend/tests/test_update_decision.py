import asyncio

import pytest

from app.engine import ConfirmRejectedError, RunnerEngine
from app.events import EventHub
from app.models import ItemRecord, ItemState, RunRecord
from app.storage import JsonStore


@pytest.mark.asyncio
async def test_update_wait_and_manual_completion_are_separate(tmp_path) -> None:
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    item = ItemRecord(task_id="t", task_name="fixture")
    engine.current_run = RunRecord(queue_id="q", queue_name="fixture", trigger_source="manual", items=[item])
    engine._active_task_id = "t"
    engine._manual_accepting_task_id = "t"
    engine._runner_task = asyncio.create_task(engine._wait_for_update_decision(item, "等待更新"))
    await asyncio.sleep(0)
    assert item.state == ItemState.WAITING_UPDATE
    with pytest.raises(ConfirmRejectedError):
        await engine.confirm("t")
    with pytest.raises(ConfirmRejectedError):
        await engine.decide_update("t", "unknown")
    await engine.decide_update("t", "wait")
    assert await engine._runner_task == "wait"
    assert not engine._manual_confirmed.is_set()


@pytest.mark.asyncio
async def test_stop_wins_over_an_update_decision(tmp_path) -> None:
    engine = RunnerEngine(JsonStore(tmp_path / "data"), EventHub())
    item = ItemRecord(task_id="t", task_name="fixture")
    engine.current_run = RunRecord(queue_id="q", queue_name="fixture", trigger_source="manual", items=[item])
    engine._active_task_id = "t"
    engine._runner_task = asyncio.create_task(engine._wait_for_update_decision(item, "等待更新"))
    await asyncio.sleep(0)
    await engine.decide_update("t", "fail")
    engine._stop_requested.set()
    assert await engine._runner_task == "stop"
