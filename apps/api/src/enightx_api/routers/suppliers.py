import uuid
from datetime import datetime, timezone
from decimal import Decimal
from typing import List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy.orm import Session

from ..arithmetic import round_money, calculate_moving_average_cost
from ..database import get_db
from ..models import (
    Supplier,
    PurchaseOrder,
    PurchaseOrderItem,
    GoodsReceivedNote,
    GRNItem,
    SupplierSettlement,
    Product,
    BranchInventory,
)
from ..schemas import (
    SupplierCreateRequest,
    SupplierUpdateRequest,
    SupplierResponse,
    PurchaseOrderCreateRequest,
    PurchaseOrderResponse,
    PurchaseOrderItemResponse,
    GRNCreateRequest,
    GRNResponse,
    GRNItemResponse,
    SupplierSettlementRequest,
    SupplierSettlementResponse,
)

router = APIRouter(tags=["Suppliers & Purchase Management"])

# ==========================================
# 1. Supplier Directory & Ledger
# ==========================================

@router.post("/api/v1/suppliers", response_model=SupplierResponse, status_code=status.HTTP_201_CREATED)
def create_supplier(req: SupplierCreateRequest, db: Session = Depends(get_db)):
    # Check if supplier_code exists for tenant
    existing = db.query(Supplier).filter(
        Supplier.tenant_id == req.tenant_id,
        Supplier.supplier_code == req.supplier_code
    ).first()
    if existing:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Supplier with code '{req.supplier_code}' already exists for this tenant."
        )

    supplier_id = str(uuid.uuid4())
    supplier = Supplier(
        supplier_id=supplier_id,
        tenant_id=req.tenant_id,
        supplier_code=req.supplier_code,
        name=req.name,
        contact_person=req.contact_person,
        phone=req.phone,
        email=req.email,
        address=req.address,
        balance_lkr=Decimal("0.00"),
        is_active=True,
    )
    db.add(supplier)
    db.commit()
    db.refresh(supplier)
    return supplier


@router.get("/api/v1/suppliers", response_model=List[SupplierResponse])
def list_suppliers(
    tenant_id: str = Query(...),
    is_active: Optional[bool] = Query(None),
    search: Optional[str] = Query(None),
    db: Session = Depends(get_db)
):
    query = db.query(Supplier).filter(Supplier.tenant_id == tenant_id)
    if is_active is not None:
        query = query.filter(Supplier.is_active == is_active)
    if search:
        s = f"%{search}%"
        query = query.filter(
            (Supplier.name.ilike(s)) |
            (Supplier.supplier_code.ilike(s)) |
            (Supplier.contact_person.ilike(s))
        )
    return query.order_by(Supplier.name.asc()).all()


@router.get("/api/v1/suppliers/{supplier_id}", response_model=SupplierResponse)
def get_supplier(
    supplier_id: str,
    tenant_id: str = Query(...),
    db: Session = Depends(get_db)
):
    supplier = db.query(Supplier).filter(
        Supplier.supplier_id == supplier_id,
        Supplier.tenant_id == tenant_id
    ).first()
    if not supplier:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Supplier '{supplier_id}' not found."
        )
    return supplier


@router.put("/api/v1/suppliers/{supplier_id}", response_model=SupplierResponse)
def update_supplier(
    supplier_id: str,
    req: SupplierUpdateRequest,
    tenant_id: str = Query(...),
    db: Session = Depends(get_db)
):
    supplier = db.query(Supplier).filter(
        Supplier.supplier_id == supplier_id,
        Supplier.tenant_id == tenant_id
    ).first()
    if not supplier:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Supplier '{supplier_id}' not found."
        )

    if req.name is not None:
        supplier.name = req.name
    if req.contact_person is not None:
        supplier.contact_person = req.contact_person
    if req.phone is not None:
        supplier.phone = req.phone
    if req.email is not None:
        supplier.email = req.email
    if req.address is not None:
        supplier.address = req.address
    if req.is_active is not None:
        supplier.is_active = req.is_active

    supplier.updated_at = datetime.now(timezone.utc)
    db.commit()
    db.refresh(supplier)
    return supplier


