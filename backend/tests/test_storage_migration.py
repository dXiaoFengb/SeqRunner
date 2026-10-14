from __future__ import annotations

import json

from app.storage import JsonStore


def task(task_id: str, name: str) -> dict[str, object]:
    return {
        "id": task_id, "name": name, "executable": "", "arguments": [],
        "working_directory": None, "allow_manual_confirmation": True,
        "allow_process_exit": True, "log_path": None, "success_log": None,
        "success_log_mode": "keyword", "failure_log": None,
        "failure_log_mode": "keyword", "preset_id": None,
        "preset_evidence": None, "success_log_rules": [],
        "failure_log_rules": [], "timeout_seconds": 600,
    }


def test_load_splits_legacy_multi_program_script_and_clears_slots(tmp_path):
    root = tmp_path / "data"
    root.mkdir()
    (root / "config.json").write_text(json.dumps({
        "schema_version": 1,
        "fired_slots": [],
        "queues": [{"id": "script", "name": "组合", "task_ids": ["a", "b"], "time_slots": [{"id": "slot", "enabled": True, "days": [0], "time": "09:00"}]}],
        "tasks": [task("a", "MAA"), task("b", "BetterGI")],
    }), encoding="utf-8")

    store = JsonStore(root)
    config = store.load_config()

    assert [script.task_ids for script in config.queues] == [["a"], ["b"]]
    assert all(not script.time_slots for script in config.queues)
    assert config.schema_version == 3
    assert config.plans == []
    assert "原组合定时已清空" in (store.last_config_migration_notice or "")
    persisted = json.loads((root / "config.json").read_text(encoding="utf-8"))
    assert len(persisted["queues"]) == 2
    assert persisted["schema_version"] == 3
    assert list((root / "backups").glob("config-before-v3-*.json"))


def test_load_preserves_damaged_references_without_unsafe_migration(tmp_path):
    root = tmp_path / "data"
    root.mkdir()
    source = {"schema_version": 1, "fired_slots": [], "queues": [{"id": "script", "name": "损坏", "task_ids": ["missing"], "time_slots": []}], "tasks": []}
    (root / "config.json").write_text(json.dumps(source), encoding="utf-8")

    store = JsonStore(root)
    config = store.load_config()

    assert config.queues[0].task_ids == ["missing"]
    assert "保留原配置" in (store.last_config_migration_notice or "")
    assert json.loads((root / "config.json").read_text(encoding="utf-8")) == source


def test_load_repairs_empty_script_and_orphan_without_losing_tasks(tmp_path):
    root = tmp_path / "data"
    root.mkdir()
    (root / "config.json").write_text(json.dumps({
        "schema_version": 1,
        "fired_slots": [],
        "queues": [{"id": "empty", "name": "空脚本", "task_ids": [], "time_slots": []}],
        "tasks": [task("orphan", "孤立程序")],
    }), encoding="utf-8")

    store = JsonStore(root)
    config = store.load_config()

    assert len(config.queues) == 2
    assert all(len(script.task_ids) == 1 for script in config.queues)
    assert any(item.name == "孤立程序" for item in config.tasks)
    assert "补建单软件脚本" in (store.last_config_migration_notice or "")


def test_load_upgrades_v2_plan_items_without_reordering_or_losing_slots(tmp_path):
    root = tmp_path / "data"
    root.mkdir()
    (root / "config.json").write_text(json.dumps({
        "schema_version": 2,
        "fired_slots": [],
        "queues": [
            {"id": "one", "name": "一", "task_ids": ["a"], "time_slots": []},
            {"id": "two", "name": "二", "task_ids": ["b"], "time_slots": []},
        ],
        "tasks": [task("a", "程序一"), task("b", "程序二")],
        "plans": [{
            "id": "plan", "name": "早班", "script_ids": ["two", "one"], "run_on_startup": True,
            "time_slots": [{"id": "slot", "enabled": True, "days": [0, 2], "time": "09:00"}],
        }],
    }), encoding="utf-8")

    config = JsonStore(root).load_config()

    assert config.schema_version == 3
    assert [item.script_id for item in config.plans[0].items] == ["two", "one"]
    assert all(item.days == list(range(7)) for item in config.plans[0].items)
    assert config.plans[0].startup_mode.value == "always"
    assert config.plans[0].time_slots[0].time == "09:00"
    assert list((root / "backups").glob("config-before-v3-*.json"))
