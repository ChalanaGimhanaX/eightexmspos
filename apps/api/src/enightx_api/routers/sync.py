from datetime import datetime, timezone
from typing import Optional
from fastapi import APIRouter, Depends, Query, HTTPException, status
from sqlalchemy.orm import Session
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.exc import IntegrityError
from ..schemas import (
    SyncBatchRequest,
    SyncBatchResponse,
    CatalogSyncResponse,
    CatalogProductSchema,
    CategorySchema,
)
from ..database import get_db
from ..dependencies import get_authenticated_device
from ..models import SyncBatch, SyncEvent, Product, Category, Device

router = APIRouter(prefix="/api/v1/sync", tags=["Sync"])

@router.post("/push", response_model=SyncBatchResponse)
def push_sync_batch(
    batch: SyncBatchRequest,
    device: Device = Depends(get_authenticated_device),
    db: Session = Depends(get_db)
):
    # 1. Device and tenant integrity verification (403 Forbidden)
    if batch.tenant_id != device.tenant_id:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail=f"Batch tenant_id '{batch.tenant_id}' does not match authenticated device tenant '{device.tenant_id}' (tenant_id mismatch)"
        )

    if batch.source_device_id != device.device_id:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail=f"Batch source_device_id '{batch.source_device_id}' does not match authenticated device ID '{device.device_id}' (source_device_id mismatch)"
        )

    for ev in batch.events:
        if ev.tenant_id != device.tenant_id:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail=f"Event {ev.event_id} tenant_id '{ev.tenant_id}' does not match authenticated tenant '{device.tenant_id}' (tenant_id mismatch)"
            )
        if ev.device_id != device.device_id:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail=f"Event {ev.event_id} device_id '{ev.device_id}' does not match authenticated device ID '{device.device_id}' (device_id mismatch)"
            )
        if ev.branch_id != device.branch_id:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail=f"Event {ev.event_id} branch_id '{ev.branch_id}' does not match authenticated branch ID '{device.branch_id}' (branch_id mismatch)"
            )

    # 2. Tenant-scoped idempotency check
    existing_batch = db.query(SyncBatch).filter(
        SyncBatch.batch_id == batch.batch_id,
        SyncBatch.tenant_id == device.tenant_id
    ).first()
    if existing_batch:
        device.last_sync_at = datetime.now(timezone.utc)
        db.commit()
        return SyncBatchResponse(
            batch_id=existing_batch.batch_id,
            acknowledged_sequence=existing_batch.acknowledged_sequence,
            status=existing_batch.status
        )

    # 3. Calculate the max acknowledged source sequence from the batch
    if batch.events:
        max_seq = max(ev.source_sequence for ev in batch.events)
    else:
        max_seq = batch.batch_sequence

    # 4. Create and persist batch record
    sync_batch = SyncBatch(
        batch_id=batch.batch_id,
        tenant_id=device.tenant_id,
        source_device_id=device.device_id,
        source_generation=batch.source_generation,
        batch_sequence=batch.batch_sequence,
        sent_at=batch.sent_at,
        acknowledged_sequence=max_seq,
        status="acknowledged"
    )

    try:
        db.add(sync_batch)
        db.flush()

        # Ingest and deduplicate events
        for ev in batch.events:
            stmt = pg_insert(SyncEvent).values(
                event_id=ev.event_id,
                batch_id=batch.batch_id,
                tenant_id=device.tenant_id,
                branch_id=device.branch_id,
                device_id=device.device_id,
                device_generation=ev.device_generation,
                source_sequence=ev.source_sequence,
                schema_version=ev.schema_version,
                occurred_at=ev.occurred_at,
                actor_id=ev.actor_id,
                causal_reference=ev.causal_reference,
                payload=ev.payload.model_dump(mode="json") if hasattr(ev.payload, "model_dump") else ev.payload
            ).on_conflict_do_nothing(index_elements=["event_id"])
            db.execute(stmt)

        device.last_sync_at = datetime.now(timezone.utc)
        db.commit()
    except IntegrityError:
        db.rollback()
        existing_batch = db.query(SyncBatch).filter(
            SyncBatch.batch_id == batch.batch_id,
            SyncBatch.tenant_id == device.tenant_id
        ).first()
        if existing_batch:
            return SyncBatchResponse(
                batch_id=existing_batch.batch_id,
                acknowledged_sequence=existing_batch.acknowledged_sequence,
                status=existing_batch.status
            )
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail="Batch ID collision with existing batch in another tenant"
        )

    return SyncBatchResponse(
        batch_id=batch.batch_id,
        acknowledged_sequence=max_seq,
        status="acknowledged"
    )


