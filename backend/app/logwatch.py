from __future__ import annotations

import asyncio
import codecs
import re
from dataclasses import dataclass
from pathlib import Path

from .models import LogRule, MatchMode


@dataclass(frozen=True)
class LogSignal:
    kind: str
    text: str
    matches: tuple[str, ...] = ()


class LogWatcher:
    """只消费启动后新增内容；截断和轮转都能重新接续。"""

    def __init__(
        self,
        path: str,
        success: str | None = None,
        success_mode: MatchMode = MatchMode.KEYWORD,
        failure: str | None = None,
        failure_mode: MatchMode = MatchMode.KEYWORD,
        poll_seconds: float = 0.15,
        *,
        success_rules: list[LogRule] | None = None,
        failure_rules: list[LogRule] | None = None,
        monitor_success: bool = True,
    ) -> None:
        self.path = Path(path)
        all_success = list(success_rules or []) + ([LogRule(pattern=success, mode=success_mode)] if success else [])
        all_failure = list(failure_rules or []) + ([LogRule(pattern=failure, mode=failure_mode)] if failure else [])
        self.success = self._compile_all(all_success) if monitor_success else []
        self.failure = self._compile_all(all_failure)
        self.poll_seconds = poll_seconds
        self._offset = 0
        self._identity: tuple[int, int] | None = None
        self._anchor_offset = 0
        self._anchor = b""
        self._context = ""
        self._decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        self.stream_epoch = 0
        self.last_text = ""
        # 与 last_text 分开：即使新字节暂未构成可显示文本，也算日志仍在推进。
        self.last_progress = False

    def _reset_stream(self) -> None:
        self._context = ""
        self._decoder.reset()
        self.stream_epoch += 1

    @staticmethod
    def _compile_all(rules: list[LogRule]) -> list[re.Pattern[str]]:
        return [re.compile(rule.pattern if rule.mode == MatchMode.REGEX else re.escape(rule.pattern)) for rule in rules]

    async def start_at_end(self) -> None:
        self._reset_stream()
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
        self.last_text = ""
        self.last_progress = False
        try:
            stat = self.path.stat()
        except FileNotFoundError:
            return None
        identity = (stat.st_dev, stat.st_ino)
        # 文件截断或替换后，新文件的内容都发生在本次运行期间，重新从头读取。
        if self._identity is None:
            self._offset = 0
            self._identity = identity
            self._reset_stream()
        elif identity != self._identity:
            # 常见轮转会把旧文件改名后新建同名文件。先按旧 identity 排空尾部，
            # 再切换，避免刚写出的完成或失败标记落在两次轮询之间。
            signal = self._drain_renamed_tail(self._identity)
            if signal is not None or self.last_progress:
                return signal
            self._offset = 0
            self._identity = identity
            self._reset_stream()
        elif stat.st_size < self._offset:
            self._offset = 0
            self._reset_stream()
        elif self._anchor and self._anchor_changed():
            # 有些程序用覆盖写快速完成“截断再写入”，轮询可能看不到短暂的小文件。
            self._offset = 0
            self._reset_stream()
        with self.path.open("rb") as handle:
            handle.seek(self._offset)
            data = handle.read()
            self._offset = handle.tell()
        self._refresh_anchor()
        return self._consume(data)

    def _drain_renamed_tail(self, previous_identity: tuple[int, int]) -> LogSignal | None:
        """Read unread bytes from a renamed predecessor when the filesystem still exposes it."""
        try:
            candidates = tuple(self.path.parent.iterdir())
        except FileNotFoundError:
            return None
        for candidate in candidates:
            if not candidate.is_file() or candidate == self.path:
                continue
            try:
                stat = candidate.stat()
            except FileNotFoundError:
                continue
            if (stat.st_dev, stat.st_ino) != previous_identity:
                continue
            with candidate.open("rb") as handle:
                handle.seek(self._offset)
                data = handle.read()
                self._offset = handle.tell()
                return self._consume(data)
        return None

    def _consume(self, data: bytes) -> LogSignal | None:
        if data:
            self.last_progress = True
        text = self._decoder.decode(data, final=False).replace("\r\n", "\n").replace("\r", "\n")
        if not text:
            return None
        self.last_text += text
        previous = self._context
        combined = previous + text
        selected: LogSignal | None = None
        for kind, rules in (("failure", self.failure), ("success", self.success)):
            found: list[str] = []
            for rule in rules:
                before = len(found)
                old = {(m.span(), m.group(0)) for m in rule.finditer(previous)}
                for match in rule.finditer(combined):
                    if (match.span(), match.group(0)) not in old:
                        found.append(match.group(0))
                # Preserve patterns anchored to the beginning of a read.
                if len(found) == before and (match := rule.search(text)):
                    found.append(match.group(0))
            if found:
                selected = LogSignal(kind, found[0], tuple(found))
                break
        self._context = combined.encode("utf-8")[-65536:].decode("utf-8", errors="ignore")
        return selected

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
