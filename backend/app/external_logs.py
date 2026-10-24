from __future__ import annotations

import asyncio
import fnmatch
import time
from collections.abc import Awaitable, Callable
from pathlib import Path

from .logwatch import LogSignal, LogWatcher
from .models import LogRule, MatchMode


def split_patterns(value: str) -> list[str]:
    patterns = [part.strip() for part in value.split(";") if part.strip()]
    return patterns or ["*.log", "*.txt"]


def discover(source: str, patterns: str = "*.log;*.txt") -> list[Path]:
    """Return only explicitly selected file or direct children; never recurse."""
    root = Path(source)
    if root.is_file():
        return [root]
    if not root.is_dir():
        return []
    wanted = [pattern.casefold() for pattern in split_patterns(patterns)]
    result = [item for item in root.iterdir() if item.is_file() and any(fnmatch.fnmatch(item.name.casefold(), pattern) for pattern in wanted)]
    return sorted(result, key=lambda item: (item.stat().st_mtime_ns, item.name.casefold()), reverse=True)


def external_page(path: Path, offset: int, limit: int = 64 * 1024) -> dict[str, object]:
    if offset < 0:
        raise ValueError("无效的日志位置")
    with path.open("rb") as stream:
        stream.seek(offset)
        data = stream.read(limit)
        next_offset = stream.tell()
        more = bool(stream.read(1))
    return {
        "text": data.decode("utf-8-sig", errors="replace"),
        "next_offset": next_offset if more else None,
        "size": path.stat().st_size,
        "modified_at": path.stat().st_mtime,
    }


class ExternalLogWatcher:
    """Monitor one file or direct-child logs without creating local copies."""

    def __init__(
        self, source: str, patterns: str, success: str | None, success_mode: MatchMode,
        failure: str | None, failure_mode: MatchMode, *, success_rules: list[LogRule],
        failure_rules: list[LogRule], monitor_success: bool,
        on_text: Callable[[Path, str], Awaitable[None]] | None = None,
    ) -> None:
        self.source, self.patterns = source, patterns
        self.success, self.success_mode = success, success_mode
        self.failure, self.failure_mode = failure, failure_mode
        self.success_rules, self.failure_rules = success_rules, failure_rules
        self.monitor_success = monitor_success
        self.on_text = on_text
        self.watchers: dict[Path, LogWatcher] = {}
        self.last_text = ""
        self._last_progress_at: float | None = None
        self._consecutive_read_failures = 0

    @property
    def paths(self) -> list[str]:
        return [str(path) for path in self.watchers]

    @property
    def has_sources(self) -> bool:
        return bool(self.watchers)

    async def start_at_end(self) -> None:
        await self._discover_new(at_end=True)

    async def _discover_new(self, *, at_end: bool = False) -> None:
        for path in await asyncio.to_thread(discover, self.source, self.patterns):
            if path in self.watchers:
                continue
            watcher = LogWatcher(
                str(path), self.success, self.success_mode, self.failure, self.failure_mode,
                success_rules=self.success_rules, failure_rules=self.failure_rules,
                monitor_success=self.monitor_success,
            )
            if at_end:
                await watcher.start_at_end()
            self.watchers[path] = watcher
            # 已发现来源才开始静默计时；目录尚未生成任何文件时仍交由总超时管理。
            self._last_progress_at = time.monotonic()

    async def wait_for_signal(self, cancel: asyncio.Event) -> LogSignal | None:
        while not cancel.is_set():
            try:
                await self._discover_new()
                signals: list[LogSignal] = []
                for path, watcher in tuple(self.watchers.items()):
                    signal = await asyncio.to_thread(watcher._read_new)
                    if watcher.last_progress:
                        self._last_progress_at = time.monotonic()
                    if watcher.last_text:
                        self.last_text += watcher.last_text
                        if self.on_text:
                            await self.on_text(path, watcher.last_text)
                    if signal:
                        signals.append(signal)
                # A directory may advance several files during one scan.  Do
                # not let discovery order turn a simultaneous failure into a
                # success: terminal failure evidence always wins this pass.
                if signals:
                    return next((signal for signal in signals if signal.kind != "success"), signals[0])
            except (OSError, UnicodeError) as error:
                self._consecutive_read_failures += 1
                if self._consecutive_read_failures >= 5:
                    return LogSignal("error", f"日志连续读取失败（已重试 5 次）：{error}")
                try:
                    await asyncio.wait_for(cancel.wait(), timeout=1)
                except TimeoutError:
                    pass
                continue
            self._consecutive_read_failures = 0
            await asyncio.sleep(0.15)
        return None

    async def wait_for_idle(self, timeout_seconds: int, cancel: asyncio.Event) -> LogSignal | None:
        """Return only after an active external source has stopped making byte-level progress."""
        while not cancel.is_set():
            last_progress = self._last_progress_at
            if self.has_sources and last_progress is not None and time.monotonic() - last_progress >= timeout_seconds:
                return LogSignal("idle", f"日志超过 {timeout_seconds} 秒无新增")
            try:
                await asyncio.wait_for(cancel.wait(), timeout=0.25)
            except TimeoutError:
                pass
        return None