@router.post("/api/v1/suppliers/settlement", response_model=SupplierSettlementResponse, status_code=status.HTTP_201_CREATED)
def record_supplier_settlement(
    req: SupplierSettlementRequest,
    db: Session = Depends(get_db)
):
    supplier = db.query(Supplier).filter(
        Supplier.supplier_id == req.supplier_id,
        Supplier.tenant_id == req.tenant_id
    ).first()
    if not supplier:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Supplier '{req.supplier_id}' not found."
        )

    amount = round_money(req.amount_lkr)
    if amount <= Decimal("0.00"):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Settlement amount must be greater than zero."
        )

    settlement_id = uuid.uuid4()
    paid_at = req.paid_at or datetime.now(timezone.utc)
    settlement = SupplierSettlement(
        settlement_id=settlement_id,
        tenant_id=req.tenant_id,
        supplier_id=req.supplier_id,
        amount_lkr=amount,
        payment_method=req.payment_method,
        reference_number=req.reference_number,
        paid_at=paid_at,
        notes=req.notes,
        created_by=req.created_by,
    )
    db.add(settlement)

    # Reduce supplier balance
    new_balance = round_money(supplier.balance_lkr - amount)
    supplier.balance_lkr = new_balance
    supplier.updated_at = datetime.now(timezone.utc)

    db.commit()
    db.refresh(settlement)

    return SupplierSettlementResponse(
        settlement_id=settlement.settlement_id,
        tenant_id=settlement.tenant_id,
        supplier_id=settlement.supplier_id,
        amount_lkr=settlement.amount_lkr,
        payment_method=settlement.payment_method,
        reference_number=settlement.reference_number,
        paid_at=settlement.paid_at,
        notes=settlement.notes,
        created_by=settlement.created_by,
        created_at=settlement.created_at,
        remaining_balance_lkr=new_balance,
    )


# ==========================================
# 2. Purchase Orders (PO)
# ==========================================

@router.post("/api/v1/purchases/orders", response_model=PurchaseOrderResponse, status_code=status.HTTP_201_CREATED)
def create_purchase_order(
    req: PurchaseOrderCreateRequest,
    db: Session = Depends(get_db)
):
    supplier = db.query(Supplier).filter(
        Supplier.supplier_id == req.supplier_id,
        Supplier.tenant_id == req.tenant_id
    ).first()
    if not supplier:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Supplier '{req.supplier_id}' not found."
        )

    existing_po = db.query(PurchaseOrder).filter(
        PurchaseOrder.tenant_id == req.tenant_id,
        PurchaseOrder.po_number == req.po_number
    ).first()
    if existing_po:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Purchase order with number '{req.po_number}' already exists for this tenant."
        )

    if not req.items:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Purchase order must contain at least one item."
        )

    po_id = uuid.uuid4()
    order_items = []
    total_amount = Decimal("0.00")

    for item_data in req.items:
        ordered_qty = round_money(item_data.ordered_quantity)
        unit_cost = round_money(item_data.unit_cost_lkr)
        if ordered_qty <= Decimal("0.00"):
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail=f"Ordered quantity for product '{item_data.product_id}' must be greater than zero."
            )
        line_total = round_money(ordered_qty * unit_cost)
        total_amount += line_total

        po_item = PurchaseOrderItem(
            item_id=uuid.uuid4(),
            po_id=po_id,
            product_id=item_data.product_id,
            product_name=item_data.product_name,
            ordered_quantity=ordered_qty,
            received_quantity=Decimal("0.00"),
            unit_cost_lkr=unit_cost,
            line_total_lkr=line_total,
        )
        order_items.append(po_item)

    po = PurchaseOrder(
        po_id=po_id,
        tenant_id=req.tenant_id,
        branch_id=req.branch_id,
        po_number=req.po_number,
        supplier_id=req.supplier_id,
        status="ISSUED",
        order_date=datetime.now(timezone.utc),
        expected_delivery_date=req.expected_delivery_date,
        total_amount_lkr=round_money(total_amount),
        notes=req.notes,
        created_by=req.created_by,
        items=order_items,
    )
    db.add(po)
    db.commit()
    db.refresh(po)
    return po


