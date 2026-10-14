from __future__ import annotations

from datetime import UTC, datetime, timedelta

from .engine import RunBusyError, RunnerEngine, RunValidationError
from .models import StartupMode


class QueueScheduler:
    """每分钟检查；触发标记写入配置以抵御重启和同分钟重复。"""

    def __init__(self, engine: RunnerEngine) -> None:
        self.engine = engine
        self._startup_triggered = False

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
                self.engine.config.startup_fired_dates[plan.id] = today
                await self.engine.save_config()
        except RunBusyError:
            await self.engine.record_schedule_skip(plan, datetime.now(UTC))
        except (RunValidationError, StopIteration) as error:
            await self.engine.events.publish("schedule", {"planId": plan.id, "message": f"启动时计划未执行：{error}"})

    async def tick(self, now: datetime) -> None:
        current = now.replace(second=0, microsecond=0)
        window_start = current - timedelta(minutes=10)
        fired = set(self.engine.config.fired_slots)
        changed = False
        for plan in self.engine.config.plans:
            if not plan.scheduled_enabled:
                continue
            due_keys: list[str] = []
            for slot in plan.time_slots:
                if not slot.enabled:
                    continue
                for minute in self._minutes_between(window_start, current):
                    if minute.weekday() not in slot.days or minute.strftime("%H:%M") != slot.time:
                        continue
                    due_keys.append(f"{plan.id}:{slot.id}:{minute.isoformat()}")
            new_keys = [key for key in due_keys if key not in fired]
            if not new_keys:
                continue
            # 一个队列同一检查只启动一次；其余同刻槽位一起标记。
            fired.update(new_keys)
            changed = True
            if self.engine.is_busy:
                await self.engine.record_schedule_skip(plan, current)
                continue
            try:
                await self.engine.start_plan(plan.id, "scheduled", current)
            except RunBusyError:
                await self.engine.record_schedule_skip(plan, current)
            except (RunValidationError, StopIteration) as error:
                await self.engine.events.publish("schedule", {"planId": plan.id, "message": f"定时计划未执行：{error}"})
        if changed:
            self.engine.config.fired_slots = sorted(fired)[-1000:]
            await self.engine.save_config()

    @staticmethod
    def _minutes_between(start: datetime, end: datetime):
        current = start
        while current <= end:
            yield current
            current += timedelta(minutes=1)
