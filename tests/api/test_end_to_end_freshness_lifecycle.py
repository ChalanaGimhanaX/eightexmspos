"""
Empirical End-to-End Multi-Module Integration Test for Cloud API:
Verifies owner web freshness endpoint reflections under terminal activity (Milestone 5 / Step 9):
- Terminal device enrollment and initial OFFLINE state
- Terminal heartbeat updating device status to ONLINE (<5 min threshold)
- Terminal sync batch push with multi-module lifecycle events (OPEN_SHIFT, TRANSFER, SALE, CLOSE_SHIFT)
- Owner freshness endpoint accurately aggregating branch and device status
- Cryptographic tenant isolation preventing cross-tenant freshness visibility
"""

import uuid
from datetime import datetime, timezone, timedelta
import pytest
from fastapi.testclient import TestClient
from src.enightx_api.models import Device, SyncBatch, SyncEvent


def test_owner_freshness_reflects_terminal_activity_lifecycle(clean_client, device_factory, db_session):
    # 1. Setup unique isolated tenant and branch
    uid = uuid.uuid4().hex[:8]
    tenant_id = f"TENANT_E2E_LIFECYCLE_{uid}"
    branch_id = f"BRANCH_E2E_{uid}"
    device_id = f"dev_e2e_term_{uid}"
    device_token = f"tok_e2e_secret_{uuid.uuid4().hex}"

    # Provision device with initially no heartbeats or syncs
    device_factory(
        tenant_id=tenant_id,
        branch_id=branch_id,
        device_id=device_id,
        token=device_token,
        device_code=f"POS-{uid[:4]}",
        device_name="Counter 1 E2E Terminal",
        is_active=True,
        status="OFFLINE"
    )

    # 2. Initial Freshness Query: device and branch should be OFFLINE
    res_initial = clean_client.get(
        f"/api/v1/owner/freshness?tenant_id={tenant_id}",
        headers={"X-Device-Token": device_token}
    )
    assert res_initial.status_code == 200
    data_initial = res_initial.json()
    assert data_initial["tenant_id"] == tenant_id
    assert data_initial["total_devices"] == 1
    assert data_initial["offline_devices_count"] == 1
    assert data_initial["online_devices_count"] == 0
    assert len(data_initial["branches"]) == 1
    assert data_initial["branches"][0]["status"] == "OFFLINE"
    dev_item_initial = data_initial["branches"][0]["devices"][0]
    assert dev_item_initial["status"] == "OFFLINE"
    assert dev_item_initial["last_sync_at"] is None
    assert dev_item_initial["sync_latency_seconds"] is None

    # 3. Terminal Activity: Terminal sends heartbeat to API
    hb_payload = {
        "device_id": device_id,
        "token": device_token,
        "app_version": "1.0.5",
        "status": "ONLINE"
    }
    res_hb = clean_client.post("/api/v1/devices/heartbeat", json=hb_payload)
    assert res_hb.status_code == 200
    assert res_hb.json()["acknowledged"] is True

    # 4. Freshness Query after Heartbeat: status reflects ONLINE terminal activity (< 5m)
    res_after_hb = clean_client.get(
        f"/api/v1/owner/freshness?tenant_id={tenant_id}",
        headers={"X-Device-Token": device_token}
    )
    assert res_after_hb.status_code == 200
    data_hb = res_after_hb.json()
    assert data_hb["online_devices_count"] == 1
    assert data_hb["offline_devices_count"] == 0
    assert data_hb["branches"][0]["status"] == "ONLINE"
    dev_item_hb = data_hb["branches"][0]["devices"][0]
    assert dev_item_hb["status"] == "ONLINE"
    assert dev_item_hb["last_heartbeat_at"] is not None

    # 5. Terminal Activity: Terminal pushes multi-module lifecycle sync batch
    now_dt = datetime.now(timezone.utc)
    now_iso = now_dt.isoformat()
    batch_id = str(uuid.uuid4())
    lifecycle_events = [
        {
            "event_id": str(uuid.uuid4()),
            "tenant_id": tenant_id,
            "branch_id": branch_id,
            "device_id": device_id,
            "device_generation": 1,
            "source_sequence": 1,
            "schema_version": "1.0",
            "occurred_at": now_iso,
            "actor_id": "cashier_e2e",
            "causal_reference": None,
            "payload": {
                "action": "OPEN_SHIFT",
                "opening_float": "7500.50",
                "counter_id": "POS01"
            }
        },
        {
            "event_id": str(uuid.uuid4()),
            "tenant_id": tenant_id,
            "branch_id": branch_id,
            "device_id": device_id,
            "device_generation": 1,
            "source_sequence": 2,
            "schema_version": "1.0",
            "occurred_at": now_iso,
            "actor_id": "mgr_e2e",
            "causal_reference": None,
            "payload": {
                "action": "TRANSFER_DISPATCH",
                "transfer_number": f"TRF-{uid}",
                "dest_branch_id": "BRANCH_DEST_02",
                "dispatched_qty": "15.0"
            }
        },
        {
            "event_id": str(uuid.uuid4()),
            "tenant_id": tenant_id,
            "branch_id": branch_id,
            "device_id": device_id,
            "device_generation": 1,
            "source_sequence": 3,
            "schema_version": "1.0",
            "occurred_at": now_iso,
            "actor_id": "cashier_e2e",
            "causal_reference": None,
            "payload": {
                "action": "SALE_COMMITTED",
                "receipt_number": f"{branch_id}-C01-000001",
                "grand_total": "5000.00",
                "tender_cash": "6000.00",
                "change_given": "1000.00"
            }
        },
        {
            "event_id": str(uuid.uuid4()),
            "tenant_id": tenant_id,
            "branch_id": branch_id,
            "device_id": device_id,
            "device_generation": 1,
            "source_sequence": 4,
            "schema_version": "1.0",
            "occurred_at": now_iso,
            "actor_id": "cashier_e2e",
            "causal_reference": None,
            "payload": {
                "action": "CLOSE_SHIFT",
                "expected_cash": "12500.50",
                "actual_counted_cash": "12500.50",
                "variance": "0.00"
            }
        }
    ]

    push_payload = {
        "batch_id": batch_id,
        "tenant_id": tenant_id,
        "source_device_id": device_id,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": now_iso,
        "events": lifecycle_events
    }

    res_push = clean_client.post(
        "/api/v1/sync/push",
        json=push_payload,
        headers={"X-Device-Token": device_token}
    )
    assert res_push.status_code == 200
    push_data = res_push.json()
    assert push_data["status"] == "acknowledged"
    assert push_data["acknowledged_sequence"] == 4

    # 6. Freshness Query after Sync: verify sync timestamp and sync latency
    res_after_sync = clean_client.get(
        f"/api/v1/owner/freshness?tenant_id={tenant_id}",
        headers={"X-Device-Token": device_token}
    )
    assert res_after_sync.status_code == 200
    data_sync = res_after_sync.json()
    assert data_sync["online_devices_count"] == 1
    dev_item_sync = data_sync["branches"][0]["devices"][0]
    assert dev_item_sync["last_sync_at"] is not None
    assert dev_item_sync["sync_latency_seconds"] is not None
    assert dev_item_sync["sync_latency_seconds"] < 15.0 # Just synchronized seconds ago!

    # 7. Cross-Tenant Security Boundary Verification
    # A device from another tenant cannot query freshness of this tenant
    other_uid = uuid.uuid4().hex[:8]
    other_token = f"tok_other_{other_uid}"
    device_factory(
        tenant_id=f"TENANT_OTHER_{other_uid}",
        branch_id=f"BRANCH_OTHER_{other_uid}",
        device_id=f"dev_other_{other_uid}",
        token=other_token,
        is_active=True
    )
    res_forbidden = clean_client.get(
        f"/api/v1/owner/freshness?tenant_id={tenant_id}",
        headers={"X-Device-Token": other_token}
    )
    assert res_forbidden.status_code == 403
    assert "does not match requested tenant" in res_forbidden.json()["detail"].lower()

    # Querying the other tenant returns 0 devices for this tenant's branch
    res_other_fresh = clean_client.get(
        f"/api/v1/owner/freshness?tenant_id=TENANT_OTHER_{other_uid}",
        headers={"X-Device-Token": other_token}
    )
    assert res_other_fresh.status_code == 200
    assert res_other_fresh.json()["total_devices"] == 1
    assert res_other_fresh.json()["branches"][0]["branch_id"] == f"BRANCH_OTHER_{other_uid}"

