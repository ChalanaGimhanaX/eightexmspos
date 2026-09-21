from decimal import Decimal
from enum import Enum
from typing import List, Optional
from pydantic import BaseModel, Field
from datetime import datetime
import uuid

class TenderType(str, Enum):
    CASH = "CASH"
    CARD = "CARD"
    QR = "QR"
    CREDIT = "CREDIT"

class SaleLineSchema(BaseModel):
    line_id: uuid.UUID
    product_id: str
    product_name: str
    barcode: str
    quantity: Decimal
    unit_price: Decimal
    discount_rate: Decimal = Decimal("0.0")
    discount_fixed: Decimal = Decimal("0.0")
    tax_rate: Decimal = Decimal("0.0")
    line_total: Decimal

class TenderSchema(BaseModel):
    tender_id: uuid.UUID
    tender_type: TenderType
    amount_tendered: Decimal
    change_given: Decimal = Decimal("0.0")
    payment_reference: Optional[str] = None

class SalePayload(BaseModel):
    sale_id: uuid.UUID
    receipt_number: str
    shift_id: uuid.UUID
    customer_id: Optional[str] = None
    subtotal: Decimal
    discount_total: Decimal = Decimal("0.0")
    tax_total: Decimal = Decimal("0.0")
    grand_total: Decimal
    lines: List[SaleLineSchema]
    tenders: List[TenderSchema]

class SaleEventSchema(BaseModel):
    event_id: uuid.UUID
    tenant_id: str
    branch_id: str
    device_id: str
    device_generation: int = Field(ge=1)
    source_sequence: int = Field(ge=1)
    schema_version: str = "1.0"
    occurred_at: datetime
    actor_id: str
    causal_reference: Optional[str] = None
    payload: SalePayload

class SyncBatchRequest(BaseModel):
    batch_id: uuid.UUID
    tenant_id: str
    source_device_id: str
    source_generation: int = Field(ge=1)
    batch_sequence: int = Field(ge=1)
    sent_at: datetime
    events: List[SaleEventSchema]

class SyncBatchResponse(BaseModel):
    batch_id: uuid.UUID
    acknowledged_sequence: int
    status: str = "acknowledged"

class DeviceEnrollmentRequest(BaseModel):
    tenant_id: str
    branch_id: str
    device_code: str
    device_name: str
    hardware_fingerprint: str
    app_version: str

class DeviceEnrollmentResponse(BaseModel):
    device_id: str
    device_generation: int
    token: str

# --- Supplier Schemas ---
class SupplierCreateRequest(BaseModel):
    tenant_id: str
    supplier_code: str
    name: str
    contact_person: Optional[str] = None
    phone: Optional[str] = None
    email: Optional[str] = None
    address: Optional[str] = None

class SupplierUpdateRequest(BaseModel):
    name: Optional[str] = None
    contact_person: Optional[str] = None
    phone: Optional[str] = None
    email: Optional[str] = None
    address: Optional[str] = None
    is_active: Optional[bool] = None

class SupplierResponse(BaseModel):
    supplier_id: str
    tenant_id: str
    supplier_code: str
    name: str
    contact_person: Optional[str] = None
    phone: Optional[str] = None
    email: Optional[str] = None
    address: Optional[str] = None
    balance_lkr: Decimal
    is_active: bool
    created_at: datetime
    updated_at: datetime

    class Config:
        from_attributes = True

# --- Purchase Order Schemas ---
class PurchaseOrderItemCreate(BaseModel):
    product_id: str
    product_name: str
    ordered_quantity: Decimal
    unit_cost_lkr: Decimal

class PurchaseOrderCreateRequest(BaseModel):
    tenant_id: str
    branch_id: str
    po_number: str
    supplier_id: str
    expected_delivery_date: Optional[datetime] = None
    notes: Optional[str] = None
    created_by: str
    items: List[PurchaseOrderItemCreate]

class PurchaseOrderItemResponse(BaseModel):
    item_id: uuid.UUID
    product_id: str
    product_name: str
    ordered_quantity: Decimal
    received_quantity: Decimal
    unit_cost_lkr: Decimal
    line_total_lkr: Decimal

    class Config:
        from_attributes = True

class PurchaseOrderResponse(BaseModel):
    po_id: uuid.UUID
    tenant_id: str
    branch_id: str
    po_number: str
    supplier_id: str
    status: str
    order_date: datetime
    expected_delivery_date: Optional[datetime] = None
    total_amount_lkr: Decimal
    notes: Optional[str] = None
    created_by: str
    created_at: datetime
    updated_at: datetime
    items: List[PurchaseOrderItemResponse]

    class Config:
        from_attributes = True

# --- Goods Received Note (GRN) Schemas ---
class GRNItemCreate(BaseModel):
    product_id: str
    product_name: str
    received_quantity: Decimal
    unit_cost_lkr: Decimal
    batch_number: Optional[str] = None
    expiry_date: Optional[datetime] = None

class GRNCreateRequest(BaseModel):
    tenant_id: str
    branch_id: str
    grn_number: str
    supplier_id: str
    po_id: Optional[uuid.UUID] = None
    supplier_invoice_number: Optional[str] = None
    received_by: str
    received_at: Optional[datetime] = None
    notes: Optional[str] = None
    items: List[GRNItemCreate]

class GRNItemResponse(BaseModel):
    item_id: uuid.UUID
    product_id: str
    product_name: str
    received_quantity: Decimal
    unit_cost_lkr: Decimal
    line_total_lkr: Decimal
    batch_number: Optional[str] = None
    expiry_date: Optional[datetime] = None

    class Config:
        from_attributes = True

class GRNResponse(BaseModel):
    grn_id: uuid.UUID
    tenant_id: str
    branch_id: str
    grn_number: str
    po_id: Optional[uuid.UUID] = None
    supplier_id: str
    supplier_invoice_number: Optional[str] = None
    received_at: datetime
    received_by: str
    total_cost_lkr: Decimal
    status: str
    notes: Optional[str] = None
    created_at: datetime
    items: List[GRNItemResponse]

    class Config:
        from_attributes = True

# --- Supplier Settlement Schemas ---
class SupplierSettlementRequest(BaseModel):
    tenant_id: str
    supplier_id: str
    amount_lkr: Decimal
    payment_method: str = "CASH"  # CASH, BANK_TRANSFER, CHEQUE
    reference_number: Optional[str] = None
    paid_at: Optional[datetime] = None
    notes: Optional[str] = None
    created_by: str

class SupplierSettlementResponse(BaseModel):
    settlement_id: uuid.UUID
    tenant_id: str
    supplier_id: str
    amount_lkr: Decimal
    payment_method: str
    reference_number: Optional[str] = None
    paid_at: datetime
    notes: Optional[str] = None
    created_by: str
    created_at: datetime
    remaining_balance_lkr: Decimal

    class Config:
        from_attributes = True
