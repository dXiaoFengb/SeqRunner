import asyncio
import os
import subprocess
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from app.detail_logs import DetailLogs
from app.models import HistoryDocument, ItemRecord, RunRecord
from app.processes import OwnedProcess, capture_owned_process
from app.ui_logs import WindowLogCapture, appended, ocr_appended, read_march_window


def test_snapshot_overlap_and_ambiguity():
    assert appended("一\n二\n", "一\n二\n三\n") == "三\n"
    assert appended("一\n二\n", "二\n三\n") == "三\n"
    assert appended("一\n二\n", "其他内容") is None
    assert appended("一\n", "") == ""


def test_ocr_overlap_ignores_small_recognition_error_but_rejects_ambiguity():
    assert ocr_appended(".baseline\n", "baseline\nnew line\n") == "new line\n"
    assert ocr_appended("same text\nsame text\n", "same text\nsame text\nnew line\n") == "new line\n"
    assert ocr_appended("same text\nsame text\n", "same text\nnew line\n") is None
    assert ocr_appended("旧日志\n等待结束\n", "等待结束\n正在运行\n") == "正在运行\n"
    assert ocr_appended("旧日志\n等待结束\n", "等 待 结 束\n正在运行\n") == "正在运行\n"
    assert ocr_appended("等待结束\n等待结束\n", "等待结束\n正在运行\n") is None


@pytest.mark.asyncio
async def test_ocr_reconnect_recovers_proven_overlap(tmp_path):
    snapshots = iter(["连接模拟器\n等待结束\n", RuntimeError("窗口被遮挡"),
                      "等待结束\n正在运行\n"])
    emitted = []

    async def read(_):
        value = next(snapshots)
        if isinstance(value, Exception):
            raise value
        return value

    async def emit(text):
        emitted.append(text)

    item = ItemRecord(task_id="t", task_name="MAA")
    capture = WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit, read,
                               source="画面识别（可能有错字或遗漏）", matcher=ocr_appended)
    for _ in range(3):
        await capture._poll()
    assert emitted == ["正在运行\n"]
    assert "中断期间的内容可能遗漏" in item.ui_log_status


@pytest.mark.asyncio
async def test_fallback_rebaselines_without_replaying_old_messages(tmp_path):
    item = ItemRecord(task_id="t", task_name="测试")
    seen = []
    async def unavailable(_):
        raise RuntimeError("控件不可读")
    snapshots = iter(["旧内容\n", "旧内容\n新增\n"])
    async def fallback(_):
        return next(snapshots)
    async def emit(text):
        seen.append(text)
    capture = WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit,
                               unavailable, fallback_reader=fallback)
    for _ in range(5):
        await capture._poll()
    assert seen == ["新增\n"]
    assert "可能遗漏" in item.ui_log_status


@pytest.mark.asyncio
async def test_window_capture_baselines_and_preserves_only_new_text(tmp_path):
    store = DetailLogs(tmp_path)
    item = ItemRecord(task_id="t", task_name="测试")
    snapshots = iter(["旧内容\n", "旧内容\n新增一\n", "新增一\n新增二\n", "无法确认", "无法确认后续"])
    events = []

    async def read(_):
        return next(snapshots)

    async def emit(text):
        events.append(text)

    capture = WindowLogCapture(store, item, OwnedProcess(1, 1), emit, read)
    for _ in range(5):
        await capture._poll()
    assert events == ["新增一\n", "新增二\n", "后续"]
    page = store.page(item.ui_log_id, 0)
    assert "".join(entry["text"] for entry in page["entries"]) == "新增一\n新增二\n后续"
    assert "旧内容" not in str(page)


@pytest.mark.asyncio
async def test_unavailable_source_does_not_create_log(tmp_path):
    async def read(_):
        raise RuntimeError("窗口不可读")

    async def emit(_):
        pytest.fail("不应发送日志")

    item = ItemRecord(task_id="t", task_name="测试")
    await WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit, read)._poll()
    assert item.ui_log_id is None
    assert "窗口不可读" in item.ui_log_status


