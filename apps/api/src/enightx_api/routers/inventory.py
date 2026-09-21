import uuid
from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
from typing import List, Optional
from fastapi import APIRouter, Depends, HTTPException, Query, status
from pydantic import BaseModel
from sqlalchemy.orm import Session

from ..schemas import (
    StockTransferRequest,
    StockTransferResponse,
    StockTransferItemResponse,
    BranchStockAdjustmentRequest,
    BranchInventoryResponse,
)
from ..database import get_db
from ..models import BranchInventory, StockTransfer, StockTransferItem

router = APIRouter(prefix="/api/v1/inventory", tags=["Inventory & Multi-Branch Transfers"])

def round_lkr(value: Decimal) -> Decimal:
    return value.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)

class DispatchRequest(BaseModel):
    dispatched_by: str
    notes: Optional[str] = None

class ReceiveRequest(BaseModel):
    received_by: str
    notes: Optional[str] = None

def to_transfer_response(t: StockTransfer) -> StockTransferResponse:
    items = [
        StockTransferItemResponse(
            item_id=i.item_id,
            product_id=i.product_id,
            requested_quantity=round_lkr(i.requested_quantity),
            dispatched_quantity=round_lkr(i.dispatched_quantity),
            received_quantity=round_lkr(i.received_quantity)
        )
        for i in t.items
    ]
    return StockTransferResponse(
        transfer_id=t.transfer_id,
        tenant_id=t.tenant_id,
        source_branch_id=t.source_branch_id,
        dest_branch_id=t.dest_branch_id,
        status=t.status,
        requested_by=t.requested_by,
        dispatched_by=t.dispatched_by,
        received_by=t.received_by,
        requested_at=t.requested_at,
        dispatched_at=t.dispatched_at,
        received_at=t.received_at,
        notes=t.notes,
        items=items
    )

def get_or_create_inventory(
    db: Session,
    tenant_id: str,
    branch_id: str,
    product_id: str
) -> BranchInventory:
    inv = db.query(BranchInventory).filter(
        BranchInventory.tenant_id == tenant_id,
        BranchInventory.branch_id == branch_id,
        BranchInventory.product_id == product_id
    ).first()
    if not inv:
        inv = BranchInventory(
            id=uuid.uuid4(),
            tenant_id=tenant_id,
            branch_id=branch_id,
            product_id=product_id,
            stock_on_hand=Decimal("0.00"),
            stock_in_transit=Decimal("0.00"),
            reorder_point=Decimal("0.00")
        )
        db.add(inv)
        db.flush()
    return inv

@router.post("/branch/adjust", response_model=BranchInventoryResponse)
def adjust_branch_stock(request: BranchStockAdjustmentRequest, db: Session = Depends(get_db)):
    inv = get_or_create_inventory(db, request.tenant_id, request.branch_id, request.product_id)
    inv.stock_on_hand = round_lkr(request.stock_on_hand)
    inv.reorder_point = round_lkr(request.reorder_point)
    inv.updated_at = datetime.now(timezone.utc)
    db.commit()
    db.refresh(inv)

    return BranchInventoryResponse(
        tenant_id=inv.tenant_id,
        branch_id=inv.branch_id,
        product_id=inv.product_id,
        stock_on_hand=round_lkr(inv.stock_on_hand),
        stock_in_transit=round_lkr(inv.stock_in_transit),
        reorder_point=round_lkr(inv.reorder_point),
        updated_at=inv.updated_at
    )

@router.get("/branch/{branch_id}", response_model=List[BranchInventoryResponse])
def get_branch_inventory(
    branch_id: str,
    tenant_id: str = "TENANT_LK_01",
    db: Session = Depends(get_db)
):
    rows = db.query(BranchInventory).filter(
        BranchInventory.tenant_id == tenant_id,
        BranchInventory.branch_id == branch_id
    ).all()
    return [
        BranchInventoryResponse(
            tenant_id=r.tenant_id,
            branch_id=r.branch_id,
            product_id=r.product_id,
            stock_on_hand=round_lkr(r.stock_on_hand),
            stock_in_transit=round_lkr(r.stock_in_transit),
            reorder_point=round_lkr(r.reorder_point),
            updated_at=r.updated_at
        )
        for r in rows
    ]

@router.post("/transfers/request", response_model=StockTransferResponse, status_code=status.HTTP_201_CREATED)
def request_stock_transfer(request: StockTransferRequest, db: Session = Depends(get_db)):
    if not request.items:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Transfer request must contain at least one item"
        )
    if request.source_branch_id == request.dest_branch_id:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Source and destination branches cannot be identical"
        )

    transfer_id = uuid.uuid4()
    transfer = StockTransfer(
        transfer_id=transfer_id,
        tenant_id=request.tenant_id,
        source_branch_id=request.source_branch_id,
        dest_branch_id=request.dest_branch_id,
        status="REQUESTED",
        requested_by=request.requested_by,
        notes=request.notes,
        requested_at=datetime.now(timezone.utc)
    )
    db.add(transfer)
    db.flush()

    for item in request.items:
        qty = round_lkr(item.requested_quantity)
        if qty <= Decimal("0.00"):
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail=f"Requested quantity for product '{item.product_id}' must be greater than zero"
            )
        transfer_item = StockTransferItem(
            item_id=uuid.uuid4(),
            transfer_id=transfer_id,
            product_id=item.product_id,
            requested_quantity=qty,
            dispatched_quantity=Decimal("0.00"),
            received_quantity=Decimal("0.00")
        )
        db.add(transfer_item)

    db.commit()
    db.refresh(transfer)
    return to_transfer_response(transfer)

