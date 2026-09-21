from fastapi import APIRouter
import uuid
from ..schemas import DeviceEnrollmentRequest, DeviceEnrollmentResponse

router = APIRouter(prefix="/api/v1/devices", tags=["Devices"])

@router.post("/enroll", response_model=DeviceEnrollmentResponse)
def enroll_device(request: DeviceEnrollmentRequest):
    # Generates a persistent device ID and assigns generation 1
    device_id = f"dev_{uuid.uuid4().hex[:12]}"
    token = f"tok_{uuid.uuid4().hex}"
    return DeviceEnrollmentResponse(
        device_id=device_id,
        device_generation=1,
        token=token
    )
