from __future__ import annotations

import asyncio
import copy
import logging
import os
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Literal

from .completion_actions import CompletionActionDispatcher
from .detail_logs import DetailCapture, DetailLogs
from .events import EventHub
from .logwatch import LogSignal, LogWatcher
from .maa_region_capture import read_maa_region
from .models import (
    CompletionAction,
    CompletionSource,
    ItemRecord,
    ItemState,
    QueueDefinition,
    RunRecord,
    RunState,
    SchedulePlan,
    TaskDefinition,
)
from .presets import PRESETS
from .processes import (
    OwnedProcess,
    capture_owned_process,
    force_stop_process_tree,
    process_tree_alive,
    request_graceful_close,
)
from .storage import JsonStore
from .ui_logs import WindowLogCapture, ocr_appended


class RunBusyError(RuntimeError):
    pass


class RunValidationError(RuntimeError):
    """运行前的完整性问题；调用方可将全部问题一次呈现给用户。"""

    def __init__(self, problems: list[str]) -> None:
        self.problems = problems
        super().__init__("；".join(problems))


class ConfirmRejectedError(RuntimeError):
    pass


class _CompletionCancelled(Exception):
    pass


class RunnerEngine:
    """单一运行器；配置在每轮开始时深拷贝，防止热更新改变执行顺序。"""

    def __init__(
        self,
        store: JsonStore,
        events: EventHub,
        *,
        cooldown_seconds: float = 10,
        close_grace_seconds: float = 3,
        logger: logging.Logger | None = None,
        completion_dispatcher: CompletionActionDispatcher | None = None,
    ) -> None:
        self.store = store
        self.events = events
        self.cooldown_seconds = cooldown_seconds
        self.close_grace_seconds = close_grace_seconds
        self.config = store.load_config()
        self.migration_notice = store.last_config_migration_notice
        self.history = store.load_history()
        self.detail_logs = DetailLogs(store.data_directory)
        self.current_run: RunRecord | None = None
        self._runner_task: asyncio.Task[None] | None = None
        self._stop_requested = asyncio.Event()
        self._manual_confirmed = asyncio.Event()
        self._owned_process: OwnedProcess | None = None
        self._auxiliary_owned_processes: list[OwnedProcess] = []
        self._active_task_id: str | None = None
        self._manual_accepting_task_id: str | None = None
        self._cooldown_until: datetime | None = None
        self._plan_by_run_id: dict[str, SchedulePlan] = {}
        self._completion_dispatcher = completion_dispatcher or CompletionActionDispatcher()
        self._completion_task: asyncio.Task[None] | None = None
        self._completion_cancel = asyncio.Event()
        self._pending_completion: dict[str, object] | None = None
        self._logger = logger or logging.getLogger("sequential-runner")

    @property
    def is_busy(self) -> bool:
        return self._runner_task is not None and not self._runner_task.done()

    async def save_config(self) -> None:
        self.store.save_config(self.config)
        await self.events.publish("config", self.config.model_dump(mode="json"))

    def queue_by_id(self, queue_id: str) -> QueueDefinition:
        return next(queue for queue in self.config.queues if queue.id == queue_id)

    def plan_by_id(self, plan_id: str) -> SchedulePlan:
        return next(plan for plan in self.config.plans if plan.id == plan_id)

    async def start(self, queue_id: str, source: Literal["manual", "scheduled"]) -> RunRecord:
        if self.is_busy:
            raise RunBusyError("已有软件脚本正在运行，当前触发被跳过")
        await self.cancel_pending_completion_action("新的运行已开始")
        queue = copy.deepcopy(self.queue_by_id(queue_id))
        task_book = {task.id: copy.deepcopy(task) for task in self.config.tasks}
        if len(queue.task_ids) != 1:
            raise RunValidationError([f"软件脚本“{queue.name}”必须且只能包含一个程序"])
        if queue.task_ids[0] not in task_book:
            raise RunValidationError([f"软件脚本“{queue.name}”引用的程序不存在"])
        selected = [task_book[item] for item in queue.task_ids if item in task_book]
        record = RunRecord(queue_id=queue.id, queue_name=queue.name, trigger_source=source)
        record.items = [
            ItemRecord(task_id=item.id, task_name=item.name, script_id=queue.id, script_name=queue.name)
            for item in selected
        ]
        self.current_run = record
        self._stop_requested.clear()
        self._manual_confirmed.clear()
        self._runner_task = asyncio.create_task(self._run(record, selected))
        await self._publish_state()
        return record

    async def start_all(self) -> RunRecord:
        """按配置中的脚本卡顺序展开为一条手动运行记录，不改写配置。"""
        if self.is_busy:
            raise RunBusyError("已有脚本正在运行，当前触发被跳过")
        await self.cancel_pending_completion_action("新的运行已开始")
        selected, items, problems = self._collect_scripts(copy.deepcopy(self.config.queues))
        if problems:
            raise RunValidationError(problems)
        if not selected:
            raise RunValidationError(["没有可运行的脚本程序"])
        record = RunRecord(
            queue_id="__all__",
            queue_name="全部脚本",
            trigger_source="manual",
            run_mode="all_scripts",
            items=items,
        )
        self.current_run = record
        self._stop_requested.clear()
        self._manual_confirmed.clear()
        self._runner_task = asyncio.create_task(self._run(record, selected))
        await self._publish_state()
        return record

    async def start_plan(
        self, plan_id: str, source: Literal["scheduled", "startup"], due_at: datetime | None = None
    ) -> RunRecord:
        """按计划中保存的软件顺序展开；始终复用唯一运行器。"""
        if self.is_busy:
            raise RunBusyError("已有计划正在运行，当前计划触发被跳过")
        await self.cancel_pending_completion_action("新的运行已开始")
        plan = copy.deepcopy(self.plan_by_id(plan_id))
        if not plan.items:
            record = RunRecord(
                queue_id=plan.id,
                queue_name=plan.name,
                trigger_source=source,
                run_mode="plan",
                plan_id=plan.id,
                items=[],
                completion_action=plan.completion_action,
            )
            record.state = RunState.COMPLETED
            record.ended_at = datetime.now(UTC)
            record.message = "计划尚未添加软件脚本，已跳过本次执行"
            self.history.runs.insert(0, record)
            self._save_new_history()
            self.current_run = record
            await self._publish_state()
            return record
        if len({item.script_id for item in plan.items}) != len(plan.items):
            raise RunValidationError([f"计划“{plan.name}”不能重复添加同一软件脚本"])
        script_book = {script.id: script for script in copy.deepcopy(self.config.queues)}
        missing = [item.script_id for item in plan.items if item.script_id not in script_book]
        if missing:
            raise RunValidationError([f"计划“{plan.name}”引用的软件脚本不存在"])
        weekday = (due_at or datetime.now().astimezone()).weekday()
        eligible = [item for item in plan.items if weekday in item.days]
        selected, items, problems = self._collect_scripts([script_book[item.script_id] for item in eligible])
        if problems:
            raise RunValidationError(problems)
        record = RunRecord(
            queue_id=plan.id,
            queue_name=plan.name,
            trigger_source=source,
            run_mode="plan",
            plan_id=plan.id,
            items=items,
            completion_action=plan.completion_action,
        )
        if not selected:
            record.state = RunState.COMPLETED
            record.ended_at = datetime.now(UTC)
            record.message = "当天没有匹配“运行周几”的软件任务"
            self.history.runs.insert(0, record)
            self._save_new_history()
            self.current_run = record
            await self._publish_state()
            return record
        self.current_run = record
        self._plan_by_run_id[record.id] = plan
        self._stop_requested.clear()
        self._manual_confirmed.clear()
        self._runner_task = asyncio.create_task(self._run(record, selected))
        await self._publish_state()
        return record

    def _collect_scripts(
        self, scripts: list[QueueDefinition]
    ) -> tuple[list[TaskDefinition], list[ItemRecord], list[str]]:
        task_book = {task.id: copy.deepcopy(task) for task in self.config.tasks}
        problems: list[str] = []
        selected: list[TaskDefinition] = []
        items: list[ItemRecord] = []
        for script in scripts:
            label = f"脚本“{script.name}”"
            if len(script.task_ids) != 1:
                problems.append(f"{label}必须且只能包含一个程序")
                continue
            task = task_book.get(script.task_ids[0])
            if task is None:
                problems.append(f"{label}引用的程序不存在")
                continue
            if not task.executable.strip():
                problems.append(f"{label}的“{task.name}”未选择程序路径")
                continue
            preset_problem = self._preset_problem(task)
            if preset_problem:
                problems.append(f"{label}的“{task.name}”{preset_problem}")
                continue
            selected.append(task)
            items.append(ItemRecord(task_id=task.id, task_name=task.name, script_id=script.id, script_name=script.name))
        return selected, items, problems

    async def record_schedule_skip(self, plan: SchedulePlan, due_at: datetime) -> None:
        message = f"计划“{plan.name}”在 {due_at.strftime('%Y-%m-%d %H:%M')} 因上一计划尚未完成而跳过"
        if self.current_run:
            self.current_run.scheduler_skips.append(message)
        await self.events.publish("schedule", {"planId": plan.id, "message": message})
        await self._publish_state()

    @staticmethod
    def _preset_problem(task: TaskDefinition) -> str | None:
        if not task.preset_id:
            return None
        preset = PRESETS.get(task.preset_id)
        if preset is None:
            return "选择了未知预设"
        if Path(task.executable).name.casefold() not in {
            name.casefold() for name in preset["executables"]
        }:
            return "的程序路径与预设不匹配"
        if not task.preset_evidence:
            return "的预设规则尚未应用"
        return None

    async def stop(self) -> None:
        if not self.is_busy:
            await self.cancel_pending_completion_action("用户已取消完成后操作")
            return
        self._stop_requested.set()
        if self.current_run:
            self.current_run.state = RunState.STOPPING
        await self._publish_state()

    async def confirm(self, task_id: str) -> None:
        if not self.is_busy or self._active_task_id != task_id:
            raise ConfirmRejectedError("当前没有等待该程序人工确认的运行")
        if self._manual_accepting_task_id != task_id:
            raise ConfirmRejectedError("当前程序不需要人工确认，或已进入下一阶段")
        self._manual_confirmed.set()

    async def wait_until_idle(self) -> None:
        if self._runner_task:
            with __import__("contextlib").suppress(asyncio.CancelledError):
                await self._runner_task

    async def _run(self, record: RunRecord, tasks: list[TaskDefinition]) -> None:
        try:
            for index, (item, task) in enumerate(zip(record.items, tasks, strict=True)):
                if self._stop_requested.is_set():
                    item.state = ItemState.STOPPED
                    break
                await self._run_item(record, item, task, has_next=index < len(tasks) - 1)
                if self._stop_requested.is_set():
                    break
            record.state = RunState.STOPPED if self._stop_requested.is_set() else RunState.COMPLETED
            record.ended_at = datetime.now(UTC)
        finally:
            self._active_task_id = None
            self._manual_accepting_task_id = None
            self._owned_process = None
            self._auxiliary_owned_processes = []
            self.history.runs.insert(0, record)
            self._save_new_history()
            await self._publish_state()
            plan = self._plan_by_run_id.pop(record.id, None)
            if plan and self._run_succeeded(record):
                self._schedule_completion_action(record, plan)

    def _save_new_history(self) -> None:
        removed = self.history.runs[200:]
        self.history.runs = self.history.runs[:200]
        self.store.save_history(self.history)
        self.detail_logs.delete_runs(removed)

    @staticmethod
    def _run_succeeded(record: RunRecord) -> bool:
        return record.state == RunState.COMPLETED and bool(record.items) and all(
            item.state == ItemState.SUCCEEDED for item in record.items
        )

    def _schedule_completion_action(self, record: RunRecord, plan: SchedulePlan) -> None:
        if plan.completion_action == CompletionAction.NONE:
            return
        self._completion_cancel = asyncio.Event()
        self._completion_task = asyncio.create_task(self._wait_then_complete_action(record, plan))

    async def cancel_pending_completion_action(self, reason: str) -> None:
        if not self._pending_completion:
            return
        self._completion_cancel.set()
        self._pending_completion["status"] = "cancelled"
        self._pending_completion["message"] = reason
        run_id = str(self._pending_completion["runId"])
        record = next((item for item in self.history.runs if item.id == run_id), None)
        if record:
            record.completion_action_status = f"已取消：{reason}"
            self.store.save_history(self.history)
        await self._publish_state()

    async def _wait_then_complete_action(self, record: RunRecord, plan: SchedulePlan) -> None:
        self._pending_completion = {
            "runId": record.id, "planId": plan.id, "planName": plan.name,
            "action": plan.completion_action.value, "status": "delaying",
            "remainingSeconds": plan.completion_delay_minutes * 60,
        }
        try:
            if plan.completion_delay_minutes:
                await self._completion_countdown(plan.completion_delay_minutes * 60, "delaying")
            self._pending_completion["status"] = "countdown"
            await self._completion_countdown(60, "countdown")
            self._pending_completion["status"] = "executing"
            self._pending_completion["remainingSeconds"] = 0
            await self._publish_state()
            await self._completion_dispatcher.execute(plan.completion_action)
            record.completion_action_status = "请求退出软件" if plan.completion_action == CompletionAction.EXIT_APP else "已执行"
            self._pending_completion["status"] = "executed"
        except _CompletionCancelled:
            return
        except Exception as error:  # noqa: BLE001 - system adapter errors must become a visible history result.
            record.completion_action_status = f"执行失败：{error}"
            if self._pending_completion:
                self._pending_completion["status"] = "failed"
                self._pending_completion["message"] = str(error)
        finally:
            self.store.save_history(self.history)
            await self._publish_state()

    async def _completion_countdown(self, seconds: int, status: str) -> None:
        for remaining in range(seconds, -1, -1):
            if self._completion_cancel.is_set():
                raise _CompletionCancelled()
            if self._pending_completion:
                self._pending_completion["status"] = status
                self._pending_completion["remainingSeconds"] = remaining
            await self._publish_state()
            if remaining:
                try:
                    await asyncio.wait_for(self._completion_cancel.wait(), timeout=1)
                except TimeoutError:
                    continue
                raise _CompletionCancelled()

    async def _run_item(
        self, record: RunRecord, item: ItemRecord, task: TaskDefinition, *, has_next: bool
    ) -> None:
        if not task.log_path:
            item.detail_log_status = "未配置日志路径"
            await self._run_item_inner(record, item, task, has_next=has_next)
            return
        capture = None
        try:
            capture = DetailCapture(self.detail_logs, task.log_path, item)
            capture.start()
        except Exception as error:
            item.detail_log_status = f"详细日志采集失败：{error}"
            self._logger.exception("详细日志采集启动失败")
        try:
            await self._run_item_inner(record, item, task, has_next=has_next)
        finally:
            if capture:
                try:
                    await capture.finish()
                except Exception as error:
                    item.detail_log_status = f"详细日志采集失败：{error}"
                    self._logger.exception("详细日志采集收尾失败")

    async def _run_item_inner(
        self, record: RunRecord, item: ItemRecord, task: TaskDefinition, *, has_next: bool
    ) -> None:
        item.state = ItemState.VALIDATING
        self._active_task_id = task.id
        self._manual_confirmed.clear()
        self._manual_accepting_task_id = None
        await self._log(item, "开始校验任务配置")
        await self._publish_state()
        executable = Path(task.executable)
        if not executable.exists():
            await self._fail_then_cool(item, "目标程序不存在", has_next=has_next, task=task)
            return
        watcher: LogWatcher | None = None
        if task.log_path:
            watcher = LogWatcher(
                task.log_path,
                task.success_log,
                task.success_log_mode,
                task.failure_log,
                task.failure_log_mode,
                success_rules=task.success_log_rules,
                failure_rules=task.failure_log_rules,
            )
            try:
                # 必须先取得游标，后启动目标进程，避免历史成功行误判本轮。
                await watcher.start_at_end()
            except (OSError, UnicodeError) as error:
                await self._fail_then_cool(item, f"日志读取失败：{error}", has_next=has_next, task=task)
                return
        if task.preset_id == "maa":
            await self._maa_adb_preflight(item, task)
        if task.preset_id == "maaend" and not await self._start_maaend_game(item, task):
            if self._stop_requested.is_set():
                item.state = ItemState.STOPPED
                await self._cleanup_auxiliary(item, task)
                return
            await self._fail_then_cool(item, "游戏启动失败", has_next=has_next, task=task)
            return
        try:
            item.state = ItemState.STARTING
            item.started_at = datetime.now(UTC)
            await self._publish_state()
            process = await asyncio.create_subprocess_exec(
                str(executable),
                *task.arguments,
                cwd=task.working_directory or str(executable.parent),
                creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0,
            )
            self._owned_process = await capture_owned_process(process.pid)
            await self._log(item, f"已启动受管进程 PID {process.pid}")
        except (OSError, RuntimeError, ValueError) as error:  # 启动错误是可记录的单项失败。
            await self._fail_then_cool(item, f"启动失败：{error}", has_next=has_next, task=task)
            return

        item.state = ItemState.WAITING_MANUAL if task.allow_manual_confirmation else ItemState.MONITORING
        self._manual_accepting_task_id = task.id if task.allow_manual_confirmation else None
        ui_capture = None
        if self._owned_process and (task.preset_id == "march7th" or (task.preset_id == "maa" and task.ui_log_region)):
            async def emit_ui_log(text: str) -> None:
                await self._publish_state()
                await self.events.publish("script_log", {"runId": record.id, "taskId": task.id, "text": text})

            if task.preset_id == "maa" and task.ui_log_region:
                async def read_maa(process: OwnedProcess) -> str:
                    return await read_maa_region(process, task.ui_log_region)

                ui_capture = WindowLogCapture(self.detail_logs, item, self._owned_process, emit_ui_log,
                                              reader=read_maa, notify=self._publish_state,
                                              source="画面识别（可能有错字或遗漏）", matcher=ocr_appended)
            else:
                ui_capture = WindowLogCapture(self.detail_logs, item, self._owned_process, emit_ui_log,
                                              notify=self._publish_state)
            ui_capture.start()
        elif task.preset_id == "maa":
            item.ui_log_status = "尚未设置 MAA 界面日志区域，请在脚本编辑页框选"
        else:
            item.ui_log_status = "当前预设暂无可靠的界面日志来源"
        await self._publish_state()
        try:
            outcome, detail, log_detail = await self._wait_for_outcome(process, task, watcher)
        finally:
            if ui_capture:
                try:
                    await ui_capture.finish()
                except Exception as error:
                    item.ui_log_status = f"界面日志采集失败：{error}"
                    self._logger.exception("界面日志采集收尾失败")
        item.ended_at = datetime.now(UTC)
        if log_detail:
            item.log_summary.append(log_detail)
            await self._log(item, log_detail)
        if outcome == "stopped":
            item.state = ItemState.STOPPED
            await self._log(item, "收到全局停止请求，仅收尾当前受管进程")
            await self._cleanup_owned(item)
            await self._cleanup_auxiliary(item, task)
            return
        if outcome == "success":
            self._manual_accepting_task_id = None
            item.state = ItemState.SUCCEEDED
            item.completion_source = detail  # type: ignore[assignment]
            await self._log(item, f"任务成功，来源：{detail}")
            await self._publish_state()
            await self._cleanup_owned(item)
            await self._cleanup_auxiliary(item, task)
            await self._finish_after_cleanup(item, ItemState.SUCCEEDED, has_next=has_next)
            return
        item.exit_code = process.returncode
        await self._fail_then_cool(item, str(detail), has_next=has_next, task=task)

    async def _maa_adb_preflight(self, item: ItemRecord, task: TaskDefinition) -> None:
        adapter = task.maa_adapter
        if not adapter or not adapter.adb_executable or not adapter.adb_address:
            await self._log(item, "未配置安卓模拟器 ADB 预检，继续由 MAA GUI 自行连接")
            return
        try:
            process = await asyncio.create_subprocess_exec(
                adapter.adb_executable, "-s", adapter.adb_address, "get-state",
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0,
            )
            stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=5)
            if process.returncode == 0 and stdout.decode(errors="replace").strip() == "device":
                await self._log(item, f"安卓模拟器 ADB 预检通过：{adapter.adb_address}")
            else:
                detail = stderr.decode(errors="replace").strip() or stdout.decode(errors="replace").strip() or "设备未就绪"
                await self._log(item, f"安卓模拟器 ADB 预检未通过（{detail}），仍继续启动 MAA GUI")
        except (OSError, TimeoutError) as error:
            await self._log(item, f"安卓模拟器 ADB 预检失败（{error}），仍继续启动 MAA GUI")

    async def _start_maaend_game(self, item: ItemRecord, task: TaskDefinition) -> bool:
        adapter = task.maaend_adapter
        if not adapter or not adapter.game_executable:
            return True
        target = Path(adapter.game_executable)
        if not target.exists():
            await self._log(item, "MaaEnd 游戏路径不存在")
            return False
        try:
            game = await asyncio.create_subprocess_exec(
                str(target), *adapter.game_arguments, cwd=str(target.parent),
                creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0,
            )
            self._auxiliary_owned_processes.append(await capture_owned_process(game.pid))
            await self._log(item, f"已启动受管 Windows 游戏 PID {game.pid}，等待 {adapter.game_wait_seconds} 秒")
            try:
                await asyncio.wait_for(self._stop_requested.wait(), timeout=adapter.game_wait_seconds)
                return False
            except TimeoutError:
                return True
        except (OSError, RuntimeError, ValueError) as error:
            await self._log(item, f"MaaEnd 游戏启动失败：{error}")
            return False

    async def _wait_for_outcome(
        self,
        process: asyncio.subprocess.Process,
        task: TaskDefinition,
        watcher: LogWatcher | None,
    ) -> tuple[str, CompletionSource | str, str | None]:
        signal_tasks: dict[asyncio.Task[object], str] = {
            asyncio.create_task(process.wait()): "process",
            asyncio.create_task(self._stop_requested.wait()): "stop",
            asyncio.create_task(asyncio.sleep(task.timeout_seconds)): "timeout",
        }
        if watcher:
            signal_tasks[asyncio.create_task(watcher.wait_for_signal(self._stop_requested))] = "log"
        if task.allow_manual_confirmation:
            signal_tasks[asyncio.create_task(self._manual_confirmed.wait())] = "manual"
        try:
            while signal_tasks:
                done, _ = await asyncio.wait(signal_tasks, return_when=asyncio.FIRST_COMPLETED)
                for completed in done:
                    kind = signal_tasks.pop(completed)
                    value = completed.result()
                    if kind == "stop" and self._stop_requested.is_set():
                        return "stopped", "stopped", None
                    if kind == "manual" and self._manual_confirmed.is_set():
                        return "success", CompletionSource.MANUAL, None
                    if kind == "log" and isinstance(value, LogSignal):
                        if value.kind == "success":
                            return "success", CompletionSource.SUCCESS_LOG, value.text
                        if value is not None:
                            return "failure", value.text, value.text
                    if kind == "process" and isinstance(value, int):
                        if value != 0:
                            return "failure", f"进程以退出码 {value} 结束", None
                        if task.has_success_log():
                            return "failure", "已配置成功日志，但目标进程在命中前退出", None
                        if task.allow_process_exit or not task.has_success_log():
                            return "success", CompletionSource.PROCESS_EXIT, None
                        return "failure", "目标进程退出，未获得完成信号", None
                    if kind == "timeout":
                        return "failure", f"运行超时（{task.timeout_seconds} 秒）", None
        finally:
            for pending in signal_tasks:
                pending.cancel()
            await asyncio.gather(*signal_tasks, return_exceptions=True)
        return "failure", "未获得完成信号", None

    async def _cleanup_owned(self, item: ItemRecord) -> None:
        owned = self._owned_process
        if owned is None:
            return
        item.state = ItemState.CLOSING
        self._manual_accepting_task_id = None
        await self._log(item, "请求目标主窗口正常关闭")
        await self._publish_state()
        await request_graceful_close(owned)
        deadline = asyncio.get_running_loop().time() + self.close_grace_seconds
        while await process_tree_alive(owned) and asyncio.get_running_loop().time() < deadline:
            await asyncio.sleep(0.05)
        if await process_tree_alive(owned):
            item.state = ItemState.FORCE_CLOSING
            await self._log(item, "正常关闭超时，仅强制收尾本轮受管进程树")
            await self._publish_state()
            await force_stop_process_tree(owned, self.close_grace_seconds)
        self._owned_process = None

    async def _cleanup_auxiliary(self, item: ItemRecord, task: TaskDefinition) -> None:
        adapter = task.maaend_adapter
        owned = self._auxiliary_owned_processes
        self._auxiliary_owned_processes = []
        if not owned or not adapter or not adapter.close_game_on_finish:
            return
        item.state = ItemState.CLOSING
        await self._log(item, "请求本轮启动的 Windows 游戏正常关闭")
        await self._publish_state()
        for process in owned:
            await request_graceful_close(process)
        deadline = asyncio.get_running_loop().time() + self.close_grace_seconds
        for process in owned:
            while await process_tree_alive(process) and asyncio.get_running_loop().time() < deadline:
                await asyncio.sleep(0.05)
            if await process_tree_alive(process):
                item.state = ItemState.FORCE_CLOSING
                await force_stop_process_tree(process, self.close_grace_seconds)

    async def _fail_then_cool(self, item: ItemRecord, message: str, *, has_next: bool, task: TaskDefinition) -> None:
        item.state = ItemState.FAILED
        item.error_summary = message
        await self._log(item, f"任务失败：{message}")
        await self._publish_state()
        await self._cleanup_owned(item)
        await self._cleanup_auxiliary(item, task)
        await self._finish_after_cleanup(item, ItemState.FAILED, has_next=has_next)

    async def _finish_after_cleanup(
        self, item: ItemRecord, final_state: ItemState, *, has_next: bool
    ) -> None:
        """Only an intermediate item waits before dispatching its successor."""
        if not has_next:
            self._cooldown_until = None
            if self._stop_requested.is_set():
                item.state = ItemState.STOPPED
            else:
                item.state = final_state
                await self._log(item, "收尾完成，已是最后一项，整轮即将结束")
            await self._publish_state()
            return
        await self._cooldown(item, final_state)

    async def _cooldown(self, item: ItemRecord, final_state: ItemState) -> None:
        if self._stop_requested.is_set():
            item.state = ItemState.STOPPED
            await self._publish_state()
            return
        item.state = ItemState.COOLING_DOWN
        self._cooldown_until = datetime.now(UTC) + timedelta(seconds=self.cooldown_seconds)
        await self._log(item, f"收尾完成，固定冷却 {self.cooldown_seconds:g} 秒")
        await self._publish_state()
        try:
            await asyncio.wait_for(self._stop_requested.wait(), timeout=self.cooldown_seconds)
        except TimeoutError:
            item.state = final_state
            self._cooldown_until = None
            await self._publish_state()
            return
        item.state = ItemState.STOPPED
        self._cooldown_until = None
        await self._publish_state()

    async def _publish_state(self) -> None:
        await self.events.publish("runtime", self.snapshot())

    async def _log(self, item: ItemRecord, text: str) -> None:
        """Only expose the bounded per-item summary already stored in history."""
        self._logger.info("任务 %s (%s): %s", item.task_name, item.task_id, text)
        await self.events.publish("log", {"taskId": item.task_id, "message": text})

    def snapshot(self) -> dict:
        cooldown_seconds = 0
        if self._cooldown_until:
            cooldown_seconds = max(0, int((self._cooldown_until - datetime.now(UTC)).total_seconds()))
        active_item = None
        if self.current_run and self._active_task_id:
            active_item = next(
                (item for item in self.current_run.items if item.task_id == self._active_task_id), None
            )
        total = len(self.current_run.items) if self.current_run else 0
        current_index = (
            next((index + 1 for index, item in enumerate(self.current_run.items) if item is active_item), 0)
            if self.current_run and active_item
            else 0
        )
        return {
            "busy": bool(self.current_run and self.current_run.state in {RunState.RUNNING, RunState.STOPPING}),
            "activeTaskId": self._active_task_id,
            "cooldownSeconds": cooldown_seconds,
            "cooldownUntil": self._cooldown_until.isoformat() if self._cooldown_until else None,
            "migrationNotice": self.migration_notice,
            "completionAction": dict(self._pending_completion) if self._pending_completion else None,
            "runCenter": {
                "currentScriptId": active_item.script_id if active_item else None,
                "currentScriptName": active_item.script_name if active_item else None,
                "currentTaskId": active_item.task_id if active_item else None,
                "currentTaskName": active_item.task_name if active_item else None,
                "currentState": active_item.state.value if active_item else None,
                "completionSource": active_item.completion_source.value if active_item and active_item.completion_source else None,
                "currentIndex": current_index,
                "totalPrograms": total,
            },
            "run": self.current_run.model_dump(mode="json") if self.current_run else None,
        }
