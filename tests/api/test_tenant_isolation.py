"""
Comprehensive test suite for Cloud API Multi-Tenancy & Device Security (Milestone 1 / R1).
Covers:
- Authentication boundaries: missing token (401), invalid token (401), deactivated device (401)
- Catalog tenant filtering & deleted items scoping (zero cross-tenant data leakage)
- Push batch/event tenant, device, and branch mismatch rejection (403 Forbidden)
- Tenant-scoped idempotency and cross-tenant batch conflict prevention
- Sync timestamp tracking (device.last_sync_at) for catalog pull and batch push
- Device heartbeat state tracking (last_heartbeat_at, status) and active device validation
"""

import uuid
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from pathlib import Path
import sys
import pytest
from fastapi.testclient import TestClient

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from src.enightx_api.main import app
from src.enightx_api.database import SessionLocal, Base, engine
from src.enightx_api.models import Device, Product, Category, SyncBatch, SyncEvent

# Isolated test tenant identifiers
TENANT_A = "TENANT_ISO_ALPHA"
TENANT_B = "TENANT_ISO_BETA"
BRANCH_A = "BRANCH_ISO_A01"
BRANCH_B = "BRANCH_ISO_B01"
DEVICE_A_ID = "dev_iso_alpha_01"
DEVICE_B_ID = "dev_iso_beta_01"
TOKEN_A = "tok_iso_alpha_secret_token_111"
TOKEN_B = "tok_iso_beta_secret_token_222"


def cleanup_isolation_data(db):
    """Purge all isolation test data across models in reverse dependency order."""
    db.query(SyncEvent).filter(SyncEvent.tenant_id.in_([TENANT_A, TENANT_B])).delete(synchronize_session=False)
    db.query(SyncBatch).filter(SyncBatch.tenant_id.in_([TENANT_A, TENANT_B])).delete(synchronize_session=False)
    db.query(Product).filter(Product.tenant_id.in_([TENANT_A, TENANT_B])).delete(synchronize_session=False)
    db.query(Category).filter(Category.tenant_id.in_([TENANT_A, TENANT_B])).delete(synchronize_session=False)
    db.query(Device).filter(Device.tenant_id.in_([TENANT_A, TENANT_B])).delete(synchronize_session=False)
    db.commit()


@pytest.fixture(autouse=True)
def setup_isolation_environment(device_factory):
    """Setup clean test devices for Tenant A and Tenant B, and cleanup on teardown."""
    db = SessionLocal()
    try:
        cleanup_isolation_data(db)
    finally:
        db.close()

    # Provision Device A and Device B
    device_factory(
        tenant_id=TENANT_A,
        branch_id=BRANCH_A,
        device_id=DEVICE_A_ID,
        token=TOKEN_A,
        device_code="CTR-ALPHA",
        device_name="Counter Alpha",
        is_active=True,
        status="ONLINE"
    )
    device_factory(
        tenant_id=TENANT_B,
        branch_id=BRANCH_B,
        device_id=DEVICE_B_ID,
        token=TOKEN_B,
        device_code="CTR-BETA",
        device_name="Counter Beta",
        is_active=True,
        status="ONLINE"
    )

    yield

    db = SessionLocal()
    try:
        cleanup_isolation_data(db)
    finally:
        db.close()


def create_iso_event(
    event_id: str,
    tenant_id: str,
    branch_id: str,
    device_id: str,
    seq: int = 1
):
    now_iso = datetime.now(timezone.utc).isoformat()
    return {
        "event_id": event_id,
        "tenant_id": tenant_id,
        "branch_id": branch_id,
        "device_id": device_id,
        "device_generation": 1,
        "source_sequence": seq,
        "schema_version": "1.0",
        "occurred_at": now_iso,
        "actor_id": "usr_iso_cashier",
        "causal_reference": None,
        "payload": {
            "sale_id": str(uuid.uuid4()),
            "receipt_number": f"{branch_id}-{device_id}-{seq:06d}",
            "shift_id": str(uuid.uuid4()),
            "customer_id": None,
            "subtotal": "1000.00",
            "discount_total": "0.00",
            "tax_total": "180.00",
            "grand_total": "1180.00",
            "lines": [
                {
                    "line_id": str(uuid.uuid4()),
                    "product_id": "prod_iso_item",
                    "product_name": "Test Isolation Item",
                    "barcode": "5901234567890",
                    "quantity": "1.00",
                    "unit_price": "1000.00",
                    "discount_rate": "0.00",
                    "discount_fixed": "0.00",
                    "tax_rate": "0.1800",
                    "line_total": "1180.00"
                }
            ],
            "tenders": [
                {
                    "tender_id": str(uuid.uuid4()),
                    "tender_type": "CASH",
                    "amount_tendered": "1200.00",
                    "change_given": "20.00",
                    "payment_reference": None
                }
            ]
        }
    }


