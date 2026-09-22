import pytest
from decimal import Decimal
from pathlib import Path
import sys

# Add apps/api to path
repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from src.enightx_api.arithmetic import (
    round_money,
    calculate_line,
    calculate_shift_expected_cash,
    calculate_sale_totals,
    calculate_tenders
)


def test_empirical_rounding_midpoint_away_from_zero():
    """
    Stress-tests Python Decimal quantize ROUND_HALF_UP against LKR currency midpoint rules.
    Verifies that .005, .015, .025, etc. strictly round away from zero.
    """
    cases = [
        ("0.005", "0.01"),
        ("0.015", "0.02"),
        ("0.025", "0.03"),
        ("0.035", "0.04"),
        ("0.045", "0.05"),
        ("0.055", "0.06"),
        ("0.065", "0.07"),
        ("0.075", "0.08"),
        ("0.085", "0.09"),
        ("0.095", "0.10"),
        ("-0.005", "-0.01"),
        ("-0.015", "-0.02"),
        ("-0.025", "-0.03"),
        ("-0.035", "-0.04"),
        ("-0.045", "-0.05"),
    ]
    for inp, expected in cases:
        assert round_money(Decimal(inp)) == Decimal(expected), f"Failed on {inp}"


def test_empirical_vat18_and_complex_discounts():
    """
    Stress-tests Sri Lankan VAT 18% on lines with percentage discounts, fixed discounts,
    and fractional quantities.
    """
    # 1. 18% VAT with 10% discount on 2 units of 1455.50
    res1 = calculate_line(
        quantity=Decimal("2.0"),
        unit_price=Decimal("1455.50"),
        discount_rate=Decimal("0.10"),
        tax_rate=Decimal("0.18")
    )
    assert res1["subtotal"] == Decimal("2911.00")
    assert res1["discount_amount"] == Decimal("291.10")
    assert res1["tax_amount"] == Decimal("471.58")
    assert res1["line_total"] == Decimal("3091.48")

    # 2. 18% VAT with fixed discount 50.00 on 3 units of 850.75
    # Subtotal = 2552.25, Discount = 50.00, Net = 2502.25
    # Tax = round(2502.25 * 0.18) = round(450.405) = 450.41 (Half-up!)
    res2 = calculate_line(
        quantity=Decimal("3.0"),
        unit_price=Decimal("850.75"),
        discount_fixed=Decimal("50.00"),
        tax_rate=Decimal("0.18")
    )
    assert res2["subtotal"] == Decimal("2552.25")
    assert res2["discount_amount"] == Decimal("50.00")
    assert res2["tax_amount"] == Decimal("450.41")
    assert res2["line_total"] == Decimal("2952.66")

    # 3. Discount exceeds subtotal -> capped
    res3 = calculate_line(
        quantity=Decimal("1.0"),
        unit_price=Decimal("500.00"),
        discount_fixed=Decimal("700.00"),
        tax_rate=Decimal("0.18")
    )
    assert res3["subtotal"] == Decimal("500.00")
    assert res3["discount_amount"] == Decimal("500.00")
    assert res3["tax_amount"] == Decimal("0.00")
    assert res3["line_total"] == Decimal("0.00")


def test_empirical_split_tenders_and_order_independence():
    """
    Stress-tests tender calculation with mixed types (CASH, CARD, QR) and odd amounts,
    ensuring order independence and strict change derivation.
    """
    grand_total = Decimal("3456.77")

    # Tender order 1: CARD, QR, CASH
    tenders_order_1 = [
        {"type": "CARD", "amount": Decimal("1000.00")},
        {"type": "QR", "amount": Decimal("456.77")},
        {"type": "CASH", "amount": Decimal("2500.00")},
    ]
    res1 = calculate_tenders(grand_total, tenders_order_1)
    assert res1["change_given"] == Decimal("500.00")
    assert res1["net_cash_received"] == Decimal("2000.00")

    # Tender order 2: CASH first
    tenders_order_2 = [
        {"type": "CASH", "amount": Decimal("2500.00")},
        {"type": "QR", "amount": Decimal("456.77")},
        {"type": "CARD", "amount": Decimal("1000.00")},
    ]
    res2 = calculate_tenders(grand_total, tenders_order_2)
    assert res2["change_given"] == Decimal("500.00")
    assert res2["net_cash_received"] == Decimal("2000.00")


def test_empirical_tender_boundary_exceptions():
    """Verifies that invalid tender amounts raise ValueError."""
    grand_total = Decimal("2000.00")

    # Under-tendered (raises Total tendered is less than grand total)
    with pytest.raises(ValueError, match="less than grand total"):
        calculate_tenders(grand_total, [{"type": "CASH", "amount": Decimal("1999.99")}])

    # Non-cash exceeds grand total
    with pytest.raises(ValueError, match="cannot exceed grand total"):
        calculate_tenders(grand_total, [{"type": "CARD", "amount": Decimal("2500.00")}])

    # Combined under-tendered (Card 1500 + Cash 400 = 1900 < 2000)
    with pytest.raises(ValueError, match="less than grand total"):
        calculate_tenders(
            grand_total,
            [
                {"type": "CARD", "amount": Decimal("1500.00")},
                {"type": "CASH", "amount": Decimal("400.00")},
            ],
        )


def test_empirical_multi_shift_drawer_100_transactions():
    """
    Simulates 120 drawer operations across multiple shifts, verifying that the drawer formula:
    Expected Cash = Opening Float + Cash Received - Change Given - Cash Refunds + Cash In - Cash Out
    maintains zero penny discrepancy.
    """
    opening_float = Decimal("5000.00")
    cash_received = Decimal("0.00")
    change_given = Decimal("0.00")
    cash_refunds = Decimal("0.00")
    cash_in = Decimal("0.00")
    cash_out = Decimal("0.00")

    running_expected = opening_float

    for t in range(120):
        action = t % 5
        if action == 0:  # Cash sale with odd change
            received = Decimal("300.50")
            change = Decimal("49.25")
            cash_received += received
            change_given += change
            running_expected += (received - change)
        elif action == 1:  # Cash In (deposit)
            cin = Decimal("250.00")
            cash_in += cin
            running_expected += cin
        elif action == 2:  # Cash Out (petty cash)
            cout = Decimal("120.75")
            cash_out += cout
            running_expected -= cout
        elif action == 3:  # Cash refund
            cref = Decimal("75.50")
            cash_refunds += cref
            running_expected -= cref
        elif action == 4:  # Exact cash sale
            received = Decimal("150.00")
            cash_received += received
            running_expected += received

    computed = calculate_shift_expected_cash(
        opening_float, cash_received, change_given, cash_refunds, cash_in, cash_out
    )
    assert computed == running_expected
