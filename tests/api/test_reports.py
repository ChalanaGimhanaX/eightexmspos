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

def test_sales_summary_and_top_products_reports():
    suffix = uuid.uuid4().hex[:6]
    tenant_id = f"TENANT_RPT_{suffix}"
    branch_id = f"BR_RPT_{suffix}"
    device_id = f"DEV_RPT_{suffix}"
    actor_id = f"USR_CASHIER_{suffix}"

    now = datetime.now(timezone.utc).isoformat()

    batch_id = str(uuid.uuid4())
    event_id1 = str(uuid.uuid4())
    event_id2 = str(uuid.uuid4())

    batch_payload = {
        "batch_id": batch_id,
        "tenant_id": tenant_id,
        "source_device_id": device_id,
        "source_generation": 1,
        "batch_sequence": 1,
        "sent_at": now,
        "events": [
            {
                "event_id": event_id1,
                "tenant_id": tenant_id,
                "branch_id": branch_id,
                "device_id": device_id,
                "device_generation": 1,
                "source_sequence": 1,
                "schema_version": "1.0",
                "occurred_at": now,
                "actor_id": actor_id,
                "payload": {
                    "sale_id": str(uuid.uuid4()),
                    "receipt_number": f"RCP-01-{suffix}",
                    "shift_id": str(uuid.uuid4()),
                    "subtotal": "1020.00",
                    "discount_total": "20.00",
                    "tax_total": "50.00",
                    "grand_total": "1050.00",
                    "lines": [
                        {
                            "line_id": str(uuid.uuid4()),
                            "product_id": f"prd_spark_plug_{suffix}",
                            "product_name": "Spark Plug Platinum",
                            "barcode": f"SP_{suffix}",
                            "quantity": "2.00",
                            "unit_price": "500.00",
                            "discount_fixed": "0.00",
                            "tax_rate": "0.05",
                            "line_total": "1050.00"
                        }
                    ],
                    "tenders": [
                        {
                            "tender_id": str(uuid.uuid4()),
                            "tender_type": "CASH",
                            "amount_tendered": "1050.00",
                            "change_given": "0.00"
                        }
                    ]
                }
            },
            {
                "event_id": event_id2,
                "tenant_id": tenant_id,
                "branch_id": branch_id,
                "device_id": device_id,
                "device_generation": 1,
                "source_sequence": 2,
                "schema_version": "1.0",
                "occurred_at": now,
                "actor_id": actor_id,
                "payload": {
                    "sale_id": str(uuid.uuid4()),
                    "receipt_number": f"RCP-02-{suffix}",
                    "shift_id": str(uuid.uuid4()),
                    "subtotal": "2500.00",
                    "discount_total": "0.00",
                    "tax_total": "125.00",
                    "grand_total": "2625.00",
                    "lines": [
                        {
                            "line_id": str(uuid.uuid4()),
                            "product_id": f"prd_battery_{suffix}",
                            "product_name": "Car Battery 12V",
                            "barcode": f"BAT_{suffix}",
                            "quantity": "1.00",
                            "unit_price": "2500.00",
                            "discount_fixed": "0.00",
                            "tax_rate": "0.05",
                            "line_total": "2625.00"
                        }
                    ],
                    "tenders": [
                        {
                            "tender_id": str(uuid.uuid4()),
                            "tender_type": "CARD",
                            "amount_tendered": "2000.00",
                            "change_given": "0.00"
                        },
                        {
                            "tender_id": str(uuid.uuid4()),
                            "tender_type": "QR",
                            "amount_tendered": "625.00",
                            "change_given": "0.00"
                        }
                    ]
                }
            }
        ]
    }

    # Ingest sync batch with 2 sales
    push_res = client.post("/api/v1/sync/push", json=batch_payload)
    assert push_res.status_code == 200

    # 1. Test sales summary report
    summary_res = client.get(f"/api/v1/reports/sales/summary?tenant_id={tenant_id}")
    assert summary_res.status_code == 200
    summary = summary_res.json()
    assert summary["total_sales_count"] == 2
    assert Decimal(str(summary["total_revenue"])) == Decimal("3675.00")
    assert Decimal(str(summary["total_tax"])) == Decimal("175.00")
    assert Decimal(str(summary["total_discount"])) == Decimal("20.00")
    assert Decimal(str(summary["tender_breakdown"]["CASH"])) == Decimal("1050.00")
    assert Decimal(str(summary["tender_breakdown"]["CARD"])) == Decimal("2000.00")
    assert Decimal(str(summary["tender_breakdown"]["QR"])) == Decimal("625.00")
    assert Decimal(str(summary["average_ticket_size"])) == Decimal("1837.50")

    # 2. Test top products report
    top_res = client.get(f"/api/v1/reports/top-products?tenant_id={tenant_id}&limit=5")
    assert top_res.status_code == 200
    top_items = top_res.json()
    assert len(top_items) == 2
    assert top_items[0]["product_name"] == "Car Battery 12V"
    assert Decimal(str(top_items[0]["revenue"])) == Decimal("2625.00")

    # 3. Test cashier performance report
    cashier_res = client.get(f"/api/v1/reports/cashier-performance?tenant_id={tenant_id}")
    assert cashier_res.status_code == 200
    perf = cashier_res.json()
    assert len(perf) == 1
    assert perf[0]["cashier_id"] == actor_id
    assert Decimal(str(perf[0]["total_sales_amount"])) == Decimal("3675.00")

    # 4. Test dashboard summary endpoint
    dash_res = client.get(f"/api/v1/reports/dashboard?tenant_id={tenant_id}")
    assert dash_res.status_code == 200
    dash = dash_res.json()
    assert dash["total_orders"] == 2
    assert Decimal(str(dash["total_revenue"])) == Decimal("3675.00")

