from __future__ import annotations

import asyncio
import logging
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from pydantic import BaseModel

from .engine import RunBusyError, RunnerEngine
from .events import EventHub
from .models import AppConfig, QueueDefinition, TaskDefinition
from .scheduler import QueueScheduler
from .storage import JsonStore


class QueueStartIn(BaseModel):
    queue_id: str


class ConfirmIn(BaseModel):
    task_id: str


def create_app(data_directory: Path | None = None) -> FastAPI:
    root = data_directory or Path(__file__).resolve().parents[2] / "data"
    logs = root / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    logger = logging.getLogger("sequential-runner")
    if not logger.handlers:
        logger.setLevel(logging.INFO)
        logger.addHandler(logging.FileHandler(logs / "backend.log", encoding="utf-8"))
    hub = EventHub()
    engine = RunnerEngine(JsonStore(root), hub, logger=logger)
    scheduler = QueueScheduler(engine)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        async def timer_loop() -> None:
            while True:
                await scheduler.tick(__import__("datetime").datetime.now())
                await asyncio.sleep(60)

        task = asyncio.create_task(timer_loop())
        try:
            yield
        finally:
            await engine.stop()
            await engine.wait_until_idle()
            task.cancel()

    app = FastAPI(title="顺序任务调度器", lifespan=lifespan)
    app.state.engine = engine

    @app.get("/health")
    async def health():
        return {"status": "ok"}

    @app.get("/api/config", response_model=AppConfig)
    async def get_config():
        return engine.config

    @app.put("/api/config", response_model=AppConfig)
    async def put_config(config: AppConfig):
        if engine.is_busy:
            raise HTTPException(409, "队列运行中，配置将于本轮结束后再修改")
        engine.config = config
        await engine.save_config()
        return engine.config

    @app.post("/api/queues", response_model=QueueDefinition)
    async def add_queue(queue: QueueDefinition):
        engine.config.queues.append(queue)
        await engine.save_config()
        return queue

    @app.post("/api/tasks", response_model=TaskDefinition)
    async def add_task(task: TaskDefinition):
        engine.config.tasks.append(task)
        await engine.save_config()
        return task

    @app.post("/api/start")
    async def start(payload: QueueStartIn):
        try:
            return await engine.start(payload.queue_id, "manual")
        except StopIteration:
            raise HTTPException(404, "队列不存在") from None
        except RunBusyError as error:
            raise HTTPException(409, str(error)) from error

    @app.post("/api/stop")
    async def stop():
        await engine.stop()
        return {"status": "stopping"}

    @app.post("/api/confirm")
    async def confirm(payload: ConfirmIn):
        await engine.confirm(payload.task_id)
        return {"status": "accepted"}

    @app.get("/api/history")
    async def history():
        return engine.history

    @app.get("/api/runtime")
    async def runtime():
        return engine.snapshot()

    @app.websocket("/ws")
    async def websocket(socket: WebSocket):
        await socket.accept()
        try:
            await socket.send_json({"type": "runtime", "data": engine.snapshot()})
            async for event in hub.subscribe():
                await socket.send_json(event)
        except WebSocketDisconnect:
            return

    return app


app = create_app()
