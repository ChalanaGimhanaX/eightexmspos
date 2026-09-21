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

<<<<<<< HEAD
class DeviceHeartbeatRequest(BaseModel):
    device_id: str
    device_generation: int = Field(ge=1)
    hardware_fingerprint: str
    app_version: str
    db_size_bytes: Optional[int] = None
    last_sync_sequence: Optional[int] = 0
    status: Optional[str] = "online"
    sent_at: datetime

class DeviceHeartbeatResponse(BaseModel):
    status: str = "ok"
    server_time_utc: datetime
    acknowledged_sequence: int = 0
    command: Optional[str] = None

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

class CustomerCreateRequest(BaseModel):
    name: str
    phone: str
    email: Optional[str] = None
    nic_or_brn: Optional[str] = None
    credit_limit: Decimal = Decimal("0.00")
    tenant_id: str = "TENANT_LK_01"

class CustomerResponse(BaseModel):
    customer_id: str
    tenant_id: str
    name: str
    phone: str
    email: Optional[str] = None
    nic_or_brn: Optional[str] = None
    credit_limit: Decimal
    current_balance: Decimal
    available_credit: Decimal
    is_active: bool
    created_at: datetime

class CustomerInvoiceRequest(BaseModel):
    customer_id: str
    tenant_id: str
    branch_id: str
    counter_id: str
    amount: Decimal
    reference_id: Optional[str] = None
    actor_id: str
    notes: Optional[str] = None
    occurred_at: Optional[datetime] = None

class CustomerInvoiceResponse(BaseModel):
    entry_id: uuid.UUID
    customer_id: str
    amount: Decimal
    new_balance: Decimal
    available_credit: Decimal
    occurred_at: datetime

class CustomerSettlementRequest(BaseModel):
    customer_id: str
    tenant_id: str
    branch_id: str
    counter_id: str
    amount: Decimal
    payment_method: str = "CASH"
    reference_id: Optional[str] = None
    actor_id: str
    notes: Optional[str] = None
    occurred_at: Optional[datetime] = None

class CustomerSettlementResponse(BaseModel):
    entry_id: uuid.UUID
    customer_id: str
    amount_settled: Decimal
    remaining_balance: Decimal
    settled_at: datetime

class CustomerLedgerEntryResponse(BaseModel):
    entry_id: uuid.UUID
    customer_id: str
    entry_type: str
    amount: Decimal
    balance_after: Decimal
    reference_id: Optional[str] = None
    payment_method: Optional[str] = None
    actor_id: str
    notes: Optional[str] = None
    occurred_at: datetime

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

class BranchFreshnessItem(BaseModel):
    device_id: str
    device_code: str
    device_name: str
    tenant_id: str
    branch_id: str
    last_seen_at: Optional[datetime] = None
    last_sync_sequence: int = 0
    app_version: str
    is_stale: bool
    status: str
    stale_reason: Optional[str] = None

class BranchFreshnessReportResponse(BaseModel):
    tenant_id: str
    branch_id: Optional[str] = None
    checked_at: datetime
    has_stale_counters: bool
    warning_message: Optional[str] = None
    devices: List[BranchFreshnessItem]

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

class HeartbeatRequest(BaseModel):
    device_id: str
    token: str
    app_version: str
    status: str = "ONLINE"
    battery_level: Optional[int] = None
    ip_address: Optional[str] = None

class HeartbeatResponse(BaseModel):
    acknowledged: bool = True
    server_time: datetime

class CategorySchema(BaseModel):
    category_id: str
    name: str
    description: Optional[str] = None
    is_active: bool = True
    updated_at: datetime

class CatalogProductSchema(BaseModel):
    product_id: str
    category_id: Optional[str] = None
    barcode: str
    name: str
    name_si: Optional[str] = None
    name_ta: Optional[str] = None
    unit_price: Decimal
    cost_basis: Decimal = Decimal("0.00")
    tax_rate: Decimal = Decimal("0.0000")
    is_active: bool = True
    updated_at: datetime

class CatalogSyncResponse(BaseModel):
    server_time: datetime
    products: List[CatalogProductSchema] = []
    categories: List[CategorySchema] = []
    deleted_item_ids: List[str] = []
    has_more: bool = False

