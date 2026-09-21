from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
from typing import Dict, List, Optional
from fastapi import APIRouter, Depends, Query
from sqlalchemy.orm import Session
from sqlalchemy import text

from ..schemas import (
    SalesSummaryReportResponse,
    TopProductReportItem,
    CashierPerformanceItem,
    DashboardSummaryResponse,
)
from ..database import get_db
from ..models import SyncEvent

router = APIRouter(prefix="/api/v1/reports", tags=["Reports & Analytics"])

def round_lkr(value: Decimal) -> Decimal:
    return value.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)

@router.get("/sales/summary", response_model=SalesSummaryReportResponse)
def get_sales_summary(
    tenant_id: str = "TENANT_LK_01",
    branch_id: Optional[str] = None,
    from_utc: Optional[datetime] = None,
    to_utc: Optional[datetime] = None,
    db: Session = Depends(get_db)
):
    query = db.query(SyncEvent).filter(SyncEvent.tenant_id == tenant_id)
    if branch_id:
        query = query.filter(SyncEvent.branch_id == branch_id)
    if from_utc:
        query = query.filter(SyncEvent.occurred_at >= from_utc)
    if to_utc:
        query = query.filter(SyncEvent.occurred_at <= to_utc)

    events = query.all()

    total_sales_count = len(events)
    total_revenue = Decimal("0.00")
    total_tax = Decimal("0.00")
    total_discount = Decimal("0.00")
    tender_breakdown: Dict[str, Decimal] = {
        "CASH": Decimal("0.00"),
        "CARD": Decimal("0.00"),
        "QR": Decimal("0.00"),
        "CREDIT": Decimal("0.00")
    }

    for ev in events:
        payload = ev.payload or {}
        grand_total = Decimal(str(payload.get("grand_total", 0)))
        tax_total = Decimal(str(payload.get("tax_total", 0)))
        discount_total = Decimal(str(payload.get("discount_total", 0)))

        total_revenue += grand_total
        total_tax += tax_total
        total_discount += discount_total

        for t in payload.get("tenders", []):
            ttype = t.get("tender_type", "CASH")
            amount = Decimal(str(t.get("amount_tendered", 0))) - Decimal(str(t.get("change_given", 0)))
            tender_breakdown[ttype] = tender_breakdown.get(ttype, Decimal("0.00")) + amount

    avg_ticket = round_lkr(total_revenue / total_sales_count) if total_sales_count > 0 else Decimal("0.00")

    return SalesSummaryReportResponse(
        tenant_id=tenant_id,
        branch_id=branch_id,
        total_sales_count=total_sales_count,
        total_revenue=round_lkr(total_revenue),
        total_tax=round_lkr(total_tax),
        total_discount=round_lkr(total_discount),
        tender_breakdown={k: round_lkr(v) for k, v in tender_breakdown.items()},
        average_ticket_size=avg_ticket
    )

@router.get("/top-products", response_model=List[TopProductReportItem])
def get_top_products(
    tenant_id: str = "TENANT_LK_01",
    branch_id: Optional[str] = None,
    limit: int = Query(10, le=50),
    db: Session = Depends(get_db)
):
    query = db.query(SyncEvent).filter(SyncEvent.tenant_id == tenant_id)
    if branch_id:
        query = query.filter(SyncEvent.branch_id == branch_id)

    events = query.all()
    products_map: Dict[str, Dict] = {}

    for ev in events:
        payload = ev.payload or {}
        for line in payload.get("lines", []):
            pid = line.get("product_id")
            pname = line.get("product_name", pid)
            qty = Decimal(str(line.get("quantity", 0)))
            total = Decimal(str(line.get("line_total", 0)))

            if pid not in products_map:
                products_map[pid] = {
                    "product_id": pid,
                    "product_name": pname,
                    "quantity_sold": Decimal("0.00"),
                    "revenue": Decimal("0.00")
                }
            products_map[pid]["quantity_sold"] += qty
            products_map[pid]["revenue"] += total

    sorted_products = sorted(
        products_map.values(),
        key=lambda x: x["revenue"],
        reverse=True
    )[:limit]

    return [
        TopProductReportItem(
            product_id=p["product_id"],
            product_name=p["product_name"],
            quantity_sold=round_lkr(p["quantity_sold"]),
            revenue=round_lkr(p["revenue"])
        )
        for p in sorted_products
    ]

