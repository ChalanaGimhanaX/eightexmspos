from contextlib import asynccontextmanager
from fastapi import FastAPI
from sqlalchemy import text
from .routers import health, sync, devices, updates, owner
from .config import settings
from .database import engine, Base

@asynccontextmanager
async def lifespan(app: FastAPI):
    Base.metadata.create_all(bind=engine)
    if engine.dialect.name == "postgresql":
        with engine.begin() as conn:
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS last_heartbeat_at TIMESTAMPTZ;"))
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS last_sync_at TIMESTAMPTZ;"))
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT TRUE;"))
            conn.execute(text("ALTER TABLE devices ADD COLUMN IF NOT EXISTS status VARCHAR(32) NOT NULL DEFAULT 'ONLINE';"))
            conn.execute(text("CREATE UNIQUE INDEX IF NOT EXISTS ix_devices_token ON devices (token);"))
    yield

app = FastAPI(
    title="Enightx POS API",
    version="1.0.0",
    description="Enightx Cloud Synchronization and Device Management API",
    lifespan=lifespan
)

app.include_router(health.router)
app.include_router(sync.router)
app.include_router(devices.router)
app.include_router(updates.router)
app.include_router(owner.router, prefix="/api/v1/owner", tags=["owner"])

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host=settings.HOST, port=settings.PORT)
