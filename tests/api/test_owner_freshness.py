"""
Test suite for Owner Web Freshness Endpoint (Milestone 4 / Feature 25 & 26).
Verifies:
- Device with recent heartbeat (<5 min) returns ONLINE
- Device with heartbeat 10 min ago returns STALE
- Device with heartbeat 48 hr ago returns OFFLINE
- Device never communicated (no heartbeat/sync) returns OFFLINE with None sync latency
- Device with recent sync (<5 min) and old heartbeat returns ONLINE
- Branch status aggregation (ONLINE if any online, STALE if any stale, OFFLINE if all offline)
- Strict tenant isolation (devices/branches of other tenants not returned)
- Token validation (200 on valid, 403 on cross-tenant, 401 on invalid, 401 on deactivated, 200 on omitted)
- Querying a tenant with no devices returns empty summary
"""

import sys
import uuid
from datetime import datetime, timezone, timedelta
from pathlib import Path
from unittest.mock import patch
import pytest
from fastapi.testclient import TestClient

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from src.enightx_api.main import app
from src.enightx_api.database import SessionLocal
from src.enightx_api.models import Device

# Unique tenant prefixes per test to prevent cross-test interference
TENANT_TEST_ONLINE = "TENANT_OWNER_ONLINE"
TENANT_TEST_STALE = "TENANT_OWNER_STALE"
TENANT_TEST_OFFLINE = "TENANT_OWNER_OFFLINE"
TENANT_TEST_NEVER = "TENANT_OWNER_NEVER"
TENANT_TEST_SYNC_REC = "TENANT_OWNER_SYNC_REC"
TENANT_TEST_AGG = "TENANT_OWNER_AGG"
TENANT_TEST_ISO_A = "TENANT_OWNER_ISO_A"
TENANT_TEST_ISO_B = "TENANT_OWNER_ISO_B"
TENANT_TEST_AUTH_A = "TENANT_OWNER_AUTH_A"
TENANT_TEST_AUTH_B = "TENANT_OWNER_AUTH_B"
TENANT_TEST_DEACT = "TENANT_OWNER_DEACT"
TENANT_TEST_EMPTY = "TENANT_OWNER_EMPTY"


def cleanup_all_owner_test_devices(db):
    db.query(Device).filter(Device.tenant_id.like("TENANT_OWNER_%")).delete(synchronize_session=False)
    db.commit()


@pytest.fixture(autouse=True)
def clean_test_tenants():
    db = SessionLocal()
    try:
        cleanup_all_owner_test_devices(db)
    finally:
        db.close()

    yield

    db = SessionLocal()
    try:
        cleanup_all_owner_test_devices(db)
    finally:
        db.close()


@pytest.fixture
def client():
    return TestClient(app)


def test_owner_freshness_device_online(client):
    """Device with heartbeat 2 minutes ago must be classified as ONLINE."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev = Device(
            device_id="dev_fresh_online_01",
            tenant_id=TENANT_TEST_ONLINE,
            branch_id="branch_a01",
            device_code="CTR-01",
            device_name="Counter 1",
            hardware_fingerprint="hw_online_1",
            app_version="1.0.0",
            device_generation=1,
            token="tok_fresh_online_1",
            is_active=True,
            status="ONLINE",
            last_heartbeat_at=now - timedelta(minutes=2),
            last_sync_at=now - timedelta(minutes=3)
        )
        db.add(dev)
        db.commit()
    finally:
        db.close()

    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_ONLINE}")
    assert res.status_code == 200
    data = res.json()

    assert data["tenant_id"] == TENANT_TEST_ONLINE
    assert data["total_devices"] == 1
    assert data["online_devices_count"] == 1
    assert data["stale_devices_count"] == 0
    assert data["offline_devices_count"] == 0

    assert len(data["branches"]) == 1
    branch = data["branches"][0]
    assert branch["branch_id"] == "branch_a01"
    assert branch["status"] == "ONLINE"
    assert len(branch["devices"]) == 1
    d_item = branch["devices"][0]
    assert d_item["device_id"] == "dev_fresh_online_01"
    assert d_item["status"] == "ONLINE"
    assert d_item["sync_latency_seconds"] is not None
    assert d_item["sync_latency_seconds"] >= 170.0


def test_owner_freshness_device_stale(client):
    """Device with heartbeat 10 minutes ago must be classified as STALE."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev = Device(
            device_id="dev_fresh_stale_01",
            tenant_id=TENANT_TEST_STALE,
            branch_id="branch_a01",
            device_code="CTR-02",
            device_name="Counter 2",
            hardware_fingerprint="hw_stale_1",
            app_version="1.0.0",
            device_generation=1,
            token="tok_fresh_stale_1",
            is_active=True,
            status="ONLINE",
            last_heartbeat_at=now - timedelta(minutes=10),
            last_sync_at=now - timedelta(minutes=15)
        )
        db.add(dev)
        db.commit()
    finally:
        db.close()

    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_STALE}")
    assert res.status_code == 200
    data = res.json()

    assert data["total_devices"] == 1
    assert data["online_devices_count"] == 0
    assert data["stale_devices_count"] == 1
    assert data["offline_devices_count"] == 0

    branch = data["branches"][0]
    assert branch["status"] == "STALE"
    assert branch["devices"][0]["status"] == "STALE"


