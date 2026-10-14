from __future__ import annotations

import asyncio
import logging
from contextlib import asynccontextmanager
from datetime import UTC, datetime
from pathlib import Path

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

from .engine import ConfirmRejectedError, RunBusyError, RunnerEngine, RunValidationError
from .events import EventHub
from .models import AppConfig, QueueDefinition, StartupMode, TaskDefinition
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


class AdbCheckIn(BaseModel):
    executable: str
    address: str


def validate_single_software_config(config: AppConfig) -> None:
    task_ids = {task.id for task in config.tasks}
    problems: list[str] = []
    seen: set[str] = set()
    for script in config.queues:
        if script.time_slots:
            problems.append(f"软件脚本“{script.name}”不能设置定时；请在计划管理创建多软件计划")
        if len(script.task_ids) != 1:
            problems.append(f"软件脚本“{script.name}”必须且只能包含一个程序")
            continue
        task_id = script.task_ids[0]
        if task_id not in task_ids:
            problems.append(f"软件脚本“{script.name}”引用的程序不存在")
        elif task_id in seen:
            problems.append(f"程序不能同时属于多个软件脚本：{task_id}")
        seen.add(task_id)
    if problems:
        raise ValueError("；".join(problems))


def validate_schedule_plans(config: AppConfig) -> None:
    """校验计划引用与启用时间槽，避免依赖前端提示维持调度不变量。"""
    scripts = {script.id for script in config.queues}
    startup_plans = [plan.name for plan in config.plans if plan.startup_mode != StartupMode.NEVER]
    problems: list[str] = []
    if len(startup_plans) > 1:
        problems.append("仅允许一个计划设置为“启动时运行”：" + "、".join(startup_plans))
    occupied: list[tuple[str, str, set[int]]] = []
    for plan in config.plans:
        script_ids = [item.script_id for item in plan.items]
        # 计划页允许先保存空任务或单任务草稿，再由用户补全定时和任务。
        # 真正启动时 RunnerEngine.start_plan 会保持至少两个软件的运行不变量。
        if len(set(script_ids)) != len(script_ids):
            problems.append(f"计划“{plan.name}”不能重复添加同一软件脚本")
        missing = [script_id for script_id in script_ids if script_id not in scripts]
        if missing:
            problems.append(f"计划“{plan.name}”引用了不存在的软件脚本")
        if not plan.scheduled_enabled:
            continue
        for slot in plan.time_slots:
            if not slot.enabled:
                continue
            days = set(slot.days)
            for other_name, other_time, other_days in occupied:
                if slot.time == other_time and days & other_days:
                    overlap = "、".join("周" + "一二三四五六日"[day] for day in sorted(days & other_days))
                    problems.append(
                        f"计划“{plan.name}”与“{other_name}”在 {slot.time}（{overlap}）同时触发，不能同时保存"
                    )
            occupied.append((plan.name, slot.time, days))
    if problems:
        raise ValueError("；".join(problems))


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

        await scheduler.run_startup_once()
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
            raise HTTPException(409, "软件脚本运行中，配置将于本轮结束后再修改")
        try:
            validate_single_software_config(config)
            validate_schedule_plans(config)
            engine.config = config
            await engine.save_config()
        except ValueError as error:
            raise HTTPException(422, str(error)) from error
        except OSError as error:
            raise HTTPException(500, f"配置保存失败，原配置未被覆盖：{error}") from error
        return engine.config

    @app.post("/api/queues", response_model=QueueDefinition)
    async def add_queue(queue: QueueDefinition):
        candidate = engine.config.model_copy(deep=True)
        candidate.queues.append(queue)
        try:
            validate_single_software_config(candidate)
            validate_schedule_plans(candidate)
        except ValueError as error:
            raise HTTPException(422, str(error)) from error
        engine.config = candidate
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
            raise HTTPException(404, "软件脚本不存在") from None
        except RunBusyError as error:
            raise HTTPException(409, str(error)) from error
        except RunValidationError as error:
            raise HTTPException(422, {"message": "软件脚本配置无效", "problems": error.problems}) from error

    @app.post("/api/start-all")
    async def start_all():
        try:
            return await engine.start_all()
        except RunBusyError as error:
            raise HTTPException(409, str(error)) from error
        except RunValidationError as error:
            raise HTTPException(422, {"message": "全部脚本尚未完成配置", "problems": error.problems}) from error

    @app.post("/api/adb-check")
    async def adb_check(payload: AdbCheckIn):
        """只读取 ADB 连通状态，不改写模拟器、MAA 或设备配置。"""
        if not payload.executable.strip() or not payload.address.strip():
            raise HTTPException(422, "请先填写 ADB 程序和安卓模拟器连接地址")
        try:
            process = await asyncio.create_subprocess_exec(
                payload.executable, "-s", payload.address, "get-state",
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=5)
        except (OSError, TimeoutError) as error:
            return {"ok": False, "message": f"连接检测失败：{error}", "checkedAt": datetime.now(UTC).isoformat()}
        output = stdout.decode(errors="replace").strip()
        detail = stderr.decode(errors="replace").strip() or output
        return {
            "ok": process.returncode == 0 and output == "device",
            "message": "安卓模拟器已连接" if process.returncode == 0 and output == "device" else (detail or "设备未就绪"),
            "checkedAt": datetime.now(UTC).isoformat(),
        }

    @app.post("/api/stop")
    async def stop():
        await engine.stop()
        return {"status": "stopping"}

    @app.post("/api/cancel-completion-action")
    async def cancel_completion_action():
        await engine.cancel_pending_completion_action("用户已取消完成后操作")
        return {"status": "cancelled"}

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