# ==============================================================================
# SECTION 1: AUTHENTICATION BOUNDARIES (401)
# ==============================================================================

def test_catalog_sync_missing_token_returns_401(clean_client):
    """GET /api/v1/sync/catalog without X-Device-Token must return 401."""
    res = clean_client.get("/api/v1/sync/catalog")
    assert res.status_code == 401
    assert "missing" in res.json().get("detail", "").lower()


def test_catalog_sync_invalid_token_returns_401(clean_client):
    """GET /api/v1/sync/catalog with unrecognized token must return 401."""
    res = clean_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": "tok_bogus_token_hex"})
    assert res.status_code == 401
    assert "invalid or unrecognized" in res.json().get("detail", "").lower()


def test_catalog_sync_deactivated_device_returns_401(clean_client, device_factory):
    """GET /api/v1/sync/catalog from a deactivated device (is_active=False) must return 401."""
    deact_token = "tok_iso_deactivated_token_999"
    device_factory(
        tenant_id=TENANT_A,
        branch_id=BRANCH_A,
        device_id="dev_iso_deactivated_01",
        token=deact_token,
        is_active=False
    )
    res = clean_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": deact_token})
    assert res.status_code == 401
    assert "deactivated" in res.json().get("detail", "").lower()


def test_sync_push_missing_token_returns_401(clean_client):
    """POST /api/v1/sync/push without X-Device-Token must return 401."""
    payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = clean_client.post("/api/v1/sync/push", json=payload)
    assert res.status_code == 401
    assert "missing" in res.json().get("detail", "").lower()


def test_sync_push_invalid_token_returns_401(clean_client):
    """POST /api/v1/sync/push with invalid token must return 401."""
    payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = clean_client.post(
        "/api/v1/sync/push",
        headers={"X-Device-Token": "tok_invalid_secret"},
        json=payload
    )
    assert res.status_code == 401
    assert "invalid or unrecognized" in res.json().get("detail", "").lower()


def test_sync_push_deactivated_device_returns_401(clean_client, device_factory):
    """POST /api/v1/sync/push from a deactivated device must return 401."""
    deact_token = "tok_iso_push_deactivated_token"
    device_factory(
        tenant_id=TENANT_A,
        branch_id=BRANCH_A,
        device_id="dev_iso_push_deact_01",
        token=deact_token,
        is_active=False
    )
    payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": TENANT_A,
        "source_device_id": "dev_iso_push_deact_01",
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = clean_client.post(
        "/api/v1/sync/push",
        headers={"X-Device-Token": deact_token},
        json=payload
    )
    assert res.status_code == 401
    assert "deactivated" in res.json().get("detail", "").lower()


# ==============================================================================
# SECTION 2: CATALOG MULTI-TENANT ISOLATION
# ==============================================================================

def test_catalog_sync_filters_products_by_tenant(clean_client, db_session):
    """Verify that catalog pull strictly returns products and categories for the authenticated tenant only."""
    now = datetime.now(timezone.utc)

    # Populate Tenant A data
    cat_a = Category(category_id="cat_iso_a", tenant_id=TENANT_A, name="Tenant A Cat", is_active=True, updated_at=now)
    p_a1 = Product(product_id="prod_iso_a_01", tenant_id=TENANT_A, category_id="cat_iso_a", barcode="111111", name="Prod A1", unit_price=Decimal("100"), is_active=True, updated_at=now)
    p_a2 = Product(product_id="prod_iso_a_02", tenant_id=TENANT_A, category_id="cat_iso_a", barcode="222222", name="Prod A2", unit_price=Decimal("200"), is_active=True, updated_at=now)

    # Populate Tenant B data
    cat_b = Category(category_id="cat_iso_b", tenant_id=TENANT_B, name="Tenant B Cat", is_active=True, updated_at=now)
    p_b1 = Product(product_id="prod_iso_b_01", tenant_id=TENANT_B, category_id="cat_iso_b", barcode="333333", name="Prod B1", unit_price=Decimal("300"), is_active=True, updated_at=now)

    db_session.add_all([cat_a, p_a1, p_a2, cat_b, p_b1])
    db_session.commit()

    # Device A query
    res_a = clean_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": TOKEN_A})
    assert res_a.status_code == 200
    data_a = res_a.json()
    prod_ids_a = {p["product_id"] for p in data_a["products"]}
    cat_ids_a = {c["category_id"] for c in data_a["categories"]}

    assert "prod_iso_a_01" in prod_ids_a
    assert "prod_iso_a_02" in prod_ids_a
    assert "prod_iso_b_01" not in prod_ids_a, "Tenant B product leaked into Tenant A catalog!"
    assert "cat_iso_a" in cat_ids_a
    assert "cat_iso_b" not in cat_ids_a, "Tenant B category leaked into Tenant A catalog!"

    # Device B query
    res_b = clean_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": TOKEN_B})
    assert res_b.status_code == 200
    data_b = res_b.json()
    prod_ids_b = {p["product_id"] for p in data_b["products"]}
    cat_ids_b = {c["category_id"] for c in data_b["categories"]}

    assert "prod_iso_b_01" in prod_ids_b
    assert "prod_iso_a_01" not in prod_ids_b, "Tenant A product leaked into Tenant B catalog!"
    assert "prod_iso_a_02" not in prod_ids_b, "Tenant A product leaked into Tenant B catalog!"
    assert "cat_iso_b" in cat_ids_b
    assert "cat_iso_a" not in cat_ids_b, "Tenant A category leaked into Tenant B catalog!"


