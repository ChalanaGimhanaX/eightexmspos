import uuid
from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
from typing import Optional
from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.orm import Session

from ..schemas import (
    ShiftCloseRequest,
    ShiftCloseResponse,
    ShiftDetailResponse,
)
from ..database import get_db
from ..models import Shift

router = APIRouter(prefix="/api/v1/shifts", tags=["Shifts"])

def round_lkr(value: Decimal) -> Decimal:
    return value.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)

@router.post("/close", response_model=ShiftCloseResponse)
def close_shift(request: ShiftCloseRequest, db: Session = Depends(get_db)):
    # 1. Idempotency check: If shift was already closed and recorded, return existing reconciliation
    existing = db.query(Shift).filter(Shift.shift_id == request.shift_id).first()
    if existing:
        return ShiftCloseResponse(
            shift_id=existing.shift_id,
            expected_cash=existing.expected_cash,
            actual_counted_cash=existing.actual_counted_cash,
            variance=existing.variance,
            status=existing.status,
            reconciled_at=existing.reconciled_at
        )

    # 2. Strict LKR Cash Reconciliation Arithmetic (Half-Up)
    # Expected Cash = Opening Float + Cash Received - Change Given - Cash Refunds + Cash In - Cash Out
    expected_cash = round_lkr(
        request.opening_float
        + request.cash_received
        - request.change_given
        - request.cash_refunds
        + request.cash_in
        - request.cash_out
    )

    actual_counted = round_lkr(request.actual_counted_cash)
    variance = round_lkr(actual_counted - expected_cash)
    now = datetime.now(timezone.utc)

    # 3. Create and persist shift record
    shift = Shift(
        shift_id=request.shift_id,
        tenant_id=request.tenant_id,
        branch_id=request.branch_id,
        counter_id=request.counter_id,
        cashier_id=request.cashier_id,
        opened_at=request.opened_at,
        closed_at=request.closed_at,
        opening_float=round_lkr(request.opening_float),
        cash_received=round_lkr(request.cash_received),
        change_given=round_lkr(request.change_given),
        cash_refunds=round_lkr(request.cash_refunds),
        cash_in=round_lkr(request.cash_in),
        cash_out=round_lkr(request.cash_out),
        expected_cash=expected_cash,
        actual_counted_cash=actual_counted,
        variance=variance,
        status="closed",
        notes=request.notes,
        actor_id=request.actor_id,
        reconciled_at=now
    )

    db.add(shift)
    db.commit()
    db.refresh(shift)

    return ShiftCloseResponse(
        shift_id=shift.shift_id,
        expected_cash=shift.expected_cash,
        actual_counted_cash=shift.actual_counted_cash,
        variance=shift.variance,
        status=shift.status,
        reconciled_at=shift.reconciled_at
    )

@router.get("/{shift_id}", response_model=ShiftDetailResponse)
def get_shift_details(shift_id: uuid.UUID, db: Session = Depends(get_db)):
    shift = db.query(Shift).filter(Shift.shift_id == shift_id).first()
    if not shift:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Shift {shift_id} not found"
        )
    return ShiftDetailResponse(
        shift_id=shift.shift_id,
        tenant_id=shift.tenant_id,
        branch_id=shift.branch_id,
        counter_id=shift.counter_id,
        cashier_id=shift.cashier_id,
        opened_at=shift.opened_at,
        closed_at=shift.closed_at,
        opening_float=shift.opening_float,
        cash_received=shift.cash_received,
        change_given=shift.change_given,
        cash_refunds=shift.cash_refunds,
        cash_in=shift.cash_in,
        cash_out=shift.cash_out,
        expected_cash=shift.expected_cash,
        actual_counted_cash=shift.actual_counted_cash,
        variance=shift.variance,
        status=shift.status,
        notes=shift.notes,
        actor_id=shift.actor_id,
        reconciled_at=shift.reconciled_at
    )