@pytest.mark.asyncio
async def test_source_and_failure_reason_are_retained(tmp_path):
    async def read(_):
        raise RuntimeError("目标窗口已最小化")

    async def emit(_):
        pytest.fail("不应发送日志")

    item = ItemRecord(task_id="t", task_name="MAA")
    capture = WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit, read,
                               source="画面识别（可能有错字或遗漏）")
    await capture._poll()
    assert "画面识别" in item.ui_log_status
    assert "目标窗口已最小化" in item.ui_log_status


@pytest.mark.asyncio
async def test_reconnection_keeps_incomplete_status_and_rebaselines(tmp_path):
    snapshots = iter(["旧\n", RuntimeError("中断"), "中断期间\n", "中断期间\n新\n"])
    emitted = []

    async def read(_):
        value = next(snapshots)
        if isinstance(value, Exception):
            raise value
        return value

    async def emit(text):
        emitted.append(text)

    item = ItemRecord(task_id="t", task_name="测试")
    capture = WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit, read)
    for _ in range(4):
        await capture._poll()
    assert emitted == ["新\n"]
    assert "中断期间的内容可能遗漏" in item.ui_log_status


@pytest.mark.asyncio
async def test_clear_rebaselines_without_replaying_old_log(tmp_path):
    snapshots = iter(["旧\n", "", "旧\n新基线\n", "旧\n新基线\n新增\n"])
    emitted = []

    async def read(_):
        return next(snapshots)

    async def emit(text):
        emitted.append(text)

    item = ItemRecord(task_id="t", task_name="测试")
    capture = WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit, read)
    for _ in range(4):
        await capture._poll()
    assert emitted == ["新增\n"]
    assert "改写期间的内容可能遗漏" in item.ui_log_status


@pytest.mark.asyncio
async def test_reused_or_unowned_pid_is_never_read(monkeypatch):
    import app.ui_logs as module

    monkeypatch.setattr(module, "owns_process", lambda _: None)
    with pytest.raises(RuntimeError, match="受管窗口不可用"):
        await read_march_window(OwnedProcess(1, 1))


@pytest.mark.asyncio
async def test_slow_reader_does_not_delay_task_cleanup(tmp_path):
    started = asyncio.Event()

    async def read(_):
        started.set()
        await asyncio.Event().wait()

    async def emit(_):
        pass

    item = ItemRecord(task_id="t", task_name="测试")
    capture = WindowLogCapture(DetailLogs(tmp_path), item, OwnedProcess(1, 1), emit, read)
    capture.start()
    await asyncio.wait_for(started.wait(), 1)
    await asyncio.wait_for(capture.finish(), 1)


def test_ui_and_raw_retention_are_independent(tmp_path):
    store = DetailLogs(tmp_path)
    item = ItemRecord(task_id="t", task_name="测试", detail_log_id="f6e372af-0e2c-4fd7-9434-f229c3a1643f", ui_log_id="69220a50-b013-4b4c-8109-850c9c62daca")
    run = RunRecord(queue_id="q", queue_name="q", trigger_source="manual", ended_at=datetime.now(UTC) - timedelta(days=8), items=[item])
    for identifier in (item.detail_log_id, item.ui_log_id):
        store.append(identifier, "内容")
    assert store.archive(HistoryDocument(runs=[run])) == []
    assert store.path(item.ui_log_id, True).exists()
    assert store.page(item.ui_log_id, 0)["entries"][0]["text"] == "内容"
    store.delete_runs([run])
    assert not store.path(item.ui_log_id, True).exists()
    assert not store.path(item.detail_log_id, True).exists()


