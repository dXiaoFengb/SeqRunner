from __future__ import annotations

from pathlib import Path

import pytest

from app.models import AppConfig, QueueDefinition, TaskDefinition
from app.storage import JsonStore


def test_failed_atomic_save_preserves_previous_document(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    store = JsonStore(tmp_path / "data")
    original_task = TaskDefinition(name="原程序")
    original = AppConfig(queues=[QueueDefinition(name="原配置", task_ids=[original_task.id])], tasks=[original_task])
    store.save_config(original)

    def fail_replace(_source: Path, _destination: Path) -> None:
        raise OSError("simulated replace failure")

    monkeypatch.setattr("app.storage.os.replace", fail_replace)
    replacement_task = TaskDefinition(name="新程序")
    with pytest.raises(OSError):
        store.save_config(AppConfig(queues=[QueueDefinition(name="新配置", task_ids=[replacement_task.id])], tasks=[replacement_task]))

    assert store.load_config().queues[0].name == "原配置"


def test_history_without_cross_script_fields_remains_compatible(tmp_path: Path) -> None:
    store = JsonStore(tmp_path / "data")
    store.data_directory.mkdir(parents=True)
    store.history_path.write_text(
        '{"schema_version":1,"runs":[{"id":"old","queue_id":"q","queue_name":"旧脚本","trigger_source":"manual","items":[{"task_id":"t","task_name":"旧程序"}]}]}',
        encoding="utf-8",
    )

    item = store.load_history().runs[0].items[0]
    assert item.script_id is None
    assert item.script_name is None
