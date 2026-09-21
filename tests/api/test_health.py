from pathlib import Path
import sys

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_health_endpoint():
    response = client.get("/health")
    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "ok"
    assert data["service"] == "enightx-api"
    assert "timestamp" in data

def test_device_enrollment():
    payload = {
        "tenant_id": "tenant_123",
        "branch_id": "branch_colombo_01",
        "device_code": "CTR-01",
        "device_name": "Counter 1 Main",
        "hardware_fingerprint": "hw_fprint_abc999",
        "app_version": "1.0.0"
    }
    response = client.post("/api/v1/devices/enroll", json=payload)
    assert response.status_code == 200
    data = response.json()
    assert data["device_id"].startswith("dev_")
    assert data["device_generation"] == 1
    assert data["token"].startswith("tok_")
