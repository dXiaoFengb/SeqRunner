from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

import psutil
import pytest

from app.processes import OwnedProcess
from app.windows_job import WindowsJobObserver


@pytest.mark.skipif(os.name != 'nt', reason='Windows Job fixture')
def test_job_observes_target_after_short_intermediate_exit(tmp_path: Path, monkeypatch) -> None:
    script = tmp_path / 'family.py'
    pid_file = tmp_path / 'target.pid'
    script.write_text(
        'import os, pathlib, subprocess, sys, time\n'
        'role, marker = sys.argv[1], pathlib.Path(sys.argv[2])\n'
        'if role == "root":\n'
        '    time.sleep(0.8)\n'
        '    subprocess.Popen([sys.executable, __file__, "middle", str(marker)])\n'
        '    time.sleep(0.12)\n'
        'elif role == "middle":\n'
        '    subprocess.Popen([sys.executable, __file__, "target", str(marker)])\n'
        'else:\n'
        '    marker.write_text(str(os.getpid()))\n'
        '    time.sleep(5)\n', encoding='utf-8')
    executable = getattr(sys, '_base_executable', sys.executable)
    root = subprocess.Popen([executable, str(script), 'root', str(pid_file)], creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    job = None
    target = None
    try:
        identity = OwnedProcess(root.pid, psutil.Process(root.pid).create_time())
        job = WindowsJobObserver(identity)
        root.wait(timeout=5)
        deadline = time.monotonic() + 3
        while not pid_file.exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        assert pid_file.exists()
        target = psutil.Process(int(pid_file.read_text()))
        target_identity = OwnedProcess(target.pid, target.create_time())
        monkeypatch.setattr(job, '_drain_notifications', lambda: None)
        members = job.members(executable, identity.created_at)
        assert target_identity in members
        assert job.contains(target_identity)
        job.close()
        job = None
        assert target.is_running()
    finally:
        if job:
            job.close()
        if target and target.is_running():
            target.terminate()
            target.wait(timeout=3)
        if root.poll() is None:
            root.terminate()
            root.wait(timeout=3)
