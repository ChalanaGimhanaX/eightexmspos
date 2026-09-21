from fastapi import APIRouter
from ..schemas import SyncBatchRequest, SyncBatchResponse

router = APIRouter(prefix="/api/v1/sync", tags=["Sync"])

@router.post("/push", response_model=SyncBatchResponse)
def push_sync_batch(batch: SyncBatchRequest):
    # Calculates the max acknowledged source sequence from the batch
    max_seq = 0
    if batch.events:
        max_seq = max(ev.source_sequence for ev in batch.events)
    else:
        max_seq = batch.batch_sequence

    return SyncBatchResponse(
        batch_id=batch.batch_id,
        acknowledged_sequence=max_seq,
        status="acknowledged"
    )
