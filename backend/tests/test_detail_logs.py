from datetime import UTC, date, datetime, timedelta

import pytest

from app.detail_logs import DetailCapture, DetailLogs
from app.models import HistoryDocument, ItemRecord, RunRecord


def content(store, identifier):
    page = store.page(identifier, 0)
    entries = page["entries"]
    while page["next_offset"] is not None:
        page = store.page(identifier, page["next_offset"])
        entries.extend(page["entries"])
    return "".join(entry["text"] for entry in entries)


@pytest.mark.asyncio
async def test_capture_new_content_truncation_and_rotation(tmp_path):
    source = tmp_path / "source.log"
    source.write_text("旧内容\n", encoding="utf-8")
    store = DetailLogs(tmp_path)
    item = ItemRecord(task_id="task", task_name="程序")
    capture = DetailCapture(store, str(source), item)
    capture.start()
    with source.open("ab") as stream:
        stream.write("新内容\n".encode())
    capture._read()
    rotated = tmp_path / "source.old"
    source.rename(rotated)
    with rotated.open("ab") as stream:
        stream.write("轮转前\n".encode())
    source.write_bytes("轮转后\n".encode())
    capture._read()
    source.write_bytes("短\n".encode())
    capture._read()
    await capture.finish()
    assert content(store, item.detail_log_id) == "新内容\n轮转前\n轮转后\n短\n"
    assert source.read_text(encoding="utf-8") == "短\n"


@pytest.mark.asyncio
async def test_missing_file_and_partial_multibyte_sequence(tmp_path):
    source = tmp_path / "late.log"
    store = DetailLogs(tmp_path)
    item = ItemRecord(task_id="t", task_name="程序")
    capture = DetailCapture(store, str(source), item)
    capture.start()
    assert item.detail_log_status == "日志文件尚未出现"
    source.write_bytes("你".encode()[:2])
    capture._read()
    with source.open("ab") as stream:
        stream.write("你".encode()[2:] + b"\n")
    capture._read()
    await capture.finish()
    assert content(store, item.detail_log_id) == "你\n"


def test_archive_paging_and_cleanup_selection(tmp_path):
    store = DetailLogs(tmp_path)
    item = ItemRecord(task_id="t", task_name="程序", detail_log_id="f6e372af-0e2c-4fd7-9434-f229c3a1643f")
    for i in range(30):
        store.append(item.detail_log_id, f"{i}:" + "测" * 5000 + "\n")
    ended = datetime(2026, 1, 1, tzinfo=UTC)
    run = RunRecord(queue_id="q", queue_name="队列", trigger_source="manual", ended_at=ended, items=[item])
    history = HistoryDocument(runs=[run])
    original = content(store, item.detail_log_id)
    assert not store.archive(history, ended + timedelta(days=6))
    assert store.path(item.detail_log_id).exists()
    assert not store.archive(history, ended + timedelta(days=7))
    assert not store.path(item.detail_log_id).exists()
    assert store.path(item.detail_log_id, True).exists()
    assert content(store, item.detail_log_id) == original
    assert store.matching(history, date(2026, 1, 3)) == [item]
    store.delete(item.detail_log_id)
    assert not store.path(item.detail_log_id, True).exists()


def test_compression_failure_keeps_original(tmp_path, monkeypatch):
    store = DetailLogs(tmp_path)
    identifier = "f6e372af-0e2c-4fd7-9434-f229c3a1643f"
    store.append(identifier, "原文")
    def fail(*args):
        raise OSError("模拟故障")
    monkeypatch.setattr("app.detail_logs.os.replace", fail)
    with pytest.raises(OSError):
        store._archive_one(identifier)
    assert content(store, identifier) == "原文"
    assert not store.path(identifier, True).exists()


def test_api_preview_cleanup_and_paged_read(tmp_path):
    from fastapi.testclient import TestClient

    from app.main import create_app

    app = create_app(tmp_path / "data")
    engine = app.state.engine
    item = ItemRecord(task_id="t", task_name="程序", detail_log_id="f6e372af-0e2c-4fd7-9434-f229c3a1643f")
    run = RunRecord(queue_id="q", queue_name="队列", trigger_source="manual", ended_at=datetime(2026, 1, 1, tzinfo=UTC), items=[item])
    engine.history.runs = [run]
    engine.detail_logs.append(item.detail_log_id, "受控日志")
    with TestClient(app) as client:
        path = f"/api/history/{run.id}/details/{item.detail_log_id}"
        assert client.get(path).json()["entries"][0]["text"] == "受控日志"
        preview = client.post("/api/history/detail-cleanup/preview", json={"cutoff": "2026-01-03"}).json()
        assert preview["count"] == 1
        assert client.post("/api/history/detail-cleanup", json={"cutoff": "2026-01-03", "ids": []}).status_code == 409
        assert client.get(path).status_code == 200
        assert client.post("/api/history/detail-cleanup", json={"cutoff": "2026-01-03", "ids": preview["ids"]}).json()["count"] == 1
        assert client.get(path).status_code == 404
        assert len(engine.history.runs) == 1
        assert item.detail_log_status == "详细日志已清理"


@pytest.mark.asyncio
async def test_rewrite_larger_than_previous_size_is_detected(tmp_path):
    source = tmp_path / "source.log"
    source.write_bytes(b"old text")
    store = DetailLogs(tmp_path)
    item = ItemRecord(task_id="t", task_name="程序")
    capture = DetailCapture(store, str(source), item)
    capture.start()
    source.write_bytes(b"brand new, longer text")
    capture._read()
    await capture.finish()
    assert content(store, item.detail_log_id) == "brand new, longer text"


def test_pruned_history_cleans_only_owned_detail(tmp_path):
    from app.engine import RunnerEngine
    from app.events import EventHub
    from app.storage import JsonStore

    engine = RunnerEngine(JsonStore(tmp_path), EventHub())
    item = ItemRecord(task_id="t", task_name="程序", detail_log_id="f6e372af-0e2c-4fd7-9434-f229c3a1643f")
    expired = RunRecord(queue_id="q", queue_name="旧", trigger_source="manual", items=[item])
    engine.detail_logs.append(item.detail_log_id, "副本")
    external = tmp_path / "source.log"
    external.write_bytes(b"original")
    engine.history.runs = [RunRecord(queue_id="q", queue_name=str(i), trigger_source="manual") for i in range(200)] + [expired]
    engine._save_new_history()
    assert len(engine.history.runs) == 200
    assert not engine.detail_logs.path(item.detail_log_id).exists()
    assert external.read_bytes() == b"original"
