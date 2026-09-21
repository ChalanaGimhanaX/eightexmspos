import hmac
import hashlib
import uuid
from datetime import datetime, timezone, timedelta
from typing import Optional, List
from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.orm import Session
from sqlalchemy import func, desc
from pydantic import BaseModel, Field

from ..database import get_db
from ..models import Device, License, ProviderPayment

router = APIRouter(prefix="/api/v1/provider", tags=["Provider Administration"])

PROVIDER_SIGNING_KEY = "enightx_licence_secret_key_prod_2026"

def compute_license_signature(tenant_id: str, device_id: str, plan_type: str, expires_at: Optional[datetime]) -> str:
    expires_str = expires_at.strftime("%Y-%m-%dT%H:%M:%SZ") if expires_at else "PERMANENT"
    payload = f"{tenant_id}:{device_id}:{plan_type}:{expires_str}"
    sig = hmac.new(PROVIDER_SIGNING_KEY.encode("utf-8"), payload.encode("utf-8"), hashlib.sha256).digest()
    import base64
    return base64.b64encode(sig).decode("utf-8")


class IssueLicenseRequest(BaseModel):
    tenant_id: str
    device_id: str
    plan_type: str = Field(description="Trial, Subscription, or Permanent")
    duration_days: Optional[int] = None


class LicenseResponse(BaseModel):
    license_id: str
    tenant_id: str
    device_id: str
    plan_type: str
    issued_at: datetime
    expires_at: Optional[datetime]
    is_frozen: bool
    freeze_reason: Optional[str]
    signature: str


class FreezeDeviceRequest(BaseModel):
    reason: str


class VerifyPaymentRequest(BaseModel):
    tenant_id: str
    amount: float = 10000.00
    currency: str = "LKR"
    payment_method: str = "BANK_TRANSFER"
    reference: Optional[str] = None
    verified_by: str = "Provider Admin"


@router.get("/overview")
def get_provider_overview(db: Session = Depends(get_db)):
    # 1. Businesses / Tenants
    tenants = db.query(Device.tenant_id).distinct().all()
    tenant_list = [t[0] for t in tenants]

    # 2. Devices
    devices = db.query(Device).order_by(desc(Device.created_at)).all()
    now = datetime.now(timezone.utc)
    
    device_items = []
    online_count = 0
    offline_count = 0
    frozen_count = 0

    for d in devices:
        is_online = False
        if d.last_seen_at:
            delta = (now - d.last_seen_at).total_seconds()
            is_online = delta <= 300  # 5 minutes threshold

        if d.is_frozen:
            frozen_count += 1
        elif is_online:
            online_count += 1
        else:
            offline_count += 1

        device_items.append({
            "device_id": d.device_id,
            "tenant_id": d.tenant_id,
            "branch_id": d.branch_id,
            "device_name": d.device_name,
            "app_version": d.app_version,
            "status": "frozen" if d.is_frozen else ("online" if is_online else "offline"),
            "is_frozen": bool(d.is_frozen),
            "freeze_reason": d.freeze_reason,
            "last_seen_at": d.last_seen_at.isoformat() if d.last_seen_at else None,
            "created_at": d.created_at.isoformat() if d.created_at else None,
        })

    # 3. Licenses
    licenses = db.query(License).order_by(desc(License.issued_at)).limit(50).all()
    license_items = []
    for lic in licenses:
        license_items.append({
            "license_id": lic.license_id,
            "tenant_id": lic.tenant_id,
            "device_id": lic.device_id,
            "plan_type": lic.plan_type,
            "issued_at": lic.issued_at.isoformat(),
            "expires_at": lic.expires_at.isoformat() if lic.expires_at else None,
            "is_frozen": lic.is_frozen,
            "freeze_reason": lic.freeze_reason,
            "signature": lic.signature
        })

    # 4. Payments
    payments = db.query(ProviderPayment).order_by(desc(ProviderPayment.verified_at)).limit(50).all()
    payment_items = []
    total_revenue = 0.0
    for p in payments:
        total_revenue += float(p.amount)
        payment_items.append({
            "payment_id": p.payment_id,
            "tenant_id": p.tenant_id,
            "amount": float(p.amount),
            "currency": p.currency,
            "payment_method": p.payment_method,
            "reference": p.reference,
            "verified_by": p.verified_by,
            "verified_at": p.verified_at.isoformat()
        })

    return {
        "summary": {
            "total_businesses": len(tenant_list),
            "total_devices": len(devices),
            "devices_online": online_count,
            "devices_offline": offline_count,
            "devices_frozen": frozen_count,
            "total_licenses_issued": len(licenses),
            "total_revenue_lkr": total_revenue
        },
        "tenants": tenant_list,
        "devices": device_items,
        "licenses": license_items,
        "payments": payment_items
    }


