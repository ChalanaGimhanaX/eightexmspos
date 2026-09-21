import json
from pathlib import Path
import jsonschema

repo_root = Path(__file__).resolve().parent.parent.parent

def test_sale_event_schema_validation():
    schema_path = repo_root / "contracts" / "v1" / "schemas" / "sale-event.json"
    with open(schema_path, "r", encoding="utf-8") as f:
        schema = json.load(f)

    valid_sale_event = {
        "event_id": "a0000000-0000-0000-0000-000000000001",
        "tenant_id": "tenant_lk_001",
        "branch_id": "branch_colombo_main",
        "device_id": "counter_01",
        "device_generation": 1,
        "source_sequence": 1,
        "schema_version": "1.0",
        "occurred_at": "2026-09-21T10:00:00Z",
        "actor_id": "user_cashier_01",
        "causal_reference": None,
        "payload": {
            "sale_id": "b0000000-0000-0000-0000-000000000001",
            "receipt_number": "B01-C01-000001",
            "shift_id": "c0000000-0000-0000-0000-000000000001",
            "customer_id": None,
            "subtotal": "1000.00",
            "discount_total": "100.00",
            "tax_total": "162.00",
            "grand_total": "1062.00",
            "lines": [
                {
                    "line_id": "d0000000-0000-0000-0000-000000000001",
                    "product_id": "prod_spark_plug",
                    "product_name": "NGK Spark Plug BP6ES",
                    "barcode": "4964336000011",
                    "quantity": "2.0",
                    "unit_price": "500.00",
                    "discount_rate": "0.10",
                    "discount_fixed": "0.00",
                    "tax_rate": "0.18",
                    "line_total": "1062.00"
                }
            ],
            "tenders": [
                {
                    "tender_id": "e0000000-0000-0000-0000-000000000001",
                    "tender_type": "CASH",
                    "amount_tendered": "1500.00",
                    "change_given": "438.00",
                    "payment_reference": None
                }
            ]
        }
    }

    # Should validate without throwing jsonschema.ValidationError
    jsonschema.validate(instance=valid_sale_event, schema=schema)
