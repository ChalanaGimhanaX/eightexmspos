import os
import json
import logging
from typing import List, Dict, Any, Optional
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from pydantic import BaseModel

router = APIRouter(tags=["Updates"])
logger = logging.getLogger("enightx_api.updates")

VERSION_JSON_PATHS = [
    "/srv/enightx/downloads/version.json",
    os.path.abspath(os.path.join(os.path.dirname(__file__), "../../../../../downloads/version.json")),
]

def get_latest_manifest() -> Dict[str, Any]:
    for path in VERSION_JSON_PATHS:
        if os.path.exists(path):
            try:
                with open(path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                    if isinstance(data, dict) and data.get("version"):
                        return data
            except Exception as e:
                logger.error(f"Error reading version manifest from {path}: {e}")
    return {}

class BroadcastPayload(BaseModel):
    version: Optional[str] = None
    releaseNotes: str = ""
    downloadUrl: str = ""
    updateZipUrl: Optional[str] = None
    setupZipUrl: Optional[str] = None
    sha256: str = ""
    zipSha256: Optional[str] = None
    zipSizeBytes: Optional[int] = None
    publishedAtUtc: str = ""
    mandatory: bool = False

class UpdateConnectionManager:
    def __init__(self):
        self.active_connections: List[WebSocket] = []

    async def connect(self, websocket: WebSocket):
        await websocket.accept()
        self.active_connections.append(websocket)
        logger.info(f"WebSocket client connected. Total active: {len(self.active_connections)}")

    def disconnect(self, websocket: WebSocket):
        if websocket in self.active_connections:
            self.active_connections.remove(websocket)
            logger.info(f"WebSocket client disconnected. Total active: {len(self.active_connections)}")

    async def broadcast(self, message: Dict[str, Any]):
        payload_text = json.dumps(message)
        dead_connections = []
        for conn in list(self.active_connections):
            try:
                await conn.send_text(payload_text)
            except Exception as e:
                logger.warning(f"Failed to send to client, removing: {e}")
                dead_connections.append(conn)
        for dead in dead_connections:
            self.disconnect(dead)

manager = UpdateConnectionManager()

@router.websocket("/ws/updates")
async def websocket_updates_endpoint(websocket: WebSocket):
    """
    WebSocket endpoint for counter desktop terminals.
    Terminals stay connected to receive instant live update notifications.
    Immediately transmits the latest version manifest on connect so terminals
    never miss an update due to transient reconnections.
    """
    await manager.connect(websocket)
    try:
        # Immediately notify newly connected terminal of the latest manifest
        latest = get_latest_manifest()
        if latest and latest.get("version"):
            await websocket.send_text(json.dumps({
                "event": "update_available",
                "manifest": latest
            }))
            logger.info(f"Pushed initial update_available manifest (v{latest.get('version')}) to connected client.")

        while True:
            data = await websocket.receive_text()
            # Handle heartbeat ping/pong
            if data == "ping":
                await websocket.send_text(json.dumps({"event": "pong"}))
    except WebSocketDisconnect:
        manager.disconnect(websocket)
    except Exception as e:
        logger.debug(f"WebSocket connection closed: {e}")
        manager.disconnect(websocket)

@router.post("/api/v1/updates/broadcast")
async def broadcast_update_event(payload: Optional[BroadcastPayload] = None):
    """
    Trigger real-time broadcast of a new version release to all connected desktop terminals.
    If payload is omitted or incomplete, defaults to latest /srv/enightx/downloads/version.json.
    """
    manifest = payload.model_dump() if payload else get_latest_manifest()
    if not manifest or not manifest.get("version"):
        manifest = get_latest_manifest()

    message = {
        "event": "update_available",
        "manifest": manifest
    }
    await manager.broadcast(message)
    return {
        "status": "broadcast_sent",
        "version": manifest.get("version", ""),
        "recipient_count": len(manager.active_connections)
    }

