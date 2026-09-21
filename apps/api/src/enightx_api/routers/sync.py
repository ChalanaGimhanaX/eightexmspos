from fastapi import APIRouter, Depends
from sqlalchemy.orm import Session
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.exc import IntegrityError
from ..schemas import SyncBatchRequest, SyncBatchResponse
from ..database import get_db
from ..models import SyncBatch, SyncEvent

router = APIRouter(prefix="/api/v1/sync", tags=["Sync"])

@router.post("/push", response_model=SyncBatchResponse)
def push_sync_batch(batch: SyncBatchRequest, db: Session = Depends(get_db)):
    # 1. Idempotency check: If batch has already been processed, return existing status and ack sequence
    existing_batch = db.query(SyncBatch).filter(SyncBatch.batch_id == batch.batch_id).first()
    if existing_batch:
        return SyncBatchResponse(
            batch_id=existing_batch.batch_id,
            acknowledged_sequence=existing_batch.acknowledged_sequence,
            status=existing_batch.status
        )

    # 2. Calculate the max acknowledged source sequence from the batch
    if batch.events:
        max_seq = max(ev.source_sequence for ev in batch.events)
    else:
        max_seq = batch.batch_sequence

    # 3. Create and persist batch record
    sync_batch = SyncBatch(
        batch_id=batch.batch_id,
        tenant_id=batch.tenant_id,
        source_device_id=batch.source_device_id,
        source_generation=batch.source_generation,
        batch_sequence=batch.batch_sequence,
        sent_at=batch.sent_at,
        acknowledged_sequence=max_seq,
        status="acknowledged"
    )

    try:
        db.add(sync_batch)
        db.flush()

        # 4. Ingest and deduplicate events
        for ev in batch.events:
            stmt = pg_insert(SyncEvent).values(
                event_id=ev.event_id,
                batch_id=batch.batch_id,
                tenant_id=ev.tenant_id,
                branch_id=ev.branch_id,
                device_id=ev.device_id,
                device_generation=ev.device_generation,
                source_sequence=ev.source_sequence,
                schema_version=ev.schema_version,
                occurred_at=ev.occurred_at,
                actor_id=ev.actor_id,
                causal_reference=ev.causal_reference,
                payload=ev.payload.model_dump(mode="json")
            ).on_conflict_do_nothing(index_elements=["event_id"])
            db.execute(stmt)

        db.commit()
    except IntegrityError:
        db.rollback()
        existing_batch = db.query(SyncBatch).filter(SyncBatch.batch_id == batch.batch_id).first()
        if existing_batch:
            return SyncBatchResponse(
                batch_id=existing_batch.batch_id,
                acknowledged_sequence=existing_batch.acknowledged_sequence,
                status=existing_batch.status
            )
        raise

    return SyncBatchResponse(
        batch_id=batch.batch_id,
        acknowledged_sequence=max_seq,
        status="acknowledged"
    )
