from decimal import Decimal
from enum import Enum
from typing import List, Optional, Dict
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

class SalesSummaryReportResponse(BaseModel):
    tenant_id: str
    branch_id: Optional[str] = None
    total_sales_count: int
    total_revenue: Decimal
    total_tax: Decimal
    total_discount: Decimal
    tender_breakdown: Dict[str, Decimal]
    average_ticket_size: Decimal

class TopProductReportItem(BaseModel):
    product_id: str
    product_name: str
    quantity_sold: Decimal
    revenue: Decimal

class CashierPerformanceItem(BaseModel):
    cashier_id: str
    shifts_worked: int
    total_sales_amount: Decimal
    total_variance: Decimal

class DashboardSummaryResponse(BaseModel):
    tenant_id: str
    total_revenue: Decimal
    total_orders: int
    total_customer_debt: Decimal
    low_stock_alerts: int
    tender_breakdown: Dict[str, Decimal]

