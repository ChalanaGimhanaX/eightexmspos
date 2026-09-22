"""
Adversarial Challenge & Stress-Test Suite for Milestone 1:
Cloud API Multi-Tenancy & Device Security.

Empirically tests four core adversarial vectors against:
- GET /api/v1/sync/catalog
- POST /api/v1/sync/push

Vectors tested:
1. Forged / empty / malformed / whitespace X-Device-Token headers.
2. Cross-tenant event injection attempts (e.g. valid batch tenant but rogue event tenant or device).
3. Attempting to pull another tenant's catalog data by tampering with headers or query params.
4. Attempting to hijack or overwrite another tenant's batch_id.

All tests utilize unique, dynamic tenant/device/token identifiers to guarantee
100% test isolation even during concurrent database operations.
"""

import uuid
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from pathlib import Path
import sys
import pytest
from fastapi.testclient import TestClient

# Ensure apps/api is in sys.path
repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from src.enightx_api.main import app
from src.enightx_api.database import SessionLocal
from src.enightx_api.models import Device, Product, Category, SyncBatch, SyncEvent


@pytest.fixture
def adv_client():
    """Uninstrumented pristine TestClient for adversarial testing."""
    return TestClient(app)


@pytest.fixture
def adv_env():
    """
    Provisions two isolated dynamic tenants (Tenant Alpha & Tenant Beta)
    with unique tokens, devices, and branches. Automatically tears down
    only the created data after test execution.
    """
    uid = uuid.uuid4().hex[:8]
    tenant_a = f"TENANT_ADV_ALPHA_{uid}"
    tenant_b = f"TENANT_ADV_BETA_{uid}"
    branch_a = f"BRANCH_A_{uid}"
    branch_b = f"BRANCH_B_{uid}"
    dev_a_id = f"dev_adv_a_{uid}"
    dev_b_id = f"dev_adv_b_{uid}"
    token_a = f"tok_adv_a_{uuid.uuid4().hex}"
    token_b = f"tok_adv_b_{uuid.uuid4().hex}"

    db = SessionLocal()
    now = datetime.now(timezone.utc)

    dev_a = Device(
        device_id=dev_a_id,
        tenant_id=tenant_a,
        branch_id=branch_a,
        device_code=f"CTR-A-{uid[:4]}",
        device_name="Counter Alpha Adv",
        hardware_fingerprint=f"hw_{dev_a_id}",
        app_version="1.0.0",
        device_generation=1,
        token=token_a,
        created_at=now,
        is_active=True,
        status="ONLINE"
    )
    dev_b = Device(
        device_id=dev_b_id,
        tenant_id=tenant_b,
        branch_id=branch_b,
        device_code=f"CTR-B-{uid[:4]}",
        device_name="Counter Beta Adv",
        hardware_fingerprint=f"hw_{dev_b_id}",
        app_version="1.0.0",
        device_generation=1,
        token=token_b,
        created_at=now,
        is_active=True,
        status="ONLINE"
    )
    db.add_all([dev_a, dev_b])
    db.commit()
    db.close()

    env_data = {
        "tenant_a": tenant_a,
        "tenant_b": tenant_b,
        "branch_a": branch_a,
        "branch_b": branch_b,
        "dev_a_id": dev_a_id,
        "dev_b_id": dev_b_id,
        "token_a": token_a,
        "token_b": token_b,
    }

    yield env_data

    # Teardown dynamic entities
    clean_db = SessionLocal()
    try:
        clean_db.query(SyncEvent).filter(SyncEvent.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        clean_db.query(SyncBatch).filter(SyncBatch.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        clean_db.query(Product).filter(Product.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        clean_db.query(Category).filter(Category.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        clean_db.query(Device).filter(Device.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        clean_db.commit()
    finally:
        clean_db.close()


def make_adv_event(event_id: str, tenant_id: str, branch_id: str, device_id: str, seq: int = 1, payload_extra: dict = None):
    payload = {
        "sale_id": str(uuid.uuid4()),
        "receipt_number": f"{branch_id}-{device_id}-{seq:06d}",
        "shift_id": str(uuid.uuid4()),
        "customer_id": None,
        "subtotal": "500.00",
        "discount_total": "0.00",
        "tax_total": "90.00",
        "grand_total": "590.00",
        "lines": [
            {
                "line_id": str(uuid.uuid4()),
                "product_id": f"prod_{uuid.uuid4().hex[:6]}",
                "product_name": "Adv Item",
                "barcode": "123456789012",
                "quantity": "1.00",
                "unit_price": "500.00",
                "discount_rate": "0.00",
                "discount_fixed": "0.00",
                "tax_rate": "0.1800",
                "line_total": "590.00"
            }
        ],
        "tenders": [
            {
                "tender_id": str(uuid.uuid4()),
                "tender_type": "CASH",
                "amount_tendered": "600.00",
                "change_given": "10.00",
                "payment_reference": None
            }
        ]
    }
    if payload_extra:
        payload.update(payload_extra)

    return {
        "event_id": event_id,
        "tenant_id": tenant_id,
        "branch_id": branch_id,
        "device_id": device_id,
        "device_generation": 1,
        "source_sequence": seq,
        "schema_version": "1.0",
        "occurred_at": datetime.now(timezone.utc).isoformat(),
        "actor_id": "usr_adv_attacker",
        "causal_reference": None,
        "payload": payload
    }


# ==============================================================================
# VECTOR 1: FORGED / EMPTY / MALFORMED / WHITESPACE X-DEVICE-TOKEN HEADERS
# ==============================================================================

def test_v1_missing_token_header_blocked(adv_client):
    """Omission of X-Device-Token header must return 401 on both sync endpoints."""
    # Catalog GET
    res_cat = adv_client.get("/api/v1/sync/catalog")
    assert res_cat.status_code == 401
    assert "missing" in res_cat.json().get("detail", "").lower()

    # Push POST
    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": "TENANT_DUMMY",
        "source_device_id": "dev_dummy",
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res_push = adv_client.post("/api/v1/sync/push", json=batch_payload)
    assert res_push.status_code == 401
    assert "missing" in res_push.json().get("detail", "").lower()


def test_v1_empty_token_header_blocked(adv_client):
    """Empty string in X-Device-Token header must return 401."""
    res_cat = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": ""})
    assert res_cat.status_code == 401

    res_push = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": ""}, json={})
    assert res_push.status_code == 401


@pytest.mark.parametrize("bad_token", [
    " ",
    "   ",
    "\t",
    "\r\n",
    " \t \n ",
])
def test_v1_whitespace_token_headers_blocked(adv_client, bad_token):
    """Whitespace-only X-Device-Token headers (spaces, tabs, newlines) must return 401."""
    res_cat = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": bad_token})
    assert res_cat.status_code == 401

    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": "TENANT_DUMMY",
        "source_device_id": "dev_dummy",
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res_push = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": bad_token}, json=batch_payload)
    assert res_push.status_code == 401


def test_v1_token_with_surrounding_whitespace_blocked(adv_client, adv_env):
    """Valid token with leading or trailing whitespace must not match and return 401."""
    valid_token = adv_env["token_a"]
    padded_token = f"  {valid_token}  "

    res_cat = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": padded_token})
    assert res_cat.status_code == 401
    assert "invalid or unrecognized" in res_cat.json().get("detail", "").lower()


def test_v1_forged_random_token_blocked(adv_client):
    """Forged token that does not exist in DB must return 401."""
    forged = f"tok_forged_hex_{uuid.uuid4().hex}"
    res_cat = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": forged})
    assert res_cat.status_code == 401
    assert "invalid or unrecognized" in res_cat.json().get("detail", "").lower()


@pytest.mark.parametrize("sqli_payload", [
    "' OR '1'='1",
    "tok_' OR 1=1 --",
    "'; DROP TABLE devices; --",
    "' UNION SELECT null, null, null --",
])
def test_v1_sql_injection_in_token_blocked(adv_client, sqli_payload):
    """SQL injection payloads in X-Device-Token must be treated safely and return 401."""
    res = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": sqli_payload})
    assert res.status_code == 401


def test_v1_oversized_token_header_blocked(adv_client):
    """Extremely long token string (buffer overflow attempt, 10,000 chars) must return 401."""
    long_token = "tok_" + "A" * 10000
    res = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": long_token})
    assert res.status_code == 401


def test_v1_deactivated_device_blocked(adv_client, adv_env):
    """Valid token for an active=False device must return 401 (not 200 or 403)."""
    db = SessionLocal()
    dev = db.query(Device).filter(Device.device_id == adv_env["dev_a_id"]).first()
    dev.is_active = False
    db.commit()
    db.close()

    res_cat = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": adv_env["token_a"]})
    assert res_cat.status_code == 401
    assert "deactivated" in res_cat.json().get("detail", "").lower()


@pytest.mark.parametrize("inactive_status", ["deactivated", "inactive", "DEACTIVATED", "INACTIVE"])
def test_v1_inactive_device_status_blocked(adv_client, adv_env, inactive_status):
    """Valid token for device with status='deactivated' or 'inactive' must return 401."""
    db = SessionLocal()
    dev = db.query(Device).filter(Device.device_id == adv_env["dev_a_id"]).first()
    dev.is_active = True
    dev.status = inactive_status
    db.commit()
    db.close()

    res_cat = adv_client.get("/api/v1/sync/catalog", headers={"X-Device-Token": adv_env["token_a"]})
    assert res_cat.status_code == 401
    assert "deactivated" in res_cat.json().get("detail", "").lower()


# ==============================================================================
# VECTOR 2: CROSS-TENANT EVENT INJECTION ATTEMPTS
# ==============================================================================

def test_v2_batch_tenant_mismatch_blocked(adv_client, adv_env):
    """Device A attempts to push batch claiming to belong to Tenant B -> 403 Forbidden."""
    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": adv_env["tenant_b"],  # Rogue tenant!
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res.status_code == 403
    assert "tenant_id" in res.json().get("detail", "").lower()


def test_v2_batch_device_mismatch_blocked(adv_client, adv_env):
    """Device A attempts to push batch claiming to be Device B -> 403 Forbidden."""
    batch_payload = {
        "batch_id": str(uuid.uuid4()),
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_b_id"],  # Rogue source device!
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": []
    }
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res.status_code == 403
    assert "source_device_id" in res.json().get("detail", "").lower()


def test_v2_event_tenant_injection_blocked_with_zero_persistence(adv_client, adv_env):
    """
    Valid batch header (Tenant A / Dev A), but an event contains Tenant B tenant_id:
    Must return 403 Forbidden and persist ZERO events or batches.
    """
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    rogue_event = make_adv_event(
        event_id=event_id,
        tenant_id=adv_env["tenant_b"],  # Rogue tenant!
        branch_id=adv_env["branch_a"],
        device_id=adv_env["dev_a_id"],
        seq=1
    )
    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [rogue_event]
    }
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res.status_code == 403
    assert "tenant_id" in res.json().get("detail", "").lower()

    # Verify zero database persistence
    db = SessionLocal()
    persisted_batch = db.query(SyncBatch).filter(SyncBatch.batch_id == uuid.UUID(batch_id)).first()
    persisted_event = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_id)).first()
    db.close()
    assert persisted_batch is None, "Rogue batch was persisted to database!"
    assert persisted_event is None, "Rogue event was persisted to database!"


def test_v2_mixed_batch_poisoning_atomic_rejection(adv_client, adv_env):
    """
    A batch containing 3 events:
    - Event 1: Valid for Tenant A
    - Event 2: Poisoned (Tenant B)
    - Event 3: Valid for Tenant A
    Must reject the entire batch with 403 and persist NEITHER Event 1, 2, nor 3.
    """
    batch_id = str(uuid.uuid4())
    ev1_id = str(uuid.uuid4())
    ev2_id = str(uuid.uuid4())
    ev3_id = str(uuid.uuid4())

    ev1 = make_adv_event(ev1_id, adv_env["tenant_a"], adv_env["branch_a"], adv_env["dev_a_id"], 1)
    ev2 = make_adv_event(ev2_id, adv_env["tenant_b"], adv_env["branch_a"], adv_env["dev_a_id"], 2)  # Poison
    ev3 = make_adv_event(ev3_id, adv_env["tenant_a"], adv_env["branch_a"], adv_env["dev_a_id"], 3)

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 3,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [ev1, ev2, ev3]
    }
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res.status_code == 403

    db = SessionLocal()
    persisted_batch = db.query(SyncBatch).filter(SyncBatch.batch_id == uuid.UUID(batch_id)).first()
    persisted_ev1 = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(ev1_id)).first()
    persisted_ev2 = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(ev2_id)).first()
    persisted_ev3 = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(ev3_id)).first()
    db.close()

    assert persisted_batch is None, "Batch should not be created on poisoned event"
    assert persisted_ev1 is None, "Valid event 1 should be rolled back/not inserted"
    assert persisted_ev2 is None, "Poisoned event 2 must not be inserted"
    assert persisted_ev3 is None, "Event 3 must not be inserted"