def test_owner_freshness_device_offline_48h(client):
    """Device with heartbeat 48 hours ago must be classified as OFFLINE."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev = Device(
            device_id="dev_fresh_offline_01",
            tenant_id=TENANT_TEST_OFFLINE,
            branch_id="branch_a01",
            device_code="CTR-03",
            device_name="Counter 3",
            hardware_fingerprint="hw_off_1",
            app_version="1.0.0",
            device_generation=1,
            token="tok_fresh_offline_1",
            is_active=True,
            status="ONLINE",
            last_heartbeat_at=now - timedelta(hours=48),
            last_sync_at=now - timedelta(hours=50)
        )
        db.add(dev)
        db.commit()
    finally:
        db.close()

    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_OFFLINE}")
    assert res.status_code == 200
    data = res.json()

    assert data["total_devices"] == 1
    assert data["online_devices_count"] == 0
    assert data["stale_devices_count"] == 0
    assert data["offline_devices_count"] == 1

    branch = data["branches"][0]
    assert branch["status"] == "OFFLINE"
    assert branch["devices"][0]["status"] == "OFFLINE"


def test_owner_freshness_never_communicated_offline(client):
    """Device with no heartbeat and no sync timestamp must return OFFLINE with None sync latency."""
    db = SessionLocal()
    try:
        dev = Device(
            device_id="dev_fresh_never_01",
            tenant_id=TENANT_TEST_NEVER,
            branch_id="branch_a01",
            device_code="CTR-04",
            device_name="Counter 4",
            hardware_fingerprint="hw_never_1",
            app_version="1.0.0",
            device_generation=1,
            token="tok_fresh_never_1",
            is_active=True,
            status="ONLINE",
            last_heartbeat_at=None,
            last_sync_at=None
        )
        db.add(dev)
        db.commit()
    finally:
        db.close()

    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_NEVER}")
    assert res.status_code == 200
    data = res.json()

    assert data["total_devices"] == 1
    assert data["offline_devices_count"] == 1
    d_item = data["branches"][0]["devices"][0]
    assert d_item["status"] == "OFFLINE"
    assert d_item["last_heartbeat_at"] is None
    assert d_item["last_sync_at"] is None
    assert d_item["sync_latency_seconds"] is None


def test_owner_freshness_recent_sync_overrides_stale_heartbeat(client):
    """If heartbeat is 12h ago but sync is 1m ago, device is classified as ONLINE."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev = Device(
            device_id="dev_fresh_sync_recent",
            tenant_id=TENANT_TEST_SYNC_REC,
            branch_id="branch_a01",
            device_code="CTR-05",
            device_name="Counter 5",
            hardware_fingerprint="hw_sync_rec",
            app_version="1.0.0",
            device_generation=1,
            token="tok_fresh_sync_rec",
            is_active=True,
            status="ONLINE",
            last_heartbeat_at=now - timedelta(hours=12),
            last_sync_at=now - timedelta(minutes=1)
        )
        db.add(dev)
        db.commit()
    finally:
        db.close()

    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_SYNC_REC}")
    assert res.status_code == 200
    data = res.json()
    assert data["online_devices_count"] == 1
    assert data["branches"][0]["devices"][0]["status"] == "ONLINE"


