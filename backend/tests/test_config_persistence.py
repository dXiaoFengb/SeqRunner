from __future__ import annotations

import asyncio
import json
from datetime import UTC, datetime
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.engine import RunnerEngine
from app.events import EventHub
from app.main import create_app
from app.models import (
    AppConfig,
    PlanTask,
    QueueDefinition,
    SchedulePlan,
    StartupMode,
    TaskDefinition,
    TimeSlot,
)
from app.scheduler import QueueScheduler
from app.storage import JsonStore


def seed_app(tmp_path: Path) -> tuple[object, Path]:
    data_directory = tmp_path / "data"
    task = TaskDefinition(name="原程序")
    original = AppConfig(queues=[QueueDefinition(name="原脚本", task_ids=[task.id])], tasks=[task])
    JsonStore(data_directory).save_config(original)
    return create_app(data_directory, instance_id="config-persistence"), data_directory


def fail_save(_config: AppConfig) -> None:
    raise OSError("模拟配置写入失败")


@pytest.mark.parametrize("kind", ["config", "queue", "task"])
def test_api_save_failure_keeps_runtime_disk_and_config_events_unchanged(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, kind: str
) -> None:
    app, data_directory = seed_app(tmp_path)
    with TestClient(app) as client:
        engine = app.state.engine
        if kind == "queue":
            draft = TaskDefinition(name="待编排程序")
            assert client.post("/api/tasks", json=draft.model_dump(mode="json")).status_code == 200
        original = engine.config.model_dump(mode="json")
        original_config_file = (data_directory / "config.json").read_bytes()
        published: list[tuple[str, dict[str, object]]] = []

        async def capture(event_type: str, data: dict[str, object]) -> None:
            published.append((event_type, data))

        monkeypatch.setattr(engine.events, "publish", capture)
        monkeypatch.setattr(engine.store, "save_config", fail_save)
        if kind == "config":
            replacement = TaskDefinition(name="新程序")
            response = client.put(
                "/api/config",
                json=AppConfig(queues=[QueueDefinition(name="新脚本", task_ids=[replacement.id])], tasks=[replacement]).model_dump(mode="json"),
            )
        elif kind == "queue":
            response = client.post("/api/queues", json=QueueDefinition(name="新脚本", task_ids=[draft.id]).model_dump(mode="json"))
        else:
            response = client.post("/api/tasks", json=TaskDefinition(name="新程序").model_dump(mode="json"))

        assert response.status_code == 500
        assert client.get("/api/config").json() == original
        assert engine.config.model_dump(mode="json") == original
        assert (data_directory / "config.json").read_bytes() == original_config_file
        assert not [event for event in published if event[0] == "config"]


