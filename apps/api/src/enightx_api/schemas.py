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

class ShiftCloseRequest(BaseModel):
    shift_id: uuid.UUID
    tenant_id: str
    branch_id: str
    counter_id: str
    cashier_id: str
    opened_at: datetime
    closed_at: datetime
    opening_float: Decimal
    cash_received: Decimal = Decimal("0.00")
    change_given: Decimal = Decimal("0.00")
    cash_refunds: Decimal = Decimal("0.00")
    cash_in: Decimal = Decimal("0.00")
    cash_out: Decimal = Decimal("0.00")
    actual_counted_cash: Decimal
    notes: Optional[str] = None
    actor_id: Optional[str] = None

class ShiftCloseResponse(BaseModel):
    shift_id: uuid.UUID
    expected_cash: Decimal
    actual_counted_cash: Decimal
    variance: Decimal
    status: str = "closed"
    reconciled_at: datetime

class ShiftDetailResponse(BaseModel):
    shift_id: uuid.UUID
    tenant_id: str
    branch_id: str
    counter_id: str
    cashier_id: str
    opened_at: datetime
    closed_at: datetime
    opening_float: Decimal
    cash_received: Decimal
    change_given: Decimal
    cash_refunds: Decimal
    cash_in: Decimal
    cash_out: Decimal
    expected_cash: Decimal
    actual_counted_cash: Decimal
    variance: Decimal
    status: str
    notes: Optional[str] = None
    actor_id: Optional[str] = None
    reconciled_at: datetime

