from datetime import datetime
from sqlalchemy import Column, String, Integer, DateTime, ForeignKey, Boolean, Numeric, func
from sqlalchemy.dialects.postgresql import UUID, JSONB
from sqlalchemy.orm import relationship
from .database import Base

class Device(Base):
    __tablename__ = "devices"

    device_id = Column(String, primary_key=True)
    tenant_id = Column(String, nullable=False)
    branch_id = Column(String, nullable=False)
    device_code = Column(String, nullable=False)
    device_name = Column(String, nullable=False)
    hardware_fingerprint = Column(String, nullable=False)
    app_version = Column(String, nullable=False)
    device_generation = Column(Integer, nullable=False, default=1)
    token = Column(String, nullable=False)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    last_seen_at = Column(DateTime(timezone=True), nullable=True)
    status = Column(String(32), nullable=False, default="online")
    last_sync_sequence = Column(Integer, nullable=False, default=0)
    db_size_bytes = Column(Integer, nullable=True)

    heartbeats = relationship("DeviceHeartbeat", back_populates="device", cascade="all, delete-orphan")

class DeviceHeartbeat(Base):
    __tablename__ = "device_heartbeats"

    heartbeat_id = Column(UUID(as_uuid=True), primary_key=True)
    device_id = Column(String, ForeignKey("devices.device_id", ondelete="CASCADE"), nullable=False)
    received_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    app_version = Column(String, nullable=False)
    db_size_bytes = Column(Integer, nullable=True)
    last_sync_sequence = Column(Integer, nullable=True)
    status = Column(String(32), nullable=False, default="online")

    device = relationship("Device", back_populates="heartbeats")

class SyncBatch(Base):
    __tablename__ = "sync_batches"

    batch_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String, nullable=False)
    source_device_id = Column(String, nullable=False)
    source_generation = Column(Integer, nullable=False)
    batch_sequence = Column(Integer, nullable=False)
    sent_at = Column(DateTime(timezone=True), nullable=False)
    acknowledged_sequence = Column(Integer, nullable=False, default=0)
    status = Column(String, nullable=False, default="acknowledged")
    received_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

    events = relationship("SyncEvent", back_populates="batch", cascade="all, delete-orphan")

class SyncEvent(Base):
    __tablename__ = "sync_events"

    event_id = Column(UUID(as_uuid=True), primary_key=True)
    batch_id = Column(UUID(as_uuid=True), ForeignKey("sync_batches.batch_id", ondelete="CASCADE"), nullable=True)
    tenant_id = Column(String, nullable=False)
    branch_id = Column(String, nullable=False)
    device_id = Column(String, nullable=False)
    device_generation = Column(Integer, nullable=False)
    source_sequence = Column(Integer, nullable=False)
    schema_version = Column(String, nullable=False)
    occurred_at = Column(DateTime(timezone=True), nullable=False)
    actor_id = Column(String, nullable=False)
    causal_reference = Column(String, nullable=True)
    payload = Column(JSONB, nullable=False)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

    batch = relationship("SyncBatch", back_populates="events")

class Shift(Base):
    __tablename__ = "shifts"

    shift_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False, index=True)
    branch_id = Column(String(64), nullable=False)
    counter_id = Column(String(64), nullable=False)
    cashier_id = Column(String(64), nullable=False)
    opened_at = Column(DateTime(timezone=True), nullable=False)
    closed_at = Column(DateTime(timezone=True), nullable=False)
    opening_float = Column(Numeric(12, 2), nullable=False)
    cash_received = Column(Numeric(12, 2), nullable=False, default=0.00)
    change_given = Column(Numeric(12, 2), nullable=False, default=0.00)
    cash_refunds = Column(Numeric(12, 2), nullable=False, default=0.00)
    cash_in = Column(Numeric(12, 2), nullable=False, default=0.00)
    cash_out = Column(Numeric(12, 2), nullable=False, default=0.00)
    expected_cash = Column(Numeric(12, 2), nullable=False)
    actual_counted_cash = Column(Numeric(12, 2), nullable=False)
    variance = Column(Numeric(12, 2), nullable=False)
    status = Column(String(32), nullable=False, default="closed")
    notes = Column(String(256), nullable=True)
    actor_id = Column(String(64), nullable=True)
    reconciled_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

class Customer(Base):
    __tablename__ = "customers"

    customer_id = Column(String(64), primary_key=True)
    tenant_id = Column(String(64), nullable=False, index=True)
    name = Column(String(128), nullable=False)
    phone = Column(String(32), nullable=False, index=True)
    email = Column(String(128), nullable=True)
    nic_or_brn = Column(String(32), nullable=True)
    credit_limit = Column(Numeric(12, 2), nullable=False, default=0.00)
    current_balance = Column(Numeric(12, 2), nullable=False, default=0.00)
    is_active = Column(Boolean, nullable=False, default=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())

    ledger_entries = relationship("CustomerLedger", back_populates="customer", cascade="all, delete-orphan")

