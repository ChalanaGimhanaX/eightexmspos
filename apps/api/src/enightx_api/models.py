from datetime import datetime
from sqlalchemy import Column, String, Integer, DateTime, ForeignKey, Numeric, func
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


