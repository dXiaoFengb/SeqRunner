from __future__ import annotations

from pathlib import Path

import pytest

from app.models import AppConfig, QueueDefinition
from app.storage import JsonStore


def test_failed_atomic_save_preserves_previous_document(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    store = JsonStore(tmp_path / "data")
    original = AppConfig(queues=[QueueDefinition(name="原配置")])
    store.save_config(original)

    def fail_replace(_source: Path, _destination: Path) -> None:
        raise OSError("simulated replace failure")

    monkeypatch.setattr("app.storage.os.replace", fail_replace)
    with pytest.raises(OSError):
        store.save_config(AppConfig(queues=[QueueDefinition(name="新配置")]))

    assert store.load_config().queues[0].name == "原配置"
