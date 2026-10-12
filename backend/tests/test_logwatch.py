from __future__ import annotations

import asyncio
from pathlib import Path

import pytest

from app.logwatch import LogWatcher
from app.models import MatchMode


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
