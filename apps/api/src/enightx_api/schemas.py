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

class StockTransferItemRequest(BaseModel):
    product_id: str
    requested_quantity: Decimal

class StockTransferItemResponse(BaseModel):
    item_id: uuid.UUID
    product_id: str
    requested_quantity: Decimal
    dispatched_quantity: Decimal
    received_quantity: Decimal

class StockTransferRequest(BaseModel):
    tenant_id: str = "TENANT_LK_01"
    source_branch_id: str
    dest_branch_id: str
    requested_by: str
    notes: Optional[str] = None
    items: List[StockTransferItemRequest]

class StockTransferResponse(BaseModel):
    transfer_id: uuid.UUID
    tenant_id: str
    source_branch_id: str
    dest_branch_id: str
    status: str
    requested_by: str
    dispatched_by: Optional[str] = None
    received_by: Optional[str] = None
    requested_at: datetime
    dispatched_at: Optional[datetime] = None
    received_at: Optional[datetime] = None
    notes: Optional[str] = None
    items: List[StockTransferItemResponse]

class BranchStockAdjustmentRequest(BaseModel):
    tenant_id: str = "TENANT_LK_01"
    branch_id: str
    product_id: str
    stock_on_hand: Decimal
    reorder_point: Decimal = Decimal("0.00")

class BranchInventoryResponse(BaseModel):
    tenant_id: str
    branch_id: str
    product_id: str
    stock_on_hand: Decimal
    stock_in_transit: Decimal
    reorder_point: Decimal
    updated_at: datetime

