from pathlib import Path
import sys
import uuid
from decimal import Decimal

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_create_customer():
    suffix = uuid.uuid4().hex[:6]
    payload = {
        "name": f"Nimal Perera {suffix}",
        "phone": f"0771{suffix[:6]}",
        "email": f"nimal_{suffix}@example.com",
        "nic_or_brn": f"1985123{suffix[:4]}V",
        "credit_limit": "50000.00",
        "tenant_id": "TENANT_LK_01"
    }
    res = client.post("/api/v1/customers", json=payload)
    assert res.status_code == 201
    data = res.json()
    assert data["customer_id"].startswith("cust_")
    assert data["name"] == payload["name"]
    assert Decimal(str(data["credit_limit"])) == Decimal("50000.00")
    assert Decimal(str(data["current_balance"])) == Decimal("0.00")
    assert Decimal(str(data["available_credit"])) == Decimal("50000.00")
    assert data["is_active"] is True

def test_customer_invoice_and_credit_limit():
    suffix = uuid.uuid4().hex[:6]
    create_res = client.post("/api/v1/customers", json={
        "name": f"Kamal Stores {suffix}",
        "phone": f"0712{suffix[:6]}",
        "credit_limit": "20000.00",
        "tenant_id": "TENANT_LK_01"
    })
    assert create_res.status_code == 201
    customer_id = create_res.json()["customer_id"]

    # 1. Invoice of 12000.00 (within limit)
    inv1_res = client.post("/api/v1/customers/invoice", json={
        "customer_id": customer_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_COLOMBO_01",
        "counter_id": "CTR_01",
        "amount": "12000.00",
        "reference_id": "INV-1001",
        "actor_id": "USR_CASHIER_01",
        "notes": "Bulk purchase on credit"
    })
    assert inv1_res.status_code == 200
    inv1_data = inv1_res.json()
    assert Decimal(str(inv1_data["amount"])) == Decimal("12000.00")
    assert Decimal(str(inv1_data["new_balance"])) == Decimal("12000.00")
    assert Decimal(str(inv1_data["available_credit"])) == Decimal("8000.00")

    # 2. Invoice of 9000.00 (exceeds available credit of 8000.00)
    inv2_res = client.post("/api/v1/customers/invoice", json={
        "customer_id": customer_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_COLOMBO_01",
        "counter_id": "CTR_01",
        "amount": "9000.00",
        "actor_id": "USR_CASHIER_01"
    })
    assert inv2_res.status_code == 400
    assert "credit limit exceeded" in inv2_res.json()["detail"].lower()

def test_customer_settlement_and_ledger():
    suffix = uuid.uuid4().hex[:6]
    create_res = client.post("/api/v1/customers", json={
        "name": f"Sunil Auto {suffix}",
        "phone": f"0755{suffix[:6]}",
        "credit_limit": "30000.00",
        "tenant_id": "TENANT_LK_01"
    })
    customer_id = create_res.json()["customer_id"]

    # 1. Charge invoice: 18000.00
    client.post("/api/v1/customers/invoice", json={
        "customer_id": customer_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_KANDY_01",
        "counter_id": "CTR_02",
        "amount": "18000.00",
        "reference_id": "INV-2001",
        "actor_id": "USR_MGR"
    })

    # 2. Partial settlement: 8000.00 (via Cash)
    set1_res = client.post("/api/v1/customers/settlement", json={
        "customer_id": customer_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_KANDY_01",
        "counter_id": "CTR_02",
        "amount": "8000.00",
        "payment_method": "CASH",
        "reference_id": "RCP-5001",
        "actor_id": "USR_CASHIER_01",
        "notes": "Partial debt payment"
    })
    assert set1_res.status_code == 200
    set1_data = set1_res.json()
    assert Decimal(str(set1_data["amount_settled"])) == Decimal("8000.00")
    assert Decimal(str(set1_data["remaining_balance"])) == Decimal("10000.00")

    # 3. Final settlement: 10000.00 (via Bank Transfer)
    set2_res = client.post("/api/v1/customers/settlement", json={
        "customer_id": customer_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "BR_KANDY_01",
        "counter_id": "CTR_02",
        "amount": "10000.00",
        "payment_method": "BANK_TRANSFER",
        "reference_id": "TXN_BOC_9876",
        "actor_id": "USR_MGR"
    })
    assert set2_res.status_code == 200
    assert Decimal(str(set2_res.json()["remaining_balance"])) == Decimal("0.00")

    # 4. Check ledger entries
    ledger_res = client.get(f"/api/v1/customers/{customer_id}/ledger")
    assert ledger_res.status_code == 200
    ledger = ledger_res.json()
    assert len(ledger) == 3
    # Entries ordered by occurred_at desc
    types = [e["entry_type"] for e in ledger]
    assert "INVOICE" in types
    assert types.count("SETTLEMENT") == 2

def test_customer_not_found():
    res = client.get("/api/v1/customers/cust_nonexistent")
    assert res.status_code == 404
