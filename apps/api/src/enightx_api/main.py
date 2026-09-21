from contextlib import asynccontextmanager
from fastapi import FastAPI
from .routers import health, sync, devices, updates, shifts, customers, inventory, reports, suppliers, provider
from .dashboard import router as dashboard_router
from .provider_dashboard import router as provider_dashboard_router
from .config import settings
from .database import engine, Base

@asynccontextmanager
async def lifespan(app: FastAPI):
    Base.metadata.create_all(bind=engine)
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
app.include_router(shifts.router)
app.include_router(customers.router)
app.include_router(inventory.router)
app.include_router(reports.router)
app.include_router(suppliers.router)
app.include_router(dashboard_router)
app.include_router(provider.router)
app.include_router(provider_dashboard_router)

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host=settings.HOST, port=settings.PORT)
