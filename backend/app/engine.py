from __future__ import annotations

import asyncio
import copy
import hashlib
import logging
import os
import time
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Literal, TypeVar

from .completion_actions import CompletionActionDispatcher
from .detail_logs import DetailLogs
from .events import EventHub
from .external_logs import ExternalLogWatcher
from .logwatch import LogSignal, LogWatcher
from .maa_region_capture import read_maa_region
from .maa_update_evidence import MaaUpdateEvidence, MaaUpdaterLogEvidence, UpdateEvent
from .models import (
    AppConfig,
    CompletionAction,
    CompletionSource,
    ItemRecord,
    ItemState,
    QueueDefinition,
    ReadableEvent,
    RunRecord,
    RunState,
    SchedulePlan,
    TaskDefinition,
)
from .presets import PRESETS
from .processes import (
    OwnedProcess,
    capture_owned_process,
    executable_running,
    force_stop_process_tree,
    process_tree_alive,
    request_graceful_close,
)
from .readable_reports import SCRIPT_TRANSLATION_VERSION, local_report_provider
from .startup_control import ControlSelector, query_uia
from .storage import JsonStore
from .ui_logs import WindowLogCapture, ocr_appended
from .update_lineage import UpdateLineage, snapshot_processes
from .update_rules import UpdateRuleReader, UpdateRules
from .update_supervisor import UpdateSupervisor


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


