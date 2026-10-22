from __future__ import annotations

import asyncio
from pathlib import Path

import pytest

from app.external_logs import ExternalLogWatcher, discover, external_page
from app.models import ItemRecord, MatchMode, RunRecord


def test_discover_only_direct_matching_files(tmp_path):
    (tmp_path / "first.log").write_text("one", encoding="utf-8")
    (tmp_path / "ignored.md").write_text("no", encoding="utf-8")
    nested = tmp_path / "nested"; nested.mkdir()
    (nested / "hidden.log").write_text("no", encoding="utf-8")
    assert [path.name for path in discover(str(tmp_path), "*.log;*.txt")] == ["first.log"]
    assert external_page(tmp_path / "first.log", 0)["text"] == "one"


@pytest.mark.asyncio
async def test_new_directory_log_is_read_without_local_copy(tmp_path):
    source = tmp_path / "logs"; source.mkdir()
    watcher = ExternalLogWatcher(
        str(source), "*.log", "DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
        success_rules=[], failure_rules=[], monitor_success=True,
    )
    await watcher.start_at_end()
    target = source / "part-01.log"
    target.write_text("DONE\n", encoding="utf-8")
    signal = await asyncio.wait_for(watcher.wait_for_signal(asyncio.Event()), timeout=2)
    assert signal and signal.kind == "success"
    assert watcher.paths == [str(target)]
    assert "DONE" in watcher.last_text
    assert not (tmp_path / "detail_logs").exists()


@pytest.mark.asyncio
async def test_multiple_direct_files_contribute_to_the_same_completion_rules(tmp_path):
    source = tmp_path / "logs"; source.mkdir()
    first = source / "part-01.log"; second = source / "part-02.txt"
    first.write_bytes(b"old")
    second.write_bytes(b"old")
    watcher = ExternalLogWatcher(
        str(source), "*.log;*.txt", "DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
        success_rules=[], failure_rules=[], monitor_success=True,
    )
    await watcher.start_at_end()
    first.write_bytes(b"old\nignored")
    second.write_bytes(b"old\nDONE")
    signal = await asyncio.wait_for(watcher.wait_for_signal(asyncio.Event()), timeout=2)
    assert signal and signal.kind == "success"
    assert {Path(path).name for path in watcher.paths} == {"part-01.log", "part-02.txt"}


@pytest.mark.asyncio
async def test_external_log_watcher_recovers_from_truncation(tmp_path):
    source = tmp_path / "current.log"
    source.write_bytes(b"a long old line that will be replaced")
    watcher = ExternalLogWatcher(
        str(source), "*.log", "DONE", MatchMode.KEYWORD, None, MatchMode.KEYWORD,
        success_rules=[], failure_rules=[], monitor_success=True,
    )
    await watcher.start_at_end()
    source.write_bytes(b"DONE")
    signal = await asyncio.wait_for(watcher.wait_for_signal(asyncio.Event()), timeout=2)
    assert signal and signal.kind == "success"


def test_external_log_api_only_reads_paths_recorded_in_history(tmp_path):
    from fastapi.testclient import TestClient

    from app.main import create_app

    source = tmp_path / "outside.log"
    source.write_bytes(b"line one\nline two\n")
    app = create_app(tmp_path / "data")
    item = ItemRecord(
        task_id="t",
        task_name="程序",
        external_log_files=[str(source)],
        external_log_status="已记录 1 个外部日志文件",
    )
    run = RunRecord(queue_id="q", queue_name="队列", trigger_source="manual", items=[item])
    app.state.engine.history.runs = [run]
    with TestClient(app) as client:
        base = f"/api/history/{run.id}/external-logs/{item.task_id}"
        files = client.get(base)
        assert files.status_code == 200
        assert files.json()["files"][0]["name"] == "outside.log"
        page = client.get(base + "/0?offset=0")
        assert page.status_code == 200
        assert page.json()["text"] == "line one\nline two\n"
        assert client.get(base + "/1").status_code == 404
        assert client.get(f"/api/history/{run.id}/external-logs/not-recorded").status_code == 404
        source.unlink()
        assert client.get(base + "/0?offset=0").status_code == 404
