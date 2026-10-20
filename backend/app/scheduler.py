from __future__ import annotations

from datetime import UTC, datetime, timedelta

from .engine import RunBusyError, RunnerEngine, RunValidationError
from .models import AppConfig, SchedulePlan, StartupMode


class QueueScheduler:
    """每分钟检查；触发标记写入配置以抵御重启和同分钟重复。"""

    def __init__(self, engine: RunnerEngine) -> None:
        self.engine = engine
        self._startup_triggered = False
        self._pending_fired_slots: set[str] = set()

    async def run_startup_once(self) -> None:
        """本次后端启动只检查一次启动计划，且依旧交给同一执行器。"""
        if self._startup_triggered:
            return
        self._startup_triggered = True
        plans = [plan for plan in self.engine.config.plans if plan.startup_mode != StartupMode.NEVER]
        if not plans:
            return
        plan = plans[0]
        local_now = datetime.now().astimezone()
        today = local_now.date().isoformat()
        if plan.startup_mode == StartupMode.DAILY_FIRST and self.engine.config.startup_fired_dates.get(plan.id) == today:
            return
        try:
            await self.engine.start_plan(plan.id, "startup", local_now)
            if plan.startup_mode == StartupMode.DAILY_FIRST:
                def mark_startup_fired(candidate: AppConfig) -> None:
                    candidate.startup_fired_dates[plan.id] = today

                try:
                    await self.engine.update_config(mark_startup_fired)
                except OSError as error:
                    await self.engine.events.publish(
                        "schedule",
                        {"planId": plan.id, "message": f"启动计划已执行，但每日标记保存失败：{error}"},
                    )
        except RunBusyError:
            await self.engine.record_schedule_skip(plan, datetime.now(UTC))
        except (RunValidationError, StopIteration) as error:
            await self.engine.events.publish("schedule", {"planId": plan.id, "message": f"启动时计划未执行：{error}"})

    async def tick(self, now: datetime) -> None:
        current = now.replace(second=0, microsecond=0)
        window_start = current - timedelta(minutes=10)
        fired = set(self.engine.config.fired_slots) | self._pending_fired_slots
        candidates: list[tuple[datetime, int, SchedulePlan]] = []
        for plan_index, plan in enumerate(self.engine.config.plans):
            if not plan.scheduled_enabled:
                continue
            due_keys: list[tuple[datetime, str]] = []
            for slot in plan.time_slots:
                if not slot.enabled:
                    continue
                for minute in self._minutes_between(window_start, current):
                    if minute.weekday() not in slot.days or minute.strftime("%H:%M") != slot.time:
                        continue
                    due_keys.append((minute, f"{plan.id}:{slot.id}:{minute.isoformat()}"))
            new_keys = [(minute, key) for minute, key in due_keys if key not in fired]
            if not new_keys:
                continue
            # 一个队列同一检查只启动一次；其余同刻槽位一起标记。
            fired.update(key for _, key in new_keys)
            self._pending_fired_slots.update(key for _, key in new_keys)
            candidates.append((min(minute for minute, _ in new_keys), plan_index, plan))

        # 计划卡顺序只影响界面；恢复窗口内的定时项始终按实际预定时刻处理。
        for due_at, _, plan in sorted(candidates, key=lambda candidate: (candidate[0], candidate[1])):
            if self.engine.is_busy:
                await self.engine.record_schedule_skip(plan, due_at)
                continue
            try:
                await self.engine.start_plan(plan.id, "scheduled", due_at)
            except RunBusyError:
                await self.engine.record_schedule_skip(plan, due_at)
            except (RunValidationError, StopIteration) as error:
                await self.engine.events.publish("schedule", {"planId": plan.id, "message": f"定时计划未执行：{error}"})
        if self._pending_fired_slots:
            pending = set(self._pending_fired_slots)

            def persist_fired_slots(candidate: AppConfig) -> None:
                candidate.fired_slots = sorted(set(candidate.fired_slots) | pending)[-1000:]

            try:
                await self.engine.update_config(persist_fired_slots)
            except OSError as error:
                await self.engine.events.publish(
                    "schedule",
                    {"planId": None, "message": f"定时触发标记保存失败：{error}"},
                )
            else:
                self._pending_fired_slots.difference_update(pending)

    @staticmethod
    def _minutes_between(start: datetime, end: datetime):
        current = start
        while current <= end:
            yield current
            current += timedelta(minutes=1)
