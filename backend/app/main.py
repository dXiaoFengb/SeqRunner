from __future__ import annotations

import asyncio
import logging
from collections.abc import Awaitable, Callable
from contextlib import asynccontextmanager
from dataclasses import asdict
from datetime import UTC, date, datetime
from pathlib import Path

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

from .engine import ConfirmRejectedError, RunBusyError, RunnerEngine, RunValidationError
from .events import EventHub
from .maa_region_capture import read_maa_region, run_helper
from .models import AppConfig, QueueDefinition, StartupMode, TaskDefinition, UiLogRegion
from .presets import apply_preset, public_presets
from .processes import OwnedProcess
from .scheduler import QueueScheduler
from .storage import JsonStore
from .ui_log_region import list_windows_for_executable


class DetailCleanupIn(BaseModel):
    cutoff: date
    ids: list[str] | None = None


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


class WindowListIn(BaseModel):
    executable: str


class RegionWindowIn(BaseModel):
    pid: int
    created_at: float
    region: UiLogRegion


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
        # 计划页允许先保存空任务草稿；有一个有效任务即可作为单软件计划执行。
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


class SchedulerHealth:
    """Keep scheduler failures observable without making the API unavailable."""

    def __init__(self) -> None:
        self.status = "healthy"
        self.last_error: str | None = None
        self.failed_at: str | None = None

    def mark_degraded(self, phase: str, error: Exception) -> None:
        self.status = "degraded"
        self.last_error = f"{phase}: {type(error).__name__}"
        self.failed_at = datetime.now(UTC).isoformat()

    def mark_healthy(self) -> None:
        self.status = "healthy"
        self.last_error = None
        self.failed_at = None

    def snapshot(self) -> dict[str, str | None]:
        return {
            "status": self.status,
            "lastError": self.last_error,
            "failedAt": self.failed_at,
        }


async def _report_scheduler_failure(
    health: SchedulerHealth, hub: EventHub, logger: logging.Logger, phase: str, error: Exception
) -> None:
    health.mark_degraded(phase, error)
    label = "启动计划检查" if phase == "startup" else "定时调度检查"
    logger.exception("%s失败", label)
    try:
        await hub.publish(
            "schedule",
            {"planId": None, "message": f"{label}异常，下一轮会自动重试（{type(error).__name__}）"},
        )
    except asyncio.CancelledError:
        raise
    except Exception:
        logger.exception("调度错误通知失败")


