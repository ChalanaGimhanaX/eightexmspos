from pathlib import Path
import sys
import uuid
from datetime import datetime, timezone

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_device_enrollment_and_persistence():
    unique_suffix = uuid.uuid4().hex[:8]
    payload = {
        "tenant_id": f"tenant_{unique_suffix}",
        "branch_id": "branch_colombo_01",
        "device_code": f"CTR-{unique_suffix}",
        "device_name": "Counter Test Station",
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0"
    }
    
    # 1. First enrollment
    res1 = client.post("/api/v1/devices/enroll", json=payload)
    assert res1.status_code == 200
    data1 = res1.json()
    assert data1["device_id"].startswith("dev_")
    assert data1["device_generation"] == 1
    assert data1["token"].startswith("tok_")

    # 2. Re-enrollment with same fingerprint should return existing credentials
    res2 = client.post("/api/v1/devices/enroll", json=payload)
    assert res2.status_code == 200
    data2 = res2.json()
    assert data2["device_id"] == data1["device_id"]
    assert data2["token"] == data1["token"]

def test_device_heartbeat_success():
    unique_suffix = uuid.uuid4().hex[:8]
    enroll_payload = {
        "tenant_id": f"tenant_{unique_suffix}",
        "branch_id": "branch_kandy_01",
        "device_code": f"CTR-KD-{unique_suffix}",
        "device_name": "Kandy Counter 1",
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0"
    }
    enroll_res = client.post("/api/v1/devices/enroll", json=enroll_payload)
    assert enroll_res.status_code == 200
    enroll_data = enroll_res.json()
    device_id = enroll_data["device_id"]
    token = enroll_data["token"]

    heartbeat_payload = {
        "device_id": device_id,
        "device_generation": 1,
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.1",
        "db_size_bytes": 1048576,
        "last_sync_sequence": 15,
        "status": "online",
        "sent_at": datetime.now(timezone.utc).isoformat()
    }

    # Test Bearer token auth
    res = client.post(
        "/api/v1/devices/heartbeat",
        json=heartbeat_payload,
        headers={"Authorization": f"Bearer {token}"}
    )
    assert res.status_code == 200
    data = res.json()
    assert data["status"] == "ok"
    assert "server_time_utc" in data
    assert data["acknowledged_sequence"] == 15

def test_device_heartbeat_x_device_token_header():
    unique_suffix = uuid.uuid4().hex[:8]
    enroll_payload = {
        "tenant_id": f"tenant_{unique_suffix}",
        "branch_id": "branch_galle_01",
        "device_code": f"CTR-GL-{unique_suffix}",
        "device_name": "Galle Counter 1",
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0"
    }
    enroll_res = client.post("/api/v1/devices/enroll", json=enroll_payload)
    assert enroll_res.status_code == 200
    enroll_data = enroll_res.json()
    device_id = enroll_data["device_id"]
    token = enroll_data["token"]

    heartbeat_payload = {
        "device_id": device_id,
        "device_generation": 1,
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0",
        "db_size_bytes": 524288,
        "last_sync_sequence": 8,
        "status": "online",
        "sent_at": datetime.now(timezone.utc).isoformat()
    }

    # Test X-Device-Token header auth
    res = client.post(
        "/api/v1/devices/heartbeat",
        json=heartbeat_payload,
        headers={"X-Device-Token": token}
    )
    assert res.status_code == 200
    data = res.json()
    assert data["status"] == "ok"
    assert data["acknowledged_sequence"] == 8

def test_device_heartbeat_invalid_token():
    unique_suffix = uuid.uuid4().hex[:8]
    heartbeat_payload = {
        "device_id": f"dev_{unique_suffix}",
        "device_generation": 1,
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0",
        "sent_at": datetime.now(timezone.utc).isoformat()
    }
    res = client.post(
        "/api/v1/devices/heartbeat",
        json=heartbeat_payload,
        headers={"Authorization": "Bearer tok_invalid_token"}
    )
    assert res.status_code == 401

def test_device_heartbeat_missing_token():
    unique_suffix = uuid.uuid4().hex[:8]
    heartbeat_payload = {
        "device_id": f"dev_{unique_suffix}",
        "device_generation": 1,
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0",
        "sent_at": datetime.now(timezone.utc).isoformat()
    }
    res = client.post(
        "/api/v1/devices/heartbeat",
        json=heartbeat_payload
    )
    assert res.status_code == 401

def test_device_heartbeat_fingerprint_mismatch():
    unique_suffix = uuid.uuid4().hex[:8]
    enroll_payload = {
        "tenant_id": f"tenant_{unique_suffix}",
        "branch_id": "branch_colombo_02",
        "device_code": f"CTR-CB-{unique_suffix}",
        "device_name": "Colombo Counter 2",
        "hardware_fingerprint": f"hw_fp_{unique_suffix}",
        "app_version": "1.0.0"
    }
    enroll_res = client.post("/api/v1/devices/enroll", json=enroll_payload)
    assert enroll_res.status_code == 200
    enroll_data = enroll_res.json()
    device_id = enroll_data["device_id"]
    token = enroll_data["token"]

    heartbeat_payload = {
        "device_id": device_id,
        "device_generation": 1,
        "hardware_fingerprint": "hw_fp_spoofed_different_hardware",
        "app_version": "1.0.0",
        "sent_at": datetime.now(timezone.utc).isoformat()
    }

    res = client.post(
        "/api/v1/devices/heartbeat",
        json=heartbeat_payload,
        headers={"Authorization": f"Bearer {token}"}
    )
    assert res.status_code == 401
    assert "fingerprint" in res.json()["detail"].lower()