@router.post("/licenses/issue", response_model=LicenseResponse)
def issue_license(req: IssueLicenseRequest, db: Session = Depends(get_db)):
    now = datetime.now(timezone.utc)
    expires_at = None

    plan_normalized = req.plan_type.capitalize()
    if plan_normalized == "Trial":
        duration = req.duration_days or 2
        expires_at = now + timedelta(days=duration)
    elif plan_normalized == "Subscription":
        duration = req.duration_days or 30
        expires_at = now + timedelta(days=duration)
    elif plan_normalized == "Permanent":
        expires_at = None
    else:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Unsupported plan type '{req.plan_type}'. Must be Trial, Subscription, or Permanent."
        )

    lic_id = f"LIC_{uuid.uuid4().hex[:12].upper()}"
    sig = compute_license_signature(req.tenant_id, req.device_id, plan_normalized, expires_at)

    lic = License(
        license_id=lic_id,
        tenant_id=req.tenant_id,
        device_id=req.device_id,
        plan_type=plan_normalized,
        issued_at=now,
        expires_at=expires_at,
        is_frozen=False,
        freeze_reason=None,
        signature=sig
    )

    db.add(lic)
    db.commit()
    db.refresh(lic)

    return LicenseResponse(
        license_id=lic.license_id,
        tenant_id=lic.tenant_id,
        device_id=lic.device_id,
        plan_type=lic.plan_type,
        issued_at=lic.issued_at,
        expires_at=lic.expires_at,
        is_frozen=lic.is_frozen,
        freeze_reason=lic.freeze_reason,
        signature=lic.signature
    )


@router.post("/devices/{device_id}/freeze")
def freeze_device(device_id: str, req: FreezeDeviceRequest, db: Session = Depends(get_db)):
    device = db.query(Device).filter(Device.device_id == device_id).first()
    if not device:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=f"Device '{device_id}' not found.")

    device.is_frozen = True
    device.freeze_reason = req.reason
    device.status = "frozen"

    # Also mark active licenses for this device as frozen
    db.query(License).filter(License.device_id == device_id).update({
        "is_frozen": True,
        "freeze_reason": req.reason
    })

    db.commit()
    return {"status": "ok", "message": f"Device '{device_id}' has been frozen.", "reason": req.reason}


@router.post("/devices/{device_id}/unfreeze")
def unfreeze_device(device_id: str, db: Session = Depends(get_db)):
    device = db.query(Device).filter(Device.device_id == device_id).first()
    if not device:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=f"Device '{device_id}' not found.")

    device.is_frozen = False
    device.freeze_reason = None
    device.status = "online"

    db.query(License).filter(License.device_id == device_id).update({
        "is_frozen": False,
        "freeze_reason": None
    })

    db.commit()
    return {"status": "ok", "message": f"Device '{device_id}' has been unfrozen."}


@router.post("/payments/verify")
def verify_payment(req: VerifyPaymentRequest, db: Session = Depends(get_db)):
    payment_id = f"PAY_{uuid.uuid4().hex[:12].upper()}"
    payment = ProviderPayment(
        payment_id=payment_id,
        tenant_id=req.tenant_id,
        amount=req.amount,
        currency=req.currency,
        payment_method=req.payment_method,
        reference=req.reference,
        verified_by=req.verified_by,
        verified_at=datetime.now(timezone.utc)
    )
    db.add(payment)

    # Issue renewed subscription for this tenant's devices
    devices = db.query(Device).filter(Device.tenant_id == req.tenant_id).all()
    issued_licenses = []
    now = datetime.now(timezone.utc)
    expires_at = now + timedelta(days=30)

    for dev in devices:
        lic_id = f"LIC_{uuid.uuid4().hex[:12].upper()}"
        sig = compute_license_signature(req.tenant_id, dev.device_id, "Subscription", expires_at)
        lic = License(
            license_id=lic_id,
            tenant_id=req.tenant_id,
            device_id=dev.device_id,
            plan_type="Subscription",
            issued_at=now,
            expires_at=expires_at,
            is_frozen=False,
            freeze_reason=None,
            signature=sig
        )
        db.add(lic)
        issued_licenses.append(lic_id)

    db.commit()

    return {
        "status": "ok",
        "payment_id": payment_id,
        "tenant_id": req.tenant_id,
        "amount": req.amount,
        "licenses_issued": issued_licenses,
        "valid_until": expires_at.isoformat()
    }
