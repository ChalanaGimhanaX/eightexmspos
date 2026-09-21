import uuid
from datetime import datetime, timezone
from typing import Optional
from fastapi import APIRouter, Depends, Header, HTTPException, status
from sqlalchemy.orm import Session

from ..schemas import (
    DeviceEnrollmentRequest,
    DeviceEnrollmentResponse,
    DeviceHeartbeatRequest,
    DeviceHeartbeatResponse,
)
from ..database import get_db
from ..models import Device, DeviceHeartbeat

router = APIRouter(prefix="/api/v1/devices", tags=["Devices"])

def get_device_token(
    authorization: Optional[str] = Header(None),
    x_device_token: Optional[str] = Header(None, alias="X-Device-Token")
) -> str:
    if authorization and authorization.startswith("Bearer "):
        token = authorization[7:].strip()
        if token:
            return token
    if x_device_token and x_device_token.strip():
        return x_device_token.strip()
    raise HTTPException(
        status_code=status.HTTP_401_UNAUTHORIZED,
        detail="Missing device authentication token"
    )

@router.post("/enroll", response_model=DeviceEnrollmentResponse)
def enroll_device(request: DeviceEnrollmentRequest, db: Session = Depends(get_db)):
    # Check if a device with the same hardware_fingerprint and tenant_id already exists
    existing = db.query(Device).filter(
        Device.tenant_id == request.tenant_id,
        Device.hardware_fingerprint == request.hardware_fingerprint
    ).first()

    if existing:
        existing.app_version = request.app_version
        existing.device_name = request.device_name
        existing.device_code = request.device_code
        existing.branch_id = request.branch_id
        existing.last_seen_at = datetime.now(timezone.utc)
        db.commit()
        db.refresh(existing)
        return DeviceEnrollmentResponse(
            device_id=existing.device_id,
            device_generation=existing.device_generation,
            token=existing.token
        )

    device_id = f"dev_{uuid.uuid4().hex[:12]}"
    token = f"tok_{uuid.uuid4().hex}"
    new_device = Device(
        device_id=device_id,
        tenant_id=request.tenant_id,
        branch_id=request.branch_id,
        device_code=request.device_code,
        device_name=request.device_name,
        hardware_fingerprint=request.hardware_fingerprint,
        app_version=request.app_version,
        device_generation=1,
        token=token,
        status="online",
        last_seen_at=datetime.now(timezone.utc)
    )
    db.add(new_device)
    db.commit()
    db.refresh(new_device)

    return DeviceEnrollmentResponse(
        device_id=new_device.device_id,
        device_generation=new_device.device_generation,
        token=new_device.token
    )

@router.post("/heartbeat", response_model=DeviceHeartbeatResponse)
def device_heartbeat(
    request: DeviceHeartbeatRequest,
    token: str = Depends(get_device_token),
    db: Session = Depends(get_db)
):
    device = db.query(Device).filter(Device.device_id == request.device_id).first()
    if not device or device.token != token:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid device credentials or token"
        )

    if device.hardware_fingerprint != request.hardware_fingerprint:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Hardware fingerprint mismatch"
        )

    now = datetime.now(timezone.utc)
    device.last_seen_at = now
    device.app_version = request.app_version
    device.status = request.status or "online"
    if request.db_size_bytes is not None:
        device.db_size_bytes = request.db_size_bytes
    if request.last_sync_sequence is not None:
        device.last_sync_sequence = max(device.last_sync_sequence or 0, request.last_sync_sequence)

    heartbeat_entry = DeviceHeartbeat(
        heartbeat_id=uuid.uuid4(),
        device_id=device.device_id,
        received_at=now,
        app_version=request.app_version,
        db_size_bytes=request.db_size_bytes,
        last_sync_sequence=request.last_sync_sequence,
        status=request.status or "online"
    )
    db.add(heartbeat_entry)
    db.commit()

    return DeviceHeartbeatResponse(
        status="ok",
        server_time_utc=now,
        acknowledged_sequence=device.last_sync_sequence or 0
    )