def test_v2_event_device_mismatch_blocked(adv_client, adv_env):
    """Event device_id does not match authenticated device -> 403 Forbidden."""
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    bad_event = make_adv_event(event_id, adv_env["tenant_a"], adv_env["branch_a"], "dev_spoofed_counter_99", 1)

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [bad_event]
    }
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res.status_code == 403
    assert "device_id" in res.json().get("detail", "").lower()


def test_v2_event_branch_mismatch_blocked(adv_client, adv_env):
    """Event branch_id does not match authenticated branch -> 403 Forbidden."""
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    bad_event = make_adv_event(event_id, adv_env["tenant_a"], "BRANCH_OTHER_FAKE", adv_env["dev_a_id"], 1)

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [bad_event]
    }
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res.status_code == 403
    assert "branch_id" in res.json().get("detail", "").lower()


# ==============================================================================
# VECTOR 3: CATALOG TAMPERING & SNOOPING ATTEMPTS
# ==============================================================================

def test_v3_catalog_query_param_tampering_ignored(adv_client, adv_env):
    """
    Device A passes ?tenant_id=Tenant_B in catalog query:
    The API must ignore the rogue query parameter and strictly scope to Device A's tenant.
    """
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    # Create products for Tenant A and Tenant B
    p_a = Product(
        product_id=f"p_a_{uuid.uuid4().hex[:6]}",
        tenant_id=adv_env["tenant_a"],
        barcode=f"111{uuid.uuid4().hex[:6]}",
        name="Tenant A Secret Product",
        unit_price=Decimal("1500.00"),
        is_active=True,
        updated_at=now
    )
    p_b = Product(
        product_id=f"p_b_{uuid.uuid4().hex[:6]}",
        tenant_id=adv_env["tenant_b"],
        barcode=f"222{uuid.uuid4().hex[:6]}",
        name="Tenant B Classified Product",
        unit_price=Decimal("9999.00"),
        is_active=True,
        updated_at=now
    )
    db.add_all([p_a, p_b])
    db.commit()
    db.close()

    # Query with tenant_id spoofing
    url = f"/api/v1/sync/catalog?tenant_id={adv_env['tenant_b']}"
    res = adv_client.get(url, headers={"X-Device-Token": adv_env["token_a"]})
    assert res.status_code == 200

    catalog = res.json()
    product_names = [p["name"] for p in catalog["products"]]
    assert "Tenant A Secret Product" in product_names
    assert "Tenant B Classified Product" not in product_names, "Cross-tenant catalog leakage detected via query param tampering!"


