import uuid
from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
from typing import List, Optional
from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy.orm import Session
from sqlalchemy import or_

from ..schemas import (
    CustomerCreateRequest,
    CustomerResponse,
    CustomerInvoiceRequest,
    CustomerInvoiceResponse,
    CustomerSettlementRequest,
    CustomerSettlementResponse,
    CustomerLedgerEntryResponse,
)
from ..database import get_db
from ..models import Customer, CustomerLedger

router = APIRouter(prefix="/api/v1/customers", tags=["Customers & Ledger"])

def round_lkr(value: Decimal) -> Decimal:
    return value.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)

def to_customer_response(customer: Customer) -> CustomerResponse:
    credit_limit = round_lkr(customer.credit_limit)
    current_balance = round_lkr(customer.current_balance)
    available_credit = max(Decimal("0.00"), credit_limit - current_balance)
    return CustomerResponse(
        customer_id=customer.customer_id,
        tenant_id=customer.tenant_id,
        name=customer.name,
        phone=customer.phone,
        email=customer.email,
        nic_or_brn=customer.nic_or_brn,
        credit_limit=credit_limit,
        current_balance=current_balance,
        available_credit=available_credit,
        is_active=customer.is_active,
        created_at=customer.created_at
    )

@router.post("", response_model=CustomerResponse, status_code=status.HTTP_201_CREATED)
def create_customer(request: CustomerCreateRequest, db: Session = Depends(get_db)):
    customer_id = f"cust_{uuid.uuid4().hex[:10]}"
    customer = Customer(
        customer_id=customer_id,
        tenant_id=request.tenant_id,
        name=request.name.strip(),
        phone=request.phone.strip(),
        email=request.email.strip() if request.email else None,
        nic_or_brn=request.nic_or_brn.strip() if request.nic_or_brn else None,
        credit_limit=round_lkr(request.credit_limit),
        current_balance=Decimal("0.00"),
        is_active=True,
        created_at=datetime.now(timezone.utc),
        updated_at=datetime.now(timezone.utc)
    )
    db.add(customer)
    db.commit()
    db.refresh(customer)
    return to_customer_response(customer)

@router.get("", response_model=List[CustomerResponse])
def list_customers(
    tenant_id: str = "TENANT_LK_01",
    search: Optional[str] = None,
    limit: int = Query(50, le=200),
    db: Session = Depends(get_db)
):
    query = db.query(Customer).filter(Customer.tenant_id == tenant_id)
    if search:
        s = f"%{search.strip()}%"
        query = query.filter(
            or_(
                Customer.name.ilike(s),
                Customer.phone.ilike(s),
                Customer.nic_or_brn.ilike(s)
            )
        )
    customers = query.order_by(Customer.name.asc()).limit(limit).all()
    return [to_customer_response(c) for c in customers]

@router.get("/{customer_id}", response_model=CustomerResponse)
def get_customer(customer_id: str, db: Session = Depends(get_db)):
    customer = db.query(Customer).filter(Customer.customer_id == customer_id).first()
    if not customer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Customer '{customer_id}' not found"
        )
    return to_customer_response(customer)

