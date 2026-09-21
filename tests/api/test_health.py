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

def test_health_head_endpoint():
    response = client.head("/health")
    assert response.status_code == 200
    assert response.headers.get("content-type") == "application/json"

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

def test_device_heartbeat():
    enroll_payload = {
        "tenant_id": "tenant_123",
        "branch_id": "branch_colombo_01",
        "device_code": "CTR-02",
        "device_name": "Counter 2 Main",
        "hardware_fingerprint": "hw_fprint_xyz777",
        "app_version": "1.0.0"
    }
    enroll_res = client.post("/api/v1/devices/enroll", json=enroll_payload)
    assert enroll_res.status_code == 200
    device_data = enroll_res.json()
    dev_id = device_data["device_id"]
    token = device_data["token"]

    # Valid heartbeat
    hb_payload = {
        "device_id": dev_id,
        "token": token,
        "app_version": "1.0.1",
        "status": "ONLINE"
    }
    hb_res = client.post("/api/v1/devices/heartbeat", json=hb_payload)
    assert hb_res.status_code == 200
    assert hb_res.json()["acknowledged"] is True

    # Invalid token
    bad_res = client.post("/api/v1/devices/heartbeat", json={
        "device_id": dev_id,
        "token": "bad_token",
        "app_version": "1.0.1"
    })
    assert bad_res.status_code == 401

    # Non-existent device
    nf_res = client.post("/api/v1/devices/heartbeat", json={
        "device_id": "dev_non_existent",
        "token": "any_token",
        "app_version": "1.0.1"
    })
    assert nf_res.status_code == 404