class Category(Base):
    __tablename__ = "categories"

    category_id = Column(String, primary_key=True)
    tenant_id = Column(String, nullable=False, default="TENANT_LK_01")
    name = Column(String, nullable=False)
    description = Column(String, nullable=True)
    is_active = Column(Boolean, nullable=False, default=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())
    deleted_at = Column(DateTime(timezone=True), nullable=True)

class Product(Base):
    __tablename__ = "products"

    product_id = Column(String, primary_key=True)
    tenant_id = Column(String, nullable=False, index=True, default="TENANT_LK_01")
    category_id = Column(String, ForeignKey("categories.category_id", ondelete="SET NULL"), nullable=True)
    barcode = Column(String, nullable=False, index=True)
    name = Column(String, nullable=False)
    name_si = Column(String, nullable=True)
    name_ta = Column(String, nullable=True)
    unit_price = Column(Numeric(12, 2), nullable=False)
    cost_basis = Column(Numeric(12, 2), nullable=False, default=0)
    tax_rate = Column(Numeric(6, 4), nullable=False, default=0)
    stock_on_hand = Column(Numeric(12, 2), nullable=False, default=0)
    is_active = Column(Boolean, nullable=False, default=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())
    deleted_at = Column(DateTime(timezone=True), nullable=True)

    category = relationship("Category", backref="products")


class CustomerLedger(Base):
    __tablename__ = "customer_ledger"

    entry_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False)
    customer_id = Column(String(64), ForeignKey("customers.customer_id", ondelete="CASCADE"), nullable=False, index=True)
    branch_id = Column(String(64), nullable=False)
    counter_id = Column(String(64), nullable=False)
    entry_type = Column(String(32), nullable=False)
    amount = Column(Numeric(12, 2), nullable=False)
    balance_after = Column(Numeric(12, 2), nullable=False)
    reference_id = Column(String(128), nullable=True)
    payment_method = Column(String(32), nullable=True)
    actor_id = Column(String(64), nullable=False)
    notes = Column(String(256), nullable=True)
    occurred_at = Column(DateTime(timezone=True), nullable=False)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

    customer = relationship("Customer", back_populates="ledger_entries")

class BranchInventory(Base):
    __tablename__ = "branch_inventory"

    id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False)
    branch_id = Column(String(64), nullable=False, index=True)
    product_id = Column(String(64), nullable=False)
    stock_on_hand = Column(Numeric(12, 2), nullable=False, default=0.00)
    stock_in_transit = Column(Numeric(12, 2), nullable=False, default=0.00)
    reorder_point = Column(Numeric(12, 2), nullable=False, default=0.00)
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())

class StockTransfer(Base):
    __tablename__ = "stock_transfers"

    transfer_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False)
    source_branch_id = Column(String(64), nullable=False, index=True)
    dest_branch_id = Column(String(64), nullable=False, index=True)
    status = Column(String(32), nullable=False, default="REQUESTED", index=True)
    requested_by = Column(String(64), nullable=False)
    dispatched_by = Column(String(64), nullable=True)
    received_by = Column(String(64), nullable=True)
    requested_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    dispatched_at = Column(DateTime(timezone=True), nullable=True)
    received_at = Column(DateTime(timezone=True), nullable=True)
    notes = Column(String(256), nullable=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

    items = relationship("StockTransferItem", back_populates="transfer", cascade="all, delete-orphan")

class StockTransferItem(Base):
    __tablename__ = "stock_transfer_items"

    item_id = Column(UUID(as_uuid=True), primary_key=True)
    transfer_id = Column(UUID(as_uuid=True), ForeignKey("stock_transfers.transfer_id", ondelete="CASCADE"), nullable=False, index=True)
    product_id = Column(String(64), nullable=False)
    requested_quantity = Column(Numeric(12, 2), nullable=False)
    dispatched_quantity = Column(Numeric(12, 2), nullable=False, default=0.00)
    received_quantity = Column(Numeric(12, 2), nullable=False, default=0.00)

    transfer = relationship("StockTransfer", back_populates="items")

class Supplier(Base):
    __tablename__ = "suppliers"

    supplier_id = Column(String(64), primary_key=True)
    tenant_id = Column(String(64), nullable=False, index=True)
    supplier_code = Column(String(64), nullable=False)
    name = Column(String(255), nullable=False)
    contact_person = Column(String(255), nullable=True)
    phone = Column(String(64), nullable=True)
    email = Column(String(255), nullable=True)
    address = Column(String(500), nullable=True)
    balance_lkr = Column(Numeric(18, 2), nullable=False, default=0.00)
    is_active = Column(Boolean, nullable=False, default=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())

class PurchaseOrder(Base):
    __tablename__ = "purchase_orders"

    po_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False, index=True)
    branch_id = Column(String(64), nullable=False, index=True)
    po_number = Column(String(64), nullable=False)
    supplier_id = Column(String(64), ForeignKey("suppliers.supplier_id"), nullable=False, index=True)
    status = Column(String(32), nullable=False, default="ISSUED")  # DRAFT, ISSUED, PARTIALLY_RECEIVED, RECEIVED, CANCELLED
    order_date = Column(DateTime(timezone=True), nullable=False, default=func.now())
    expected_delivery_date = Column(DateTime(timezone=True), nullable=True)
    total_amount_lkr = Column(Numeric(18, 2), nullable=False, default=0.00)
    notes = Column(String(500), nullable=True)
    created_by = Column(String(64), nullable=False)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())

    items = relationship("PurchaseOrderItem", back_populates="purchase_order", cascade="all, delete-orphan")
    supplier = relationship("Supplier")

