from pathlib import Path
import sys

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

import pytest
from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_provider_dashboard_html():
    response = client.get("/provider")
    assert response.status_code == 200
    assert "Enightx POS - Provider Central Management Portal" in response.text
    assert "Device Fleet" in response.text

def test_provider_overview():
    response = client.get("/api/v1/provider/overview")
    assert response.status_code == 200
    data = response.json()
    assert "summary" in data
    assert "devices" in data
    assert "licenses" in data
    assert "payments" in data

def test_issue_licenses():
    # 1. Trial License
    res_trial = client.post("/api/v1/provider/licenses/issue", json={
        "tenant_id": "TENANT_LK_01",
        "device_id": "C01",
        "plan_type": "Trial",
        "duration_days": 2
    })
    assert res_trial.status_code == 200
    trial_data = res_trial.json()
    assert trial_data["plan_type"] == "Trial"
    assert trial_data["expires_at"] is not None
    assert len(trial_data["signature"]) > 10

    # 2. Permanent License
    res_perm = client.post("/api/v1/provider/licenses/issue", json={
        "tenant_id": "TENANT_LK_01",
        "device_id": "C01",
        "plan_type": "Permanent"
    })
    assert res_perm.status_code == 200
    perm_data = res_perm.json()
    assert perm_data["plan_type"] == "Permanent"
    assert perm_data["expires_at"] is None

def test_freeze_and_unfreeze_device():
    import uuid
    # Enroll device first and get server-assigned device_id
    res_enroll = client.post("/api/v1/devices/enroll", json={
        "tenant_id": "TENANT_LK_01",
        "branch_id": "B01",
        "device_code": "C01",
        "device_name": "Test Counter 01",
        "hardware_fingerprint": f"hw_fp_freeze_{uuid.uuid4().hex[:8]}",
        "app_version": "1.0.0"
    })
    assert res_enroll.status_code == 200
    device_id = res_enroll.json()["device_id"]

    # Freeze device
    res_freeze = client.post(f"/api/v1/provider/devices/{device_id}/freeze", json={
        "reason": "Overdue payment violation"
    })
    assert res_freeze.status_code == 200
    assert res_freeze.json()["reason"] == "Overdue payment violation"

    # Verify device status in overview
    res_overview = client.get("/api/v1/provider/overview")
    dev = next((d for d in res_overview.json()["devices"] if d["device_id"] == device_id), None)
    assert dev is not None
    assert dev["is_frozen"] is True
    assert dev["freeze_reason"] == "Overdue payment violation"

    # Unfreeze device
    res_unfreeze = client.post(f"/api/v1/provider/devices/{device_id}/unfreeze")
    assert res_unfreeze.status_code == 200

    res_overview_after = client.get("/api/v1/provider/overview")
    dev_after = next((d for d in res_overview_after.json()["devices"] if d["device_id"] == device_id), None)
    assert dev_after is not None
    assert dev_after["is_frozen"] is False

def test_verify_payment():
    res = client.post("/api/v1/provider/payments/verify", json={
        "tenant_id": "TENANT_LK_01",
        "amount": 10000.00,
        "currency": "LKR",
        "payment_method": "BANK_TRANSFER",
        "reference": "TEST-BOC-12345",
        "verified_by": "Senior Auditor"
    })
    assert res.status_code == 200
    data = res.json()
    assert data["status"] == "ok"
    assert data["amount"] == 10000.00
    assert "valid_until" in data
