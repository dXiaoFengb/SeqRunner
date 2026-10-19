from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Literal
from uuid import uuid4

from pydantic import BaseModel, Field, model_validator

SCHEMA_VERSION = 3


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


class StartupMode(str, Enum):
    NEVER = "never"
    ALWAYS = "always"
    DAILY_FIRST = "daily_first"


class CompletionAction(str, Enum):
    NONE = "none"
    SHUTDOWN = "shutdown"
    FORCE_SHUTDOWN = "force_shutdown"
    RESTART = "restart"
    HIBERNATE = "hibernate"
    SLEEP = "sleep"
    EXIT_APP = "exit_app"
    LOGOFF = "logoff"


class TimeSlot(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    enabled: bool = True
    days: list[int] = Field(default_factory=lambda: list(range(7)), min_length=1)
    time: str = Field(pattern=r"^([01]\d|2[0-3]):[0-5]\d$")


class MAAAdapterConfig(BaseModel):
    """本应用自己的 MAA GUI 启动前检查；绝不写入 MAA 配置。"""

    installation_directory: str | None = None
    adb_executable: str | None = None
    adb_address: str | None = None
    last_check_at: datetime | None = None
    last_check_ok: bool | None = None
    last_check_message: str | None = None


class MaaEndAdapterConfig(BaseModel):
    """仅编排本轮创建的 Windows 游戏进程与 MaaEnd 启动顺序。"""

    installation_directory: str | None = None
    game_executable: str | None = None
    game_arguments: list[str] = Field(default_factory=list)
    game_wait_seconds: int = Field(default=60, ge=0, le=3600)
    close_game_on_finish: bool = True


class UiLogRegion(BaseModel):
    """用户确认的客户区像素范围；使用前仍必须核对窗口身份和尺寸。"""

    executable: str = Field(min_length=1)
    window_class: str = Field(min_length=1)
    client_width: int = Field(gt=0)
    client_height: int = Field(gt=0)
    x: int = Field(ge=0)
    y: int = Field(ge=0)
    width: int = Field(gt=0)
    height: int = Field(gt=0)

    @model_validator(mode="after")
    def validate_bounds(self) -> UiLogRegion:
        if self.x + self.width > self.client_width or self.y + self.height > self.client_height:
            raise ValueError("界面日志区域必须位于窗口客户区内")
        return self


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
    ui_log_region: UiLogRegion | None = None
    success_log: str | None = None
    success_log_mode: MatchMode = MatchMode.KEYWORD
    failure_log: str | None = None
    failure_log_mode: MatchMode = MatchMode.KEYWORD
    preset_id: str | None = None
    preset_evidence: str | None = None
    success_log_rules: list[LogRule] = Field(default_factory=list)
    failure_log_rules: list[LogRule] = Field(default_factory=list)
    timeout_seconds: int = Field(default=600, ge=1, le=86_400)
    maa_adapter: MAAAdapterConfig | None = None
    maaend_adapter: MaaEndAdapterConfig | None = None

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


class PlanTask(BaseModel):
    """计划中的单软件项；运行星期只影响该计划的当日展开。"""

    id: str = Field(default_factory=lambda: str(uuid4()))
    script_id: str
    days: list[int] = Field(default_factory=lambda: list(range(7)), min_length=1)


class SchedulePlan(BaseModel):
    """面向用户的多软件计划；脚本本身仍保存在 queues/tasks。"""

    id: str = Field(default_factory=lambda: str(uuid4()))
    name: str = Field(min_length=1, max_length=120)
    items: list[PlanTask] = Field(default_factory=list)
    time_slots: list[TimeSlot] = Field(default_factory=list)
    startup_mode: StartupMode = StartupMode.NEVER
    scheduled_enabled: bool = True
    completion_action: CompletionAction = CompletionAction.NONE
    completion_delay_minutes: int = Field(default=0, ge=0, le=10_080)


class AppConfig(BaseModel):
    # 损坏的旧配置必须可被读取并以诊断方式阻止运行，不能因为版本字段
    # 直接把用户留在无法打开应用的状态。所有正常保存的新配置均为 v3。
    schema_version: Literal[1, 2, SCHEMA_VERSION] = SCHEMA_VERSION
    queues: list[QueueDefinition] = Field(default_factory=list)
    tasks: list[TaskDefinition] = Field(default_factory=list)
    plans: list[SchedulePlan] = Field(default_factory=list)
    fired_slots: list[str] = Field(default_factory=list)
    startup_fired_dates: dict[str, str] = Field(default_factory=dict)


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
    detail_log_id: str | None = None
    detail_log_status: str | None = None
    ui_log_id: str | None = None
    ui_log_status: str | None = None


class RunRecord(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    queue_id: str
    queue_name: str
    trigger_source: Literal["manual", "scheduled", "startup"]
    # 单脚本是既有行为；all_scripts 只由手动“运行全部脚本”创建。
    run_mode: Literal["script", "all_scripts", "plan"] = "script"
    plan_id: str | None = None
    state: RunState = RunState.RUNNING
    started_at: datetime = Field(default_factory=datetime.now)
    ended_at: datetime | None = None
    items: list[ItemRecord] = Field(default_factory=list)
    message: str | None = None
    scheduler_skips: list[str] = Field(default_factory=list)
    run_log_id: str | None = None
    run_log_status: str | None = None
    completion_action: CompletionAction = CompletionAction.NONE
    completion_action_status: str | None = None


class HistoryDocument(BaseModel):
    schema_version: Literal[1, 2, SCHEMA_VERSION] = SCHEMA_VERSION
    runs: list[RunRecord] = Field(default_factory=list)