def test_api_updates_persist_then_publish_config_events(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    app, data_directory = seed_app(tmp_path)
    with TestClient(app) as client:
        engine = app.state.engine
        published: list[tuple[str, dict[str, object]]] = []

        async def capture(event_type: str, data: dict[str, object]) -> None:
            published.append((event_type, data))

        monkeypatch.setattr(engine.events, "publish", capture)
        replacement = TaskDefinition(name="替换程序")
        response = client.put(
            "/api/config",
            json=AppConfig(
                queues=[QueueDefinition(name="替换脚本", task_ids=[replacement.id])], tasks=[replacement]
            ).model_dump(mode="json"),
        )
        assert response.status_code == 200

        draft = TaskDefinition(name="待编排程序")
        assert client.post("/api/tasks", json=draft.model_dump(mode="json")).status_code == 200
        assert client.post(
            "/api/queues", json=QueueDefinition(name="新增脚本", task_ids=[draft.id]).model_dump(mode="json")
        ).status_code == 200

        config_events = [data for event_type, data in published if event_type == "config"]
        assert len(config_events) == 3
        assert config_events[-1] == engine.config.model_dump(mode="json")
        assert json.loads((data_directory / "config.json").read_text(encoding="utf-8")) == engine.config.model_dump(mode="json")


@pytest.mark.asyncio
async def test_concurrent_config_updates_keep_both_appends(tmp_path: Path) -> None:
    store = JsonStore(tmp_path / "data")
    engine = RunnerEngine(store, EventHub())
    first = TaskDefinition(name="程序一")
    second = TaskDefinition(name="程序二")
    first_published = asyncio.Event()
    release_first_publish = asyncio.Event()
    published = 0

    async def hold_first_publish(_event_type: str, _data: dict[str, object]) -> None:
        nonlocal published
        published += 1
        if published == 1:
            first_published.set()
            await release_first_publish.wait()

    engine.events.publish = hold_first_publish  # type: ignore[method-assign]
    first_update = asyncio.create_task(
        engine.update_config(lambda candidate: candidate.tasks.append(first.model_copy(deep=True)))
    )
    await first_published.wait()
    second_update = asyncio.create_task(
        engine.update_config(lambda candidate: candidate.tasks.append(second.model_copy(deep=True)))
    )
    await asyncio.sleep(0)
    release_first_publish.set()
    await asyncio.gather(first_update, second_update)

    assert [task.name for task in engine.config.tasks] == ["程序一", "程序二"]
    assert [task.name for task in store.load_config().tasks] == ["程序一", "程序二"]


@pytest.mark.asyncio
async def test_scheduler_keeps_pending_slots_when_persistence_fails(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    plan = SchedulePlan(name="定时", items=[PlanTask(script_id="one")], time_slots=[TimeSlot(time="09:00", days=[0])])
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[plan]))
    engine = RunnerEngine(store, EventHub())
    scheduler = QueueScheduler(engine)
    started: list[str] = []
    events: list[tuple[str, dict[str, object]]] = []
    original_save = engine.store.save_config

    async def capture_start(plan_id: str, source: str, due_at: datetime | None = None) -> None:
        started.append(f"{plan_id}:{source}")

    async def capture_event(event_type: str, data: dict[str, object]) -> None:
        events.append((event_type, data))

    engine.start_plan = capture_start  # type: ignore[method-assign]
    engine.events.publish = capture_event  # type: ignore[method-assign]
    monkeypatch.setattr(engine.store, "save_config", fail_save)
    moment = datetime(2026, 10, 12, 9, 0, tzinfo=UTC)

    await scheduler.tick(moment)
    await scheduler.tick(moment)

    assert started == [f"{plan.id}:scheduled"]
    assert engine.config.fired_slots == []
    assert store.load_config().fired_slots == []
    assert not [event for event in events if event[0] == "config"]
    assert any("保存失败" in str(event[1]["message"]) for event in events if event[0] == "schedule")

    monkeypatch.setattr(engine.store, "save_config", original_save)
    await scheduler.tick(moment)

    assert started == [f"{plan.id}:scheduled"]
    assert len(engine.config.fired_slots) == 1
    assert store.load_config().fired_slots == engine.config.fired_slots


@pytest.mark.asyncio
async def test_startup_marker_failure_does_not_mutate_runtime_config(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    plan = SchedulePlan(name="启动计划", items=[PlanTask(script_id="one")], startup_mode=StartupMode.DAILY_FIRST)
    store = JsonStore(tmp_path / "data")
    store.save_config(AppConfig(plans=[plan]))
    engine = RunnerEngine(store, EventHub())
    scheduler = QueueScheduler(engine)
    started: list[str] = []
    events: list[tuple[str, dict[str, object]]] = []

    async def capture_start(plan_id: str, source: str, due_at: datetime | None = None) -> None:
        started.append(f"{plan_id}:{source}")

    async def capture_event(event_type: str, data: dict[str, object]) -> None:
        events.append((event_type, data))

    engine.start_plan = capture_start  # type: ignore[method-assign]
    engine.events.publish = capture_event  # type: ignore[method-assign]
    monkeypatch.setattr(engine.store, "save_config", fail_save)

    await scheduler.run_startup_once()
    await scheduler.run_startup_once()

    assert started == [f"{plan.id}:startup"]
    assert engine.config.startup_fired_dates == {}
    assert store.load_config().startup_fired_dates == {}
    assert any("每日标记保存失败" in str(event[1]["message"]) for event in events if event[0] == "schedule")
