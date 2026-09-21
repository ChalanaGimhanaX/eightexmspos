import base64
import json
from pathlib import Path
import sys

from fastapi import FastAPI
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "apps" / "api"))
from src.enightx_api.routers.updates import router

app = FastAPI()
app.include_router(router)
client = TestClient(app)


def publish(tmp_path, monkeypatch):
    path = tmp_path / "version-v2.json"
    path.write_text(json.dumps({"payload": base64.b64encode(json.dumps({"protocolVersion": 2, "releaseId": "1.0.4"}).encode()).decode(), "signature": "client-verifies-this"}))
    monkeypatch.setenv("ENIGHTX_RELEASE_MANIFEST", str(path))


def test_reconnect_gets_hint_but_ping_does_not_repeat_release(tmp_path, monkeypatch):
    publish(tmp_path, monkeypatch)
    for _ in range(2):
        with client.websocket_connect("/ws/updates") as ws:
            assert ws.receive_json() == {"event": "update_available", "releaseId": "1.0.4"}
            for _ in range(3):
                ws.send_text("ping")
                assert ws.receive_json() == {"event": "pong"}


def test_publisher_requires_configured_token(tmp_path, monkeypatch):
    publish(tmp_path, monkeypatch)
    monkeypatch.delenv("ENIGHTX_RELEASE_TOKEN", raising=False)
    assert client.post("/api/v1/updates/broadcast").status_code == 503
    monkeypatch.setenv("ENIGHTX_RELEASE_TOKEN", "test-only")
    assert client.post("/api/v1/updates/broadcast").status_code == 401
    response = client.post("/api/v1/updates/broadcast", headers={"Authorization": "Bearer test-only"}, json={"downloadUrl": "https://untrusted/evil.exe"})
    assert response.status_code == 200
    assert response.json()["releaseId"] == "1.0.4"


def test_missing_manifest_does_not_break_connection(tmp_path, monkeypatch):
    monkeypatch.setenv("ENIGHTX_RELEASE_MANIFEST", str(tmp_path / "missing.json"))
    with client.websocket_connect("/ws/updates") as ws:
        ws.send_text("ping")
        assert ws.receive_json() == {"event": "pong"}
