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


@pytest.mark.asyncio
async def test_idle_guard_only_starts_after_a_source_is_discovered_and_resets_on_progress(tmp_path):
    source = tmp_path / "logs"; source.mkdir()
    watcher = ExternalLogWatcher(
        str(source), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
        success_rules=[], failure_rules=[], monitor_success=True,
    )
    cancel = asyncio.Event()
    before_source = asyncio.create_task(watcher.wait_for_idle(1, cancel))
    await asyncio.sleep(.05)
    assert not before_source.done()
    cancel.set()
    assert await before_source is None
    cancel = asyncio.Event()
    target = source / "part.log"; target.write_text("first", encoding="utf-8")
    await watcher._discover_new()
    target.write_text("first\nsecond", encoding="utf-8")
    inner = next(iter(watcher.watchers.values()))
    await asyncio.to_thread(inner._read_new)
    assert inner.last_progress
    assert await asyncio.wait_for(watcher.wait_for_idle(1, cancel), timeout=2)


@pytest.mark.asyncio
async def test_transient_read_errors_are_retried_before_a_terminal_failure(tmp_path, monkeypatch):
    target = tmp_path / "current.log"; target.write_text("old", encoding="utf-8")
    watcher = ExternalLogWatcher(
        str(target), "*.log", None, MatchMode.KEYWORD, None, MatchMode.KEYWORD,
        success_rules=[], failure_rules=[], monitor_success=True,
    )
    await watcher.start_at_end()
    inner = next(iter(watcher.watchers.values()))
    attempts = 0
    original = inner._read_new
    def intermittent():
        nonlocal attempts
        attempts += 1
        if attempts < 3:
            raise OSError("temporary lock")
        return original()
    monkeypatch.setattr(inner, "_read_new", intermittent)
    target.write_text("old\nnew", encoding="utf-8")
    cancel = asyncio.Event()
    pending = asyncio.create_task(watcher.wait_for_signal(cancel))
    await asyncio.sleep(2.2)
    cancel.set()
    assert await pending is None
    assert attempts >= 3


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
