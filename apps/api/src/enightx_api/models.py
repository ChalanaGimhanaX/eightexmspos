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
    tenant_id = Column(String, nullable=False, default="TENANT_LK_01")
    category_id = Column(String, ForeignKey("categories.category_id", ondelete="SET NULL"), nullable=True)
    barcode = Column(String, nullable=False, index=True)
    name = Column(String, nullable=False)
    name_si = Column(String, nullable=True)
    name_ta = Column(String, nullable=True)
    unit_price = Column(Numeric(12, 2), nullable=False)
    cost_basis = Column(Numeric(12, 2), nullable=False, default=0)
    tax_rate = Column(Numeric(6, 4), nullable=False, default=0)
    is_active = Column(Boolean, nullable=False, default=True)
    created_at = Column(DateTime(timezone=True), nullable=False, default=func.now())
    updated_at = Column(DateTime(timezone=True), nullable=False, default=func.now(), onupdate=func.now())
    deleted_at = Column(DateTime(timezone=True), nullable=True)

    category = relationship("Category", backref="products")

