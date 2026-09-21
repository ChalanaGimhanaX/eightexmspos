import uuid
from decimal import Decimal
import pytest
from fastapi.testclient import TestClient

from enightx_api.main import app
from enightx_api.database import get_db, SessionLocal
from enightx_api.models import (
    Supplier,
    PurchaseOrder,
    GoodsReceivedNote,
    SupplierSettlement,
    Product,
    BranchInventory,
)

client = TestClient(app)

TENANT_ID = "test-tenant-grn"
BRANCH_ID = "test-branch-grn"

@pytest.fixture(autouse=True)
def clean_db():
    db = SessionLocal()
    try:
        # Clean up test tenant records
        db.query(SupplierSettlement).filter(SupplierSettlement.tenant_id == TENANT_ID).delete()
        db.query(GoodsReceivedNote).filter(GoodsReceivedNote.tenant_id == TENANT_ID).delete()
        db.query(PurchaseOrder).filter(PurchaseOrder.tenant_id == TENANT_ID).delete()
        db.query(BranchInventory).filter(BranchInventory.tenant_id == TENANT_ID).delete()
        db.query(Product).filter(Product.tenant_id == TENANT_ID).delete()
        db.query(Supplier).filter(Supplier.tenant_id == TENANT_ID).delete()
        db.commit()
    finally:
        db.close()


def test_create_and_list_suppliers():
    # 1. Create Supplier
    payload = {
        "tenant_id": TENANT_ID,
        "supplier_code": "SUP-001",
        "name": "Lanka Agro Supplies",
        "contact_person": "Sunil Perera",
        "phone": "+94771234567",
        "email": "sunil@lankaagro.lk",
        "address": "123 Galle Road, Colombo 03"
    }
    res = client.post("/api/v1/suppliers", json=payload)
    assert res.status_code == 201
    data = res.json()
    assert data["supplier_code"] == "SUP-001"
    assert data["name"] == "Lanka Agro Supplies"
    assert Decimal(str(data["balance_lkr"])) == Decimal("0.00")
    supplier_id = data["supplier_id"]

    # 2. Duplicate supplier code rejected
    dup_res = client.post("/api/v1/suppliers", json=payload)
    assert dup_res.status_code == 400
    assert "already exists" in dup_res.json()["detail"]

    # 3. List suppliers
    list_res = client.get(f"/api/v1/suppliers?tenant_id={TENANT_ID}")
    assert list_res.status_code == 200
    suppliers = list_res.json()
    assert len(suppliers) == 1
    assert suppliers[0]["supplier_id"] == supplier_id

    # 4. Search suppliers
    search_res = client.get(f"/api/v1/suppliers?tenant_id={TENANT_ID}&search=Lanka")
    assert search_res.status_code == 200
    assert len(search_res.json()) == 1

    # 5. Get supplier by ID
    get_res = client.get(f"/api/v1/suppliers/{supplier_id}?tenant_id={TENANT_ID}")
    assert get_res.status_code == 200
    assert get_res.json()["name"] == "Lanka Agro Supplies"

    # 6. Update supplier
    upd_res = client.put(
        f"/api/v1/suppliers/{supplier_id}?tenant_id={TENANT_ID}",
        json={"phone": "+94779998877", "contact_person": "Kamal Perera"}
    )
    assert upd_res.status_code == 200
    assert upd_res.json()["phone"] == "+94779998877"
    assert upd_res.json()["contact_person"] == "Kamal Perera"


def test_purchase_order_lifecycle():
    # Setup supplier
    sup_res = client.post("/api/v1/suppliers", json={
        "tenant_id": TENANT_ID,
        "supplier_code": "SUP-PO-01",
        "name": "Ceylon Mills"
    })
    supplier_id = sup_res.json()["supplier_id"]

    # 1. Create Purchase Order
    po_payload = {
        "tenant_id": TENANT_ID,
        "branch_id": BRANCH_ID,
        "po_number": "PO-2026-001",
        "supplier_id": supplier_id,
        "notes": "Urgent stock for weekend",
        "created_by": "manager_1",
        "items": [
            {
                "product_id": "PROD-RICE-5KG",
                "product_name": "Samba Rice 5kg",
                "ordered_quantity": "20.00",
                "unit_cost_lkr": "1250.00"
            },
            {
                "product_id": "PROD-SUGAR-1KG",
                "product_name": "White Sugar 1kg",
                "ordered_quantity": "50.00",
                "unit_cost_lkr": "240.00"
            }
        ]
    }
    po_res = client.post("/api/v1/purchases/orders", json=po_payload)
    assert po_res.status_code == 201
    po = po_res.json()
    assert po["status"] == "ISSUED"
    # Line 1: 20 * 1250 = 25,000.00
    # Line 2: 50 * 240 = 12,000.00
    # Total: 37,000.00
    assert Decimal(str(po["total_amount_lkr"])) == Decimal("37000.00")
    assert len(po["items"]) == 2
    po_id = po["po_id"]

    # 2. Get PO by ID
    get_po = client.get(f"/api/v1/purchases/orders/{po_id}?tenant_id={TENANT_ID}")
    assert get_po.status_code == 200
    assert get_po.json()["po_number"] == "PO-2026-001"

    # 3. Duplicate PO number rejected
    dup_po = client.post("/api/v1/purchases/orders", json=po_payload)
    assert dup_po.status_code == 400

    # 4. Cancel PO
    cancel_res = client.post(f"/api/v1/purchases/orders/{po_id}/cancel?tenant_id={TENANT_ID}")
    assert cancel_res.status_code == 200
    assert cancel_res.json()["status"] == "CANCELLED"


