from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Literal
from uuid import uuid4

from pydantic import BaseModel, Field

SCHEMA_VERSION = 1


class MatchMode(str, Enum):
    KEYWORD = "keyword"
    REGEX = "regex"


class CompletionSource(str, Enum):
    MANUAL = "manual"
    PROCESS_EXIT = "process_exit"
    SUCCESS_LOG = "success_log"


class ItemState(str, Enum):
    QUEUED = "queued"
    VALIDATING = "validating"
    STARTING = "starting"
    MONITORING = "monitoring"
    WAITING_MANUAL = "waiting_manual"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CLOSING = "closing"
    FORCE_CLOSING = "force_closing"
    COOLING_DOWN = "cooling_down"
    STOPPED = "stopped"


class RunState(str, Enum):
    IDLE = "idle"
    RUNNING = "running"
    STOPPING = "stopping"
    COMPLETED = "completed"
    STOPPED = "stopped"


class TimeSlot(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    enabled: bool = True
    days: list[int] = Field(default_factory=lambda: list(range(7)), min_length=1)
    time: str = Field(pattern=r"^([01]\d|2[0-3]):[0-5]\d$")


class TaskDefinition(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    name: str = Field(min_length=1, max_length=120)
    # 空路径允许作为尚未完成的编辑草稿；运行前仍由执行器严格校验。
    executable: str = ""
    arguments: list[str] = Field(default_factory=list)
    working_directory: str | None = None
    allow_manual_confirmation: bool = False
    allow_process_exit: bool = True
    log_path: str | None = None
    success_log: str | None = None
    success_log_mode: MatchMode = MatchMode.KEYWORD
    failure_log: str | None = None
    failure_log_mode: MatchMode = MatchMode.KEYWORD
    preset_id: str | None = None
    preset_evidence: str | None = None
    success_log_rules: list[LogRule] = Field(default_factory=list)
    failure_log_rules: list[LogRule] = Field(default_factory=list)
    timeout_seconds: int = Field(default=600, ge=1, le=86_400)

    def has_success_log(self) -> bool:
        return bool(self.log_path and (self.success_log or self.success_log_rules))


class LogRule(BaseModel):
    pattern: str = Field(min_length=1, max_length=500)
    mode: MatchMode = MatchMode.KEYWORD


class QueueDefinition(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    name: str = Field(min_length=1, max_length=120)
    task_ids: list[str] = Field(default_factory=list)
    time_slots: list[TimeSlot] = Field(default_factory=list)


class AppConfig(BaseModel):
    schema_version: Literal[SCHEMA_VERSION] = SCHEMA_VERSION
    queues: list[QueueDefinition] = Field(default_factory=list)
    tasks: list[TaskDefinition] = Field(default_factory=list)
    fired_slots: list[str] = Field(default_factory=list)


class ItemRecord(BaseModel):
    task_id: str
    task_name: str
    # 旧历史没有归属字段时，保持可读；全部脚本运行才会填充它们。
    script_id: str | None = None
    script_name: str | None = None
    state: ItemState = ItemState.QUEUED
    completion_source: CompletionSource | None = None
    started_at: datetime | None = None
    ended_at: datetime | None = None
    exit_code: int | None = None
    error_summary: str | None = None
    log_summary: list[str] = Field(default_factory=list)


class RunRecord(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    queue_id: str
    queue_name: str
    trigger_source: Literal["manual", "scheduled"]
    # 单脚本是既有行为；all_scripts 只由手动“运行全部脚本”创建。
    run_mode: Literal["script", "all_scripts"] = "script"
    state: RunState = RunState.RUNNING
    started_at: datetime = Field(default_factory=datetime.now)
    ended_at: datetime | None = None
    items: list[ItemRecord] = Field(default_factory=list)
    message: str | None = None


class HistoryDocument(BaseModel):
    schema_version: Literal[SCHEMA_VERSION] = SCHEMA_VERSION
    runs: list[RunRecord] = Field(default_factory=list)