@router.get("/cashier-performance", response_model=List[CashierPerformanceItem])
def get_cashier_performance(
    tenant_id: str = "TENANT_LK_01",
    branch_id: Optional[str] = None,
    db: Session = Depends(get_db)
):
    cashiers_map: Dict[str, Dict] = {}

    # 1. Sum sales by actor_id from sync_events
    ev_query = db.query(SyncEvent).filter(SyncEvent.tenant_id == tenant_id)
    if branch_id:
        ev_query = ev_query.filter(SyncEvent.branch_id == branch_id)
    for ev in ev_query.all():
        actor = ev.actor_id
        if actor not in cashiers_map:
            cashiers_map[actor] = {
                "cashier_id": actor,
                "shifts_worked": 0,
                "total_sales_amount": Decimal("0.00"),
                "total_variance": Decimal("0.00")
            }
        cashiers_map[actor]["total_sales_amount"] += Decimal(str(ev.payload.get("grand_total", 0)))

    # 2. Check shifts table if exists
    try:
        shift_sql = "SELECT cashier_id, count(*), coalesce(sum(variance), 0) FROM shifts WHERE tenant_id = :tid"
        params = {"tid": tenant_id}
        if branch_id:
            shift_sql += " AND branch_id = :bid"
            params["bid"] = branch_id
        shift_sql += " GROUP BY cashier_id"

        result = db.execute(text(shift_sql), params).fetchall()
        for row in result:
            cid, count, var = row[0], int(row[1]), Decimal(str(row[2]))
            if cid not in cashiers_map:
                cashiers_map[cid] = {
                    "cashier_id": cid,
                    "shifts_worked": 0,
                    "total_sales_amount": Decimal("0.00"),
                    "total_variance": Decimal("0.00")
                }
            cashiers_map[cid]["shifts_worked"] = count
            cashiers_map[cid]["total_variance"] = var
    except Exception:
        pass

    return [
        CashierPerformanceItem(
            cashier_id=c["cashier_id"],
            shifts_worked=c["shifts_worked"],
            total_sales_amount=round_lkr(c["total_sales_amount"]),
            total_variance=round_lkr(c["total_variance"])
        )
        for c in sorted(cashiers_map.values(), key=lambda x: x["total_sales_amount"], reverse=True)
    ]

@router.get("/dashboard", response_model=DashboardSummaryResponse)
def get_dashboard_summary(
    tenant_id: str = "TENANT_LK_01",
    db: Session = Depends(get_db)
):
    # 1. Total revenue and orders from sync_events
    events = db.query(SyncEvent).filter(SyncEvent.tenant_id == tenant_id).all()
    total_orders = len(events)
    total_revenue = Decimal("0.00")
    tender_breakdown: Dict[str, Decimal] = {
        "CASH": Decimal("0.00"),
        "CARD": Decimal("0.00"),
        "QR": Decimal("0.00"),
        "CREDIT": Decimal("0.00")
    }

    for ev in events:
        p = ev.payload or {}
        total_revenue += Decimal(str(p.get("grand_total", 0)))
        for t in p.get("tenders", []):
            ttype = t.get("tender_type", "CASH")
            amt = Decimal(str(t.get("amount_tendered", 0))) - Decimal(str(t.get("change_given", 0)))
            tender_breakdown[ttype] = tender_breakdown.get(ttype, Decimal("0.00")) + amt

    # 2. Total customer debt
    total_customer_debt = Decimal("0.00")
    try:
        debt_res = db.execute(
            text("SELECT coalesce(sum(current_balance), 0) FROM customers WHERE tenant_id = :tid"),
            {"tid": tenant_id}
        ).scalar()
        if debt_res is not None:
            total_customer_debt = Decimal(str(debt_res))
    except Exception:
        pass

    # 3. Low stock alerts count
    low_stock_alerts = 0
    try:
        low_res = db.execute(
            text("SELECT count(*) FROM branch_inventory WHERE tenant_id = :tid AND stock_on_hand <= reorder_point"),
            {"tid": tenant_id}
        ).scalar()
        if low_res is not None:
            low_stock_alerts = int(low_res)
    except Exception:
        pass

    return DashboardSummaryResponse(
        tenant_id=tenant_id,
        total_revenue=round_lkr(total_revenue),
        total_orders=total_orders,
        total_customer_debt=round_lkr(total_customer_debt),
        low_stock_alerts=low_stock_alerts,
        tender_breakdown={k: round_lkr(v) for k, v in tender_breakdown.items()}
    )

