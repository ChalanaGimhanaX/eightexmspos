from typing import Optional
from fastapi import Header, HTTPException, status, Depends
from sqlalchemy.orm import Session
from .database import get_db
from .models import Device

def get_authenticated_device(
    x_device_token: Optional[str] = Header(None, alias="X-Device-Token"),
    db: Session = Depends(get_db)
) -> Device:
    """
    Authenticate incoming sync requests via the X-Device-Token header.
    Looks up the enrolled device in the database and ensures it is active.
    Raises 401 UNAUTHORIZED if the header is missing, token is unrecognized,
    or the device has been deactivated.
    """
    if not x_device_token:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Missing X-Device-Token header"
        )

    device = db.query(Device).filter(Device.token == x_device_token).first()
    if not device:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid or unrecognized device token"
        )

    # Check for deactivated device
    if hasattr(device, "is_active") and not device.is_active:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Device enrollment has been deactivated"
        )
    if hasattr(device, "status") and device.status and device.status.lower() in ("deactivated", "inactive"):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Device enrollment has been deactivated"
        )

    return device