@router.post("/transfers/{transfer_id}/dispatch", response_model=StockTransferResponse)
def dispatch_stock_transfer(
    transfer_id: uuid.UUID,
    request: DispatchRequest,
    db: Session = Depends(get_db)
):
    transfer = db.query(StockTransfer).filter(StockTransfer.transfer_id == transfer_id).first()
    if not transfer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Transfer '{transfer_id}' not found"
        )
    if transfer.status != "REQUESTED":
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Cannot dispatch transfer with status '{transfer.status}' (expected 'REQUESTED')"
        )

    now = datetime.now(timezone.utc)
    for item in transfer.items:
        source_inv = get_or_create_inventory(
            db, transfer.tenant_id, transfer.source_branch_id, item.product_id
        )
        if source_inv.stock_on_hand < item.requested_quantity:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail=(
                    f"Insufficient stock for product '{item.product_id}' at branch '{transfer.source_branch_id}'. "
                    f"Available: {source_inv.stock_on_hand}, Requested: {item.requested_quantity}"
                )
            )
        # Deduct from source branch stock on hand
        source_inv.stock_on_hand = round_lkr(source_inv.stock_on_hand - item.requested_quantity)
        source_inv.updated_at = now

        # Add to destination branch in-transit stock
        dest_inv = get_or_create_inventory(
            db, transfer.tenant_id, transfer.dest_branch_id, item.product_id
        )
        dest_inv.stock_in_transit = round_lkr(dest_inv.stock_in_transit + item.requested_quantity)
        dest_inv.updated_at = now

        item.dispatched_quantity = item.requested_quantity

    transfer.status = "DISPATCHED"
    transfer.dispatched_by = request.dispatched_by
    transfer.dispatched_at = now
    if request.notes:
        transfer.notes = f"{transfer.notes or ''} | Dispatch: {request.notes}".strip(" |")

    db.commit()
    db.refresh(transfer)
    return to_transfer_response(transfer)

@router.post("/transfers/{transfer_id}/receive", response_model=StockTransferResponse)
def receive_stock_transfer(
    transfer_id: uuid.UUID,
    request: ReceiveRequest,
    db: Session = Depends(get_db)
):
    transfer = db.query(StockTransfer).filter(StockTransfer.transfer_id == transfer_id).first()
    if not transfer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Transfer '{transfer_id}' not found"
        )
    if transfer.status != "DISPATCHED":
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Cannot receive transfer with status '{transfer.status}' (expected 'DISPATCHED')"
        )

    now = datetime.now(timezone.utc)
    for item in transfer.items:
        dest_inv = get_or_create_inventory(
            db, transfer.tenant_id, transfer.dest_branch_id, item.product_id
        )
        # Move from in-transit to stock on hand
        dest_inv.stock_in_transit = max(
            Decimal("0.00"),
            round_lkr(dest_inv.stock_in_transit - item.dispatched_quantity)
        )
        dest_inv.stock_on_hand = round_lkr(dest_inv.stock_on_hand + item.dispatched_quantity)
        dest_inv.updated_at = now

        item.received_quantity = item.dispatched_quantity

    transfer.status = "RECEIVED"
    transfer.received_by = request.received_by
    transfer.received_at = now
    if request.notes:
        transfer.notes = f"{transfer.notes or ''} | Receive: {request.notes}".strip(" |")

    db.commit()
    db.refresh(transfer)
    return to_transfer_response(transfer)

@router.get("/transfers", response_model=List[StockTransferResponse])
def list_stock_transfers(
    tenant_id: str = "TENANT_LK_01",
    branch_id: Optional[str] = None,
    status_filter: Optional[str] = None,
    limit: int = Query(50, le=200),
    db: Session = Depends(get_db)
):
    query = db.query(StockTransfer).filter(StockTransfer.tenant_id == tenant_id)
    if branch_id:
        query = query.filter(
            (StockTransfer.source_branch_id == branch_id) | (StockTransfer.dest_branch_id == branch_id)
        )
    if status_filter:
        query = query.filter(StockTransfer.status == status_filter)

    transfers = query.order_by(StockTransfer.requested_at.desc()).limit(limit).all()
    return [to_transfer_response(t) for t in transfers]

@router.get("/transfers/{transfer_id}", response_model=StockTransferResponse)
def get_stock_transfer(transfer_id: uuid.UUID, db: Session = Depends(get_db)):
    transfer = db.query(StockTransfer).filter(StockTransfer.transfer_id == transfer_id).first()
    if not transfer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Transfer '{transfer_id}' not found"
        )
    return to_transfer_response(transfer)