@router.post("/invoice", response_model=CustomerInvoiceResponse)
def add_customer_credit_invoice(request: CustomerInvoiceRequest, db: Session = Depends(get_db)):
    customer = db.query(Customer).filter(Customer.customer_id == request.customer_id).first()
    if not customer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Customer '{request.customer_id}' not found"
        )
    if not customer.is_active:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Customer '{customer.name}' account is inactive"
        )

    amount = round_lkr(request.amount)
    if amount <= Decimal("0.00"):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Invoice amount must be greater than zero"
        )

    credit_limit = round_lkr(customer.credit_limit)
    current_balance = round_lkr(customer.current_balance)
    new_balance = round_lkr(current_balance + amount)

    # Check credit limit (if credit_limit > 0)
    if credit_limit > Decimal("0.00") and new_balance > credit_limit:
        available = max(Decimal("0.00"), credit_limit - current_balance)
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Credit limit exceeded. Limit: LKR {credit_limit}, Available: LKR {available}"
        )

    occurred = request.occurred_at or datetime.now(timezone.utc)
    entry_id = uuid.uuid4()
    ledger_entry = CustomerLedger(
        entry_id=entry_id,
        tenant_id=request.tenant_id,
        customer_id=customer.customer_id,
        branch_id=request.branch_id,
        counter_id=request.counter_id,
        entry_type="INVOICE",
        amount=amount,
        balance_after=new_balance,
        reference_id=request.reference_id,
        payment_method=None,
        actor_id=request.actor_id,
        notes=request.notes,
        occurred_at=occurred,
        created_at=datetime.now(timezone.utc)
    )

    customer.current_balance = new_balance
    customer.updated_at = datetime.now(timezone.utc)
    db.add(ledger_entry)
    db.commit()

    avail = max(Decimal("0.00"), credit_limit - new_balance) if credit_limit > Decimal("0.00") else Decimal("0.00")
    return CustomerInvoiceResponse(
        entry_id=entry_id,
        customer_id=customer.customer_id,
        amount=amount,
        new_balance=new_balance,
        available_credit=avail,
        occurred_at=occurred
    )

@router.post("/settlement", response_model=CustomerSettlementResponse)
def settle_customer_debt(request: CustomerSettlementRequest, db: Session = Depends(get_db)):
    customer = db.query(Customer).filter(Customer.customer_id == request.customer_id).first()
    if not customer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Customer '{request.customer_id}' not found"
        )

    amount = round_lkr(request.amount)
    if amount <= Decimal("0.00"):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Settlement amount must be greater than zero"
        )

    current_balance = round_lkr(customer.current_balance)
    new_balance = round_lkr(current_balance - amount)
    occurred = request.occurred_at or datetime.now(timezone.utc)
    entry_id = uuid.uuid4()

    ledger_entry = CustomerLedger(
        entry_id=entry_id,
        tenant_id=request.tenant_id,
        customer_id=customer.customer_id,
        branch_id=request.branch_id,
        counter_id=request.counter_id,
        entry_type="SETTLEMENT",
        amount=amount,
        balance_after=new_balance,
        reference_id=request.reference_id,
        payment_method=request.payment_method,
        actor_id=request.actor_id,
        notes=request.notes,
        occurred_at=occurred,
        created_at=datetime.now(timezone.utc)
    )

    customer.current_balance = new_balance
    customer.updated_at = datetime.now(timezone.utc)
    db.add(ledger_entry)
    db.commit()

    return CustomerSettlementResponse(
        entry_id=entry_id,
        customer_id=customer.customer_id,
        amount_settled=amount,
        remaining_balance=new_balance,
        settled_at=occurred
    )

@router.get("/{customer_id}/ledger", response_model=List[CustomerLedgerEntryResponse])
def get_customer_ledger(
    customer_id: str,
    limit: int = Query(50, le=200),
    db: Session = Depends(get_db)
):
    customer = db.query(Customer).filter(Customer.customer_id == customer_id).first()
    if not customer:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Customer '{customer_id}' not found"
        )
    entries = db.query(CustomerLedger).filter(
        CustomerLedger.customer_id == customer_id
    ).order_by(CustomerLedger.occurred_at.desc()).limit(limit).all()

    return [
        CustomerLedgerEntryResponse(
            entry_id=e.entry_id,
            customer_id=e.customer_id,
            entry_type=e.entry_type,
            amount=round_lkr(e.amount),
            balance_after=round_lkr(e.balance_after),
            reference_id=e.reference_id,
            payment_method=e.payment_method,
            actor_id=e.actor_id,
            notes=e.notes,
            occurred_at=e.occurred_at
        )
        for e in entries
    ]
