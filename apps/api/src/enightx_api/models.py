from datetime import datetime
from sqlalchemy import Column, String, Integer, DateTime, ForeignKey, func
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