def test_catalog_sync_deleted_items_scoped_to_tenant(clean_client, db_session):
    """Verify deleted_item_ids in catalog delta pull strictly return tombstones belonging to the caller tenant."""
    t0 = datetime.now(timezone.utc) - timedelta(hours=1)
    t_now = datetime.now(timezone.utc)

    # Soft-delete for Tenant A
    p_del_a = Product(
        product_id="prod_iso_a_del",
        tenant_id=TENANT_A,
        barcode="999111",
        name="Deleted A",
        unit_price=Decimal("10"),
        is_active=False,
        updated_at=t_now,
        deleted_at=t_now
    )
    # Soft-delete for Tenant B
    p_del_b = Product(
        product_id="prod_iso_b_del",
        tenant_id=TENANT_B,
        barcode="999222",
        name="Deleted B",
        unit_price=Decimal("20"),
        is_active=False,
        updated_at=t_now,
        deleted_at=t_now
    )
    db_session.add_all([p_del_a, p_del_b])
    db_session.commit()

    since_iso = t0.isoformat()

    # Device A should only see prod_iso_a_del
    res_a = clean_client.get(f"/api/v1/sync/catalog?since={since_iso}", headers={"X-Device-Token": TOKEN_A})
    assert res_a.status_code == 200
    deleted_a = res_a.json()["deleted_item_ids"]
    assert "prod_iso_a_del" in deleted_a
    assert "prod_iso_b_del" not in deleted_a, "Tenant B tombstone leaked to Tenant A!"

    # Device B should only see prod_iso_b_del
    res_b = clean_client.get(f"/api/v1/sync/catalog?since={since_iso}", headers={"X-Device-Token": TOKEN_B})
    assert res_b.status_code == 200
    deleted_b = res_b.json()["deleted_item_ids"]
    assert "prod_iso_b_del" in deleted_b
    assert "prod_iso_a_del" not in deleted_b, "Tenant A tombstone leaked to Tenant B!"


# ==============================================================================
# SECTION 3: SYNC PUSH INTEGRITY & SECURITY (403)
# ==============================================================================

def test_sync_push_rejects_batch_tenant_mismatch(clean_client):
    """Device A attempts to push a batch specifying Tenant B -> 403 Forbidden."""
    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": TENANT_B,  # Mismatch! Device A belongs to TENANT_A
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_payload)
    assert res.status_code == 403
    assert "tenant_id" in res.json().get("detail", "").lower()


def test_sync_push_rejects_batch_device_mismatch(clean_client):
    """Device A attempts to push a batch claiming to be Device B -> 403 Forbidden."""
    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_B_ID,  # Mismatch! Token A is for DEVICE_A_ID
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_payload)
    assert res.status_code == 403
    assert "source_device_id" in res.json().get("detail", "").lower()


def test_sync_push_rejects_event_tenant_mismatch(clean_client, db_session):
    """Batch has valid tenant, but an event inside contains a foreign tenant_id -> 403 Forbidden."""
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    bad_event = create_iso_event(event_id, tenant_id=TENANT_B, branch_id=BRANCH_A, device_id=DEVICE_A_ID)

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [bad_event]
    }
    res = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_payload)
    assert res.status_code == 403
    assert "tenant_id" in res.json().get("detail", "").lower()

    # Verify zero database persistence
    persisted_ev = db_session.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_id)).first()
    assert persisted_ev is None


def test_sync_push_rejects_event_device_mismatch(clean_client, db_session):
    """Batch has valid device, but an event inside contains a foreign device_id -> 403 Forbidden."""
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    bad_event = create_iso_event(event_id, tenant_id=TENANT_A, branch_id=BRANCH_A, device_id="dev_foreign_99")

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [bad_event]
    }
    res = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_payload)
    assert res.status_code == 403
    assert "device_id" in res.json().get("detail", "").lower()

    persisted_ev = db_session.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_id)).first()
    assert persisted_ev is None


