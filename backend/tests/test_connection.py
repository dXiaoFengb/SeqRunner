from __future__ import annotations

from fastapi.testclient import TestClient

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