def test_owner_freshness_branch_aggregation(client):
    """
    Branch A: 1 ONLINE, 1 OFFLINE -> branch is ONLINE
    Branch B: 1 STALE, 1 OFFLINE -> branch is STALE
    Branch C: 1 OFFLINE -> branch is OFFLINE
    """
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        d1 = Device(
            device_id="dev_agg_01", tenant_id=TENANT_TEST_AGG, branch_id="b_alpha",
            device_code="C1", device_name="Counter 1", hardware_fingerprint="hw1",
            app_version="1.0.0", token="tok_agg_1",
            last_heartbeat_at=now - timedelta(minutes=1)
        )
        d2 = Device(
            device_id="dev_agg_02", tenant_id=TENANT_TEST_AGG, branch_id="b_alpha",
            device_code="C2", device_name="Counter 2", hardware_fingerprint="hw2",
            app_version="1.0.0", token="tok_agg_2",
            last_heartbeat_at=now - timedelta(hours=40)
        )
        d3 = Device(
            device_id="dev_agg_03", tenant_id=TENANT_TEST_AGG, branch_id="b_beta",
            device_code="C3", device_name="Counter 3", hardware_fingerprint="hw3",
            app_version="1.0.0", token="tok_agg_3",
            last_heartbeat_at=now - timedelta(minutes=20)
        )
        d4 = Device(
            device_id="dev_agg_04", tenant_id=TENANT_TEST_AGG, branch_id="b_beta",
            device_code="C4", device_name="Counter 4", hardware_fingerprint="hw4",
            app_version="1.0.0", token="tok_agg_4",
            last_heartbeat_at=now - timedelta(hours=50)
        )
        d5 = Device(
            device_id="dev_agg_05", tenant_id=TENANT_TEST_AGG, branch_id="b_gamma",
            device_code="C5", device_name="Counter 5", hardware_fingerprint="hw5",
            app_version="1.0.0", token="tok_agg_5",
            last_heartbeat_at=now - timedelta(hours=72)
        )
        db.add_all([d1, d2, d3, d4, d5])
        db.commit()
    finally:
        db.close()

    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_AGG}")
    assert res.status_code == 200
    data = res.json()

    assert data["total_devices"] == 5
    assert data["online_devices_count"] == 1
    assert data["stale_devices_count"] == 1
    assert data["offline_devices_count"] == 3

    branch_map = {b["branch_id"]: b["status"] for b in data["branches"]}
    assert branch_map["b_alpha"] == "ONLINE"
    assert branch_map["b_beta"] == "STALE"
    assert branch_map["b_gamma"] == "OFFLINE"


def test_owner_freshness_tenant_isolation(client):
    """Ensure Tenant A never sees Tenant B's branches or devices."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev_a = Device(
            device_id="dev_iso_tenant_a", tenant_id=TENANT_TEST_ISO_A, branch_id="branch_iso_a",
            device_code="CA", device_name="Counter A", hardware_fingerprint="hwa",
            app_version="1.0.0", token="tok_iso_a",
            last_heartbeat_at=now - timedelta(minutes=1)
        )
        dev_b = Device(
            device_id="dev_iso_tenant_b", tenant_id=TENANT_TEST_ISO_B, branch_id="branch_iso_b",
            device_code="CB", device_name="Counter B", hardware_fingerprint="hwb",
            app_version="1.0.0", token="tok_iso_b",
            last_heartbeat_at=now - timedelta(minutes=1)
        )
        db.add_all([dev_a, dev_b])
        db.commit()
    finally:
        db.close()

    # Query Tenant A
    res_a = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_ISO_A}")
    assert res_a.status_code == 200
    data_a = res_a.json()
    assert data_a["total_devices"] == 1
    assert data_a["branches"][0]["branch_id"] == "branch_iso_a"
    assert data_a["branches"][0]["devices"][0]["device_id"] == "dev_iso_tenant_a"

    # Query Tenant B
    res_b = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_ISO_B}")
    assert res_b.status_code == 200
    data_b = res_b.json()
    assert data_b["total_devices"] == 1
    assert data_b["branches"][0]["branch_id"] == "branch_iso_b"
    assert data_b["branches"][0]["devices"][0]["device_id"] == "dev_iso_tenant_b"


def test_owner_freshness_token_validation(client):
    """Verify optional X-Device-Token authentication behavior."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev_a = Device(
            device_id="dev_auth_a", tenant_id=TENANT_TEST_AUTH_A, branch_id="b_auth",
            device_code="CA", device_name="Counter A", hardware_fingerprint="hwa",
            app_version="1.0.0", token="tok_valid_tenant_a",
            last_heartbeat_at=now
        )
        db.add(dev_a)
        db.commit()
    finally:
        db.close()

    # 1. Valid matching token -> 200
    res1 = client.get(
        f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_AUTH_A}",
        headers={"X-Device-Token": "tok_valid_tenant_a"}
    )
    assert res1.status_code == 200

    # 2. Token from another tenant -> 403 Forbidden
    res2 = client.get(
        f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_AUTH_B}",
        headers={"X-Device-Token": "tok_valid_tenant_a"}
    )
    assert res2.status_code == 403

    # 3. Invalid token -> 401 Unauthorized
    res3 = client.get(
        f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_AUTH_A}",
        headers={"X-Device-Token": "tok_nonexistent"}
    )
    assert res3.status_code == 401

    # 4. No token -> 200 (allowed for owner dashboard query)
    res4 = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_AUTH_A}")
    assert res4.status_code == 200


