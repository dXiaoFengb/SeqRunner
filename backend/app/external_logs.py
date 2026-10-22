from __future__ import annotations

import asyncio
import fnmatch
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
        on_text: Callable[[str], Awaitable[None]] | None = None,
    ) -> None:
        self.source, self.patterns = source, patterns
        self.success, self.success_mode = success, success_mode
        self.failure, self.failure_mode = failure, failure_mode
        self.success_rules, self.failure_rules = success_rules, failure_rules
        self.monitor_success = monitor_success
        self.on_text = on_text
        self.watchers: dict[Path, LogWatcher] = {}
        self.last_text = ""

    @property
    def paths(self) -> list[str]:
        return [str(path) for path in self.watchers]

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

    async def wait_for_signal(self, cancel: asyncio.Event) -> LogSignal | None:
        while not cancel.is_set():
            try:
                await self._discover_new()
                for watcher in tuple(self.watchers.values()):
                    signal = await asyncio.to_thread(watcher._read_new)
                    if watcher.last_text:
                        self.last_text += watcher.last_text
                        if self.on_text:
                            await self.on_text(watcher.last_text)
                    if signal:
                        return signal
            except (OSError, UnicodeError) as error:
                return LogSignal("error", f"日志读取失败：{error}")
            await asyncio.sleep(0.15)
        return None
