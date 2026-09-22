import os
import shutil
from datetime import datetime, timezone
from fastapi import APIRouter, UploadFile, File, Form, HTTPException, Depends
from fastapi.responses import FileResponse
from typing import List, Dict, Any

router = APIRouter(prefix="/api/v1/backups", tags=["Backups"])

BACKUP_ROOT_DIR = os.getenv("BACKUP_ROOT_DIR", "/tmp/enightx_backups")

@router.post("/upload")
async def upload_backup(
    file: UploadFile = File(...),
    tenant_id: str = Form(...),
    device_id: str = Form(...)
):
    """
    Receives an encrypted backup file from a POS terminal and stores it securely.
    """
    if not file.filename:
        raise HTTPException(status_code=400, detail="Missing filename")

    tenant_backup_dir = os.path.join(BACKUP_ROOT_DIR, tenant_id)
    os.makedirs(tenant_backup_dir, exist_ok=True)

    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    safe_filename = f"{device_id}_{timestamp}_{os.path.basename(file.filename)}"
    destination_path = os.path.join(tenant_backup_dir, safe_filename)

    with open(destination_path, "wb") as buffer:
        shutil.copyfileobj(file.file, buffer)

    file_size = os.path.getsize(destination_path)

    return {
        "status": "success",
        "tenant_id": tenant_id,
        "device_id": device_id,
        "filename": safe_filename,
        "file_size": file_size,
        "uploaded_at_utc": datetime.now(timezone.utc).isoformat()
    }

@router.get("", response_model=List[Dict[str, Any]])
async def list_backups(tenant_id: str = "TENANT_LK_01"):
    """
    Lists available backup snapshots for a given tenant.
    """
    tenant_backup_dir = os.path.join(BACKUP_ROOT_DIR, tenant_id)
    if not os.path.exists(tenant_backup_dir):
        return []

    backups = []
    for f in sorted(os.listdir(tenant_backup_dir), reverse=True):
        fp = os.path.join(tenant_backup_dir, f)
        if os.path.isfile(fp):
            stat = os.stat(fp)
            backups.append({
                "filename": f,
                "file_size": stat.st_size,
                "created_at_utc": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc).isoformat()
            })
    return backups

@router.get("/download/{filename}")
async def download_backup(filename: str, tenant_id: str = "TENANT_LK_01"):
    """
    Downloads an encrypted backup file for disaster recovery.
    """
    tenant_backup_dir = os.path.join(BACKUP_ROOT_DIR, tenant_id)
    file_path = os.path.join(tenant_backup_dir, os.path.basename(filename))
    if not os.path.exists(file_path):
        raise HTTPException(status_code=404, detail="Backup file not found")
    return FileResponse(file_path, filename=filename, media_type="application/octet-stream")