async def _run_scheduler_loop(
    scheduler: QueueScheduler,
    health: SchedulerHealth,
    hub: EventHub,
    logger: logging.Logger,
    *,
    sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
) -> None:
    while True:
        try:
            await scheduler.tick(datetime.now(UTC).astimezone())
        except asyncio.CancelledError:
            raise
        except Exception as error:  # noqa: BLE001 - 后台调度异常不能终止后续检查。
            await _report_scheduler_failure(health, hub, logger, "tick", error)
        else:
            health.mark_healthy()
        await sleep(60)


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
    scheduler_health = SchedulerHealth()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        async def archive_loop() -> None:
            while True:
                try:
                    before = [(item, item.detail_log_status) for run in engine.history.runs for item in run.items]
                    before_run = [(run, run.run_log_status) for run in engine.history.runs]
                    errors = await asyncio.to_thread(engine.detail_logs.archive, engine.history)
                    for error in errors:
                        logger.warning("%s", error)
                    if (any(item.detail_log_status != status for item, status in before)
                            or any(run.run_log_status != status for run, status in before_run)):
                        engine.store.save_history(engine.history)
                except Exception:  # 日志维护失败不能影响调度。
                    logger.exception("详细日志维护失败")
                await asyncio.sleep(3600)

        try:
            await scheduler.run_startup_once()
        except asyncio.CancelledError:
            raise
        except Exception as error:  # noqa: BLE001 - 启动计划异常不能阻止服务启动。
            await _report_scheduler_failure(scheduler_health, hub, logger, "startup", error)
        task = asyncio.create_task(_run_scheduler_loop(scheduler, scheduler_health, hub, logger))
        archive_task = asyncio.create_task(archive_loop())
        try:
            yield
        finally:
            await engine.stop()
            await engine.wait_until_idle()
            task.cancel()
            archive_task.cancel()

    app = FastAPI(title="顺序任务调度器", lifespan=lifespan)
    app.state.engine = engine
    app.state.events = hub
    app.state.instance_id = instance_id
    app.state.scheduler_health = scheduler_health
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["http://127.0.0.1:5173", "http://localhost:5173", "null"],
        allow_credentials=False,
        allow_methods=["GET", "POST", "PUT", "OPTIONS"],
        allow_headers=["content-type"],
    )

    @app.get("/health")
    async def health():
        return {
            "status": "ok",
            "instanceId": app.state.instance_id,
            "scheduler": app.state.scheduler_health.snapshot(),
        }

    @app.get("/api/presets")
    async def presets():
        return public_presets()

    @app.post("/api/ui-log/windows")
    async def ui_log_windows(payload: WindowListIn):
        if engine.is_busy:
            raise HTTPException(409, "运行期间不能更改日志区域")
        try:
            return [asdict(window) for window in await asyncio.to_thread(list_windows_for_executable, payload.executable)]
        except RuntimeError as error:
            raise HTTPException(422, str(error)) from error

    @app.post("/api/ui-log/select")
    async def ui_log_select(payload: RegionWindowIn):
        if engine.is_busy:
            raise HTTPException(409, "运行期间不能更改日志区域")
        try:
            selection = await run_helper("select", OwnedProcess(payload.pid, payload.created_at), payload.region)
            if selection.get("canceled"):
                return {"canceled": True}
            region = payload.region.model_copy(update=selection)
            region = UiLogRegion.model_validate(region.model_dump())
            return {"canceled": False, "region": region}
        except (RuntimeError, ValueError, TypeError, TimeoutError) as error:
            raise HTTPException(422, str(error)) from error

    @app.post("/api/ui-log/preview")
    async def ui_log_preview(payload: RegionWindowIn):
        if engine.is_busy:
            raise HTTPException(409, "运行期间不能预览日志区域")
        try:
            return {"text": await read_maa_region(OwnedProcess(payload.pid, payload.created_at), payload.region)}
        except (RuntimeError, ValueError, TypeError, TimeoutError) as error:
            raise HTTPException(422, str(error)) from error

    @app.post("/api/ui-log/frame")
    async def ui_log_frame(payload: RegionWindowIn):
        if engine.is_busy:
            raise HTTPException(409, "运行期间不能预览日志区域")
        try:
            return await run_helper("frame", OwnedProcess(payload.pid, payload.created_at), payload.region)
        except (RuntimeError, ValueError, TypeError, TimeoutError) as error:
            raise HTTPException(422, str(error)) from error

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
            await engine.replace_config(config)
        except ValueError as error:
            raise HTTPException(422, str(error)) from error
        except OSError as error:
            raise HTTPException(500, f"配置保存失败，原配置未被覆盖：{error}") from error
        return engine.config

    @app.post("/api/queues", response_model=QueueDefinition)
    async def add_queue(queue: QueueDefinition):
        try:
            def append_queue(candidate: AppConfig) -> None:
                candidate.queues.append(queue.model_copy(deep=True))
                validate_single_software_config(candidate)
                validate_schedule_plans(candidate)

            await engine.update_config(append_queue)
        except ValueError as error:
            raise HTTPException(422, str(error)) from error
        except OSError as error:
            raise HTTPException(500, f"配置保存失败，原配置未被覆盖：{error}") from error
        return queue

    @app.post("/api/tasks", response_model=TaskDefinition)
    async def add_task(task: TaskDefinition):
        try:
            await engine.update_config(lambda candidate: candidate.tasks.append(task.model_copy(deep=True)))
        except OSError as error:
            raise HTTPException(500, f"配置保存失败，原配置未被覆盖：{error}") from error
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

    @app.get("/api/history/{run_id}/details/{detail_id}")
    async def history_detail(run_id: str, detail_id: str, offset: int = 0):
        record = next((run for run in engine.history.runs if run.id == run_id), None)
        if not record or (record.run_log_id != detail_id and not any(detail_id in (item.detail_log_id, item.ui_log_id) for item in record.items)):
            raise HTTPException(404, "详细日志不存在")
        try:
            return await asyncio.to_thread(engine.detail_logs.page, detail_id, offset)
        except (OSError, ValueError) as error:
            raise HTTPException(404, str(error)) from error

    @app.post("/api/history/detail-cleanup/preview")
    async def preview_detail_cleanup(payload: DetailCleanupIn):
        items = engine.detail_logs.matching(engine.history, payload.cutoff)
        return {"count": len(items), "cutoff": payload.cutoff.isoformat(), "ids": [item.detail_log_id or item.ui_log_id for item in items]}

    @app.post("/api/history/detail-cleanup")
    async def cleanup_details(payload: DetailCleanupIn):
        items = engine.detail_logs.matching(engine.history, payload.cutoff)
        if payload.ids is None or set(payload.ids) != {item.detail_log_id or item.ui_log_id for item in items}:
            raise HTTPException(409, "清理范围已变化，请重新预览")
        removed = 0
        for item in items:
            try:
                for id_field, status_field in (("detail_log_id", "detail_log_status"), ("ui_log_id", "ui_log_status")):
                    identifier = getattr(item, id_field)
                    if identifier:
                        await asyncio.to_thread(engine.detail_logs.delete, identifier)
                        setattr(item, id_field, None)
                        setattr(item, status_field, "详细日志已清理")
                removed += 1
            except (OSError, ValueError) as error:
                engine.store.save_history(engine.history)
                raise HTTPException(500, f"已清理 {removed} 条，后续清理失败：{error}") from error
        engine.store.save_history(engine.history)
        return {"count": removed}

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
