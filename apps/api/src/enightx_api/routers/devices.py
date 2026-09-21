from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.orm import Session
from datetime import datetime, timezone
import uuid
from ..schemas import (
    DeviceEnrollmentRequest,
    DeviceEnrollmentResponse,
    HeartbeatRequest,
    HeartbeatResponse
)
from ..database import get_db
from ..models import Device

router = APIRouter(prefix="/api/v1/devices", tags=["Devices"])

@router.post("/enroll", response_model=DeviceEnrollmentResponse)
def enroll_device(request: DeviceEnrollmentRequest, db: Session = Depends(get_db)):
    device_id = f"dev_{uuid.uuid4().hex[:12]}"
    token = f"tok_{uuid.uuid4().hex}"
    device = Device(
        device_id=device_id,
        tenant_id=request.tenant_id,
        branch_id=request.branch_id,
        device_code=request.device_code,
        device_name=request.device_name,
        hardware_fingerprint=request.hardware_fingerprint,
        app_version=request.app_version,
        device_generation=1,
        token=token
    )
    db.add(device)
    db.commit()
    return DeviceEnrollmentResponse(
        device_id=device_id,
        device_generation=1,
        token=token
    )

@router.post("/heartbeat", response_model=HeartbeatResponse)
def device_heartbeat(request: HeartbeatRequest, db: Session = Depends(get_db)):
    device = db.query(Device).filter(Device.device_id == request.device_id).first()
    if not device:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Device not found")
    if device.token != request.token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid device token")

    # Update app version if upgraded
    if request.app_version and device.app_version != request.app_version:
        device.app_version = request.app_version
        db.commit()

    return HeartbeatResponse(
        acknowledged=True,
        server_time=datetime.now(timezone.utc)
    )