def test_v3_catalog_header_tampering_ignored(adv_client, adv_env):
    """
    Device A attempts to send X-Tenant-ID: Tenant_B or X-Tenant: Tenant_B header.
    The API must completely ignore foreign tenant headers and scope to the token's tenant.
    """
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    p_b = Product(
        product_id=f"p_b_hdr_{uuid.uuid4().hex[:6]}",
        tenant_id=adv_env["tenant_b"],
        barcode=f"333{uuid.uuid4().hex[:6]}",
        name="Tenant B Proprietary Formula",
        unit_price=Decimal("4999.00"),
        is_active=True,
        updated_at=now
    )
    db.add(p_b)
    db.commit()
    db.close()

    headers = {
        "X-Device-Token": adv_env["token_a"],
        "X-Tenant-ID": adv_env["tenant_b"],
        "X-Tenant": adv_env["tenant_b"],
    }
    res = adv_client.get("/api/v1/sync/catalog", headers=headers)
    assert res.status_code == 200

    product_names = [p["name"] for p in res.json()["products"]]
    assert "Tenant B Proprietary Formula" not in product_names, "Cross-tenant catalog leakage via header spoofing!"


def test_v3_catalog_deleted_item_tombstones_strict_isolation(adv_client, adv_env):
    """
    When fetching deleted item tombstones via ?since=...,
    Tenant A must never receive tombstone IDs belonging to Tenant B.
    """
    t0 = datetime.now(timezone.utc) - timedelta(minutes=10)
    t_del = datetime.now(timezone.utc)

    del_id_a = f"prod_del_a_{uuid.uuid4().hex[:6]}"
    del_id_b = f"prod_del_b_{uuid.uuid4().hex[:6]}"

    db = SessionLocal()
    p_del_a = Product(
        product_id=del_id_a,
        tenant_id=adv_env["tenant_a"],
        barcode="888001",
        name="Deleted Item A",
        unit_price=Decimal("10.00"),
        is_active=False,
        updated_at=t_del,
        deleted_at=t_del
    )
    p_del_b = Product(
        product_id=del_id_b,
        tenant_id=adv_env["tenant_b"],
        barcode="888002",
        name="Deleted Item B",
        unit_price=Decimal("20.00"),
        is_active=False,
        updated_at=t_del,
        deleted_at=t_del
    )
    db.add_all([p_del_a, p_del_b])
    db.commit()
    db.close()

    since_str = t0.isoformat()
    res = adv_client.get(f"/api/v1/sync/catalog?since={since_str}", headers={"X-Device-Token": adv_env["token_a"]})
    assert res.status_code == 200

    deleted_ids = res.json()["deleted_item_ids"]
    assert del_id_a in deleted_ids
    assert del_id_b not in deleted_ids, "Tenant B tombstone leaked to Tenant A catalog sync!"


