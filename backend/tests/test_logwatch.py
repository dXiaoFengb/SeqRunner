from __future__ import annotations

import asyncio
from pathlib import Path

import pytest

from app.logwatch import LogWatcher
from app.models import MatchMode


def append(path: Path, data: bytes) -> None:
    with path.open("ab") as handle:
        handle.write(data)


@pytest.mark.asyncio
@pytest.mark.parametrize("mode,pattern,parts", [
    (MatchMode.KEYWORD, "DONE", [b"DO", b"NE"]),
    (MatchMode.REGEX, r"D.O.N.E", [b"D.O", b".N.E"]),
    (MatchMode.KEYWORD, "完成", ["完".encode()[:2], "完".encode()[2:] + "成".encode()]),
])
async def test_signal_spans_reads(tmp_path: Path, mode: MatchMode, pattern: str, parts: list[bytes]) -> None:
    path = tmp_path / "job.log"
    path.write_bytes(b"")
    watcher = LogWatcher(str(path), pattern, mode)
    await watcher.start_at_end()
    append(path, parts[0])
    assert watcher._read_new() is None
    append(path, parts[1])
    assert watcher._read_new().kind == "success"


@pytest.mark.asyncio
async def test_failure_priority_and_no_old_context_repeat(tmp_path: Path) -> None:
    path = tmp_path / "job.log"
    path.write_bytes(b"DONE old")
    watcher = LogWatcher(str(path), "DONE", failure="FAILED")
    await watcher.start_at_end()
    append(path, b" FA")
    assert watcher._read_new() is None
    append(path, b"ILED DONE")
    assert watcher._read_new().kind == "failure"


@pytest.mark.asyncio
async def test_split_failure_and_new_read_anchor(tmp_path: Path) -> None:
    path = tmp_path / "job.log"
    path.write_bytes(b"")
    watcher = LogWatcher(str(path), failure="FAILED")
    await watcher.start_at_end()
    append(path, b"FA")
    assert watcher._read_new() is None
    append(path, b"ILED")
    assert watcher._read_new().kind == "failure"
    watcher = LogWatcher(str(path), r"^DONE", MatchMode.REGEX)
    await watcher.start_at_end()
    append(path, b"other")
    assert watcher._read_new() is None
    append(path, b"DONE")
    assert watcher._read_new().kind == "success"


@pytest.mark.asyncio
async def test_universal_newline_behavior(tmp_path: Path) -> None:
    path = tmp_path / "job.log"
    path.write_bytes(b"")
    watcher = LogWatcher(str(path), r"READY\n", MatchMode.REGEX)
    await watcher.start_at_end()
    append(path, b"READY\r")
    assert watcher._read_new().kind == "success"
    watcher = LogWatcher(str(path), r"DONE\n", MatchMode.REGEX)
    await watcher.start_at_end()
    append(path, b"DONE\r\n")
    assert watcher._read_new().kind == "success"


@pytest.mark.asyncio
@pytest.mark.parametrize("change", ["truncate", "replace", "overwrite"])
async def test_reset_does_not_join_different_file_generations(tmp_path: Path, change: str) -> None:
    path = tmp_path / "job.log"
    path.write_bytes(b"")
    watcher = LogWatcher(str(path), "DONE")
    await watcher.start_at_end()
    append(path, b"DO")
    assert watcher._read_new() is None
    if change == "replace":
        replacement = tmp_path / "replacement.log"
        replacement.write_bytes(b"NE")
        replacement.replace(path)
    elif change == "overwrite":
        path.write_bytes(b"NEX")
    else:
        path.write_bytes(b"N")
    assert watcher._read_new() is None
    append(path, b"DONE")
    assert watcher._read_new().kind == "success"


@pytest.mark.asyncio
async def test_context_is_bounded_and_large_reads_are_scanned(tmp_path: Path) -> None:
    path = tmp_path / "job.log"
    path.write_bytes(b"")
    watcher = LogWatcher(str(path), "DONE")
    await watcher.start_at_end()
    append(path, b"x" * 70000 + b"DO")
    assert watcher._read_new() is None
    assert len(watcher._context.encode("utf-8")) <= 65536
    append(path, b"NE")
    assert watcher._read_new().kind == "success"
    watcher = LogWatcher(str(path), "DONE")
    await watcher.start_at_end()
    append(path, b"DONE" + b"x" * 70000)
    assert watcher._read_new().kind == "success"

    watcher = LogWatcher(str(path), r"BEGINx{65536}END", MatchMode.REGEX)
    await watcher.start_at_end()
    append(path, b"BEGIN" + b"x" * 65536)
    assert watcher._read_new() is None
    append(path, b"END")
    assert watcher._read_new() is None


@pytest.mark.asyncio
async def test_ignores_existing_content_and_follows_truncation(tmp_path: Path) -> None:
    path = tmp_path / "job.log"
    path.write_text("DONE old\n", encoding="utf-8")
    watcher = LogWatcher(str(path), "DONE", MatchMode.KEYWORD, "FAILED", MatchMode.KEYWORD, 0.01)
    await watcher.start_at_end()
    cancel = asyncio.Event()
    wait = asyncio.create_task(watcher.wait_for_signal(cancel))
    await asyncio.sleep(0.03)
    path.write_text("working\n", encoding="utf-8")
    await asyncio.sleep(0.03)
    path.write_text("DONE new\n", encoding="utf-8")
    result = await asyncio.wait_for(wait, 1)
    assert result and result.kind == "success" and result.text == "DONE"
