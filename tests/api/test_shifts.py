from pathlib import Path
import sys
import uuid
from datetime import datetime, timezone
from decimal import Decimal

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_shift_close_exact_balance():
    shift_id = str(uuid.uuid4())
    opened = datetime(2026, 9, 21, 8, 0, 0, tzinfo=timezone.utc).isoformat()
    closed = datetime(2026, 9, 21, 16, 0, 0, tzinfo=timezone.utc).isoformat()

    payload = {
        "shift_id": shift_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_COLOMBO_01",
        "counter_id": "CTR_01",
        "cashier_id": "USR_KASUN",
        "opened_at": opened,
        "closed_at": closed,
        "opening_float": "5000.00",
        "cash_received": "12500.50",
        "change_given": "2500.50",
        "cash_refunds": "1000.00",
        "cash_in": "500.00",
        "cash_out": "200.00",
        "actual_counted_cash": "14300.00",
        "notes": "Evening shift balanced",
        "actor_id": "USR_KAMAL"
    }

    res = client.post("/api/v1/shifts/close", json=payload)
    assert res.status_code == 200
    data = res.json()
    assert data["shift_id"] == shift_id
    assert Decimal(str(data["expected_cash"])) == Decimal("14300.00")
    assert Decimal(str(data["actual_counted_cash"])) == Decimal("14300.00")
    assert Decimal(str(data["variance"])) == Decimal("0.00")
    assert data["status"] == "closed"
    assert "reconciled_at" in data

def test_shift_close_shortage():
    shift_id = str(uuid.uuid4())
    opened = datetime(2026, 9, 21, 8, 0, 0, tzinfo=timezone.utc).isoformat()
    closed = datetime(2026, 9, 21, 16, 0, 0, tzinfo=timezone.utc).isoformat()

    payload = {
        "shift_id": shift_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_COLOMBO_01",
        "counter_id": "CTR_01",
        "cashier_id": "USR_KASUN",
        "opened_at": opened,
        "closed_at": closed,
        "opening_float": "10000.00",
        "cash_received": "0.00",
        "change_given": "0.00",
        "cash_refunds": "0.00",
        "cash_in": "0.00",
        "cash_out": "0.00",
        "actual_counted_cash": "9850.25",
        "notes": "Cash shortage of 149.75 LKR"
    }

    res = client.post("/api/v1/shifts/close", json=payload)
    assert res.status_code == 200
    data = res.json()
    assert Decimal(str(data["expected_cash"])) == Decimal("10000.00")
    assert Decimal(str(data["actual_counted_cash"])) == Decimal("9850.25")
    assert Decimal(str(data["variance"])) == Decimal("-149.75")

def test_shift_close_overage():
    shift_id = str(uuid.uuid4())
    opened = datetime(2026, 9, 21, 8, 0, 0, tzinfo=timezone.utc).isoformat()
    closed = datetime(2026, 9, 21, 16, 0, 0, tzinfo=timezone.utc).isoformat()

    payload = {
        "shift_id": shift_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_KANDY_01",
        "counter_id": "CTR_02",
        "cashier_id": "USR_NIMAL",
        "opened_at": opened,
        "closed_at": closed,
        "opening_float": "10000.00",
        "cash_received": "0.00",
        "change_given": "0.00",
        "cash_refunds": "0.00",
        "cash_in": "0.00",
        "cash_out": "0.00",
        "actual_counted_cash": "10125.50",
        "notes": "Cash overage of 125.50 LKR"
    }

    res = client.post("/api/v1/shifts/close", json=payload)
    assert res.status_code == 200
    data = res.json()
    assert Decimal(str(data["variance"])) == Decimal("125.50")

def test_shift_close_idempotent_retry():
    shift_id = str(uuid.uuid4())
    opened = datetime(2026, 9, 21, 8, 0, 0, tzinfo=timezone.utc).isoformat()
    closed = datetime(2026, 9, 21, 16, 0, 0, tzinfo=timezone.utc).isoformat()

    payload = {
        "shift_id": shift_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_GALLE_01",
        "counter_id": "CTR_01",
        "cashier_id": "USR_KASUN",
        "opened_at": opened,
        "closed_at": closed,
        "opening_float": "2000.00",
        "actual_counted_cash": "2000.00"
    }

    res1 = client.post("/api/v1/shifts/close", json=payload)
    assert res1.status_code == 200

    # Retry should return same without error
    res2 = client.post("/api/v1/shifts/close", json=payload)
    assert res2.status_code == 200
    assert res2.json()["shift_id"] == shift_id
    assert Decimal(str(res2.json()["variance"])) == Decimal("0.00")

def test_get_shift_details():
    shift_id = str(uuid.uuid4())
    opened = datetime(2026, 9, 21, 8, 0, 0, tzinfo=timezone.utc).isoformat()
    closed = datetime(2026, 9, 21, 16, 0, 0, tzinfo=timezone.utc).isoformat()

    payload = {
        "shift_id": shift_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_COLOMBO_01",
        "counter_id": "CTR_03",
        "cashier_id": "USR_KASUN",
        "opened_at": opened,
        "closed_at": closed,
        "opening_float": "3000.00",
        "cash_received": "7000.00",
        "change_given": "1000.00",
        "cash_refunds": "500.00",
        "cash_in": "200.00",
        "cash_out": "100.00",
        "actual_counted_cash": "8600.00",
        "notes": "Verified shift",
        "actor_id": "USR_KAMAL"
    }
    client.post("/api/v1/shifts/close", json=payload)

    res = client.get(f"/api/v1/shifts/{shift_id}")
    assert res.status_code == 200
    data = res.json()
    assert data["shift_id"] == shift_id
    assert data["cashier_id"] == "USR_KASUN"
    assert Decimal(str(data["opening_float"])) == Decimal("3000.00")
    assert Decimal(str(data["expected_cash"])) == Decimal("8600.00")
    assert Decimal(str(data["actual_counted_cash"])) == Decimal("8600.00")
    assert Decimal(str(data["variance"])) == Decimal("0.00")
    assert data["status"] == "closed"

def test_get_shift_details_not_found():
    random_id = str(uuid.uuid4())
    res = client.get(f"/api/v1/shifts/{random_id}")
    assert res.status_code == 404

