from pathlib import Path
import sys
import json

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_websocket_ping_pong():
    with client.websocket_connect("/ws/updates") as websocket:
        websocket.send_text("ping")
        data = websocket.receive_text()
        msg = json.loads(data)
        assert msg["event"] == "pong"

def test_websocket_broadcast_update():
    with client.websocket_connect("/ws/updates") as websocket:
        # Trigger broadcast via HTTP POST
        payload = {
            "version": "1.0.2",
            "releaseNotes": "Live real-time push update test",
            "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
            "sha256": "abcdef123456",
            "publishedAtUtc": "2026-09-21T15:10:00Z",
            "mandatory": False
        }
        res = client.post("/api/v1/updates/broadcast", json=payload)
        assert res.status_code == 200
        assert res.json()["status"] == "broadcast_sent"
        assert res.json()["version"] == "1.0.2"

        # Receive real-time push over WebSocket
        received_text = websocket.receive_text()
        received_data = json.loads(received_text)
        assert received_data["event"] == "update_available"
        assert received_data["manifest"]["version"] == "1.0.2"
        assert received_data["manifest"]["releaseNotes"] == "Live real-time push update test"
