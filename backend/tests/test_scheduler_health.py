from __future__ import annotations

import asyncio
import logging
import threading
from datetime import UTC, datetime

import pytest
from fastapi.testclient import TestClient

from app.main import SchedulerHealth, _run_scheduler_loop, create_app
from app.scheduler import QueueScheduler


class RecordingHub:
    def __init__(self, *, fail_publish: bool = False) -> None:
        self.events: list[tuple[str, dict[str, object]]] = []
        self.fail_publish = fail_publish

    async def publish(self, event_type: str, data: dict[str, object]) -> None:
        self.events.append((event_type, data))
        if self.fail_publish:
            raise OSError("event channel unavailable")


class FailingThenHealthyScheduler:
    def __init__(self) -> None:
        self.calls = 0
        self.second_started = asyncio.Event()
        self.release_second = asyncio.Event()
        self.second_completed = asyncio.Event()

    async def tick(self, _now) -> None:
        self.calls += 1
        if self.calls == 1:
            raise RuntimeError("controlled failure")
        if self.calls == 2:
            self.second_started.set()
            await self.release_second.wait()
            self.second_completed.set()


def test_scheduler_loop_recovers_and_restores_health(caplog: pytest.LogCaptureFixture) -> None:
    async def exercise() -> tuple[SchedulerHealth, RecordingHub, FailingThenHealthyScheduler]:
        health = SchedulerHealth()
        hub = RecordingHub()
        scheduler = FailingThenHealthyScheduler()
        paused = asyncio.Event()

        async def sleep(_seconds: float) -> None:
            if scheduler.calls == 1:
                return
            await paused.wait()

        task = asyncio.create_task(
            _run_scheduler_loop(scheduler, health, hub, logging.getLogger("scheduler-health-test"), sleep=sleep)
        )
        await scheduler.second_started.wait()
        assert health.snapshot() == {
            "status": "degraded",
            "lastError": "tick: RuntimeError",
            "failedAt": health.failed_at,
        }
        assert health.failed_at is not None
        assert datetime.fromisoformat(health.failed_at).tzinfo == UTC
        assert hub.events == [
            ("schedule", {"planId": None, "message": "定时调度检查异常，下一轮会自动重试（RuntimeError）"})
        ]

        scheduler.release_second.set()
        await scheduler.second_completed.wait()
        await asyncio.sleep(0)
        assert health.snapshot() == {"status": "healthy", "lastError": None, "failedAt": None}

        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        return health, hub, scheduler

    with caplog.at_level(logging.ERROR):
        health, hub, scheduler = asyncio.run(exercise())

    assert scheduler.calls == 2
    assert health.status == "healthy"
    assert len(hub.events) == 1
    assert "定时调度检查失败" in caplog.text


def test_scheduler_loop_survives_failed_error_notification(caplog: pytest.LogCaptureFixture) -> None:
    async def exercise() -> FailingThenHealthyScheduler:
        health = SchedulerHealth()
        hub = RecordingHub(fail_publish=True)
        scheduler = FailingThenHealthyScheduler()
        paused = asyncio.Event()

        async def sleep(_seconds: float) -> None:
            if scheduler.calls == 1:
                return
            await paused.wait()

        task = asyncio.create_task(
            _run_scheduler_loop(scheduler, health, hub, logging.getLogger("scheduler-notify-test"), sleep=sleep)
        )
        await scheduler.second_started.wait()
        scheduler.release_second.set()
        await scheduler.second_completed.wait()
        await asyncio.sleep(0)
        assert health.status == "healthy"
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        return scheduler

    with caplog.at_level(logging.ERROR):
        scheduler = asyncio.run(exercise())

    assert scheduler.calls == 2
    assert "调度错误通知失败" in caplog.text


def test_scheduler_loop_propagates_cancellation_without_degrading_health() -> None:
    class CancelledScheduler:
        async def tick(self, _now) -> None:
            raise asyncio.CancelledError()

    async def exercise() -> SchedulerHealth:
        health = SchedulerHealth()
        with pytest.raises(asyncio.CancelledError):
            await _run_scheduler_loop(CancelledScheduler(), health, RecordingHub(), logging.getLogger("scheduler-cancel-test"))
        return health

    assert asyncio.run(exercise()).snapshot() == {"status": "healthy", "lastError": None, "failedAt": None}


def test_startup_failure_keeps_api_available_and_marks_health_degraded(tmp_path, monkeypatch: pytest.MonkeyPatch) -> None:
    tick_started = threading.Event()

    async def fail_startup(self) -> None:
        raise RuntimeError("controlled startup failure")

    async def wait_in_tick(self, _now) -> None:
        tick_started.set()
        await asyncio.Future()

    monkeypatch.setattr(QueueScheduler, "run_startup_once", fail_startup)
    monkeypatch.setattr(QueueScheduler, "tick", wait_in_tick)
    app = create_app(tmp_path / "data", instance_id="startup-failure")

    with TestClient(app) as client:
        assert tick_started.wait(timeout=2)
        assert client.get("/health").json() == {
            "status": "ok",
            "instanceId": "startup-failure",
            "scheduler": {
                "status": "degraded",
                "lastError": "startup: RuntimeError",
                "failedAt": app.state.scheduler_health.failed_at,
            },
        }
        assert app.state.scheduler_health.failed_at is not None