def test_grn_moving_average_cost_and_stock_projections():
    # Setup supplier
    sup_res = client.post("/api/v1/suppliers", json={
        "tenant_id": TENANT_ID,
        "supplier_code": "SUP-GRN-01",
        "name": "Beverage Distributors"
    })
    supplier_id = sup_res.json()["supplier_id"]

    # Seed product with initial stock = 10, cost = 100.00
    db = SessionLocal()
    try:
        p = Product(
            product_id="PROD-TEA-100G",
            tenant_id=TENANT_ID,
            barcode="4791234567890",
            name="Pure Ceylon Tea 100g",
            unit_price=Decimal("180.00"),
            cost_basis=Decimal("100.00"),
            tax_rate=Decimal("0.0000"),
            stock_on_hand=Decimal("10.00"),
            is_active=True,
        )
        db.add(p)
        bi = BranchInventory(
            id=uuid.uuid4(),
            tenant_id=TENANT_ID,
            branch_id=BRANCH_ID,
            product_id="PROD-TEA-100G",
            stock_on_hand=Decimal("10.00"),
            stock_in_transit=Decimal("0.00"),
            reorder_point=Decimal("0.00"),
        )
        db.add(bi)
        db.commit()
    finally:
        db.close()

    # Receive GRN: 20 units at 130.00 each
    # Formula: ((10 * 100.00) + (20 * 130.00)) / (10 + 20) = (1000 + 2600) / 30 = 3600 / 30 = 120.00 LKR
    grn_payload = {
        "tenant_id": TENANT_ID,
        "branch_id": BRANCH_ID,
        "grn_number": "GRN-2026-001",
        "supplier_id": supplier_id,
        "supplier_invoice_number": "INV-7890",
        "received_by": "inventory_officer",
        "items": [
            {
                "product_id": "PROD-TEA-100G",
                "product_name": "Pure Ceylon Tea 100g",
                "received_quantity": "20.00",
                "unit_cost_lkr": "130.00",
                "batch_number": "BATCH-01",
            }
        ]
    }
    grn_res = client.post("/api/v1/purchases/grn", json=grn_payload)
    assert grn_res.status_code == 201
    grn = grn_res.json()
    assert grn["status"] == "RECEIVED"
    assert Decimal(str(grn["total_cost_lkr"])) == Decimal("2600.00")

    # Verify Product in Database: stock = 30.00, cost_basis = 120.00
    db = SessionLocal()
    try:
        updated_prod = db.query(Product).filter(
            Product.product_id == "PROD-TEA-100G",
            Product.tenant_id == TENANT_ID
        ).first()
        assert updated_prod is not None
        assert updated_prod.stock_on_hand == Decimal("30.00")
        assert updated_prod.cost_basis == Decimal("120.00")

        # Verify Branch Inventory: stock = 30.00
        branch_inv = db.query(BranchInventory).filter(
            BranchInventory.tenant_id == TENANT_ID,
            BranchInventory.branch_id == BRANCH_ID,
            BranchInventory.product_id == "PROD-TEA-100G"
        ).first()
        assert branch_inv is not None
        assert branch_inv.stock_on_hand == Decimal("30.00")

        # Verify Supplier Balance: 2,600.00
        sup = db.query(Supplier).filter(Supplier.supplier_id == supplier_id).first()
        assert sup.balance_lkr == Decimal("2600.00")
    finally:
        db.close()


