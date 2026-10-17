from __future__ import annotations

import asyncio
import codecs
import gzip
import hashlib
import json
import os
import threading
from datetime import UTC, date, datetime, timedelta
from pathlib import Path
from uuid import UUID, uuid4

from .models import HistoryDocument, ItemRecord, RunRecord


class DetailLogs:
    """只管理本应用生成的明细副本，不改动目标日志。"""

    def __init__(self, root: Path) -> None:
        self.root = root / "detail_logs"
        self.lock = threading.RLock()

    def path(self, identifier: str, compressed: bool = False) -> Path:
        identifier = str(UUID(identifier))
        return self.root / (identifier + (".jsonl.gz" if compressed else ".jsonl"))

    def append(self, identifier: str, text: str) -> None:
        if not text:
            return
        with self.lock:
            self.root.mkdir(parents=True, exist_ok=True)
            with self.path(identifier).open("a", encoding="utf-8", newline="\n") as stream:
                stream.write(json.dumps({"captured_at": datetime.now(UTC).isoformat(), "text": text}, ensure_ascii=False) + "\n")

    def page(self, identifier: str, offset: int, limit: int = 64 * 1024) -> dict:
        if offset < 0:
            raise ValueError("无效的日志位置")
        with self.lock:
            raw, packed = self.path(identifier), self.path(identifier, True)
            path = raw if raw.is_file() else packed
            if not path.is_file():
                raise FileNotFoundError("详细日志不存在或已清理")
            opener = gzip.open if path == packed else open
            with opener(path, "rb") as stream:
                stream.seek(offset)
                # 每条记录由 16 KB 以下的源数据形成，页边界对齐 JSON 行。
                data = bytearray()
                while len(data) < limit:
                    line = stream.readline(128 * 1024)
                    if not line:
                        break
                    data.extend(line)
                next_offset = stream.tell()
                more = bool(stream.read(1))
            entries = [json.loads(line) for line in data.decode("utf-8").splitlines()]
            return {"entries": entries, "next_offset": next_offset if more else None}

    def delete(self, identifier: str) -> None:
        with self.lock:
            for compressed in (False, True):
                self.path(identifier, compressed).unlink(missing_ok=True)

    def delete_runs(self, runs: list[RunRecord]) -> None:
        for run in runs:
            for item in run.items:
                if item.detail_log_id:
                    try:
                        self.delete(item.detail_log_id)
                    except (OSError, ValueError):
                        pass

    def archive(self, history: HistoryDocument, now: datetime | None = None) -> list[str]:
        now = now or datetime.now(UTC)
        errors = []
        for run in history.runs:
            if not run.ended_at:
                continue
            ended = run.ended_at.astimezone(UTC)
            if now - ended < timedelta(days=7):
                continue
            for item in run.items:
                if not item.detail_log_id:
                    continue
                try:
                    self._archive_one(item.detail_log_id)
                    if item.detail_log_status and item.detail_log_status.startswith("详细日志压缩失败："):
                        item.detail_log_status = "已采集"
                except (OSError, ValueError, EOFError) as error:
                    item.detail_log_status = f"详细日志压缩失败：{error}"
                    errors.append(item.detail_log_status)
        return errors

    def _archive_one(self, identifier: str) -> None:
        with self.lock:
            raw, packed = self.path(identifier), self.path(identifier, True)
            if not raw.is_file() or packed.exists():
                return
            temporary = packed.with_name(packed.name + "." + uuid4().hex + ".tmp")
            try:
                digest = hashlib.sha256()
                with raw.open("rb") as source, gzip.open(temporary, "wb") as target:
                    while chunk := source.read(65536):
                        digest.update(chunk)
                        target.write(chunk)
                verified = hashlib.sha256()
                with gzip.open(temporary, "rb") as source:
                    while chunk := source.read(65536):
                        verified.update(chunk)
                if digest.digest() != verified.digest():
                    raise OSError("压缩校验不一致")
                os.replace(temporary, packed)
                raw.unlink()
            finally:
                temporary.unlink(missing_ok=True)

    def matching(self, history: HistoryDocument, cutoff: date) -> list[ItemRecord]:
        return [item for run in history.runs if run.ended_at and run.ended_at.astimezone().date() <= cutoff
                for item in run.items if item.detail_log_id]


class DetailCapture:
    def __init__(self, store: DetailLogs, source: str, item: ItemRecord) -> None:
        self.store, self.source, self.item = store, Path(source), item
        self.offset = 0
        self.identity: tuple[int, int] | None = None
        self.anchor = b""
        self.anchor_offset = 0
        self.decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        self.stopped = asyncio.Event()
        self.task: asyncio.Task | None = None
        self.item.detail_log_id = str(uuid4())
        self.item.detail_log_status = "正在采集"

    def start(self) -> None:
        try:
            stat = self.source.stat()
            self.identity = (stat.st_dev, stat.st_ino)
            self.offset = stat.st_size
            self._refresh_anchor()
        except FileNotFoundError:
            self.item.detail_log_status = "日志文件尚未出现"
        except OSError as error:
            self.item.detail_log_status = f"日志读取失败：{error}"
            return
        self.task = asyncio.create_task(self._loop())

    def _read(self) -> None:
        try:
            stat = self.source.stat()
        except FileNotFoundError:
            self.item.detail_log_status = "日志文件尚未出现"
            return
        identity = (stat.st_dev, stat.st_ino)
        if self.identity and identity != self.identity:
            # 只按身份找回轮转文件，绝不猜测文件名。
            old_inode = self.identity[1]
            if old_inode:
                for candidate in self.source.parent.iterdir():
                    try:
                        if candidate != self.source and candidate.is_file() and candidate.stat().st_ino == old_inode:
                            self._consume(candidate, self.offset)
                            break
                    except OSError:
                        continue
            self.offset = 0
        elif stat.st_size < self.offset or self._anchor_changed():
            self.offset = 0
        self.identity = identity
        self.offset = self._consume(self.source, self.offset)
        self._refresh_anchor()
        self.item.detail_log_status = "已采集" if self.store.path(self.item.detail_log_id).is_file() else "尚无新增日志"

    def _anchor_changed(self) -> bool:
        if not self.anchor:
            return False
        with self.source.open("rb") as stream:
            stream.seek(self.anchor_offset)
            return stream.read(len(self.anchor)) != self.anchor

    def _refresh_anchor(self) -> None:
        self.anchor_offset = max(0, self.offset - 128)
        with self.source.open("rb") as stream:
            stream.seek(self.anchor_offset)
            self.anchor = stream.read(self.offset - self.anchor_offset)

    def _consume(self, path: Path, offset: int) -> int:
        with path.open("rb") as stream:
            stream.seek(offset)
            while chunk := stream.read(16384):
                self.store.append(self.item.detail_log_id, self.decoder.decode(chunk))
            return stream.tell()

    async def _loop(self) -> None:
        while not self.stopped.is_set():
            try:
                await asyncio.to_thread(self._read)
            except (OSError, ValueError) as error:
                self.item.detail_log_status = f"日志读取或保存失败：{error}"
            try:
                await asyncio.wait_for(self.stopped.wait(), timeout=0.3)
            except TimeoutError:
                pass

    async def finish(self) -> None:
        self.stopped.set()
        if not self.task:
            return
        await self.task
        try:
            await asyncio.to_thread(self._read)
            tail = self.decoder.decode(b"", final=True)
            await asyncio.to_thread(self.store.append, self.item.detail_log_id, tail)
        except (OSError, ValueError) as error:
            self.item.detail_log_status = f"日志读取或保存失败：{error}"
