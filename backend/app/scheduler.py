from __future__ import annotations

from datetime import datetime, timedelta

from .engine import RunBusyError, RunnerEngine


class QueueScheduler:
    """每分钟检查；触发标记写入配置以抵御重启和同分钟重复。"""

    def __init__(self, engine: RunnerEngine) -> None:
        self.engine = engine

    async def tick(self, now: datetime) -> None:
        current = now.replace(second=0, microsecond=0)
        window_start = current - timedelta(minutes=10)
        fired = set(self.engine.config.fired_slots)
        changed = False
        for queue in self.engine.config.queues:
            due_keys: list[str] = []
            for slot in queue.time_slots:
                if not slot.enabled:
                    continue
                for minute in self._minutes_between(window_start, current):
                    if minute.weekday() not in slot.days or minute.strftime("%H:%M") != slot.time:
                        continue
                    due_keys.append(f"{queue.id}:{slot.id}:{minute.isoformat()}")
            new_keys = [key for key in due_keys if key not in fired]
            if not new_keys:
                continue
            # 一个队列同一检查只启动一次；其余同刻槽位一起标记。
            fired.update(new_keys)
            changed = True
            if self.engine.is_busy:
                await self.engine.events.publish("schedule", {"queueId": queue.id, "message": "因忙跳过"})
                continue
            try:
                await self.engine.start(queue.id, "scheduled")
            except RunBusyError:
                await self.engine.events.publish("schedule", {"queueId": queue.id, "message": "因忙跳过"})
        if changed:
            self.engine.config.fired_slots = sorted(fired)[-1000:]
            await self.engine.save_config()

    @staticmethod
    def _minutes_between(start: datetime, end: datetime):
        current = start
        while current <= end:
            yield current
            current += timedelta(minutes=1)
