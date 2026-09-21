import json
from decimal import Decimal
from pathlib import Path
import sys

# Add apps/api to path
repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from src.enightx_api.arithmetic import (
    calculate_line,
    calculate_shift_expected_cash,
    round_money
)

def test_line_arithmetic_fixtures():
    fixtures_path = repo_root / "contracts" / "fixtures" / "arithmetic_fixtures.json"
    with open(fixtures_path, "r", encoding="utf-8") as f:
        fixtures = json.load(f)

    for case in fixtures["line_tests"]:
        qty = Decimal(str(case["quantity"]))
        price = Decimal(str(case["unit_price"]))
        disc_rate = Decimal(str(case["discount_rate"]))
        disc_fixed = Decimal(str(case["discount_fixed"]))
        tax_rate = Decimal(str(case["tax_rate"]))

        res = calculate_line(qty, price, disc_rate, disc_fixed, tax_rate)

        assert res["subtotal"] == Decimal(str(case["expected_line_subtotal"])), f"Subtotal mismatch in {case['name']}"
        assert res["discount_amount"] == Decimal(str(case["expected_discount_amount"])), f"Discount mismatch in {case['name']}"
        assert res["tax_amount"] == Decimal(str(case["expected_tax_amount"])), f"Tax mismatch in {case['name']}"
        assert res["line_total"] == Decimal(str(case["expected_line_total"])), f"Line total mismatch in {case['name']}"

def test_shift_expected_cash_fixture():
    fixtures_path = repo_root / "contracts" / "fixtures" / "arithmetic_fixtures.json"
    with open(fixtures_path, "r", encoding="utf-8") as f:
        fixtures = json.load(f)

    shift_case = fixtures["shift_cash_drawer_test"]
    expected = calculate_shift_expected_cash(
        opening_float=Decimal(str(shift_case["opening_float"])),
        cash_received=Decimal(str(shift_case["cash_received"])),
        change_given=Decimal(str(shift_case["change_given"])),
        cash_refunds=Decimal(str(shift_case["cash_refunds"])),
        cash_in=Decimal(str(shift_case["cash_in"])),
        cash_out=Decimal(str(shift_case["cash_out"]))
    )

    assert expected == Decimal(str(shift_case["expected_drawer_cash"]))
    variance = Decimal(str(shift_case["actual_counted_cash"])) - expected
    assert variance == Decimal(str(shift_case["expected_variance"]))

def test_sale_arithmetic_fixtures():
    fixtures_path = repo_root / "contracts" / "fixtures" / "arithmetic_fixtures.json"
    with open(fixtures_path, "r", encoding="utf-8") as f:
        fixtures = json.load(f)

    from src.enightx_api.arithmetic import calculate_sale_totals, calculate_tenders

    for case in fixtures["sale_tests"]:
        calc_lines = []
        for l in case["lines"]:
            calc_lines.append(calculate_line(
                quantity=Decimal(str(l["quantity"])),
                unit_price=Decimal(str(l["unit_price"])),
                discount_rate=Decimal(str(l.get("discount_rate", 0.0))),
                discount_fixed=Decimal(str(l.get("discount_fixed", 0.0))),
                tax_rate=Decimal(str(l.get("tax_rate", 0.0)))
            ))

        totals = calculate_sale_totals(calc_lines)
        assert totals["subtotal"] == Decimal(str(case["expected_subtotal"])), f"Subtotal mismatch in {case['name']}"
        assert totals["discount_total"] == Decimal(str(case["expected_discount_total"])), f"Discount mismatch in {case['name']}"
        assert totals["tax_total"] == Decimal(str(case["expected_tax_total"])), f"Tax mismatch in {case['name']}"
        assert totals["grand_total"] == Decimal(str(case["expected_grand_total"])), f"Grand total mismatch in {case['name']}"

        # Tender validation
        if "cash_tendered" in case:
            tenders = [{"type": "CASH", "amount": case["cash_tendered"]}]
        else:
            tenders = case["tenders"]

        tender_res = calculate_tenders(totals["grand_total"], tenders)
        assert tender_res["change_given"] == Decimal(str(case["expected_change"])), f"Change mismatch in {case['name']}"

