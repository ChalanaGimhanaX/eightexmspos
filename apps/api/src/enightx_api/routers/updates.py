"""Release hints only. Clients fetch the signed HTTPS manifest independently."""
import asyncio
import base64
import hmac
import json
import os
from pathlib import Path

from fastapi import APIRouter, Header, HTTPException, WebSocket, WebSocketDisconnect

router = APIRouter(tags=["Updates"])


def current_release() -> str | None:
    path = Path(os.environ.get("ENIGHTX_RELEASE_MANIFEST", "/srv/enightx/downloads/version-v2.json"))
    try:
        if path.stat().st_size > 4_000_000:
            return None
        envelope = json.loads(path.read_text())
        payload = json.loads(base64.b64decode(envelope["payload"], validate=True))
        if payload.get("protocolVersion") != 2:
            return None
        return str(payload["releaseId"])
    except (OSError, ValueError, KeyError, TypeError):
        return None


@router.websocket("/ws/updates")
async def websocket_updates_endpoint(websocket: WebSocket):
    await websocket.accept()
    seen = None
    # Poll the published manifest per connection: works across API worker processes.
    # Keep one receive task alive across heartbeat ticks, never overlapping receives.
    pending = asyncio.create_task(websocket.receive_text())
    try:
        while True:
            release = await asyncio.to_thread(current_release)
            if release and release != seen:
                await websocket.send_json({"event": "update_available", "releaseId": release})
                seen = release
            done, _ = await asyncio.wait({pending}, timeout=20)
            if done:
                message = pending.result()
                pending = asyncio.create_task(websocket.receive_text())
                if message != "ping":
                    await websocket.close(code=1008)
                    return
            await websocket.send_json({"event": "pong"})
    except (WebSocketDisconnect, RuntimeError):
        pass
    finally:
        pending.cancel()
        await asyncio.gather(pending, return_exceptions=True)


@router.post("/api/v1/updates/broadcast")
async def broadcast_update_event(authorization: str | None = Header(default=None)):
    """Compatibility endpoint for release automation; cannot inject release URLs.

    All workers notice the published manifest on their next heartbeat tick.
    """
    token = os.environ.get("ENIGHTX_RELEASE_TOKEN")
    if not token:
        raise HTTPException(503, "Release administration is not configured")
    if not authorization or not hmac.compare_digest(authorization, f"Bearer {token}"):
        raise HTTPException(401, "Invalid release credentials")
    release = await asyncio.to_thread(current_release)
    if not release:
        raise HTTPException(409, "No v2 release has been published")
    return {"status": "published_release_will_be_detected", "releaseId": release}