@router.get("/api/v1/purchases/orders", response_model=List[PurchaseOrderResponse])
def list_purchase_orders(
    tenant_id: str = Query(...),
    branch_id: Optional[str] = Query(None),
    supplier_id: Optional[str] = Query(None),
    status_filter: Optional[str] = Query(None, alias="status"),
    db: Session = Depends(get_db)
):
    query = db.query(PurchaseOrder).filter(PurchaseOrder.tenant_id == tenant_id)
    if branch_id:
        query = query.filter(PurchaseOrder.branch_id == branch_id)
    if supplier_id:
        query = query.filter(PurchaseOrder.supplier_id == supplier_id)
    if status_filter:
        query = query.filter(PurchaseOrder.status == status_filter)
    return query.order_by(PurchaseOrder.created_at.desc()).all()


@router.get("/api/v1/purchases/orders/{po_id}", response_model=PurchaseOrderResponse)
def get_purchase_order(
    po_id: uuid.UUID,
    tenant_id: str = Query(...),
    db: Session = Depends(get_db)
):
    po = db.query(PurchaseOrder).filter(
        PurchaseOrder.po_id == po_id,
        PurchaseOrder.tenant_id == tenant_id
    ).first()
    if not po:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Purchase order '{po_id}' not found."
        )
    return po


@router.post("/api/v1/purchases/orders/{po_id}/cancel", response_model=PurchaseOrderResponse)
def cancel_purchase_order(
    po_id: uuid.UUID,
    tenant_id: str = Query(...),
    db: Session = Depends(get_db)
):
    po = db.query(PurchaseOrder).filter(
        PurchaseOrder.po_id == po_id,
        PurchaseOrder.tenant_id == tenant_id
    ).first()
    if not po:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Purchase order '{po_id}' not found."
        )

    if po.status in ("RECEIVED", "PARTIALLY_RECEIVED"):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Cannot cancel purchase order in '{po.status}' status."
        )

    po.status = "CANCELLED"
    po.updated_at = datetime.now(timezone.utc)
    db.commit()
    db.refresh(po)
    return po


# ==========================================
# 3. Goods Received Notes (GRN) & Costing
# ==========================================