def test_grn_linked_to_po_fulfillment_and_settlement():
    # Setup supplier
    sup_res = client.post("/api/v1/suppliers", json={
        "tenant_id": TENANT_ID,
        "supplier_code": "SUP-LINKED-01",
        "name": "Dairy Lanka"
    })
    supplier_id = sup_res.json()["supplier_id"]

    # Create PO with 2 items: 10 units and 5 units
    po_res = client.post("/api/v1/purchases/orders", json={
        "tenant_id": TENANT_ID,
        "branch_id": BRANCH_ID,
        "po_number": "PO-DAIRY-001",
        "supplier_id": supplier_id,
        "created_by": "store_manager",
        "items": [
            {
                "product_id": "PROD-MILK-1L",
                "product_name": "Fresh Milk 1L",
                "ordered_quantity": "10.00",
                "unit_cost_lkr": "450.00"
            },
            {
                "product_id": "PROD-BUTTER-200G",
                "product_name": "Salted Butter 200g",
                "ordered_quantity": "5.00",
                "unit_cost_lkr": "800.00"
            }
        ]
    })
    assert po_res.status_code == 201
    po_id = po_res.json()["po_id"]

    # 1. Partial GRN: Receive only 4 units of Milk
    grn_part_res = client.post("/api/v1/purchases/grn", json={
        "tenant_id": TENANT_ID,
        "branch_id": BRANCH_ID,
        "grn_number": "GRN-DAIRY-001",
        "po_id": po_id,
        "supplier_id": supplier_id,
        "received_by": "store_manager",
        "items": [
            {
                "product_id": "PROD-MILK-1L",
                "product_name": "Fresh Milk 1L",
                "received_quantity": "4.00",
                "unit_cost_lkr": "450.00"
            }
        ]
    })
    assert grn_part_res.status_code == 201

    # Check PO status is PARTIALLY_RECEIVED
    po_check = client.get(f"/api/v1/purchases/orders/{po_id}?tenant_id={TENANT_ID}")
    assert po_check.status_code == 200
    assert po_check.json()["status"] == "PARTIALLY_RECEIVED"
    milk_item = [i for i in po_check.json()["items"] if i["product_id"] == "PROD-MILK-1L"][0]
    assert Decimal(str(milk_item["received_quantity"])) == Decimal("4.00")

    # 2. Complete remaining items: 6 Milk and 5 Butter
    grn_full_res = client.post("/api/v1/purchases/grn", json={
        "tenant_id": TENANT_ID,
        "branch_id": BRANCH_ID,
        "grn_number": "GRN-DAIRY-002",
        "po_id": po_id,
        "supplier_id": supplier_id,
        "received_by": "store_manager",
        "items": [
            {
                "product_id": "PROD-MILK-1L",
                "product_name": "Fresh Milk 1L",
                "received_quantity": "6.00",
                "unit_cost_lkr": "450.00"
            },
            {
                "product_id": "PROD-BUTTER-200G",
                "product_name": "Salted Butter 200g",
                "received_quantity": "5.00",
                "unit_cost_lkr": "800.00"
            }
        ]
    })
    assert grn_full_res.status_code == 201

    # Check PO status is now RECEIVED
    po_check2 = client.get(f"/api/v1/purchases/orders/{po_id}?tenant_id={TENANT_ID}")
    assert po_check2.status_code == 200
    assert po_check2.json()["status"] == "RECEIVED"

    # Total supplier balance:
    # GRN 1: 4 * 450 = 1,800.00
    # GRN 2: (6 * 450) + (5 * 800) = 2,700 + 4,000 = 6,700.00
    # Total: 8,500.00
    sup_check = client.get(f"/api/v1/suppliers/{supplier_id}?tenant_id={TENANT_ID}")
    assert Decimal(str(sup_check.json()["balance_lkr"])) == Decimal("8500.00")

    # 3. Settle / Pay Supplier: 5,000.00
    settle_res = client.post("/api/v1/suppliers/settlement", json={
        "tenant_id": TENANT_ID,
        "supplier_id": supplier_id,
        "amount_lkr": "5000.00",
        "payment_method": "BANK_TRANSFER",
        "reference_number": "TXN-BOC-123456",
        "notes": "Partial payment for PO-DAIRY-001",
        "created_by": "accountant_1"
    })
    assert settle_res.status_code == 201
    settle_data = settle_res.json()
    assert Decimal(str(settle_data["remaining_balance_lkr"])) == Decimal("3500.00")

    # Verify supplier balance in directory is 3,500.00
    sup_after_settle = client.get(f"/api/v1/suppliers/{supplier_id}?tenant_id={TENANT_ID}")
    assert Decimal(str(sup_after_settle.json()["balance_lkr"])) == Decimal("3500.00")