@pytest.mark.parametrize("bad_since", [
    "2026-01-01' OR 1=1--",
    "not-a-date",
    "2026/99/99",
    "'; DROP TABLE products;--",
    "12345678",
])
def test_v3_catalog_since_sqli_and_malformed_rejected(adv_client, adv_env, bad_since):
    """Malformed or SQL injection attempts in 'since' parameter must return 422 Unprocessable Entity."""
    res = adv_client.get(f"/api/v1/sync/catalog?since={bad_since}", headers={"X-Device-Token": adv_env["token_a"]})
    assert res.status_code == 422
    assert "invalid iso timestamp" in res.json().get("detail", "").lower()


@pytest.mark.parametrize("limit_val", [0, -1, 1001, 99999])
def test_v3_catalog_limit_bounds_enforced(adv_client, adv_env, limit_val):
    """Limit parameter < 1 or > 1000 must be rejected with 422."""
    res = adv_client.get(f"/api/v1/sync/catalog?limit={limit_val}", headers={"X-Device-Token": adv_env["token_a"]})
    assert res.status_code == 422


def test_v3_catalog_negative_offset_rejected(adv_client, adv_env):
    """Offset parameter < 0 must be rejected with 422."""
    res = adv_client.get("/api/v1/sync/catalog?offset=-1", headers={"X-Device-Token": adv_env["token_a"]})
    assert res.status_code == 422


