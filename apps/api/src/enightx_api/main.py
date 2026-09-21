from fastapi import FastAPI
from .routers import health, sync, devices, updates, reports
from .config import settings

app = FastAPI(
    title="Enightx POS API",
    version="1.0.0",
    description="Enightx Cloud Synchronization and Device Management API"
)

app.include_router(health.router)
app.include_router(sync.router)
app.include_router(devices.router)
app.include_router(updates.router)
app.include_router(reports.router)

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host=settings.HOST, port=settings.PORT)