def test_ui_log_api_and_cleanup(tmp_path):
    from fastapi.testclient import TestClient

    from app.main import create_app

    app = create_app(tmp_path)
    engine = app.state.engine
    item = ItemRecord(task_id="t", task_name="测试", ui_log_id="69220a50-b013-4b4c-8109-850c9c62daca")
    run = RunRecord(queue_id="q", queue_name="q", trigger_source="manual", ended_at=datetime(2026, 1, 1, tzinfo=UTC), items=[item])
    engine.history.runs = [run]
    engine.detail_logs.append(item.ui_log_id, "界面文本")
    with TestClient(app) as client:
        path = f"/api/history/{run.id}/details/{item.ui_log_id}"
        assert client.get(path).json()["entries"][0]["text"] == "界面文本"
        preview = client.post("/api/history/detail-cleanup/preview", json={"cutoff": "2026-01-03"}).json()
        assert preview["count"] == 1
        assert client.post("/api/history/detail-cleanup", json={"cutoff": "2026-01-03", "ids": preview["ids"]}).status_code == 200
        assert client.get(path).status_code == 404
        assert item.ui_log_status == "详细日志已清理"


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows UI Automation")
@pytest.mark.asyncio
async def test_read_controlled_owned_window():
    fixture = Path(__file__).parent / "fixtures" / "ui_log_window.ps1"
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-STA", "-Command", fixture.read_text(encoding="utf-8"),
        stderr=asyncio.subprocess.PIPE,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        owned = await capture_owned_process(child.pid)
        for attempt in range(10):
            try:
                text = await read_march_window(owned)
                break
            except RuntimeError:
                if child.returncode is not None:
                    pytest.fail((await child.stderr.read()).decode(errors="replace"))
                if attempt == 9:
                    raise
                await asyncio.sleep(0.5)
        assert text == "baseline\n"
        await asyncio.sleep(5)
        assert await read_march_window(owned) == "baseline\nnew line\n"
    finally:
        if child.returncode is None:
            child.terminate()
        await child.wait()


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows UI Automation")
@pytest.mark.asyncio
async def test_read_owned_overlay():
    fixture = Path(__file__).parent / "fixtures" / "ui_log_overlay.ps1"
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-STA", "-Command", fixture.read_text(encoding="utf-8"),
        stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        owned = await capture_owned_process(child.pid)
        for attempt in range(10):
            try:
                text = await read_march_window(owned)
                break
            except RuntimeError:
                if child.returncode is not None:
                    pytest.fail((await child.stderr.read()).decode(errors="replace"))
                if attempt == 9:
                    raise
                await asyncio.sleep(0.3)
        assert text == "baseline\n"
        await asyncio.sleep(3)
        assert await read_march_window(owned) == "baseline\nnew line\n"
    finally:
        if child.returncode is None:
            child.terminate()
        await child.wait()


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows UI Automation")
@pytest.mark.asyncio
async def test_overlay_never_reads_other_process():
    fixture = (Path(__file__).parent / "fixtures" / "ui_log_overlay.ps1").read_text(encoding="utf-8")
    children = []
    try:
        for marker in ("owned", "unrelated"):
            children.append(await asyncio.create_subprocess_exec(
                "powershell.exe", "-NoProfile", "-STA", "-Command", fixture.replace("baseline", marker),
                stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW,
            ))
        owned = await capture_owned_process(children[0].pid)
        await asyncio.sleep(2)
        assert (await read_march_window(owned)).startswith("owned")
    finally:
        for child in children:
            if child.returncode is None:
                child.terminate()
            await child.wait()


@pytest.mark.skipif(os.name != "nt", reason="需要 Windows UI Automation")
@pytest.mark.asyncio
async def test_ambiguous_overlay_is_rejected():
    fixture = (Path(__file__).parent / "fixtures" / "ui_log_overlay.ps1").read_text(encoding="utf-8")
    fixture = fixture.replace(
        "$window.Content = $log",
        "$panel = [System.Windows.Controls.StackPanel]::new(); "
        "$panel.Children.Add($log) | Out-Null; "
        "$other = [System.Windows.Controls.TextBlock]::new(); "
        "[System.Windows.Automation.AutomationProperties]::SetAutomationId($other, 'gameLogOverlayBody'); "
        "$other.Text = 'unrelated'; $panel.Children.Add($other) | Out-Null; $window.Content = $panel",
    )
    child = await asyncio.create_subprocess_exec(
        "powershell.exe", "-NoProfile", "-STA", "-Command", fixture,
        stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        owned = await capture_owned_process(child.pid)
        await asyncio.sleep(2)
        with pytest.raises(RuntimeError, match="未能唯一读取"):
            await read_march_window(owned)
    finally:
        if child.returncode is None:
            child.terminate()
        await child.wait()
