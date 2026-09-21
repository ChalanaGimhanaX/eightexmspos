from pathlib import Path
import sys
import uuid
from decimal import Decimal

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_branch_stock_adjustment_and_query():
    suffix = uuid.uuid4().hex[:6]
    product_id = f"prd_engine_oil_{suffix}"
    branch_id = f"BR_COLOMBO_{suffix}"

    adjust_payload = {
        "tenant_id": "TENANT_LK_01",
        "branch_id": branch_id,
        "product_id": product_id,
        "stock_on_hand": "100.00",
        "reorder_point": "15.00"
    }
    adj_res = client.post("/api/v1/inventory/branch/adjust", json=adjust_payload)
    assert adj_res.status_code == 200
    data = adj_res.json()
    assert data["product_id"] == product_id
    assert Decimal(str(data["stock_on_hand"])) == Decimal("100.00")
    assert Decimal(str(data["reorder_point"])) == Decimal("15.00")

    # Query branch inventory
    query_res = client.get(f"/api/v1/inventory/branch/{branch_id}")
    assert query_res.status_code == 200
    items = query_res.json()
    assert len(items) == 1
    assert items[0]["product_id"] == product_id
    assert Decimal(str(items[0]["stock_on_hand"])) == Decimal("100.00")

def test_stock_transfer_lifecycle_request_dispatch_receive():
    suffix = uuid.uuid4().hex[:6]
    product_id = f"prd_brake_pad_{suffix}"
    src_branch = f"BR_SRC_{suffix}"
    dest_branch = f"BR_DST_{suffix}"

    # 1. Setup initial stock: Source has 50, Dest has 10
    client.post("/api/v1/inventory/branch/adjust", json={
        "tenant_id": "TENANT_LK_01",
        "branch_id": src_branch,
        "product_id": product_id,
        "stock_on_hand": "50.00"
    })
    client.post("/api/v1/inventory/branch/adjust", json={
        "tenant_id": "TENANT_LK_01",
        "branch_id": dest_branch,
        "product_id": product_id,
        "stock_on_hand": "10.00"
    })

    # 2. Request transfer of 20 items from src to dest
    req_res = client.post("/api/v1/inventory/transfers/request", json={
        "tenant_id": "TENANT_LK_01",
        "source_branch_id": src_branch,
        "dest_branch_id": dest_branch,
        "requested_by": "USR_MGR_DST",
        "notes": "Urgent customer order demand",
        "items": [
            {"product_id": product_id, "requested_quantity": "20.00"}
        ]
    })
    assert req_res.status_code == 201
    transfer_data = req_res.json()
    transfer_id = transfer_data["transfer_id"]
    assert transfer_data["status"] == "REQUESTED"
    assert len(transfer_data["items"]) == 1
    assert Decimal(str(transfer_data["items"][0]["requested_quantity"])) == Decimal("20.00")

    # 3. Dispatch transfer from source branch
    disp_res = client.post(f"/api/v1/inventory/transfers/{transfer_id}/dispatch", json={
        "dispatched_by": "USR_MGR_SRC",
        "notes": "Dispatched via Express Courier LK"
    })
    assert disp_res.status_code == 200
    disp_data = disp_res.json()
    assert disp_data["status"] == "DISPATCHED"
    assert Decimal(str(disp_data["items"][0]["dispatched_quantity"])) == Decimal("20.00")

    # Verify inventory in transit:
    # Source stock on hand should now be 30 (50 - 20)
    src_inv = client.get(f"/api/v1/inventory/branch/{src_branch}").json()
    assert Decimal(str(src_inv[0]["stock_on_hand"])) == Decimal("30.00")

    # Destination stock in transit should now be 20, stock on hand still 10
    dest_inv = client.get(f"/api/v1/inventory/branch/{dest_branch}").json()
    assert Decimal(str(dest_inv[0]["stock_in_transit"])) == Decimal("20.00")
    assert Decimal(str(dest_inv[0]["stock_on_hand"])) == Decimal("10.00")

    # 4. Receive transfer at destination branch
    recv_res = client.post(f"/api/v1/inventory/transfers/{transfer_id}/receive", json={
        "received_by": "USR_MGR_DST",
        "notes": "Verified package intact"
    })
    assert recv_res.status_code == 200
    recv_data = recv_res.json()
    assert recv_data["status"] == "RECEIVED"
    assert Decimal(str(recv_data["items"][0]["received_quantity"])) == Decimal("20.00")

    # Destination stock in transit should now be 0, stock on hand should be 30 (10 + 20)
    dest_inv_after = client.get(f"/api/v1/inventory/branch/{dest_branch}").json()
    assert Decimal(str(dest_inv_after[0]["stock_in_transit"])) == Decimal("0.00")
    assert Decimal(str(dest_inv_after[0]["stock_on_hand"])) == Decimal("30.00")

def test_stock_transfer_insufficient_stock():
    suffix = uuid.uuid4().hex[:6]
    product_id = f"prd_filter_{suffix}"
    src_branch = f"BR_SRC_LOW_{suffix}"
    dest_branch = f"BR_DST_LOW_{suffix}"

    # Source only has 5
    client.post("/api/v1/inventory/branch/adjust", json={
        "tenant_id": "TENANT_LK_01",
        "branch_id": src_branch,
        "product_id": product_id,
        "stock_on_hand": "5.00"
    })

    # Request 25
    req_res = client.post("/api/v1/inventory/transfers/request", json={
        "tenant_id": "TENANT_LK_01",
        "source_branch_id": src_branch,
        "dest_branch_id": dest_branch,
        "requested_by": "USR_MGR",
        "items": [
            {"product_id": product_id, "requested_quantity": "25.00"}
        ]
    })
    transfer_id = req_res.json()["transfer_id"]

    # Dispatch should fail with 400
    disp_res = client.post(f"/api/v1/inventory/transfers/{transfer_id}/dispatch", json={
        "dispatched_by": "USR_MGR"
    })
    assert disp_res.status_code == 400
    assert "insufficient stock" in disp_res.json()["detail"].lower()

def test_stock_transfer_same_source_and_dest_rejected():
    res = client.post("/api/v1/inventory/transfers/request", json={
        "tenant_id": "TENANT_LK_01",
        "source_branch_id": "BR_COLOMBO",
        "dest_branch_id": "BR_COLOMBO",
        "requested_by": "USR_MGR",
        "items": [
            {"product_id": "PRD_01", "requested_quantity": "10.00"}
        ]
    })
    assert res.status_code == 400
    assert "identical" in res.json()["detail"].lower()

def test_stock_transfer_not_found():
    random_id = str(uuid.uuid4())
    res = client.get(f"/api/v1/inventory/transfers/{random_id}")
    assert res.status_code == 404