def test_sync_push_rejects_event_branch_mismatch(clean_client, db_session):
    """Batch has valid device, but an event inside contains a mismatched branch_id -> 403 Forbidden."""
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    bad_event = create_iso_event(event_id, tenant_id=TENANT_A, branch_id="BRANCH_UNKNOWN_99", device_id=DEVICE_A_ID)

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [bad_event]
    }
    res = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_payload)
    assert res.status_code == 403
    assert "branch_id" in res.json().get("detail", "").lower()

    persisted_ev = db_session.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_id)).first()
    assert persisted_ev is None


# ==============================================================================
# SECTION 4: IDEMPOTENCY SCOPING & SYNC TRACKING
# ==============================================================================

def test_sync_push_idempotency_scoped_to_tenant(clean_client):
    """
    Verify:
    1. Tenant A pushes a batch -> acknowledged.
    2. Tenant A retries same batch -> identical acknowledgment returned.
    3. Tenant B attempts to submit the same batch_id -> rejected (409/403) and cannot snoop Tenant A's batch.
    """
    shared_batch_id = str(uuid.uuid4())
    event_a_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    batch_a = {
        "batch_id": shared_batch_id,
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 10,
        "sent_at": now_iso,
        "events": [create_iso_event(event_a_id, TENANT_A, BRANCH_A, DEVICE_A_ID, 10)]
    }

    # 1. Device A initial push
    res1 = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_a)
    assert res1.status_code == 200
    assert res1.json()["acknowledged_sequence"] == 10

    # 2. Device A idempotent retry
    res2 = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_a)
    assert res2.status_code == 200
    assert res2.json() == res1.json()

    # 3. Device B attempts to push with same batch_id -> must NOT return Tenant A's ack
    batch_b = {
        "batch_id": shared_batch_id,
        "tenant_id": TENANT_B,
        "source_device_id": DEVICE_B_ID,
        "source_generation": 1,
        "batch_sequence": 20,
        "sent_at": now_iso,
        "events": [create_iso_event(str(uuid.uuid4()), TENANT_B, BRANCH_B, DEVICE_B_ID, 20)]
    }
    res3 = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_B}, json=batch_b)
    # Must reject with 409 Conflict (or 403) and not leak Tenant A's data
    assert res3.status_code in (403, 409)


def test_sync_updates_device_last_sync_at(clean_client, db_session):
    """Verify that successful catalog pulls and batch pushes update device.last_sync_at."""
    dev_a = db_session.query(Device).filter(Device.device_id == DEVICE_A_ID).first()
    dev_a.last_sync_at = None
    db_session.commit()

    # 1. Catalog pull updates last_sync_at
    res_cat = clean_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": TOKEN_A})
    assert res_cat.status_code == 200

    db_session.expire_all()
    dev_a = db_session.query(Device).filter(Device.device_id == DEVICE_A_ID).first()
    assert dev_a.last_sync_at is not None
    t_after_cat = dev_a.last_sync_at

    # 2. Batch push updates last_sync_at
    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": TENANT_A,
        "source_device_id": DEVICE_A_ID,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res_push = clean_client.post("/api/v1/sync/push", headers={"X-Device-Token": TOKEN_A}, json=batch_payload)
    assert res_push.status_code == 200

    db_session.expire_all()
    dev_a = db_session.query(Device).filter(Device.device_id == DEVICE_A_ID).first()
    assert dev_a.last_sync_at >= t_after_cat


def test_device_heartbeat_updates_state(clean_client, db_session):
    """Verify POST /api/v1/devices/heartbeat updates last_heartbeat_at and status in the database."""
    heartbeat_payload = {
        "device_id": DEVICE_A_ID,
        "token": TOKEN_A,
        "app_version": "1.0.5",
        "status": "ONLINE"
    }
    res = clean_client.post("/api/v1/devices/heartbeat", json=heartbeat_payload)
    assert res.status_code == 200
    assert res.json()["acknowledged"] is True

    db_session.expire_all()
    dev_a = db_session.query(Device).filter(Device.device_id == DEVICE_A_ID).first()
    assert dev_a.last_heartbeat_at is not None
    assert dev_a.status == "ONLINE"
    assert dev_a.app_version == "1.0.5"


def test_device_heartbeat_rejects_deactivated_device(clean_client, device_factory):
    """Verify POST /api/v1/devices/heartbeat rejects deactivated device."""
    deact_token = "tok_heartbeat_deactivated"
    device_factory(
        tenant_id=TENANT_A,
        branch_id=BRANCH_A,
        device_id="dev_hb_deact_01",
        token=deact_token,
        is_active=False
    )
    heartbeat_payload = {
        "device_id": "dev_hb_deact_01",
        "token": deact_token,
        "app_version": "1.0.0",
        "status": "ONLINE"
    }
    res = clean_client.post("/api/v1/devices/heartbeat", json=heartbeat_payload)
    assert res.status_code == 401
    assert "deactivated" in res.json().get("detail", "").lower()