def test_owner_freshness_deactivated_token(client):
    """Deactivated device token returns 401 Unauthorized."""
    now = datetime.now(timezone.utc)
    db = SessionLocal()
    try:
        dev = Device(
            device_id="dev_deact_01", tenant_id=TENANT_TEST_DEACT, branch_id="b_deact",
            device_code="CD", device_name="Counter D", hardware_fingerprint="hw_deact",
            app_version="1.0.0", token="tok_deactivated",
            is_active=False,
            last_heartbeat_at=now
        )
        db.add(dev)
        db.commit()
    finally:
        db.close()

    res = client.get(
        f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_DEACT}",
        headers={"X-Device-Token": "tok_deactivated"}
    )
    assert res.status_code == 401


def test_owner_freshness_empty_tenant(client):
    """Querying a tenant with no registered devices returns empty summary."""
    res = client.get(f"/api/v1/owner/freshness?tenant_id={TENANT_TEST_EMPTY}")
    assert res.status_code == 200
    data = res.json()
    assert data["tenant_id"] == TENANT_TEST_EMPTY
    assert data["total_devices"] == 0
    assert data["online_devices_count"] == 0
    assert data["stale_devices_count"] == 0
    assert data["offline_devices_count"] == 0
    assert data["branches"] == []


# ==============================================================================
# ADVERSARIAL STRESS TESTS (Milestone 4 - Features 25 & 26)
# ==============================================================================

def test_owner_freshness_boundary_exact_thresholds(client):
    """
    Stress test exact temporal boundaries:
    - 299s: ONLINE  (< 300s)
    - 300s: STALE   (== 300s, not < 300s, <= 86400s)
    - 301s: STALE   (> 300s, <= 86400s)
    - 86400s: STALE (== 86400s, <= 86400s)
    - 86401s: OFFLINE (> 86400s)
    """
    tenant = f"TENANT_OWNER_BND_{uuid.uuid4().hex[:8]}"
    fixed_now = datetime(2026, 9, 22, 12, 0, 0, tzinfo=timezone.utc)

    db = SessionLocal()
    try:
        dev_299 = Device(
            device_id=f"dev_bnd_299_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_299",
            device_code="C299", device_name="Counter 299s", hardware_fingerprint="hw_299",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=299), last_sync_at=None
        )
        dev_300 = Device(
            device_id=f"dev_bnd_300_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_300",
            device_code="C300", device_name="Counter 300s", hardware_fingerprint="hw_300",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=300), last_sync_at=None
        )
        dev_301 = Device(
            device_id=f"dev_bnd_301_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_301",
            device_code="C301", device_name="Counter 301s", hardware_fingerprint="hw_301",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=301), last_sync_at=None
        )
        dev_86400 = Device(
            device_id=f"dev_bnd_86400_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_86400",
            device_code="C86400", device_name="Counter 86400s", hardware_fingerprint="hw_86400",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=86400), last_sync_at=None
        )
        dev_86401 = Device(
            device_id=f"dev_bnd_86401_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_86401",
            device_code="C86401", device_name="Counter 86401s", hardware_fingerprint="hw_86401",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=86401), last_sync_at=None
        )
        db.add_all([dev_299, dev_300, dev_301, dev_86400, dev_86401])
        db.commit()
    finally:
        db.close()

    try:
        with patch("src.enightx_api.routers.owner.datetime") as mock_dt:
            mock_dt.now.return_value = fixed_now
            res = client.get(f"/api/v1/owner/freshness?tenant_id={tenant}")
            assert res.status_code == 200
            data = res.json()

        assert data["total_devices"] == 5
        assert data["online_devices_count"] == 1
        assert data["stale_devices_count"] == 3
        assert data["offline_devices_count"] == 1

        b_map = {b["branch_id"]: b["devices"][0]["status"] for b in data["branches"]}
        assert b_map["b_299"] == "ONLINE"
        assert b_map["b_300"] == "STALE"
        assert b_map["b_301"] == "STALE"
        assert b_map["b_86400"] == "STALE"
        assert b_map["b_86401"] == "OFFLINE"
    finally:
        db = SessionLocal()
        db.query(Device).filter(Device.tenant_id == tenant).delete(synchronize_session=False)
        db.commit()
        db.close()


