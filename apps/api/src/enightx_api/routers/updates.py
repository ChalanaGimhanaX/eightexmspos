import json
import logging
from typing import List, Dict, Any
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from pydantic import BaseModel

router = APIRouter(tags=["Updates"])
logger = logging.getLogger("enightx_api.updates")

class BroadcastPayload(BaseModel):
    version: str
    releaseNotes: str = ""
    downloadUrl: str = ""
    sha256: str = ""
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
    """
    await manager.connect(websocket)
    try:
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
async def broadcast_update_event(payload: BroadcastPayload):
    """
    Trigger real-time broadcast of a new version release to all connected desktop terminals.
    """
    message = {
        "event": "update_available",
        "manifest": payload.model_dump()
    }
    await manager.broadcast(message)
    return {
        "status": "broadcast_sent",
        "version": payload.version,
        "recipient_count": len(manager.active_connections)
    }
