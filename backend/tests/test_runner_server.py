from __future__ import annotations

import json
import queue
import subprocess
import sys
import threading
from pathlib import Path

from websockets.sync.client import connect


def _read_line(stream, lines: queue.Queue[str]) -> None:
    lines.put(stream.readline())


def test_runner_server_accepts_real_websocket_and_answers_ping(tmp_path: Path) -> None:
    """Exercise Uvicorn's packaged protocol path, not TestClient's in-memory transport."""
    backend = Path(__file__).resolve().parents[1]
    process = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "app.runner_server",
            "--data-dir",
            str(tmp_path / "data"),
            "--instance-id",
            "real-websocket-test",
        ],
        cwd=backend,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        assert process.stdout is not None
        lines: queue.Queue[str] = queue.Queue()
        threading.Thread(target=_read_line, args=(process.stdout, lines), daemon=True).start()
        ready_line = lines.get(timeout=12)
        assert ready_line.startswith("RUNNER_READY ")
        port = json.loads(ready_line.removeprefix("RUNNER_READY "))["port"]

        # The test environment may set an HTTP proxy; a loopback Uvicorn probe must never use it.
        with connect(f"ws://127.0.0.1:{port}/ws", open_timeout=5, proxy=None) as socket:
            assert json.loads(socket.recv())["type"] == "runtime"
            socket.send(json.dumps({"type": "ping", "nonce": "real-keep-alive"}))
            assert json.loads(socket.recv()) == {
                "type": "pong",
                "data": {"nonce": "real-keep-alive"},
            }
    finally:
        process.terminate()
        try:
            process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=8)
