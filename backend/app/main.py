from __future__ import annotations

import asyncio
import logging
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

from .engine import ConfirmRejectedError, RunBusyError, RunnerEngine, RunValidationError
from .events import EventHub
from .models import AppConfig, QueueDefinition, TaskDefinition
from .presets import apply_preset, public_presets
from .scheduler import QueueScheduler
from .storage import JsonStore


class QueueStartIn(BaseModel):
    queue_id: str


class ConfirmIn(BaseModel):
    task_id: str


class PresetApplyIn(BaseModel):
    executable: str
    task_id: str
    name: str = ""


def create_app(data_directory: Path | None = None, *, instance_id: str = "development") -> FastAPI:
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
    app.state.events = hub
    app.state.instance_id = instance_id
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["http://127.0.0.1:5173", "http://localhost:5173", "null"],
        allow_credentials=False,
        allow_methods=["GET", "POST", "PUT", "OPTIONS"],
        allow_headers=["content-type"],
    )

    @app.get("/health")
    async def health():
        return {"status": "ok", "instanceId": app.state.instance_id}

    @app.get("/api/presets")
    async def presets():
        return public_presets()

    @app.post("/api/presets/{preset_id}/apply", response_model=TaskDefinition)
    async def preset_apply(preset_id: str, payload: PresetApplyIn):
        try:
            return apply_preset(preset_id, payload.executable, task_id=payload.task_id, name=payload.name)
        except ValueError as error:
            raise HTTPException(422, str(error)) from error

    @app.get("/api/config", response_model=AppConfig)
    async def get_config():
        return engine.config

    @app.put("/api/config", response_model=AppConfig)
    async def put_config(config: AppConfig):
        if engine.is_busy:
            raise HTTPException(409, "队列运行中，配置将于本轮结束后再修改")
        try:
            engine.config = config
            await engine.save_config()
        except OSError as error:
            raise HTTPException(500, f"配置保存失败，原配置未被覆盖：{error}") from error
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

    @app.post("/api/start-all")
    async def start_all():
        try:
            return await engine.start_all()
        except RunBusyError as error:
            raise HTTPException(409, str(error)) from error
        except RunValidationError as error:
            raise HTTPException(422, {"message": "全部脚本尚未完成配置", "problems": error.problems}) from error

    @app.post("/api/stop")
    async def stop():
        await engine.stop()
        return {"status": "stopping"}

    @app.post("/api/confirm")
    async def confirm(payload: ConfirmIn):
        try:
            await engine.confirm(payload.task_id)
        except ConfirmRejectedError as error:
            raise HTTPException(409, str(error)) from error
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
        subscription = hub.subscribe()
        receiver: asyncio.Task[dict] | None = None
        event_sender: asyncio.Task[dict] | None = None
        try:
            await socket.send_json({"type": "runtime", "data": engine.snapshot()})
            receiver = asyncio.create_task(socket.receive_json())
            event_sender = asyncio.create_task(anext(subscription))
            while True:
                done, _ = await asyncio.wait(
                    {receiver, event_sender}, return_when=asyncio.FIRST_COMPLETED
                )
                if receiver in done:
                    message = receiver.result()
                    if isinstance(message, dict) and message.get("type") == "ping":
                        await socket.send_json({"type": "pong", "data": {"nonce": message.get("nonce")}})
                    receiver = asyncio.create_task(socket.receive_json())
                if event_sender in done:
                    await socket.send_json(event_sender.result())
                    event_sender = asyncio.create_task(anext(subscription))
        except (WebSocketDisconnect, asyncio.CancelledError):
            return
        except Exception:
            logger.exception("实时连接异常，已关闭本次会话")
            with __import__("contextlib").suppress(RuntimeError):
                await socket.close(code=1011, reason="服务器实时通信异常")
        finally:
            for task in (receiver, event_sender):
                if task:
                    task.cancel()
            await asyncio.gather(*(task for task in (receiver, event_sender) if task), return_exceptions=True)
            await subscription.aclose()

    return app


app = create_app()