# ==============================================================================
# VECTOR 4: BATCH HIJACKING & OVERWRITE ATTEMPTS (409 CONFLICT)
# ==============================================================================

def test_v4_cross_tenant_batch_id_collision_blocked_with_409(adv_client, adv_env):
    """
    Scenario:
    1. Tenant A pushes batch with batch_id = X.
    2. Tenant B pushes a batch using the exact same batch_id = X.
    Verification:
    - Tenant B must receive HTTP 409 Conflict.
    - Tenant B must NOT receive Tenant A's batch acknowledgment or sequence.
    - In the DB, Tenant A's batch must remain completely intact and uncorrupted.
    """
    shared_batch_id = str(uuid.uuid4())
    event_a_id = str(uuid.uuid4())

    batch_a = {
        "batch_id": shared_batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 42,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [make_adv_event(event_a_id, adv_env["tenant_a"], adv_env["branch_a"], adv_env["dev_a_id"], 42)]
    }

    # Tenant A initial push
    res_a = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_a)
    assert res_a.status_code == 200
    assert res_a.json()["acknowledged_sequence"] == 42

    # Tenant B tries to hijack the batch_id
    event_b_id = str(uuid.uuid4())
    batch_b = {
        "batch_id": shared_batch_id,  # Same batch_id!
        "tenant_id": adv_env["tenant_b"],
        "source_device_id": adv_env["dev_b_id"],
        "source_generation": 1,
        "batch_sequence": 99,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [make_adv_event(event_b_id, adv_env["tenant_b"], adv_env["branch_b"], adv_env["dev_b_id"], 99)]
    }

    res_b = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_b"]}, json=batch_b)
    # Must be 409 Conflict
    assert res_b.status_code == 409
    assert "collision" in res_b.json().get("detail", "").lower()

    # Empirical DB state verification
    db = SessionLocal()
    persisted_batch = db.query(SyncBatch).filter(SyncBatch.batch_id == uuid.UUID(shared_batch_id)).first()
    assert persisted_batch is not None
    assert persisted_batch.tenant_id == adv_env["tenant_a"], "Tenant B corrupted Tenant A batch tenant_id!"
    assert persisted_batch.source_device_id == adv_env["dev_a_id"]
    assert persisted_batch.batch_sequence == 42
    assert persisted_batch.acknowledged_sequence == 42

    # Verify Tenant B's event was NOT inserted
    persisted_b_event = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_b_id)).first()
    assert persisted_b_event is None, "Tenant B event was committed despite 409 conflict!"
    db.close()