@router.get("/catalog", response_model=CatalogSyncResponse)
def get_catalog_sync(
    since: Optional[str] = Query(default=None, description="ISO UTC timestamp"),
    offset: int = Query(default=0, ge=0),
    limit: int = Query(default=100, ge=1, le=1000),
    device: Device = Depends(get_authenticated_device),
    db: Session = Depends(get_db)
):
    now = datetime.now(timezone.utc)
    since_dt: Optional[datetime] = None

    if since:
        try:
            cleaned = since.strip().replace(" ", "+")
            if cleaned.endswith("Z") or cleaned.endswith("z"):
                cleaned = cleaned[:-1] + "+00:00"
            since_dt = datetime.fromisoformat(cleaned)
            if since_dt.tzinfo is None:
                since_dt = since_dt.replace(tzinfo=timezone.utc)
        except Exception:
            raise HTTPException(status_code=422, detail="Invalid ISO timestamp format for 'since'")

    # Strictly scope products and categories to authenticated device's tenant_id
    prod_query = db.query(Product).filter(Product.tenant_id == device.tenant_id)
    cat_query = db.query(Category).filter(Category.tenant_id == device.tenant_id)
    deleted_item_ids: list[str] = []

    if since_dt is not None:
        prod_query = prod_query.filter(
            Product.updated_at >= since_dt,
            Product.deleted_at.is_(None)
        )
        cat_query = cat_query.filter(
            Category.updated_at >= since_dt,
            Category.deleted_at.is_(None)
        )

        # Scoped soft-deleted items
        del_prods = db.query(Product.product_id).filter(
            Product.tenant_id == device.tenant_id,
            Product.deleted_at.isnot(None),
            Product.deleted_at >= since_dt
        ).all()
        del_cats = db.query(Category.category_id).filter(
            Category.tenant_id == device.tenant_id,
            Category.deleted_at.isnot(None),
            Category.deleted_at >= since_dt
        ).all()

        deleted_item_ids = [r[0] for r in del_prods] + [r[0] for r in del_cats]
    else:
        prod_query = prod_query.filter(Product.deleted_at.is_(None))
        cat_query = cat_query.filter(Category.deleted_at.is_(None))

    products = prod_query.order_by(Product.updated_at.asc(), Product.product_id.asc()).offset(offset).limit(limit).all()
    categories = cat_query.order_by(Category.updated_at.asc(), Category.category_id.asc()).offset(offset).limit(limit).all()

    has_more = (len(products) == limit) or (len(categories) == limit)

    product_items = [
        CatalogProductSchema(
            product_id=p.product_id,
            category_id=p.category_id,
            barcode=p.barcode,
            name=p.name,
            name_si=p.name_si,
            name_ta=p.name_ta,
            unit_price=p.unit_price,
            cost_basis=p.cost_basis,
            tax_rate=p.tax_rate,
            is_active=p.is_active,
            updated_at=p.updated_at
        )
        for p in products
    ]

    category_items = [
        CategorySchema(
            category_id=c.category_id,
            name=c.name,
            description=c.description,
            is_active=c.is_active,
            updated_at=c.updated_at
        )
        for c in categories
    ]

    # Record device.last_sync_at
    device.last_sync_at = now
    db.commit()

    return CatalogSyncResponse(
        server_time=now,
        products=product_items,
        categories=category_items,
        deleted_item_ids=deleted_item_ids,
        has_more=has_more
    )
