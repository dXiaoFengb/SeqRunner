from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path

import pytest

from app.processes import capture_owned_process
from app.startup_control import ControlSelector, query_uia


@pytest.mark.asyncio
@pytest.mark.skipif(os.name != "nt", reason="Windows UI Automation fixture")
async def test_controlled_windows_close_and_start_once(tmp_path: Path) -> None:
    script = Path(__file__).parent / "fixtures" / "startup_uia_fixture.py"
    output = tmp_path / "events.txt"
    child = await asyncio.create_subprocess_exec(getattr(sys, "_base_executable", sys.executable), str(script), str(output),
                                                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                                                creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0))
    try:
        await asyncio.sleep(0.4)
        if child.returncode is not None:
            _stdout, stderr = await child.communicate()
            pytest.fail(f"受控窗口进程提前退出（{child.returncode}）: {stderr.decode(errors='replace')}")
        owner = await capture_owned_process(child.pid)
        notice = ControlSelector("Runner Announcement Fixture", control_name="Close Notice")
        start = ControlSelector("Runner Startup Fixture", control_name="Start Fixture")
        for _ in range(30):
            try:
                found = await query_uia(owner, notice)
                if found.get("window_found"):
                    break
            except RuntimeError:
                pass
            await asyncio.sleep(0.2)
        else:
            pytest.fail("受控窗口未在限定时间出现")
        await query_uia(owner, notice, invoke=True)
        assert (await query_uia(owner, notice)).get("window_found") is False
        await query_uia(owner, start, invoke=True)
        assert output.read_text(encoding="utf-8-sig").splitlines() == ["closed", "started"]
    finally:
        if child.returncode is None:
            child.kill()
        await child.wait()


@pytest.mark.asyncio
@pytest.mark.skipif(os.name != "nt", reason="Windows UI Automation fixture")
async def test_duplicate_controls_are_not_invoked(tmp_path: Path) -> None:
    script = Path(__file__).parent / "fixtures" / "startup_uia_fixture.py"
    output = tmp_path / "events.txt"
    child = await asyncio.create_subprocess_exec(getattr(sys, "_base_executable", sys.executable), str(script), str(output), "duplicate",
                                                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                                                creationflags=getattr(__import__("subprocess"), "CREATE_NO_WINDOW", 0))
    try:
        await asyncio.sleep(0.4)
        owner = await capture_owned_process(child.pid)
        start = ControlSelector("Runner Startup Fixture", control_name="Start Fixture")
        with pytest.raises(RuntimeError):
            await query_uia(owner, start, invoke=True)
        assert not output.exists()
    finally:
        if child.returncode is None:
            child.kill()
        await child.wait()
