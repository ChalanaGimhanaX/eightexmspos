from decimal import Decimal, ROUND_HALF_UP
from typing import Dict, Any, List

CENT = Decimal("0.01")

def round_money(val: Decimal) -> Decimal:
    """Rounds a monetary value to 2 decimal places using half-up rounding."""
    return val.quantize(CENT, rounding=ROUND_HALF_UP)

def calculate_line(
    quantity: Decimal,
    unit_price: Decimal,
    discount_rate: Decimal = Decimal("0.0"),
    discount_fixed: Decimal = Decimal("0.0"),
    tax_rate: Decimal = Decimal("0.0")
) -> Dict[str, Decimal]:
    """
    Calculates line subtotal, discount, tax, and total.
    Rule:
    1. Subtotal = quantity * unit_price
    2. Discount = round((Subtotal * discount_rate) + discount_fixed)
    3. Net after discount = Subtotal - Discount
    4. Tax = round(Net after discount * tax_rate)
    5. Line total = Net after discount + Tax
    """
    subtotal = round_money(quantity * unit_price)
    discount_val = (subtotal * discount_rate) + discount_fixed
    discount_amount = round_money(discount_val)
    if discount_amount > subtotal:
        discount_amount = subtotal

    net_after_discount = subtotal - discount_amount
    tax_amount = round_money(net_after_discount * tax_rate)
    line_total = net_after_discount + tax_amount

    return {
        "subtotal": subtotal,
        "discount_amount": discount_amount,
        "tax_amount": tax_amount,
        "line_total": line_total
    }

def calculate_shift_expected_cash(
    opening_float: Decimal,
    cash_received: Decimal,
    change_given: Decimal,
    cash_refunds: Decimal,
    cash_in: Decimal,
    cash_out: Decimal
) -> Decimal:
    """
    A10 Shift Expected Cash Formula:
    Expected Cash = Opening Float + Cash Received - Change Given - Cash Refunds + Cash In - Cash Out.
    Card, QR, and credit sales do not affect physical drawer cash.
    """
    return round_money(
        opening_float + cash_received - change_given - cash_refunds + cash_in - cash_out
    )
