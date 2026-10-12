from __future__ import annotations

import asyncio
import re
from dataclasses import dataclass
from pathlib import Path

from .models import MatchMode


@dataclass(frozen=True)
class LogSignal:
    kind: str
    text: str


class LogWatcher:
    """只消费启动后新增内容；截断和轮转都能重新接续。"""

    def __init__(
        self,
        path: str,
        success: str | None,
        success_mode: MatchMode,
        failure: str | None,
        failure_mode: MatchMode,
        poll_seconds: float = 0.15,
    ) -> None:
        self.path = Path(path)
        self.success = self._compile(success, success_mode)
        self.failure = self._compile(failure, failure_mode)
        self.poll_seconds = poll_seconds
        self._offset = 0
        self._identity: tuple[int, int] | None = None
        self._anchor_offset = 0
        self._anchor = b""

    @staticmethod
    def _compile(pattern: str | None, mode: MatchMode) -> re.Pattern[str] | None:
        if not pattern:
            return None
        return re.compile(pattern if mode == MatchMode.REGEX else re.escape(pattern))

    async def start_at_end(self) -> None:
        try:
            stat = await asyncio.to_thread(self.path.stat)
            self._offset = stat.st_size
            self._identity = (stat.st_dev, stat.st_ino)
            await asyncio.to_thread(self._refresh_anchor)
        except FileNotFoundError:
            self._offset = 0
            self._identity = None

    async def wait_for_signal(self, cancel: asyncio.Event) -> LogSignal | None:
        while not cancel.is_set():
            try:
                signal = await asyncio.to_thread(self._read_new)
            except FileNotFoundError:
                # 轮转的短暂无文件窗口不是不可恢复读取错误。
                signal = None
            except (OSError, UnicodeError) as error:
                return LogSignal("error", f"日志读取失败：{error}")
            if signal is not None:
                return signal
            await asyncio.sleep(self.poll_seconds)
        return None

    def _read_new(self) -> LogSignal | None:
        try:
            stat = self.path.stat()
        except FileNotFoundError:
            return None
        identity = (stat.st_dev, stat.st_ino)
        # 文件截断或替换后，新文件的内容都发生在本次运行期间，重新从头读取。
        if self._identity is None or identity != self._identity or stat.st_size < self._offset:
            self._offset = 0
            self._identity = identity
        elif self._anchor and self._anchor_changed():
            # 有些程序用覆盖写快速完成“截断再写入”，轮询可能看不到短暂的小文件。
            self._offset = 0
        with self.path.open("r", encoding="utf-8", errors="replace") as handle:
            handle.seek(self._offset)
            text = handle.read()
            self._offset = handle.tell()
        self._refresh_anchor()
        if not text:
            return None
        if self.failure and (match := self.failure.search(text)):
            return LogSignal("failure", match.group(0))
        if self.success and (match := self.success.search(text)):
            return LogSignal("success", match.group(0))
        return None

    def _anchor_changed(self) -> bool:
        with self.path.open("rb") as handle:
            handle.seek(self._anchor_offset)
            return handle.read(len(self._anchor)) != self._anchor

    def _refresh_anchor(self) -> None:
        length = min(128, self._offset)
        self._anchor_offset = max(0, self._offset - length)
        with self.path.open("rb") as handle:
            handle.seek(self._anchor_offset)
            self._anchor = handle.read(length)