@router.post("/api/v1/purchases/grn", response_model=GRNResponse, status_code=status.HTTP_201_CREATED)
def create_grn(
    req: GRNCreateRequest,
    db: Session = Depends(get_db)
):
    # Validate supplier exists
    supplier = db.query(Supplier).filter(
        Supplier.supplier_id == req.supplier_id,
        Supplier.tenant_id == req.tenant_id
    ).first()
    if not supplier:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Supplier '{req.supplier_id}' not found."
        )

    # Validate GRN number uniqueness
    existing_grn = db.query(GoodsReceivedNote).filter(
        GoodsReceivedNote.tenant_id == req.tenant_id,
        GoodsReceivedNote.grn_number == req.grn_number
    ).first()
    if existing_grn:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"GRN with number '{req.grn_number}' already exists for this tenant."
        )

    if not req.items:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="GRN must contain at least one item."
        )

    # If linked to a PO, validate PO
    po = None
    if req.po_id:
        po = db.query(PurchaseOrder).filter(
            PurchaseOrder.po_id == req.po_id,
            PurchaseOrder.tenant_id == req.tenant_id
        ).first()
        if not po:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND,
                detail=f"Purchase order '{req.po_id}' not found."
            )
        if po.status == "CANCELLED":
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="Cannot receive goods against a cancelled purchase order."
            )

    grn_id = uuid.uuid4()
    grn_items = []
    total_cost = Decimal("0.00")
    received_at = req.received_at or datetime.now(timezone.utc)

    for item_data in req.items:
        received_qty = round_money(item_data.received_quantity)
        unit_cost = round_money(item_data.unit_cost_lkr)
        if received_qty <= Decimal("0.00"):
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail=f"Received quantity for product '{item_data.product_id}' must be greater than zero."
            )
        line_total = round_money(received_qty * unit_cost)
        total_cost += line_total

        # 1. Product Stock and Moving Weighted Average Cost Update
        product = db.query(Product).filter(
            Product.product_id == item_data.product_id,
            Product.tenant_id == req.tenant_id
        ).first()

        if product:
            new_cost = calculate_moving_average_cost(
                current_qty=product.stock_on_hand,
                current_cost=product.cost_basis,
                received_qty=received_qty,
                received_cost=unit_cost,
            )
            product.cost_basis = new_cost
            product.stock_on_hand = round_money(product.stock_on_hand + received_qty)
            product.updated_at = datetime.now(timezone.utc)
        else:
            # Create product if not already in catalog
            product = Product(
                product_id=item_data.product_id,
                tenant_id=req.tenant_id,
                barcode=item_data.product_id,
                name=item_data.product_name,
                unit_price=unit_cost,
                cost_basis=unit_cost,
                tax_rate=Decimal("0.0000"),
                stock_on_hand=received_qty,
                is_active=True,
            )
            db.add(product)

        # 2. Branch Inventory Projection Update
        branch_inv = db.query(BranchInventory).filter(
            BranchInventory.tenant_id == req.tenant_id,
            BranchInventory.branch_id == req.branch_id,
            BranchInventory.product_id == item_data.product_id
        ).first()

        if branch_inv:
            branch_inv.stock_on_hand = round_money(branch_inv.stock_on_hand + received_qty)
            branch_inv.updated_at = datetime.now(timezone.utc)
        else:
            branch_inv = BranchInventory(
                id=uuid.uuid4(),
                tenant_id=req.tenant_id,
                branch_id=req.branch_id,
                product_id=item_data.product_id,
                stock_on_hand=received_qty,
                stock_in_transit=Decimal("0.00"),
                reorder_point=Decimal("0.00"),
            )
            db.add(branch_inv)

        # 3. If linked to PO, update PO line received_quantity
        if po:
            for po_item in po.items:
                if po_item.product_id == item_data.product_id:
                    po_item.received_quantity = round_money(po_item.received_quantity + received_qty)
                    break

        grn_item = GRNItem(
            item_id=uuid.uuid4(),
            grn_id=grn_id,
            product_id=item_data.product_id,
            product_name=item_data.product_name,
            received_quantity=received_qty,
            unit_cost_lkr=unit_cost,
            line_total_lkr=line_total,
            batch_number=item_data.batch_number,
            expiry_date=item_data.expiry_date,
        )
        grn_items.append(grn_item)

    total_cost = round_money(total_cost)

    # 4. Update Supplier Payable Balance
    supplier.balance_lkr = round_money(supplier.balance_lkr + total_cost)
    supplier.updated_at = datetime.now(timezone.utc)

    # 5. Update PO Status if linked
    if po:
        all_fulfilled = True
        any_received = False
        for po_item in po.items:
            if po_item.received_quantity < po_item.ordered_quantity:
                all_fulfilled = False
            if po_item.received_quantity > Decimal("0.00"):
                any_received = True

        if all_fulfilled:
            po.status = "RECEIVED"
        elif any_received:
            po.status = "PARTIALLY_RECEIVED"
        po.updated_at = datetime.now(timezone.utc)

    grn = GoodsReceivedNote(
        grn_id=grn_id,
        tenant_id=req.tenant_id,
        branch_id=req.branch_id,
        grn_number=req.grn_number,
        po_id=req.po_id,
        supplier_id=req.supplier_id,
        supplier_invoice_number=req.supplier_invoice_number,
        received_at=received_at,
        received_by=req.received_by,
        total_cost_lkr=total_cost,
        status="RECEIVED",
        notes=req.notes,
        items=grn_items,
    )
    db.add(grn)
    db.commit()
    db.refresh(grn)
    return grn


@router.get("/api/v1/purchases/grn", response_model=List[GRNResponse])
def list_grns(
    tenant_id: str = Query(...),
    branch_id: Optional[str] = Query(None),
    supplier_id: Optional[str] = Query(None),
    db: Session = Depends(get_db)
):
    query = db.query(GoodsReceivedNote).filter(GoodsReceivedNote.tenant_id == tenant_id)
    if branch_id:
        query = query.filter(GoodsReceivedNote.branch_id == branch_id)
    if supplier_id:
        query = query.filter(GoodsReceivedNote.supplier_id == supplier_id)
    return query.order_by(GoodsReceivedNote.created_at.desc()).all()


@router.get("/api/v1/purchases/grn/{grn_id}", response_model=GRNResponse)
def get_grn(
    grn_id: uuid.UUID,
    tenant_id: str = Query(...),
    db: Session = Depends(get_db)
):
    grn = db.query(GoodsReceivedNote).filter(
        GoodsReceivedNote.grn_id == grn_id,
        GoodsReceivedNote.tenant_id == tenant_id
    ).first()
    if not grn:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Goods Received Note '{grn_id}' not found."
        )
    return grn

