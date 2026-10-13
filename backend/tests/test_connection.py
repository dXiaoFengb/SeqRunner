from __future__ import annotations

import asyncio

from fastapi.testclient import TestClient

from app.events import EventHub
from app.main import create_app


def test_health_returns_the_startup_instance_and_development_cors(tmp_path) -> None:
    with TestClient(create_app(tmp_path / "data", instance_id="this-instance")) as client:
        response = client.get("/health", headers={"Origin": "http://127.0.0.1:5173"})
    assert response.json() == {"status": "ok", "instanceId": "this-instance"}
    assert response.headers["access-control-allow-origin"] == "http://127.0.0.1:5173"


def test_preset_endpoint_exposes_manual_fallback(tmp_path) -> None:
    with TestClient(create_app(tmp_path / "data", instance_id="endpoint-test")) as client:
        presets = client.get("/api/presets").json()
    bettergi = next(item for item in presets if item["id"] == "bettergi")
    assert bettergi["manualRequired"]


def test_websocket_answers_ping_and_cleans_up_subscription(tmp_path) -> None:
    app = create_app(tmp_path / "data", instance_id="websocket-test")
    with TestClient(app) as client:
        with client.websocket_connect("/ws") as socket:
            assert socket.receive_json()["type"] == "runtime"
            socket.send_json({"type": "ping", "nonce": "keep-alive"})
            assert socket.receive_json() == {"type": "pong", "data": {"nonce": "keep-alive"}}
            assert app.state.events.subscriber_count == 1
            socket.close()
        # TestClient drives the disconnect message before leaving the app lifespan.
        assert app.state.events.subscriber_count == 0


def test_event_hub_broadcasts_and_releases_closed_subscription() -> None:
    async def exercise() -> None:
        hub = EventHub()
        subscription = hub.subscribe()
        message = asyncio.create_task(anext(subscription))
        await asyncio.sleep(0)
        assert hub.subscriber_count == 1

        await hub.publish("runtime", {"busy": False})
        assert await message == {"type": "runtime", "data": {"busy": False}}

        await subscription.aclose()
        assert hub.subscriber_count == 0

    asyncio.run(exercise())
