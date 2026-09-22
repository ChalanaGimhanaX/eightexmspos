"""
Pytest configuration and backward-compatible fixtures for Enightx Cloud API.

Provides:
- Session-level default device provisioning (TENANT_LK_01 / B01 / C01)
- Autouse header injection for existing 26 tests in test_catalog_sync and test_sync_endpoint
- Clean test client, db session, and device factory fixtures for new tenant isolation tests
"""

import os
import sys
import fcntl
from datetime import datetime, timezone
from pathlib import Path
from typing import Generator, Callable
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

# Ensure apps/api is on sys.path
repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from src.enightx_api.main import app
from src.enightx_api.database import SessionLocal, engine, Base
from src.enightx_api.models import Device

# Default device parameters matching existing tests in test_sync_endpoint.py
DEFAULT_TEST_DEVICE_ID = "C01"
DEFAULT_TEST_DEVICE_TOKEN = "tok_test_default_token_lk01"
DEFAULT_TEST_TENANT_ID = "TENANT_LK_01"
DEFAULT_TEST_BRANCH_ID = "B01"


@pytest.fixture(scope="session", autouse=True)
def serialize_concurrent_test_runs():
    """Serialize concurrent pytest processes on the shared database to eliminate data wipeout races."""
    lock_fd = os.open("/tmp/enightx_pytest_session.lock", os.O_CREAT | os.O_RDWR)
    fcntl.flock(lock_fd, fcntl.LOCK_EX)
    try:
        yield
    finally:
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_UN)
            os.close(lock_fd)
        except Exception:
            pass


@pytest.fixture(scope="session", autouse=True)
def setup_default_test_device():
    """
    Ensure the default test device exists in the database for the test session.
    This guarantees that existing tests in test_catalog_sync and test_sync_endpoint
    pass device authentication and tenant scoping as TENANT_LK_01 / B01 / C01.
    """
    Base.metadata.create_all(bind=engine)
    if engine.dialect.name == "postgresql":
        with engine.begin() as conn:
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS last_heartbeat_at TIMESTAMPTZ;"))
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS last_sync_at TIMESTAMPTZ;"))
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT TRUE;"))
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS status VARCHAR(32) NOT NULL DEFAULT 'ONLINE';"))
            conn.execute(text("CREATE UNIQUE INDEX IF NOT EXISTS ix_devices_token ON devices (token);"))

    db = SessionLocal()
    try:
        dev = db.query(Device).filter(Device.device_id == DEFAULT_TEST_DEVICE_ID).first()
        now = datetime.now(timezone.utc)
        if not dev:
            dev = Device(
                device_id=DEFAULT_TEST_DEVICE_ID,
                tenant_id=DEFAULT_TEST_TENANT_ID,
                branch_id=DEFAULT_TEST_BRANCH_ID,
                device_code="CTR-01",
                device_name="Counter 1 Main",
                hardware_fingerprint="hw_default_fingerprint",
                app_version="1.0.0",
                device_generation=1,
                token=DEFAULT_TEST_DEVICE_TOKEN,
                created_at=now,
                is_active=True,
                status="ONLINE"
            )
            db.add(dev)
            db.commit()
        else:
            # Reconcile existing device attributes to match expected constants
            dev.tenant_id = DEFAULT_TEST_TENANT_ID
            dev.branch_id = DEFAULT_TEST_BRANCH_ID
            dev.token = DEFAULT_TEST_DEVICE_TOKEN
            dev.is_active = True
            dev.status = "ONLINE"
            db.commit()
    finally:
        db.close()


@pytest.fixture(autouse=True)
def inject_default_headers_for_existing_clients(request):
    """
    Autouse fixture that ensures the TestClient instances created at module-level
    in tests/api/test_catalog_sync.py and tests/api/test_sync_endpoint.py have
    the default device token header configured, preserving 100% backward compatibility
    without modifying existing test files.
    """
    # 1. Inspect the module of the currently executing test
    if hasattr(request, "module") and hasattr(request.module, "client"):
        mod_name = getattr(request.module, "__name__", "")
        if "test_catalog_sync" in mod_name or "test_sync_endpoint" in mod_name:
            request.module.client.headers["X-Device-Token"] = DEFAULT_TEST_DEVICE_TOKEN

    # 2. Check sys.modules for any loaded variations of the legacy test modules
    for name, mod in list(sys.modules.items()):
        if ("test_catalog_sync" in name or "test_sync_endpoint" in name) and hasattr(mod, "client"):
            mod.client.headers["X-Device-Token"] = DEFAULT_TEST_DEVICE_TOKEN

    yield


@pytest.fixture
def clean_client() -> Generator[TestClient, None, None]:
    """
    Returns a pristine TestClient without default X-Device-Token header.
    Essential for negative testing (401 Unauthorized) and custom token headers.
    """
    client = TestClient(app)
    yield client


@pytest.fixture
def db_session() -> Generator:
    """Yields a dedicated SQLAlchemy session that is closed upon test completion."""
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()


@pytest.fixture
def device_factory() -> Generator[Callable, None, None]:
    """
    Factory fixture to dynamically create enrolled test devices for arbitrary tenants.
    Automatically cleans up provisioned devices on teardown.
    """
    created_device_ids = []

    def _create(
        tenant_id: str,
        branch_id: str,
        device_id: str,
        token: str,
        device_code: str = "DEV-TEST",
        device_name: str = "Test Device",
        is_active: bool = True,
        status: str = "ONLINE"
    ) -> Device:
        db = SessionLocal()
        try:
            dev = db.query(Device).filter(Device.device_id == device_id).first()
            now = datetime.now(timezone.utc)
            if not dev:
                dev = Device(
                    device_id=device_id,
                    tenant_id=tenant_id,
                    branch_id=branch_id,
                    device_code=device_code,
                    device_name=device_name,
                    hardware_fingerprint=f"hw_{device_id}",
                    app_version="1.0.0",
                    device_generation=1,
                    token=token,
                    created_at=now,
                    is_active=is_active,
                    status=status
                )
                db.add(dev)
                db.commit()
            else:
                dev.tenant_id = tenant_id
                dev.branch_id = branch_id
                dev.token = token
                dev.is_active = is_active
                dev.status = status
                db.commit()
            created_device_ids.append(device_id)
            return dev
        finally:
            db.close()

    yield _create

    # Teardown: remove created devices
    if created_device_ids:
        db = SessionLocal()
        try:
            db.query(Device).filter(Device.device_id.in_(created_device_ids)).delete(synchronize_session=False)
            db.commit()
        finally:
            db.close()