def test_v4_same_tenant_batch_idempotency_succeeds(adv_client, adv_env):
    """
    Scenario:
    Device A retries the exact same batch_id within the same tenant.
    Verification:
    - Must return 200 OK with identical acknowledgment.
    """
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 7,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [make_adv_event(event_id, adv_env["tenant_a"], adv_env["branch_a"], adv_env["dev_a_id"], 7)]
    }

    res1 = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res1.status_code == 200
    ack1 = res1.json()

    res2 = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_payload)
    assert res2.status_code == 200
    ack2 = res2.json()

    assert ack1 == ack2
    assert ack2["batch_id"] == batch_id
    assert ack2["acknowledged_sequence"] == 7


def test_v4_cross_tenant_event_tampering_immutability(adv_client, adv_env):
    """
    Scenario:
    1. Tenant A pushes an event EV_X with payload {"subtotal": "500.00"}.
    2. Tenant B pushes a new batch with a DIFFERENT batch_id, but reusing event_id = EV_X
       with malicious payload {"subtotal": "0.01"}.
    Verification:
    - In PostgreSQL, EV_X must remain strictly owned by Tenant A with the original payload.
    """
    shared_event_id = str(uuid.uuid4())
    batch_a_id = str(uuid.uuid4())
    batch_b_id = str(uuid.uuid4())

    ev_a = make_adv_event(
        event_id=shared_event_id,
        tenant_id=adv_env["tenant_a"],
        branch_id=adv_env["branch_a"],
        device_id=adv_env["dev_a_id"],
        seq=1,
        payload_extra={"subtotal": "500.00", "tamper_marker": "authentic_tenant_a"}
    )
    batch_a = {
        "batch_id": batch_a_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [ev_a]
    }
    res_a = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_a"]}, json=batch_a)
    assert res_a.status_code == 200

    # Tenant B tries to overwrite EV_X
    ev_b = make_adv_event(
        event_id=shared_event_id,
        tenant_id=adv_env["tenant_b"],
        branch_id=adv_env["branch_b"],
        device_id=adv_env["dev_b_id"],
        seq=1,
        payload_extra={"subtotal": "0.01", "tamper_marker": "malicious_overwrite_tenant_b"}
    )
    batch_b = {
        "batch_id": batch_b_id,
        "tenant_id": adv_env["tenant_b"],
        "source_device_id": adv_env["dev_b_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [ev_b]
    }
    adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_b"]}, json=batch_b)

    # Verify event in DB was NOT overwritten
    db = SessionLocal()
    persisted_ev = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(shared_event_id)).first()
    assert persisted_ev is not None
    assert persisted_ev.tenant_id == adv_env["tenant_a"], "Tenant B hijacked Tenant A event tenant_id!"
    assert persisted_ev.payload["tamper_marker"] == "authentic_tenant_a", "Tenant B overwrote Tenant A event payload!"
    assert persisted_ev.payload["subtotal"] == "500.00"
    db.close()


def test_v4_cross_tenant_batch_replay_rejected_403(adv_client, adv_env):
    """
    Scenario:
    Tenant B intercepts Tenant A's batch payload and attempts to replay it verbatim
    using Tenant B's credentials.
    Verification:
    - Must return 403 Forbidden because batch.tenant_id and source_device_id mismatch Device B.
    """
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    batch_a_verbatim = {
        "batch_id": batch_id,
        "tenant_id": adv_env["tenant_a"],
        "source_device_id": adv_env["dev_a_id"],
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": datetime.now(timezone.utc).isoformat(),
        "events": [make_adv_event(event_id, adv_env["tenant_a"], adv_env["branch_a"], adv_env["dev_a_id"], 1)]
    }

    # Sent with Token B
    res = adv_client.post("/api/v1/sync/push", headers={"X-Device-Token": adv_env["token_b"]}, json=batch_a_verbatim)
    assert res.status_code == 403
    assert "tenant_id" in res.json().get("detail", "").lower()
