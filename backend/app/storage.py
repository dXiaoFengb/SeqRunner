from __future__ import annotations

import json
import os
from datetime import UTC, datetime
from pathlib import Path
from typing import TypeVar
from uuid import uuid4

from pydantic import BaseModel

from .models import AppConfig, HistoryDocument

T = TypeVar("T", bound=BaseModel)


class JsonStore:
    """同目录临时文件加 os.replace，失败时保留原文件。"""

    def __init__(self, data_directory: Path) -> None:
        self.data_directory = data_directory
        self.config_path = data_directory / "config.json"
        self.history_path = data_directory / "history.json"
        self.last_config_migration_notice: str | None = None

    def load_config(self) -> AppConfig:
        self.last_config_migration_notice = None
        if not self.config_path.exists():
            return AppConfig()
        raw = json.loads(self.config_path.read_text(encoding="utf-8"))
        migrated, script_notice = self._migrate_single_software_scripts(raw)
        # 旧引用损坏时，第一阶段特意保留原始配置供用户修复；此时也不能
        # 再清空其定时槽或强行写成 v2。
        if self.last_config_migration_notice and script_notice is None:
            plan_notice = None
        else:
            migrated, plan_notice = self._migrate_to_plans(migrated)
        notice = "；".join(item for item in (script_notice, plan_notice) if item) or None
        config = AppConfig.model_validate(migrated)
        if notice:
            self._backup_config_before_migration(raw)
            self._save(self.config_path, config)
            self.last_config_migration_notice = notice
        return config

    def load_history(self) -> HistoryDocument:
        return self._load(self.history_path, HistoryDocument)

    def save_config(self, config: AppConfig) -> None:
        self._save(self.config_path, config)

    def save_history(self, history: HistoryDocument) -> None:
        self._save(self.history_path, history)

    def _load(self, path: Path, model: type[T]) -> T:
        if not path.exists():
            return model()
        return model.model_validate_json(path.read_text(encoding="utf-8"))

    def _save(self, path: Path, document: BaseModel) -> None:
        self.data_directory.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(f"{path.suffix}.tmp")
        payload = json.dumps(document.model_dump(mode="json"), ensure_ascii=False, indent=2)
        try:
            with temporary.open("w", encoding="utf-8", newline="\n") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temporary, path)
        except Exception:
            temporary.unlink(missing_ok=True)
            raise

    def _backup_config_before_migration(self, raw: object) -> None:
        """迁移会清空旧定时槽，先保留仅本机可用的恢复副本。"""
        backups = self.data_directory / "backups"
        backups.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
        target = backups / f"config-before-v2-{stamp}.json"
        target.write_text(json.dumps(raw, ensure_ascii=False, indent=2), encoding="utf-8")

    @staticmethod
    def _draft_task() -> dict[str, object]:
        return {
            "id": str(uuid4()), "name": "新程序", "executable": "", "arguments": [],
            "working_directory": None, "allow_manual_confirmation": True,
            "allow_process_exit": True, "log_path": None, "success_log": None,
            "success_log_mode": "keyword", "failure_log": None,
            "failure_log_mode": "keyword", "preset_id": None,
            "preset_evidence": None, "success_log_rules": [],
            "failure_log_rules": [], "timeout_seconds": 600,
        }

    def _migrate_single_software_scripts(self, raw: object) -> tuple[object, str | None]:
        """将旧的多程序脚本拆分；遇到损坏引用时绝不静默改写。"""
        if not isinstance(raw, dict):
            return raw, None
        queues = raw.get("queues")
        tasks = raw.get("tasks")
        if not isinstance(queues, list) or not isinstance(tasks, list):
            return raw, None
        task_ids = [task.get("id") for task in tasks if isinstance(task, dict) and isinstance(task.get("id"), str)]
        task_book = {task_id for task_id in task_ids}
        referenced = [task_id for queue in queues if isinstance(queue, dict) for task_id in queue.get("task_ids", []) if isinstance(task_id, str)]
        missing = [task_id for task_id in referenced if task_id not in task_book]
        duplicates = len(referenced) != len(set(referenced))
        if missing or duplicates:
            self.last_config_migration_notice = "检测到脚本引用缺失或重复的程序，已保留原配置并阻止运行；请在编辑页修复后再保存。"
            return raw, None

        changed = False
        split_count = 0
        repaired_count = 0
        next_queues: list[dict[str, object]] = []
        known = set(referenced)
        for queue in queues:
            if not isinstance(queue, dict):
                return raw, None
            ids = list(queue.get("task_ids", []))
            if len(ids) <= 1:
                if not ids:
                    task = self._draft_task()
                    tasks.append(task)
                    queue = {**queue, "task_ids": [task["id"]]}
                    known.add(task["id"])
                    changed = True
                    repaired_count += 1
                next_queues.append(queue)
                continue
            changed = True
            split_count += 1
            for index, task_id in enumerate(ids):
                task = next((item for item in tasks if isinstance(item, dict) and item.get("id") == task_id), None)
                name = task.get("name", "新程序") if isinstance(task, dict) else "新程序"
                next_queues.append({
                    **queue,
                    "id": queue.get("id") if index == 0 else str(uuid4()),
                    "name": name,
                    "task_ids": [task_id],
                    "time_slots": [],
                })
        for task in tasks:
            if isinstance(task, dict) and task.get("id") not in known:
                next_queues.append({"id": str(uuid4()), "name": task.get("name", "新程序"), "task_ids": [task["id"]], "time_slots": []})
                changed = True
                repaired_count += 1
        if not changed:
            return raw, None
        migrated = {**raw, "queues": next_queues, "tasks": tasks}
        details: list[str] = []
        if split_count:
            details.append(f"已将 {split_count} 个旧多程序脚本拆分为单软件脚本，原组合定时已清空")
        if repaired_count:
            details.append(f"已为 {repaired_count} 个旧空脚本或孤立程序补建单软件脚本")
        return migrated, "；".join(details) + "。请分别检查软件配置和定时。"

    @staticmethod
    def _migrate_to_plans(raw: object) -> tuple[object, str | None]:
        """v2 将时间槽从单软件脚本彻底移交给多软件计划管理。"""
        if not isinstance(raw, dict):
            return raw, None
        if raw.get("schema_version") == 2:
            return raw, None
        queues = raw.get("queues")
        if not isinstance(queues, list):
            return raw, None
        removed = 0
        migrated_queues: list[object] = []
        for queue in queues:
            if not isinstance(queue, dict):
                return raw, None
            slots = queue.get("time_slots", [])
            if isinstance(slots, list):
                removed += len(slots)
            migrated_queues.append({**queue, "time_slots": []})
        migrated = {
            **raw,
            "schema_version": 2,
            "queues": migrated_queues,
            "plans": raw.get("plans") if isinstance(raw.get("plans"), list) else [],
            "fired_slots": [],
        }
        if removed:
            return migrated, f"已清空 {removed} 个旧单软件定时槽，请在计划管理重新创建多软件计划"
        return migrated, "已升级到多软件计划管理；请在计划管理创建计划"