def test_owner_freshness_null_and_asymmetric_timestamps(client):
    """
    Test edge cases with null and asymmetric timestamps:
    1. Null heartbeat AND null sync -> OFFLINE, sync_latency_seconds is None.
    2. Null heartbeat, recent sync (2m ago) -> ONLINE, sync_latency_seconds ~ 120s.
    3. Recent heartbeat (2m ago), null sync -> ONLINE, sync_latency_seconds is None.
    4. Old heartbeat (30h ago), recent sync (1m ago) -> ONLINE, sync_latency_seconds ~ 60s.
    5. Recent heartbeat (1m ago), old sync (30h ago) -> ONLINE, sync_latency_seconds ~ 108000s.
    """
    tenant = f"TENANT_OWNER_NULL_{uuid.uuid4().hex[:8]}"
    fixed_now = datetime(2026, 9, 22, 12, 0, 0, tzinfo=timezone.utc)

    db = SessionLocal()
    try:
        dev_both_null = Device(
            device_id=f"dev_null_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_null",
            device_code="C_N", device_name="Never Connected", hardware_fingerprint="hw_null",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=None, last_sync_at=None
        )
        dev_sync_only = Device(
            device_id=f"dev_sync_only_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_sync_only",
            device_code="C_SO", device_name="Sync Only", hardware_fingerprint="hw_sync_only",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=None, last_sync_at=fixed_now - timedelta(seconds=120)
        )
        dev_hb_only = Device(
            device_id=f"dev_hb_only_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_hb_only",
            device_code="C_HO", device_name="Heartbeat Only", hardware_fingerprint="hw_hb_only",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=120), last_sync_at=None
        )
        dev_old_hb_new_sync = Device(
            device_id=f"dev_oh_ns_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_oh_ns",
            device_code="C_OHNS", device_name="Old HB New Sync", hardware_fingerprint="hw_ohns",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(hours=30), last_sync_at=fixed_now - timedelta(seconds=60)
        )
        dev_new_hb_old_sync = Device(
            device_id=f"dev_nh_os_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="b_nh_os",
            device_code="C_NHOS", device_name="New HB Old Sync", hardware_fingerprint="hw_nhos",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=60), last_sync_at=fixed_now - timedelta(hours=30)
        )
        db.add_all([dev_both_null, dev_sync_only, dev_hb_only, dev_old_hb_new_sync, dev_new_hb_old_sync])
        db.commit()
    finally:
        db.close()

    try:
        with patch("src.enightx_api.routers.owner.datetime") as mock_dt:
            mock_dt.now.return_value = fixed_now
            res = client.get(f"/api/v1/owner/freshness?tenant_id={tenant}")
            assert res.status_code == 200
            data = res.json()

        b_map = {b["branch_id"]: b["devices"][0] for b in data["branches"]}

        # 1. Both null
        d1 = b_map["b_null"]
        assert d1["status"] == "OFFLINE"
        assert d1["sync_latency_seconds"] is None

        # 2. Sync only
        d2 = b_map["b_sync_only"]
        assert d2["status"] == "ONLINE"
        assert d2["sync_latency_seconds"] == 120.0

        # 3. Heartbeat only
        d3 = b_map["b_hb_only"]
        assert d3["status"] == "ONLINE"
        assert d3["sync_latency_seconds"] is None

        # 4. Old HB, new Sync
        d4 = b_map["b_oh_ns"]
        assert d4["status"] == "ONLINE"
        assert d4["sync_latency_seconds"] == 60.0

        # 5. New HB, old Sync
        d5 = b_map["b_nh_os"]
        assert d5["status"] == "ONLINE"
        assert d5["sync_latency_seconds"] == 108000.0
    finally:
        db = SessionLocal()
        db.query(Device).filter(Device.tenant_id == tenant).delete(synchronize_session=False)
        db.commit()
        db.close()


