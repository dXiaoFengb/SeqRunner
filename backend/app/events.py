from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator
from typing import Any


class EventHub:
    def __init__(self) -> None:
        self._subscribers: set[asyncio.Queue[dict[str, Any]]] = set()

    async def publish(self, event_type: str, data: dict[str, Any]) -> None:
        event = {"type": event_type, "data": data}
        for subscriber in tuple(self._subscribers):
            with __import__("contextlib").suppress(asyncio.QueueFull):
                subscriber.put_nowait(event)

    async def subscribe(self) -> AsyncIterator[dict[str, Any]]:
        subscriber: asyncio.Queue[dict[str, Any]] = asyncio.Queue(maxsize=200)
        self._subscribers.add(subscriber)
        try:
            while True:
                yield await subscriber.get()
        finally:
            self._subscribers.discard(subscriber)
