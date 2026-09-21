import uuid
from datetime import datetime, timezone
from pathlib import Path
import sys

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app

client = TestClient(app)

def test_sync_push_successful():
    batch_id = str(uuid.uuid4())
    event_id = str(uuid.uuid4())
    sale_id = str(uuid.uuid4())
    shift_id = str(uuid.uuid4())
    line_id = str(uuid.uuid4())
    tender_id = str(uuid.uuid4())
    now_iso = datetime.now(timezone.utc).isoformat()

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": "TENANT_LK_01",
        "source_device_id": "C01",
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": now_iso,
        "events": [
            {
                "event_id": event_id,
                "tenant_id": "TENANT_LK_01",
                "branch_id": "B01",
                "device_id": "C01",
                "device_generation": 1,
                "source_sequence": 1,
                "schema_version": "1.0",
                "occurred_at": now_iso,
                "actor_id": "usr_cashier_01",
                "causal_reference": None,
                "payload": {
                    "sale_id": sale_id,
                    "receipt_number": "B01-C01-000001",
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
        ]
    }

    response = client.post("/api/v1/sync/push", json=batch_payload)
    assert response.status_code == 200
    data = response.json()
    assert data["batch_id"] == batch_id
    assert data["acknowledged_sequence"] == 1
    assert data["status"] == "acknowledged"

def test_sync_push_rejects_invalid_schema():
    bad_payload = {
        "batch_id": "invalid-not-a-uuid",
        "tenant_id": "TENANT_LK_01"
    }
    response = client.post("/api/v1/sync/push", json=bad_payload)
    assert response.status_code == 422
