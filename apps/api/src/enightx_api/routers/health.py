from fastapi import APIRouter
from datetime import datetime, timezone

router = APIRouter(tags=["Health"])

@router.get("/health")
def get_health():
    return {
        "status": "ok",
        "service": "enightx-api",
        "version": "1.0.0",
        "timestamp": datetime.now(timezone.utc).isoformat()
    }
