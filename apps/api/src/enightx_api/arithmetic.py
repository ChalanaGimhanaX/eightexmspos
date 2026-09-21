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

def calculate_sale_totals(lines: List[Dict[str, Decimal]]) -> Dict[str, Decimal]:
    """Calculates aggregate sale subtotal, discount, tax, and grand total."""
    subtotal = round_money(sum((l["subtotal"] for l in lines), Decimal("0.0")))
    discount_total = round_money(sum((l["discount_amount"] for l in lines), Decimal("0.0")))
    tax_total = round_money(sum((l["tax_amount"] for l in lines), Decimal("0.0")))
    grand_total = round_money(sum((l["line_total"] for l in lines), Decimal("0.0")))
    return {
        "subtotal": subtotal,
        "discount_total": discount_total,
        "tax_total": tax_total,
        "grand_total": grand_total
    }

def calculate_tenders(grand_total: Decimal, tenders: List[Dict[str, Any]]) -> Dict[str, Any]:
    """
    Validates tenders and calculates cash change and net cash effects.
    Order-independent tender processing matching C# MoneyCalculator/SaleService.
    """
    total_tendered = round_money(sum((Decimal(str(t["amount"])) for t in tenders), Decimal("0.0")))
    if total_tendered < grand_total:
        raise ValueError(f"Total tendered ({total_tendered}) is less than grand total ({grand_total})")

    non_cash_total = round_money(sum(
        (Decimal(str(t["amount"])) for t in tenders if t.get("type") != "CASH"),
        Decimal("0.0")
    ))
    if non_cash_total > grand_total:
        raise ValueError("Non-cash tenders cannot exceed grand total")

    cash_needed = grand_total - non_cash_total
    cash_total = round_money(sum(
        (Decimal(str(t["amount"])) for t in tenders if t.get("type") == "CASH"),
        Decimal("0.0")
    ))
    if cash_total < cash_needed:
        raise ValueError(f"Cash tendered ({cash_total}) is less than cash needed ({cash_needed})")

    cash_change = cash_total - cash_needed if cash_total > cash_needed else Decimal("0.0")
    return {
        "total_tendered": total_tendered,
        "non_cash_total": non_cash_total,
        "cash_total": cash_total,
        "change_given": cash_change,
        "net_cash_received": cash_total - cash_change
    }

def calculate_moving_average_cost(
    current_qty: Decimal,
    current_cost: Decimal,
    received_qty: Decimal,
    received_cost: Decimal,
) -> Decimal:
    """
    Calculates the new moving weighted average cost after receiving inventory:
    New Cost = ((current_qty * current_cost) + (received_qty * received_cost)) / (current_qty + received_qty)
    If current_qty <= 0: New Cost = received_cost
    Rounded using strict LKR Half-Up rounding to 2 decimal places.
    """
    if current_qty <= Decimal("0.0"):
        return round_money(received_cost)
    total_qty = current_qty + received_qty
    if total_qty <= Decimal("0.0"):
        return round_money(received_cost)
    total_cost = (current_qty * current_cost) + (received_qty * received_cost)
    return round_money(total_cost / total_qty)