def test_owner_freshness_branch_mixed_status_rules(client):
    """
    Branch status aggregation rules with mixed devices:
    - Branch 1: [ONLINE, STALE, OFFLINE] -> ONLINE
    - Branch 2: [STALE, OFFLINE] -> STALE
    - Branch 3: [OFFLINE, OFFLINE] -> OFFLINE
    """
    tenant = f"TENANT_OWNER_MIX_{uuid.uuid4().hex[:8]}"
    fixed_now = datetime(2026, 9, 22, 12, 0, 0, tzinfo=timezone.utc)

    db = SessionLocal()
    try:
        # Branch 1: Mixed all three
        d1_on = Device(
            device_id=f"d1_on_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_1",
            device_code="C1", device_name="Dev On", hardware_fingerprint="hw_m1",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(seconds=60)
        )
        d1_st = Device(
            device_id=f"d1_st_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_1",
            device_code="C2", device_name="Dev St", hardware_fingerprint="hw_m2",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(minutes=10)
        )
        d1_off = Device(
            device_id=f"d1_off_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_1",
            device_code="C3", device_name="Dev Off", hardware_fingerprint="hw_m3",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(hours=48)
        )

        # Branch 2: Stale + Offline
        d2_st = Device(
            device_id=f"d2_st_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_2",
            device_code="C4", device_name="Dev St2", hardware_fingerprint="hw_m4",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(minutes=15)
        )
        d2_off = Device(
            device_id=f"d2_off_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_2",
            device_code="C5", device_name="Dev Off2", hardware_fingerprint="hw_m5",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(hours=36)
        )

        # Branch 3: Offline + Offline
        d3_off1 = Device(
            device_id=f"d3_off1_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_3",
            device_code="C6", device_name="Dev Off3", hardware_fingerprint="hw_m6",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=fixed_now - timedelta(hours=40)
        )
        d3_off2 = Device(
            device_id=f"d3_off2_{uuid.uuid4().hex[:6]}", tenant_id=tenant, branch_id="branch_mix_3",
            device_code="C7", device_name="Dev Off4", hardware_fingerprint="hw_m7",
            app_version="1.0.0", token=f"tok_{uuid.uuid4().hex[:8]}", is_active=True, status="ONLINE",
            last_heartbeat_at=None
        )

        db.add_all([d1_on, d1_st, d1_off, d2_st, d2_off, d3_off1, d3_off2])
        db.commit()
    finally:
        db.close()

    try:
        with patch("src.enightx_api.routers.owner.datetime") as mock_dt:
            mock_dt.now.return_value = fixed_now
            res = client.get(f"/api/v1/owner/freshness?tenant_id={tenant}")
            assert res.status_code == 200
            data = res.json()

        assert data["total_devices"] == 7
        assert data["online_devices_count"] == 1
        assert data["stale_devices_count"] == 2
        assert data["offline_devices_count"] == 4

        b_map = {b["branch_id"]: b["status"] for b in data["branches"]}
        assert b_map["branch_mix_1"] == "ONLINE"
        assert b_map["branch_mix_2"] == "STALE"
        assert b_map["branch_mix_3"] == "OFFLINE"
    finally:
        db = SessionLocal()
        db.query(Device).filter(Device.tenant_id == tenant).delete(synchronize_session=False)
        db.commit()
        db.close()


