"""
Owner Web Overview and Freshness Monitoring Router.
Surfaces connected branch status, sync latency, and device heartbeats
for retail shop owners and administrators.
"""

from datetime import datetime, timezone
from enum import Enum
from typing import Optional, List, Dict
from fastapi import APIRouter, Depends, Query, Header, HTTPException, status
from pydantic import BaseModel, Field
from sqlalchemy.orm import Session

from ..database import get_db
from ..models import Device


class FreshnessStatus(str, Enum):
    ONLINE = "ONLINE"
    STALE = "STALE"
    OFFLINE = "OFFLINE"


class DeviceFreshnessItem(BaseModel):
    device_id: str
    device_code: Optional[str] = None
    device_name: Optional[str] = None
    status: str = Field(..., description="'ONLINE', 'STALE', or 'OFFLINE'")
    last_heartbeat_at: Optional[datetime] = None
    last_sync_at: Optional[datetime] = None
    sync_latency_seconds: Optional[float] = None
    app_version: Optional[str] = None
    is_active: bool = True


class BranchFreshnessSummary(BaseModel):
    branch_id: str
    status: str = Field(..., description="'ONLINE', 'STALE', or 'OFFLINE'")
    devices: List[DeviceFreshnessItem] = []


class OwnerFreshnessSummary(BaseModel):
    tenant_id: str
    as_of_utc: datetime
    total_devices: int
    online_devices_count: int
    stale_devices_count: int
    offline_devices_count: int
    branches: List[BranchFreshnessSummary] = []


OwnerFreshnessResponse = OwnerFreshnessSummary

router = APIRouter()

ONLINE_THRESHOLD_SECONDS = 300       # 5 minutes
STALE_THRESHOLD_SECONDS = 86400      # 24 hours


def _ensure_utc(dt: Optional[datetime]) -> Optional[datetime]:
    if dt is None:
        return None
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt


def classify_device_freshness(
    last_heartbeat_at: Optional[datetime],
    last_sync_at: Optional[datetime],
    now: datetime
) -> tuple[str, Optional[float]]:
    """
    Classify device freshness into ONLINE, STALE, or OFFLINE.
    - ONLINE: latest activity < 5 minutes ago.
    - STALE: latest activity between 5 minutes and 24 hours ago.
    - OFFLINE: latest activity > 24 hours ago or never.

    Also calculates sync_latency_seconds: elapsed seconds since last_sync_at, or None.
    """
    candidates = []
    hb_dt = _ensure_utc(last_heartbeat_at)
    sync_dt = _ensure_utc(last_sync_at)

    if hb_dt is not None:
        candidates.append(hb_dt)
    if sync_dt is not None:
        candidates.append(sync_dt)

    sync_latency_seconds: Optional[float] = None
    if sync_dt is not None:
        sync_latency_seconds = max(0.0, round((now - sync_dt).total_seconds(), 2))

    if not candidates:
        return FreshnessStatus.OFFLINE.value, sync_latency_seconds

    latest_activity = max(candidates)
    elapsed_seconds = (now - latest_activity).total_seconds()

    if elapsed_seconds < ONLINE_THRESHOLD_SECONDS:
        dev_status = FreshnessStatus.ONLINE.value
    elif elapsed_seconds <= STALE_THRESHOLD_SECONDS:
        dev_status = FreshnessStatus.STALE.value
    else:
        dev_status = FreshnessStatus.OFFLINE.value

    return dev_status, sync_latency_seconds


def classify_branch_freshness(devices: List[DeviceFreshnessItem]) -> str:
    """
    Classify branch freshness based on its devices:
    - ONLINE if any device is ONLINE (< 5 min)
    - STALE if any device is STALE and none is ONLINE (5 min - 24 hr)
    - OFFLINE if all devices are OFFLINE (> 24 hr or never) or no devices
    """
    if not devices:
        return FreshnessStatus.OFFLINE.value
    if any(d.status == FreshnessStatus.ONLINE.value for d in devices):
        return FreshnessStatus.ONLINE.value
    if any(d.status == FreshnessStatus.STALE.value for d in devices):
        return FreshnessStatus.STALE.value
    return FreshnessStatus.OFFLINE.value


@router.get("/freshness", response_model=OwnerFreshnessSummary)
def get_owner_freshness(
    tenant_id: str = Query(..., min_length=1, description="Tenant ID to query freshness overview for"),
    x_device_token: Optional[str] = Header(None, alias="X-Device-Token"),
    db: Session = Depends(get_db)
):
    """
    Read-only owner overview showing connected branch status, sync latency,
    and latest device heartbeats for the given tenant_id.
    """
    # Optional device token authentication:
    # If caller supplies an X-Device-Token, verify it belongs to the tenant
    if x_device_token:
        device = db.query(Device).filter(Device.token == x_device_token).first()
        if not device:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Invalid or unrecognized device token"
            )
        if hasattr(device, "is_active") and not device.is_active:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Device enrollment has been deactivated"
            )
        if device.tenant_id != tenant_id:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail=f"Token tenant '{device.tenant_id}' does not match requested tenant '{tenant_id}'"
            )

    now = datetime.now(timezone.utc)

    # Strictly scope query to tenant_id
    devices = (
        db.query(Device)
        .filter(Device.tenant_id == tenant_id)
        .order_by(Device.branch_id.asc(), Device.device_id.asc())
        .all()
    )

    online_count = 0
    stale_count = 0
    offline_count = 0

    branch_devices_map: Dict[str, List[DeviceFreshnessItem]] = {}

    for d in devices:
        dev_status, latency = classify_device_freshness(d.last_heartbeat_at, d.last_sync_at, now)
        if dev_status == FreshnessStatus.ONLINE.value:
            online_count += 1
        elif dev_status == FreshnessStatus.STALE.value:
            stale_count += 1
        else:
            offline_count += 1

        item = DeviceFreshnessItem(
            device_id=d.device_id,
            device_code=d.device_code,
            device_name=d.device_name,
            status=dev_status,
            last_heartbeat_at=d.last_heartbeat_at,
            last_sync_at=d.last_sync_at,
            sync_latency_seconds=latency,
            app_version=d.app_version,
            is_active=d.is_active
        )

        if d.branch_id not in branch_devices_map:
            branch_devices_map[d.branch_id] = []
        branch_devices_map[d.branch_id].append(item)

    branches: List[BranchFreshnessSummary] = []
    for branch_id, b_devs in branch_devices_map.items():
        b_status = classify_branch_freshness(b_devs)
        branches.append(BranchFreshnessSummary(
            branch_id=branch_id,
            status=b_status,
            devices=b_devs
        ))

    return OwnerFreshnessSummary(
        tenant_id=tenant_id,
        as_of_utc=now,
        total_devices=len(devices),
        online_devices_count=online_count,
        stale_devices_count=stale_count,
        offline_devices_count=offline_count,
        branches=branches
    )