ConfigUpdateResult = TypeVar("ConfigUpdateResult")
SUMMARY_LOG_LIMIT = 40
SCRIPT_LOG_BUFFER_LIMIT = 200_000
SCRIPT_EVENT_BUFFER_LIMIT = 240


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
        self._update_action = asyncio.Event()
        self._update_choice: str | None = None
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
        self._live_summary_run_id: str | None = None
        self._live_summary_sequence = 0
        self._live_summary_logs: list[dict[str, object]] = []
        self._script_log_run_id: str | None = None
        self._script_log_streams: dict[tuple[str, str], dict[str, object]] = {}
        self._script_event_run_id: str | None = None
        self._script_event_sequence = 0
        self._script_event_logs: list[dict[str, object]] = []
        self._script_event_item_sequences: dict[tuple[str, int], int] = {}
        self._logger = logger or logging.getLogger("sequential-runner")
        self._config_lock = asyncio.Lock()

    @property
    def is_busy(self) -> bool:
        return self._runner_task is not None and not self._runner_task.done()

    async def replace_config(self, candidate: AppConfig) -> None:
        """持久化成功后才替换运行中的配置并通知订阅者。"""
        async with self._config_lock:
            committed = candidate.model_copy(deep=True)
            self.store.save_config(committed)
            self.config = committed
            await self.events.publish("config", committed.model_dump(mode="json"))

    async def update_config(self, update: Callable[[AppConfig], ConfigUpdateResult]) -> ConfigUpdateResult:
        """在最新配置副本上提交一项小变更，避免并发追加相互覆盖。"""
        async with self._config_lock:
            candidate = self.config.model_copy(deep=True)
            result = update(candidate)
            self.store.save_config(candidate)
            self.config = candidate
            await self.events.publish("config", candidate.model_dump(mode="json"))
            return result

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
            ItemRecord(task_id=item.id, task_name=item.name, script_id=queue.id, script_name=queue.name,
                       log_source_configured=bool(item.log_path),
                       ui_log_source_configured=bool(item.ui_log_region) or item.preset_id == "march7th",
                       script_translation_version=SCRIPT_TRANSLATION_VERSION)
            for item in selected
        ]
        self._reset_live_summary(record)
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
        self._reset_live_summary(record)
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
            self._refresh_readable_reports(record)
            self._reset_live_summary(record)
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
            self._refresh_readable_reports(record)
            self._reset_live_summary(record)
            self.history.runs.insert(0, record)
            self._save_new_history()
            self.current_run = record
            await self._publish_state()
            return record
        self._reset_live_summary(record)
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
            items.append(ItemRecord(task_id=task.id, task_name=task.name, script_id=script.id, script_name=script.name,
                                    log_source_configured=bool(task.log_path),
                                    ui_log_source_configured=bool(task.ui_log_region) or task.preset_id == "march7th",
                                    script_translation_version=SCRIPT_TRANSLATION_VERSION))
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

    async def decide_update(self, task_id: str, action: str) -> None:
        if not self.is_busy or self._active_task_id != task_id or not self.current_run:
            raise ConfirmRejectedError("当前没有等待更新处理的任务")
        item = next((item for item in self.current_run.items if item.task_id == task_id), None)
        if item is None or not (item.update_active or item.state == ItemState.WAITING_UPDATE) or action not in ("wait", "fail", "handled", "complete"):
            raise ConfirmRejectedError("更新处理状态或操作无效")
        if action == "complete" and not item.manual_completion_available:
            raise ConfirmRejectedError("当前程序未启用人工完成确认")
        if self._update_action.is_set():
            raise ConfirmRejectedError("已有更新处理请求正在执行")
        self._update_choice = action
        self._update_action.set()

    async def _wait_for_update_decision(self, item: ItemRecord, message: str) -> str:
        """Pause the active item; waiting is never equivalent to task completion."""
        self._manual_accepting_task_id = None
        item.state = ItemState.WAITING_UPDATE
        item.update_message = message
        await self._log(item, message)
        await self._publish_state()
        stop = asyncio.create_task(self._stop_requested.wait())
        decision = asyncio.create_task(self._update_action.wait())
        try:
            await asyncio.wait((stop, decision), return_when=asyncio.FIRST_COMPLETED)
            if self._stop_requested.is_set():
                return "stop"
            return self._update_choice or "wait"
        finally:
            for pending in (stop, decision):
                if not pending.done():
                    pending.cancel()
            await asyncio.gather(stop, decision, return_exceptions=True)
            self._update_action.clear()
            self._update_choice = None

    async def wait_until_idle(self) -> None:
        if self._runner_task:
            with __import__("contextlib").suppress(asyncio.CancelledError):
                await self._runner_task

    async def _run(self, record: RunRecord, tasks: list[TaskDefinition]) -> None:
        from uuid import uuid4
        record.run_log_id = str(uuid4())
        await self._run_log(record, "本轮开始运行", phase="running")
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
            elapsed = max(0, int((record.ended_at - record.started_at.astimezone(UTC)).total_seconds()))
            result = "已停止" if record.state == RunState.STOPPED else ("全部成功" if self._run_succeeded(record) else "存在失败或未执行项")
            await self._run_log(
                record,
                f"本轮{result}，总耗时 {elapsed} 秒",
                phase="failed" if record.state == RunState.STOPPED or not self._run_succeeded(record) else "success",
            )
        finally:
            self._active_task_id = None
            self._manual_accepting_task_id = None
            self._owned_process = None
            self._auxiliary_owned_processes = []
            self._refresh_readable_reports(record)
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
        item.detail_log_status = "外部日志按原路径读取" if task.log_path else "未配置日志路径"
        await self._run_item_inner(record, item, task, has_next=has_next)

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
        if task.preset_id == "maa" and await asyncio.to_thread(executable_running, str(executable)):
            await self._fail_then_cool(item, "程序已在运行，请先自行关闭后再运行", has_next=has_next, task=task)
            return
        watcher: ExternalLogWatcher | None = None
        if task.log_path:
            async def emit_external_log(path: Path, text: str) -> None:
                await self._record_external_log(record, item, task.id, path, text)

            watcher = ExternalLogWatcher(
                task.log_path, task.log_patterns,
                task.success_log,
                task.success_log_mode,
                task.failure_log,
                task.failure_log_mode,
                success_rules=task.success_log_rules,
                failure_rules=task.failure_log_rules,
                monitor_success=not task.allow_manual_confirmation,
                on_text=emit_external_log,
            )
            try:
                # 必须先取得游标，后启动目标进程，避免历史成功行误判本轮。
                await watcher.start_at_end()
                if task.preset_id == "maa":
                    watcher.maa_updater_log = LogWatcher(str(executable.parent / "debug" / "pending-update-applier.log"))
                    await watcher.maa_updater_log.start_at_end()
                item.external_log_files = watcher.paths
                item.external_log_status = "已登记外部日志来源" if item.external_log_files else "日志目录中暂无匹配文件，运行期间会继续发现新文件"
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

        fallback_enabled = bool(task.startup_fallback and task.startup_fallback.enabled)
        item.state = ItemState.WAITING_MANUAL if task.allow_manual_confirmation and not fallback_enabled else ItemState.MONITORING
        if item.state == ItemState.WAITING_MANUAL:
            await self._log(item, "等待人工确认当前程序完成")
        self._manual_accepting_task_id = task.id if task.allow_manual_confirmation and not fallback_enabled else None
        ui_capture = None
        if self._owned_process and (task.preset_id == "march7th" or task.ui_log_region):
            async def emit_ui_log(text: str) -> None:
                await self._publish_state()
                await self._publish_script_log(
                    record,
                    task.id,
                    "ocr-region" if task.ui_log_region else "window-log",
                    "画面识别" if task.ui_log_region else "界面读取",
                    "ocr" if task.ui_log_region else "window",
                    text,
                )

            if task.ui_log_region:
                async def read_maa(process: OwnedProcess) -> str:
                    return await read_maa_region(process, task.ui_log_region)

                if task.preset_id == "march7th":
                    ui_capture = WindowLogCapture(self.detail_logs, item, self._owned_process, emit_ui_log,
                                                  notify=self._publish_state, fallback_reader=read_maa)
                else:
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
            outcome, detail, log_detail = await self._wait_for_outcome(process, task, watcher, item)
            if watcher:
                item.external_log_files = watcher.paths
                item.external_log_status = "已读取外部日志" if item.external_log_files else "运行期间未发现匹配的外部日志"
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
            item.state = ItemState.STOPPED
            await self._publish_state()
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

    async def _wait_for_outcome_with_update(
        self, process: asyncio.subprocess.Process, task: TaskDefinition,
        watcher: ExternalLogWatcher, item: ItemRecord,
    ) -> tuple[str, CompletionSource | str, str | None]:
        """Monitor update evidence without treating a restart as task completion."""
        config = task.update_monitor
        monitor = UpdateSupervisor(idle_seconds=config.idle_seconds if config else 600,
                                   total_seconds=config.total_seconds if config else 3600)
        readers: dict[Path, MaaUpdateEvidence | UpdateRuleReader] = {}
        updates: asyncio.Queue = asyncio.Queue()
        startup = task.startup_fallback if task.startup_fallback and task.startup_fallback.enabled else None
        startup_started = asyncio.Event()
        startup_tails: dict[Path, str] = {}
        stream_epochs: dict[Path, int] = {}
        original_callback = watcher.on_text
        updater_log = watcher.maa_updater_log if task.preset_id == "maa" else None
        updater_reader = MaaUpdaterLogEvidence()
        updater_epoch = updater_log.stream_epoch if updater_log else 0

        async def capture(path: Path, text: str) -> None:
            if original_callback:
                await original_callback(path, text)
            epoch = watcher.watchers[path].stream_epoch
            if path in stream_epochs and stream_epochs[path] != epoch:
                startup_tails.pop(path, None)
                if path in readers:
                    reader = readers[path]
                    reader.clear_partial() if isinstance(reader, MaaUpdateEvidence) else reader.reset()
            stream_epochs[path] = epoch
            if startup:
                combined = startup_tails.get(path, "") + text
                if startup.started_signal in combined:
                    startup_started.set()
                startup_tails[path] = combined[-max(1, len(startup.started_signal) - 1):]
            if task.preset_id == "maa":
                if path.name.casefold() != "gui.log":
                    return
                reader = readers.setdefault(path, MaaUpdateEvidence())
            elif config and config.enabled:
                reader = readers.setdefault(path, UpdateRuleReader(UpdateRules(
                    config.checking, config.downloading, config.applying,
                    config.restarting, config.failed, config.recovered,
                )))
            else:
                return
            for event in reader.feed(text):
                await updates.put((path, event, bool(startup and startup.started_signal in text)))

        watcher.on_text = capture
        stop_task = asyncio.create_task(self._stop_requested.wait())
        manual_task = asyncio.create_task(self._manual_confirmed.wait()) if task.allow_manual_confirmation else None
        process_task: asyncio.Task = asyncio.create_task(process.wait())
        log_task: asyncio.Task = asyncio.create_task(watcher.wait_for_signal(self._stop_requested))
        event_task: asyncio.Task = asyncio.create_task(updates.get())
        watched_process = self._owned_process
        lineage = UpdateLineage(watched_process, task.executable) if watched_process else None
        current_exit: int | None = None
        exit_seen = False
        exit_pending_at: float | None = None
        gui_exit_pending_at: float | None = None
        pending_exit_text: str | None = None
        active_elapsed = 0.0
        loop = asyncio.get_running_loop()
        previous = loop.time()
        last_snapshot = 0.0
        candidates = []
        update_started_wall: float | None = None
        update_source: Path | None = None
        observed_updaters: set[OwnedProcess] = set()
        startup_due = float(startup.wait_seconds) if startup else None
        start_attempted = False
        announcement_attempted = False
        startup_attention = False
        manual_recovery_requested = False
        item.manual_completion_available = task.allow_manual_confirmation

        async def wait_owned(owned: OwnedProcess) -> None:
            while await process_tree_alive(owned):
                await asyncio.sleep(0.2)

        try:
            while True:
                choice = None
                waiters = [stop_task, log_task, event_task, asyncio.create_task(self._update_action.wait())]
                if not exit_seen:
                    waiters.append(process_task)
                if manual_task and monitor.stage in ("normal", "reattached"):
                    waiters.append(manual_task)
                action_waiter = waiters[3]
                await asyncio.wait(waiters, timeout=0.15, return_when=asyncio.FIRST_COMPLETED)
                if not action_waiter.done():
                    action_waiter.cancel()
                await asyncio.gather(action_waiter, return_exceptions=True)
                now = loop.time()
                if monitor.stage in ("normal", "reattached"):
                    active_elapsed += max(0, now - previous)
                previous = now
                if self._stop_requested.is_set():
                    return "stopped", "stopped", None

                while event_task.done():
                    source, event, startup_signal_in_chunk = event_task.result()
                    if event.kind == "update_detected" and monitor.stage in ("normal", "reattached"):
                        update_source = source
                    if update_source is not None and source != update_source:
                        event_task = asyncio.create_task(updates.get())
                        await asyncio.sleep(0)
                        continue
                    if event.kind == "update_detected" and monitor.stage in ("normal", "reattached"):
                        now_wall = time.time()
                        # GUI logging can be flushed after the updater is already
                        # created. Accept its timestamp only within this run.
                        if (task.preset_id == "maa" and event.logged_at is not None and watched_process is not None
                                and watched_process.created_at <= event.logged_at <= now_wall):
                            update_started_wall = event.logged_at
                        else:
                            update_started_wall = now_wall
                        observed_updaters.clear()
                        if startup and not startup_signal_in_chunk:
                            startup_started.clear()
                            startup_tails.clear()
                    monitor.observe_event(event, now)
                    item.update_active = monitor.stage not in ("normal", "reattached")
                    if monitor.stage not in ("normal", "reattached"):
                        self._manual_accepting_task_id = None
                        if item.state == ItemState.WAITING_MANUAL:
                            item.state = ItemState.MONITORING
                        if self._manual_confirmed.is_set():
                            self._manual_confirmed.clear()
                        if manual_task and manual_task.done():
                            manual_task = asyncio.create_task(self._manual_confirmed.wait())
                    if item.update_message != monitor.message:
                        item.update_message = monitor.message
                        await self._log(item, monitor.message)
                    await self._publish_state()
                    event_task = asyncio.create_task(updates.get())
                    await asyncio.sleep(0)

                if updater_log and monitor.stage in ("restarting", "applying", "awaiting_process"):
                    try:
                        await asyncio.to_thread(updater_log._read_new)
                        if updater_epoch != updater_log.stream_epoch:
                            updater_reader.clear_partial()
                            updater_epoch = updater_log.stream_epoch
                        updater_events = updater_reader.feed(updater_log.last_text)
                    except (OSError, UnicodeError):
                        updater_events = []
                        monitor.attention("无法读取更新器日志，请人工检查")
                    for updater_event in updater_events:
                        if updater_event.kind == "updater_failure_observed":
                            monitor.attention(updater_event.detail)
                            break
                        # Updater logs are shared by installation. Even a matching
                        # process snapshot does not identify the writer of a line.
                        detail = updater_event.detail
                        if observed_updaters:
                            detail += "；已观察到本轮更新器，但无法证明该行由其写入"
                        item.update_message = detail
                        await self._log(item, detail)
                        await self._publish_state()

                if log_task.done():
                    signal = log_task.result()
                    if isinstance(signal, LogSignal):
                        if signal.kind == "success" and monitor.stage in ("normal", "reattached"):
                            return "success", CompletionSource.SUCCESS_LOG, signal.text
                        if signal.kind != "success":
                            failures = signal.matches or (signal.text,)
                            expected_exit = "MaaAssistantArknights GUI exited" if task.preset_id == "maa" else (config.restart_exit if config else "")
                            others = [value for value in failures if not expected_exit or value != expected_exit]
                            if others:
                                failure = others[0] if others else signal.text
                                if signal.kind == "error" and monitor.started_at is not None:
                                    monitor.attention("更新期间日志无法读取，请人工检查")
                                else:
                                    return "failure", failure, failure
                            elif monitor.stage not in ("restarting", "applying", "awaiting_process", "needs_attention"):
                                gui_exit_pending_at = now
                                pending_exit_text = signal.text
                    log_task = asyncio.create_task(watcher.wait_for_signal(self._stop_requested))

                if process_task.done() and not exit_seen:
                    current_exit = process_task.result()
                    exit_seen = True
                    exit_pending_at = now
                    if current_exit not in (0, None) and monitor.stage not in ("normal", "reattached"):
                        monitor.attention(f"更新期间进程以退出码 {current_exit} 结束，请人工检查")

                if lineage and now - last_snapshot >= 0.25:
                    try:
                        def observe_lineage(started_wall=update_started_wall):
                            verified = lineage.verified_live_parents()
                            processes = snapshot_processes(lineage.known_pids())
                            matches = lineage.observe(processes, verified)
                            updaters = []
                            if task.preset_id == "maa" and started_wall is not None:
                                updaters = lineage.observed_executables(
                                    processes, str(Path(task.executable).parent / "MAA.Updater.exe"), started_wall)
                            return matches, updaters

                        candidates, updaters = await asyncio.to_thread(observe_lineage)
                        observed_updaters.update(updaters)
                        candidates = [candidate for candidate in candidates if candidate != watched_process
                                      and update_started_wall is not None and candidate.created_at >= update_started_wall]
                    except (OSError, RuntimeError):
                        monitor.attention("无法读取进程归属，需要人工检查")
                    last_snapshot = now

                if exit_seen and monitor.stage == "awaiting_process":
                    candidate = monitor.choose_candidate(candidates)
                    if not candidate and manual_recovery_requested and monitor.stage != "needs_attention":
                        monitor.attention("未找到唯一且归属可验证的重启进程，请人工处理")
                    manual_recovery_requested = False
                    if candidate:
                        from .processes import owns_process

                        target = await asyncio.to_thread(owns_process, candidate)
                        if target is None:
                            monitor.attention("重启进程身份已变化，请人工检查")
                        else:
                            try:
                                same_path = (target.create_time() == candidate.created_at and
                                             os.path.normcase(os.path.realpath(target.exe())) == os.path.normcase(os.path.realpath(task.executable)))
                            except (OSError, __import__("psutil").Error):
                                same_path = False
                            rechecked = await asyncio.to_thread(owns_process, candidate)
                            try:
                                same_identity = rechecked is not None and rechecked.create_time() == candidate.created_at
                            except (OSError, __import__("psutil").Error):
                                same_identity = False
                            if not same_path or not same_identity:
                                monitor.attention("无法确认重启程序路径或身份")
                            else:
                                watched_process = self._owned_process = candidate
                                process_task = asyncio.create_task(wait_owned(candidate))
                                exit_seen = False
                                exit_pending_at = gui_exit_pending_at = None
                                for reader in readers.values():
                                    reader.reset()
                                update_source = None
                                watcher.reset_idle_clock()
                                if startup:
                                    startup_due = active_elapsed + startup.wait_seconds
                                    start_attempted = False
                                    announcement_attempted = False
                                item.update_message = monitor.message
                                item.update_active = False
                                item.state = ItemState.WAITING_MANUAL if task.allow_manual_confirmation else ItemState.MONITORING
                                self._manual_accepting_task_id = task.id if task.allow_manual_confirmation else None
                                await self._log(item, "已确认重启进程，继续等待任务完成信号")
                                await self._publish_state()

                if self._update_action.is_set() and monitor.stage != "needs_attention":
                    choice = self._update_choice
                    self._update_action.clear()
                    self._update_choice = None
                    if choice == "fail":
                        return "failure", "用户确认更新未完成，结束当前项", None
                    if choice == "complete":
                        return "success", CompletionSource.MANUAL, None
                    if choice == "handled":
                        monitor.attention("用户确认已处理更新，正在核对进程身份")
                    elif choice == "wait":
                        monitor.progress(now)

                if monitor.stage == "needs_attention" or monitor.check_deadlines(now).stage == "needs_attention":
                    choice = choice if choice == "handled" else await self._wait_for_update_decision(item, monitor.message)
                    choice_from_user = choice
                    choice = None
                    previous = loop.time()
                    if choice_from_user == "stop":
                        return "stopped", "stopped", None
                    if choice_from_user == "fail":
                        return "failure", "用户确认更新未完成，结束当前项", None
                    if choice_from_user == "complete":
                        return "success", CompletionSource.MANUAL, None
                    monitor.continue_waiting(previous)
                    if choice_from_user == "handled":
                        monitor.observe_event(UpdateEvent("update_applied", "用户确认更新已处理"), previous)
                        manual_recovery_requested = exit_seen
                        if not exit_seen:
                            from .processes import owns_process
                            target = await asyncio.to_thread(owns_process, watched_process) if watched_process else None
                            try:
                                valid = (target is not None and watched_process is not None and
                                         target.create_time() == watched_process.created_at and
                                         os.path.normcase(os.path.realpath(target.exe())) == os.path.normcase(os.path.realpath(task.executable)))
                                if valid and watched_process:
                                    rechecked = await asyncio.to_thread(owns_process, watched_process)
                                    valid = rechecked is not None and rechecked.create_time() == watched_process.created_at
                            except (OSError, __import__("psutil").Error):
                                valid = False
                            if valid:
                                monitor.stage = "reattached"
                                monitor.message = "已核对程序身份，继续监控任务"
                                item.update_active = False
                                if startup:
                                    startup_due = active_elapsed + startup.wait_seconds
                                    start_attempted = False
                                    announcement_attempted = False
                                item.state = ItemState.WAITING_MANUAL if task.allow_manual_confirmation else ItemState.MONITORING
                                self._manual_accepting_task_id = task.id if task.allow_manual_confirmation else None
                            else:
                                monitor.attention("无法确认当前程序身份，请人工处理")
                    if monitor.stage != "reattached":
                        item.state = ItemState.MONITORING
                    item.update_message = monitor.message
                    await self._publish_state()
                    continue

                if startup and startup_due is not None and monitor.stage in ("normal", "reattached"):
                    if startup_started.is_set():
                        startup_due = None
                        item.update_message = None
                        if task.allow_manual_confirmation and not exit_seen:
                            item.state = ItemState.WAITING_MANUAL
                            self._manual_accepting_task_id = task.id
                            await self._publish_state()
                    elif active_elapsed >= startup_due:
                        if start_attempted:
                            startup_attention = True
                            reason = "未确认任务已启动，请人工检查后继续等待或停止"
                        elif watched_process is None or exit_seen:
                            startup_attention = True
                            reason = "无法确认启动控件所属进程，请人工检查"
                        else:
                            try:
                                if startup.announcement and not announcement_attempted:
                                    announcement_attempted = True
                                    announcement = ControlSelector(startup.announcement.window_title,
                                                                   startup.announcement.automation_id,
                                                                   startup.announcement.control_name)
                                    inspection = await query_uia(watched_process, announcement)
                                    if self._stop_requested.is_set():
                                        return "stopped", "stopped", None
                                    if not startup_started.is_set() and inspection.get("window_found") is not False:
                                        await query_uia(watched_process, announcement, invoke=True)
                                        # Allow the software to start itself after its announcement closes.
                                        startup_due = active_elapsed + startup.wait_seconds
                                        continue
                                if self._stop_requested.is_set():
                                    return "stopped", "stopped", None
                                if not startup_started.is_set():
                                    assert startup.start is not None
                                    selector = ControlSelector(startup.start.window_title, startup.start.automation_id,
                                                               startup.start.control_name)
                                    start_attempted = True
                                    await query_uia(watched_process, selector, invoke=True)
                                startup_due = active_elapsed + startup.wait_seconds
                            except (RuntimeError, OSError, TimeoutError, ValueError):
                                start_attempted = True
                                startup_attention = True
                                reason = "无法安全确认或操作启动控件，请人工处理"
                        if startup_attention:
                            choice = await self._wait_for_update_decision(item, reason)
                            previous = loop.time()
                            if choice == "stop":
                                return "stopped", "stopped", None
                            if choice == "fail":
                                return "failure", "用户确认启动未完成，结束当前项", None
                            if choice == "complete":
                                return "success", CompletionSource.MANUAL, None
                            startup_due = active_elapsed + startup.wait_seconds
                            startup_attention = False
                            item.state = ItemState.MONITORING
                            item.update_message = "继续等待启动信号"
                            await self._publish_state()
                            continue

                if monitor.stage in ("normal", "reattached"):
                    if manual_task and manual_task.done() and self._manual_confirmed.is_set():
                        return "success", CompletionSource.MANUAL, None
                    if active_elapsed >= task.timeout_seconds:
                        return "failure", f"运行超时（{task.timeout_seconds} 秒）", None
                    idle = watcher.idle_for()
                    if not task.allow_manual_confirmation and task.log_idle_timeout_seconds is not None and idle is not None and idle >= task.log_idle_timeout_seconds:
                        return "failure", f"日志超过 {task.log_idle_timeout_seconds} 秒无新增", None
                if gui_exit_pending_at is not None and now - gui_exit_pending_at >= 0.6:
                    if monitor.stage in ("normal", "reattached"):
                        return "failure", pending_exit_text or "目标程序退出", pending_exit_text
                    if monitor.stage == "checking":
                        monitor.attention("程序退出但无法确认是否正在更新，请人工检查")
                    gui_exit_pending_at = None
                if exit_seen and exit_pending_at is not None and now - exit_pending_at >= 0.6:
                    if monitor.stage == "checking":
                        monitor.attention("程序退出但更新重启证据不足，请人工检查")
                    elif monitor.stage in ("normal", "reattached"):
                        if current_exit not in (0, None):
                            return "failure", f"进程以退出码 {current_exit} 结束", None
                        if task.allow_manual_confirmation and current_exit in (0, None):
                            exit_pending_at = None
                            await self._log(item, "程序已正常退出，仍等待人工确认")
                        elif current_exit == 0 and not task.has_success_log() and task.allow_process_exit:
                            return "success", CompletionSource.PROCESS_EXIT, None
                        else:
                            return "failure", "目标进程退出，未获得完成信号", None
        finally:
            item.update_active = False
            item.manual_completion_available = False
            self._update_choice = None
            self._update_action.clear()
            watcher.on_text = original_callback
            for pending in (stop_task, manual_task, process_task, log_task, event_task):
                if pending and not pending.done():
                    pending.cancel()
            await asyncio.gather(*(pending for pending in (stop_task, manual_task, process_task, log_task, event_task) if pending), return_exceptions=True)

    async def _wait_for_outcome(
        self,
        process: asyncio.subprocess.Process,
        task: TaskDefinition,
        watcher: ExternalLogWatcher | None,
        item: ItemRecord,
    ) -> tuple[str, CompletionSource | str, str | None]:
        update_config = task.update_monitor
        if watcher and (task.preset_id == "maa" or (update_config and update_config.enabled)
                        or (task.startup_fallback and task.startup_fallback.enabled)):
            return await self._wait_for_outcome_with_update(process, task, watcher, item)
        signal_tasks: dict[asyncio.Task[object], str] = {
            asyncio.create_task(process.wait()): "process",
            asyncio.create_task(self._stop_requested.wait()): "stop",
            asyncio.create_task(asyncio.sleep(task.timeout_seconds)): "timeout",
        }
        if watcher:
            signal_tasks[asyncio.create_task(watcher.wait_for_signal(self._stop_requested))] = "log"
            if task.log_idle_timeout_seconds is not None and not task.allow_manual_confirmation:
                signal_tasks[asyncio.create_task(
                    watcher.wait_for_idle(task.log_idle_timeout_seconds, self._stop_requested)
                )] = "idle"
        if task.allow_manual_confirmation:
            signal_tasks[asyncio.create_task(self._manual_confirmed.wait())] = "manual"
        try:
            while signal_tasks:
                done, _ = await asyncio.wait(signal_tasks, return_when=asyncio.FIRST_COMPLETED)
                # 同时到达的停止和失败信号优先于人工确认或成功信号。
                def priority(completed: asyncio.Task[object]) -> int:
                    kind = signal_tasks[completed]
                    value = completed.result()
                    if kind == "stop":
                        return 0
                    if kind == "process" and isinstance(value, int) and value != 0:
                        return 1
                    if kind == "log" and isinstance(value, LogSignal) and value.kind != "success":
                        return 2
                    return {"manual": 3, "log": 4, "process": 5, "idle": 6, "timeout": 7}[kind]

                for completed in sorted(done, key=priority):
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
                        if task.allow_manual_confirmation:
                            await self._log(item, "程序已正常退出，仍等待人工确认")
                            await self._publish_state()
                            continue
                        if task.has_success_log():
                            return "failure", "已配置成功日志，但目标进程在命中前退出", None
                        if task.allow_process_exit:
                            return "success", CompletionSource.PROCESS_EXIT, None
                        return "failure", "目标进程退出，未获得完成信号", None
                    if kind == "timeout":
                        return "failure", f"运行超时（{task.timeout_seconds} 秒）", None
                    if kind == "idle" and isinstance(value, LogSignal):
                        return "failure", value.text, value.text
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
        # 原文日志缓冲只需在初始快照或显式重同步时发送；普通状态事件避免
        # 与正在传输的 script_log 增量相互覆盖。
        await self.events.publish("runtime", self.snapshot(include_script_logs=False))

    async def _log(self, item: ItemRecord, text: str) -> None:
        try:
            local_report_provider.append_item_event(item, text)
        except Exception:
            self._logger.exception("生成可读日志事件时失败；继续运行")
        if self.current_run:
            await self._run_log(
                self.current_run,
                f"{item.task_name}：{text}",
                summary_message=text,
                task_id=item.task_id,
                phase="failed" if any(word in text for word in ("失败", "错误", "无法读取")) else self._summary_phase(item.state),
            )
        self._logger.info("任务 %s (%s): %s", item.task_name, item.task_id, text)

    def _refresh_readable_reports(self, record: RunRecord) -> None:
        """Report rendering is observational: it must never block a run or history save."""
        try:
            for item in record.items:
                item.readable_report = local_report_provider.build_item_report(item)
            record.readable_report = local_report_provider.build_run_report(record)
        except Exception:
            self._logger.exception("生成可读运行报告时失败；保留原有运行与历史记录")

    @staticmethod
    def _summary_phase(state: ItemState) -> str:
        return {
            ItemState.VALIDATING: "validation",
            ItemState.STARTING: "running",
            ItemState.MONITORING: "running",
            ItemState.WAITING_MANUAL: "waiting",
            ItemState.WAITING_UPDATE: "waiting",
            ItemState.CLOSING: "closing",
            ItemState.FORCE_CLOSING: "closing",
            ItemState.COOLING_DOWN: "cooling",
            ItemState.SUCCEEDED: "success",
            ItemState.FAILED: "failed",
            ItemState.STOPPED: "failed",
        }.get(state, "info")

    def _reset_live_summary(self, record: RunRecord) -> None:
        self._live_summary_run_id = record.id
        self._live_summary_sequence = 0
        self._live_summary_logs = []
        self._script_log_run_id = record.id
        self._script_log_streams = {}
        self._script_event_run_id = record.id
        self._script_event_sequence = 0
        self._script_event_logs = []
        self._script_event_item_sequences = {}

    async def _record_external_log(
        self, record: RunRecord, item: ItemRecord, task_id: str, path: Path, text: str
    ) -> None:
        """Fan one authorized external-log increment into every observable log contract.

        The watcher is deliberately the only caller: raw text is forwarded only to the
        bounded live preview, while the report/history side receives translated facts.
        A translation error must not hide the source increment or change run arbitration.
        """
        source_id = "external-" + hashlib.sha256(str(path).casefold().encode("utf-8")).hexdigest()[:16]
        try:
            local_report_provider.note_external_levels(item, text)
            translated = local_report_provider.append_script_events(item, text)
            await self._publish_script_events(record, item, translated)
            self._refresh_readable_reports(record)
        except Exception:
            self._logger.exception("生成脚本可读事件时失败；继续传递原始日志")
        await self._publish_script_log(record, task_id, source_id, path.name, "external", text)

    async def _publish_script_events(self, record: RunRecord, item: ItemRecord, events: list[ReadableEvent]) -> None:
        """Publish translated script facts without exposing the raw external line."""
        if not events:
            return
        if self._script_event_run_id != record.id:
            self._script_event_run_id = record.id
            self._script_event_sequence = 0
            self._script_event_logs = []
            self._script_event_item_sequences = {}
        for event in events:
            item_sequence = event.sequence
            key = (item.task_id, item_sequence) if item_sequence else None
            published_sequence = self._script_event_item_sequences.get(key) if key else None
            existing_index = next((index for index, payload in enumerate(self._script_event_logs) if payload["sequence"] == published_sequence), None)
            replace = existing_index is not None
            if published_sequence is None or not replace:
                self._script_event_sequence += 1
                published_sequence = self._script_event_sequence
                if key:
                    self._script_event_item_sequences[key] = published_sequence
            payload = {
                "runId": record.id,
                "taskId": item.task_id,
                "sequence": published_sequence,
                "event": event.model_dump(mode="json"),
                "truncated": False,
                "replace": replace,
            }
            if existing_index is None:
                self._script_event_logs.append(payload)
            else:
                self._script_event_logs[existing_index] = payload
            if len(self._script_event_logs) > SCRIPT_EVENT_BUFFER_LIMIT:
                removed = self._script_event_logs[:-SCRIPT_EVENT_BUFFER_LIMIT]
                self._script_event_logs = self._script_event_logs[-SCRIPT_EVENT_BUFFER_LIMIT:]
                removed_sequences = {entry["sequence"] for entry in removed}
                self._script_event_item_sequences = {
                    item_key: sequence for item_key, sequence in self._script_event_item_sequences.items()
                    if sequence not in removed_sequences
                }
                item.script_event_truncated = True
            await self.events.publish("script_event", payload)

    def _trim_script_streams(self) -> list[dict[str, object]]:
        """Keep realtime previews bounded without copying or changing external source files."""
        changed: list[dict[str, object]] = []
        total = sum(len(str(stream["text"])) for stream in self._script_log_streams.values())
        while total > SCRIPT_LOG_BUFFER_LIMIT:
            candidates = [stream for stream in self._script_log_streams.values() if stream["text"]]
            if not candidates:
                break
            stream = min(candidates, key=lambda item: float(item["touched"]))
            overflow = total - SCRIPT_LOG_BUFFER_LIMIT
            current = str(stream["text"])
            removed = min(len(current), max(1, overflow))
            stream["text"] = current[removed:]
            stream["truncated"] = True
            total -= removed
            if stream not in changed:
                changed.append(stream)
        return changed

    @staticmethod
    def _script_stream_payload(stream: dict[str, object], *, append: bool, text: str | None = None) -> dict[str, object]:
        return {
            "runId": stream["runId"],
            "taskId": stream["taskId"],
            "sourceId": stream["sourceId"],
            "sourceName": stream["sourceName"],
            "sourceType": stream["sourceType"],
            "sequence": stream["sequence"],
            "append": append,
            "text": str(stream["text"]) if text is None else text,
            "truncated": bool(stream["truncated"]),
            "updatedAt": stream["updatedAt"],
        }

    async def _publish_script_log(
        self,
        record: RunRecord,
        task_id: str,
        source_id: str,
        source_name: str,
        source_type: str,
        text: str,
    ) -> None:
        if not text:
            return
        if self._script_log_run_id != record.id:
            self._script_log_run_id = record.id
            self._script_log_streams = {}
        key = (task_id, source_id)
        stream = self._script_log_streams.get(key)
        if stream is None:
            stream = {
                "runId": record.id, "taskId": task_id, "sourceId": source_id,
                "sourceName": source_name, "sourceType": source_type,
                "sequence": 0, "text": "", "truncated": False,
                "touched": 0.0, "updatedAt": "",
            }
            self._script_log_streams[key] = stream
        stream["text"] = str(stream["text"]) + text
        stream["sequence"] = int(stream["sequence"]) + 1
        stream["touched"] = asyncio.get_running_loop().time()
        stream["updatedAt"] = datetime.now(UTC).isoformat()
        trimmed = self._trim_script_streams()
        await self.events.publish(
            "script_log",
            self._script_stream_payload(stream, append=int(stream["sequence"]) > 1 and stream not in trimmed, text=text),
        )
        for changed in trimmed:
            if changed is stream:
                continue
            changed["sequence"] = int(changed["sequence"]) + 1
            changed["updatedAt"] = datetime.now(UTC).isoformat()
            await self.events.publish("script_log", self._script_stream_payload(changed, append=False))

    async def _run_log(
        self,
        record: RunRecord,
        text: str,
        *,
        summary_message: str | None = None,
        task_id: str | None = None,
        phase: str = "info",
    ) -> None:
        if record.run_log_id:
            try:
                await asyncio.to_thread(self.detail_logs.append, record.run_log_id, text)
                if not (record.run_log_status or "").startswith("本轮日志保存失败"):
                    record.run_log_status = "已采集"
            except (OSError, ValueError) as error:
                record.run_log_status = f"本轮日志保存失败：{error}"
                self._logger.exception("本轮日志保存失败")
        if self._live_summary_run_id != record.id:
            self._reset_live_summary(record)
        self._live_summary_sequence += 1
        entry = {
            "runId": record.id,
            "sequence": self._live_summary_sequence,
            "taskId": task_id,
            "message": summary_message or text,
            "phase": phase,
            "timestamp": datetime.now(UTC).isoformat(),
        }
        self._live_summary_logs = [*self._live_summary_logs[-(SUMMARY_LOG_LIMIT - 1):], entry]
        await self.events.publish("log", entry)

    def snapshot(self, *, include_script_logs: bool = True) -> dict:
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
            "liveLogs": list(self._live_summary_logs) if self.current_run and self._live_summary_run_id == self.current_run.id else [],
            "scriptLogs": [
                self._script_stream_payload(stream, append=False)
                for stream in self._script_log_streams.values()
            ] if include_script_logs and self.current_run and self._script_log_run_id == self.current_run.id else [],
            "scriptEvents": list(self._script_event_logs)
            if self.current_run and self._script_event_run_id == self.current_run.id else [],
            "runCenter": {
                "currentScriptId": active_item.script_id if active_item else None,
                "currentScriptName": active_item.script_name if active_item else None,
                "currentTaskId": active_item.task_id if active_item else None,
                "currentTaskName": active_item.task_name if active_item else None,
                "currentState": active_item.state.value if active_item else None,
                "completionSource": active_item.completion_source.value if active_item and active_item.completion_source else None,
                "currentIndex": current_index,
                "totalPrograms": total,
                "readableProgress": active_item.readable_report.conclusion if active_item and active_item.readable_report else None,
            },
            "run": self.current_run.model_dump(mode="json") if self.current_run else None,
        }