def test_owner_freshness_cross_tenant_strict_isolation_multi_branch(client):
    """
    Adversarial cross-tenant isolation:
    Provision multi-branch setups for Tenant Alpha and Tenant Beta.
    Verify:
    1. Tenant Alpha sees ONLY Tenant Alpha branches and devices.
    2. Tenant Beta sees ONLY Tenant Beta branches and devices.
    3. Querying with SQL injection tenant_id parameter returns 0 devices.
    """
    uid = uuid.uuid4().hex[:8]
    tenant_a = f"TENANT_OWNER_ISO_ALPHA_{uid}"
    tenant_b = f"TENANT_OWNER_ISO_BETA_{uid}"
    now = datetime.now(timezone.utc)

    db = SessionLocal()
    try:
        # Tenant A: 2 branches, 3 devices
        d_a1 = Device(
            device_id=f"dev_a1_{uid}", tenant_id=tenant_a, branch_id="b_alpha_1",
            device_code="CA1", device_name="A Counter 1", hardware_fingerprint=f"hw_a1_{uid}",
            app_version="1.0.0", token=f"tok_a1_{uid}", is_active=True, status="ONLINE",
            last_heartbeat_at=now
        )
        d_a2 = Device(
            device_id=f"dev_a2_{uid}", tenant_id=tenant_a, branch_id="b_alpha_2",
            device_code="CA2", device_name="A Counter 2", hardware_fingerprint=f"hw_a2_{uid}",
            app_version="1.0.0", token=f"tok_a2_{uid}", is_active=True, status="ONLINE",
            last_heartbeat_at=now
        )

        # Tenant B: 2 branches, 2 devices
        d_b1 = Device(
            device_id=f"dev_b1_{uid}", tenant_id=tenant_b, branch_id="b_beta_1",
            device_code="CB1", device_name="B Counter 1", hardware_fingerprint=f"hw_b1_{uid}",
            app_version="1.0.0", token=f"tok_b1_{uid}", is_active=True, status="ONLINE",
            last_heartbeat_at=now
        )
        d_b2 = Device(
            device_id=f"dev_b2_{uid}", tenant_id=tenant_b, branch_id="b_beta_2",
            device_code="CB2", device_name="B Counter 2", hardware_fingerprint=f"hw_b2_{uid}",
            app_version="1.0.0", token=f"tok_b2_{uid}", is_active=True, status="ONLINE",
            last_heartbeat_at=now
        )

        db.add_all([d_a1, d_a2, d_b1, d_b2])
        db.commit()
    finally:
        db.close()

    try:
        # 1. Query Tenant A
        res_a = client.get(f"/api/v1/owner/freshness?tenant_id={tenant_a}")
        assert res_a.status_code == 200
        data_a = res_a.json()
        assert data_a["total_devices"] == 2
        a_branch_ids = {b["branch_id"] for b in data_a["branches"]}
        a_device_ids = {d["device_id"] for b in data_a["branches"] for d in b["devices"]}
        assert a_branch_ids == {"b_alpha_1", "b_alpha_2"}
        assert a_device_ids == {f"dev_a1_{uid}", f"dev_a2_{uid}"}

        # 2. Query Tenant B
        res_b = client.get(f"/api/v1/owner/freshness?tenant_id={tenant_b}")
        assert res_b.status_code == 200
        data_b = res_b.json()
        assert data_b["total_devices"] == 2
        b_branch_ids = {b["branch_id"] for b in data_b["branches"]}
        b_device_ids = {d["device_id"] for b in data_b["branches"] for d in b["devices"]}
        assert b_branch_ids == {"b_beta_1", "b_beta_2"}
        assert b_device_ids == {f"dev_b1_{uid}", f"dev_b2_{uid}"}

        # Zero leakage: disjoint intersection
        assert a_branch_ids.isdisjoint(b_branch_ids)
        assert a_device_ids.isdisjoint(b_device_ids)

        # 3. SQL injection attempt in tenant_id query param
        res_sqli = client.get("/api/v1/owner/freshness?tenant_id=' OR '1'='1")
        assert res_sqli.status_code == 200
        data_sqli = res_sqli.json()
        assert data_sqli["total_devices"] == 0
        assert len(data_sqli["branches"]) == 0
    finally:
        db = SessionLocal()
        db.query(Device).filter(Device.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        db.commit()
        db.close()


def test_owner_freshness_security_tokens(client):
    """
    Security verification on X-Device-Token:
    1. Tampered / nonexistent token -> 401 Unauthorized
    2. Whitespace token -> 401 Unauthorized
    3. Cross-tenant token -> 403 Forbidden
    4. Deactivated device token -> 401 Unauthorized
    5. Omitted token -> 200 OK
    """
    uid = uuid.uuid4().hex[:8]
    tenant_a = f"TENANT_OWNER_SEC_A_{uid}"
    tenant_b = f"TENANT_OWNER_SEC_B_{uid}"
    tok_a = f"tok_sec_a_{uid}"
    tok_b = f"tok_sec_b_{uid}"
    tok_deact = f"tok_sec_deact_{uid}"
    now = datetime.now(timezone.utc)

    db = SessionLocal()
    try:
        dev_a = Device(
            device_id=f"dev_sec_a_{uid}", tenant_id=tenant_a, branch_id="b_sec_a",
            device_code="CA", device_name="Counter Sec A", hardware_fingerprint=f"hw_sec_a_{uid}",
            app_version="1.0.0", token=tok_a, is_active=True, status="ONLINE",
            last_heartbeat_at=now
        )
        dev_b = Device(
            device_id=f"dev_sec_b_{uid}", tenant_id=tenant_b, branch_id="b_sec_b",
            device_code="CB", device_name="Counter Sec B", hardware_fingerprint=f"hw_sec_b_{uid}",
            app_version="1.0.0", token=tok_b, is_active=True, status="ONLINE",
            last_heartbeat_at=now
        )
        dev_deact = Device(
            device_id=f"dev_sec_deact_{uid}", tenant_id=tenant_a, branch_id="b_sec_a",
            device_code="CD", device_name="Counter Deactivated", hardware_fingerprint=f"hw_sec_deact_{uid}",
            app_version="1.0.0", token=tok_deact, is_active=False, status="ONLINE",
            last_heartbeat_at=now
        )
        db.add_all([dev_a, dev_b, dev_deact])
        db.commit()
    finally:
        db.close()

    try:
        # 1. Tampered / nonexistent token -> 401 Unauthorized
        res_tampered = client.get(
            f"/api/v1/owner/freshness?tenant_id={tenant_a}",
            headers={"X-Device-Token": f"tok_forged_random_{uuid.uuid4().hex}"}
        )
        assert res_tampered.status_code == 401
        assert "invalid or unrecognized" in res_tampered.json().get("detail", "").lower()

        # 2. Whitespace token -> 401 Unauthorized
        res_ws = client.get(
            f"/api/v1/owner/freshness?tenant_id={tenant_a}",
            headers={"X-Device-Token": "   "}
        )
        assert res_ws.status_code == 401

        # 3. Cross-tenant token -> 403 Forbidden
        res_cross = client.get(
            f"/api/v1/owner/freshness?tenant_id={tenant_a}",
            headers={"X-Device-Token": tok_b}
        )
        assert res_cross.status_code == 403
        assert "does not match requested tenant" in res_cross.json().get("detail", "").lower()

        # 4. Deactivated token -> 401 Unauthorized
        res_deact = client.get(
            f"/api/v1/owner/freshness?tenant_id={tenant_a}",
            headers={"X-Device-Token": tok_deact}
        )
        assert res_deact.status_code == 401
        assert "deactivated" in res_deact.json().get("detail", "").lower()

        # 5. Omitted token -> 200 OK
        res_omit = client.get(f"/api/v1/owner/freshness?tenant_id={tenant_a}")
        assert res_omit.status_code == 200
        assert res_omit.json()["tenant_id"] == tenant_a

        # 6. Valid matching token -> 200 OK
        res_valid = client.get(
            f"/api/v1/owner/freshness?tenant_id={tenant_a}",
            headers={"X-Device-Token": tok_a}
        )
        assert res_valid.status_code == 200
    finally:
        db = SessionLocal()
        db.query(Device).filter(Device.tenant_id.in_([tenant_a, tenant_b])).delete(synchronize_session=False)
        db.commit()
        db.close()