class PurchaseOrderItem(Base):
    __tablename__ = "purchase_order_items"

    item_id = Column(UUID(as_uuid=True), primary_key=True)
    po_id = Column(UUID(as_uuid=True), ForeignKey("purchase_orders.po_id", ondelete="CASCADE"), nullable=False, index=True)
    product_id = Column(String(64), nullable=False)
    product_name = Column(String(255), nullable=False)
    ordered_quantity = Column(Numeric(12, 2), nullable=False)
    received_quantity = Column(Numeric(12, 2), nullable=False, default=0.00)
    unit_cost_lkr = Column(Numeric(12, 2), nullable=False)
    line_total_lkr = Column(Numeric(12, 2), nullable=False)

    purchase_order = relationship("PurchaseOrder", back_populates="items")

class GoodsReceivedNote(Base):
    __tablename__ = "goods_received_notes"

    grn_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False, index=True)
    branch_id = Column(String(64), nullable=False, index=True)
    grn_number = Column(String(64), nullable=False)
    po_id = Column(UUID(as_uuid=True), ForeignKey("purchase_orders.po_id"), nullable=True, index=True)
    supplier_id = Column(String(64), ForeignKey("suppliers.supplier_id"), nullable=False, index=True)
    supplier_invoice_number = Column(String(128), nullable=True)
    received_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    received_by = Column(String(64), nullable=False)
    total_cost_lkr = Column(Numeric(18, 2), nullable=False, default=0.00)
    status = Column(String(32), nullable=False, default="RECEIVED")  # RECEIVED, CANCELLED
    notes = Column(String(500), nullable=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

    items = relationship("GRNItem", back_populates="grn", cascade="all, delete-orphan")
    supplier = relationship("Supplier")
    purchase_order = relationship("PurchaseOrder")

class GRNItem(Base):
    __tablename__ = "grn_items"

    item_id = Column(UUID(as_uuid=True), primary_key=True)
    grn_id = Column(UUID(as_uuid=True), ForeignKey("goods_received_notes.grn_id", ondelete="CASCADE"), nullable=False, index=True)
    product_id = Column(String(64), nullable=False)
    product_name = Column(String(255), nullable=False)
    received_quantity = Column(Numeric(12, 2), nullable=False)
    unit_cost_lkr = Column(Numeric(12, 2), nullable=False)
    line_total_lkr = Column(Numeric(12, 2), nullable=False)
    batch_number = Column(String(64), nullable=True)
    expiry_date = Column(DateTime(timezone=True), nullable=True)

    grn = relationship("GoodsReceivedNote", back_populates="items")

class SupplierSettlement(Base):
    __tablename__ = "supplier_settlements"

    settlement_id = Column(UUID(as_uuid=True), primary_key=True)
    tenant_id = Column(String(64), nullable=False, index=True)
    supplier_id = Column(String(64), ForeignKey("suppliers.supplier_id"), nullable=False, index=True)
    amount_lkr = Column(Numeric(18, 2), nullable=False)
    payment_method = Column(String(32), nullable=False)  # CASH, BANK_TRANSFER, CHEQUE
    reference_number = Column(String(128), nullable=True)
    paid_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    notes = Column(String(500), nullable=True)
    created_by = Column(String(64), nullable=False)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())

    supplier = relationship("Supplier")

