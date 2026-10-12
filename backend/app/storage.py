from __future__ import annotations

import json
import os
from pathlib import Path
from typing import TypeVar

from pydantic import BaseModel

from .models import AppConfig, HistoryDocument

T = TypeVar("T", bound=BaseModel)


class JsonStore:
    """同目录临时文件加 os.replace，失败时保留原文件。"""

    def __init__(self, data_directory: Path) -> None:
        self.data_directory = data_directory
        self.config_path = data_directory / "config.json"
        self.history_path = data_directory / "history.json"

    def load_config(self) -> AppConfig:
        return self._load(self.config_path, AppConfig)

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
