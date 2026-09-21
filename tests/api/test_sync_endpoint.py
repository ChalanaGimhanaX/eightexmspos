import uuid
from datetime import datetime, timezone
from pathlib import Path
import sys

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app
from src.enightx_api.database import SessionLocal
from src.enightx_api.models import SyncBatch, SyncEvent

client = TestClient(app)

def create_sample_event(event_id: str, seq: int, now_iso: str, sale_id: str | None = None):
    if sale_id is None:
        sale_id = str(uuid.uuid4())
    shift_id = str(uuid.uuid4())
    line_id = str(uuid.uuid4())
    tender_id = str(uuid.uuid4())

    return {
        "event_id": event_id,
        "tenant_id": "TENANT_LK_01",
        "branch_id": "B01",
        "device_id": "C01",
        "device_generation": 1,
        "source_sequence": seq,
        "schema_version": "1.0",
        "occurred_at": now_iso,
        "actor_id": "usr_cashier_01",
        "causal_reference": None,
        "payload": {
            "sale_id": sale_id,
            "receipt_number": f"B01-C01-{seq:06d}",
            "shift_id": shift_id,
            "customer_id": None,
            "subtotal": "1000.00",
            "discount_total": "100.00",
            "tax_total": "162.00",
            "grand_total": "1062.00",
            "lines": [
                {
                    "line_id": line_id,
                    "product_id": "prod_01",
                    "product_name": "Test Item",
                    "barcode": "5011223344556",
                    "quantity": "2.00",
                    "unit_price": "500.00",
                    "discount_rate": "0.1000",
                    "discount_fixed": "0.00",
                    "tax_rate": "0.1800",
                    "line_total": "1062.00"
                }
            ],
            "tenders": [
                {
                    "tender_id": tender_id,
                    "tender_type": "CASH",
                    "amount_tendered": "1500.00",
                    "change_given": "438.00",
                    "payment_reference": None
                }
            ]
        }
    }

def test_sync_push_successful():
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": now_iso,
        "events": [create_sample_event(event_id, 1, now_iso)]
    }

    response = client.post("/api/v1/sync/push", json=batch_payload)
    assert response.status_code == 200
    data = response.json()
    assert data["batch_id"] == batch_id
    assert data["acknowledged_sequence"] == 1
    assert data["status"] == "acknowledged"

    # Verify durable persistence in PostgreSQL
    db = SessionLocal()
    try:
        db_batch = db.query(SyncBatch).filter(SyncBatch.batch_id == uuid.UUID(batch_id)).first()
        assert db_batch is not None
        assert db_batch.tenant_id == "TENANT_LK_01"
        assert db_batch.source_device_id == "C01"
        assert db_batch.acknowledged_sequence == 1
        assert db_batch.status == "acknowledged"

        db_event = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_id)).first()
        assert db_event is not None
        assert db_event.batch_id == uuid.UUID(batch_id)
        assert db_event.source_sequence == 1
        assert db_event.payload["receipt_number"] == "B01-C01-000001"
    finally:
        db.close()

def test_sync_push_rejects_invalid_schema():
    bad_payload = {
        "batch_id": "invalid-not-a-uuid",
        "tenant_id": "TENANT_LK_01"
    }
    response = client.post("/api/v1/sync/push", json=bad_payload)
    assert response.status_code == 422

def test_sync_push_idempotent_retry():
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 5,
        "sent_at": now_iso,
        "events": [create_sample_event(event_id, 5, now_iso)]
    }

    # First attempt
    res1 = client.post("/api/v1/sync/push", json=batch_payload)
    assert res1.status_code == 200
    data1 = res1.json()

    # Second attempt (idempotent retry)
    res2 = client.post("/api/v1/sync/push", json=batch_payload)
    assert res2.status_code == 200
    data2 = res2.json()

    assert data1 == data2

    # Verify only 1 batch row and 1 event row exist in PostgreSQL
    db = SessionLocal()
    try:
        batch_count = db.query(SyncBatch).filter(SyncBatch.batch_id == uuid.UUID(batch_id)).count()
        assert batch_count == 1
        event_count = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event_id)).count()
        assert event_count == 1
    finally:
        db.close()

def test_sync_push_duplicate_event_deduplication():
    batch1_id = str(uuid.uuid4())
    batch2_id = str(uuid.uuid4())
    shared_event_id = str(uuid.uuid4())
    event2_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    # Batch 1 contains shared_event_id (seq 1)
    batch1_payload = {
        "batch_id": batch1_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": now_iso,
        "events": [create_sample_event(shared_event_id, 1, now_iso)]
    }
    res1 = client.post("/api/v1/sync/push", json=batch1_payload)
    assert res1.status_code == 200

    # Batch 2 contains shared_event_id again, plus a new event (seq 2)
    batch2_payload = {
        "batch_id": batch2_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 2,
        "sent_at": now_iso,
        "events": [
            create_sample_event(shared_event_id, 1, now_iso),
            create_sample_event(event2_id, 2, now_iso)
        ]
    }
    res2 = client.post("/api/v1/sync/push", json=batch2_payload)
    assert res2.status_code == 200
    assert res2.json()["acknowledged_sequence"] == 2

    # Verify duplicate event was not duplicated in DB
    db = SessionLocal()
    try:
        shared_count = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(shared_event_id)).count()
        assert shared_count == 1
        event2_count = db.query(SyncEvent).filter(SyncEvent.event_id == uuid.UUID(event2_id)).count()
        assert event2_count == 1
    finally:
        db.close()

def test_sync_push_multiple_events_sequence_tracking():
    batch_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 3,
        "sent_at": now_iso,
        "events": [
            create_sample_event(str(uuid.uuid4()), 10, now_iso),
            create_sample_event(str(uuid.uuid4()), 15, now_iso),
            create_sample_event(str(uuid.uuid4()), 12, now_iso)
        ]
    }

    res = client.post("/api/v1/sync/push", json=batch_payload)
    assert res.status_code == 200
    assert res.json()["acknowledged_sequence"] == 15

def test_sync_push_empty_events():
    batch_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 42,
        "sent_at": now_iso,
        "events": []
    }

    res = client.post("/api/v1/sync/push", json=batch_payload)
    assert res.status_code == 200
    assert res.json()["acknowledged_sequence"] == 42
