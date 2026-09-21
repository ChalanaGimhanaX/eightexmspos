from datetime import datetime, timezone, timedelta
from decimal import Decimal
from pathlib import Path
import sys
import uuid
import pytest

repo_root = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(repo_root / "apps" / "api"))

from fastapi.testclient import TestClient
from src.enightx_api.main import app
from src.enightx_api.database import SessionLocal, engine, Base
from src.enightx_api.models import Product, Category

client = TestClient(app)

@pytest.fixture(autouse=True)
def setup_catalog_db():
    Base.metadata.create_all(bind=engine)
    db = SessionLocal()
    try:
        # Clean up existing test data
        db.query(Product).delete()
        db.query(Category).delete()
        db.commit()
    finally:
        db.close()
    yield
    db = SessionLocal()
    try:
        db.query(Product).delete()
        db.query(Category).delete()
        db.commit()
    finally:
        db.close()


def test_catalog_pull_empty():
    response = client.get("/api/v1/sync/catalog")
    assert response.status_code == 200
    data = response.json()
    assert "server_time" in data
    assert data["products"] == []
    assert data["categories"] == []
    assert data["deleted_item_ids"] == []
    assert data["has_more"] is False


def test_catalog_pull_full_catalog():
    db = SessionLocal()
    now = datetime.now(timezone.utc)
    try:
        cat = Category(
            category_id="cat_brakes",
            tenant_id="TENANT_LK_01",
            name="Brakes & Suspension",
            description="Braking systems and parts",
            is_active=True,
            updated_at=now
        )
        db.add(cat)
        db.commit()

        p1 = Product(
            product_id="prod_bp_01",
            tenant_id="TENANT_LK_01",
            category_id="cat_brakes",
            barcode="4792001001",
            name="Front Brake Pads (Corolla)",
            name_si="ඉදිරිපස බ්‍රේක් පෑඩ්",
            name_ta="முன் பிரேக் பேட்",
            unit_price=Decimal("4500.00"),
            cost_basis=Decimal("3200.00"),
            tax_rate=Decimal("0.1800"),
            is_active=True,
            updated_at=now
        )
        p2 = Product(
            product_id="prod_bp_02",
            tenant_id="TENANT_LK_01",
            category_id="cat_brakes",
            barcode="4792001002",
            name="Rear Brake Shoes (Civic)",
            unit_price=Decimal("3800.00"),
            cost_basis=Decimal("2500.00"),
            tax_rate=Decimal("0.1800"),
            is_active=True,
            updated_at=now
        )
        db.add_all([p1, p2])
        db.commit()
    finally:
        db.close()

    response = client.get("/api/v1/sync/catalog")
    assert response.status_code == 200
    data = response.json()

    assert len(data["categories"]) == 1
    assert data["categories"][0]["category_id"] == "cat_brakes"
    assert data["categories"][0]["name"] == "Brakes & Suspension"

    assert len(data["products"]) == 2
    prod1_resp = next(p for p in data["products"] if p["product_id"] == "prod_bp_01")
    assert prod1_resp["barcode"] == "4792001001"
    assert prod1_resp["unit_price"] == "4500.00"
    assert prod1_resp["cost_basis"] == "3200.00"
    assert prod1_resp["tax_rate"] == "0.1800"
    assert prod1_resp["name_si"] == "ඉදිරිපස බ්‍රේක් පෑඩ්"
    assert prod1_resp["category_id"] == "cat_brakes"
    assert prod1_resp["is_active"] is True

    assert data["deleted_item_ids"] == []
    assert data["has_more"] is False


def test_catalog_pull_incremental_since():
    db = SessionLocal()
    t0 = datetime.now(timezone.utc) - timedelta(hours=2)
    t1 = datetime.now(timezone.utc) - timedelta(hours=1)
    t2 = datetime.now(timezone.utc)

    try:
        p_old = Product(
            product_id="prod_old",
            barcode="11111111",
            name="Old Unchanged Product",
            unit_price=Decimal("100.00"),
            cost_basis=Decimal("50.00"),
            tax_rate=Decimal("0.00"),
            is_active=True,
            updated_at=t0
        )
        p_new = Product(
            product_id="prod_new",
            barcode="22222222",
            name="Newly Added Product",
            unit_price=Decimal("200.00"),
            cost_basis=Decimal("100.00"),
            tax_rate=Decimal("0.1800"),
            is_active=True,
            updated_at=t2
        )
        db.add_all([p_old, p_new])
        db.commit()
    finally:
        db.close()

    # Query since t1 (should return only prod_new, not prod_old)
    since_iso = t1.isoformat()
    response = client.get(f"/api/v1/sync/catalog?since={since_iso}")
    assert response.status_code == 200
    data = response.json()

    assert len(data["products"]) == 1
    assert data["products"][0]["product_id"] == "prod_new"
    assert data["products"][0]["barcode"] == "22222222"


def test_catalog_pull_deleted_items():
    db = SessionLocal()
    t0 = datetime.now(timezone.utc) - timedelta(hours=2)
    t1 = datetime.now(timezone.utc) - timedelta(hours=1)
    t2 = datetime.now(timezone.utc)

    try:
        # Active product
        p_active = Product(
            product_id="prod_active",
            barcode="33333333",
            name="Active Product",
            unit_price=Decimal("300.00"),
            is_active=True,
            updated_at=t2
        )
        # Soft-deleted product
        p_deleted = Product(
            product_id="prod_deleted_item",
            barcode="44444444",
            name="Deleted Product",
            unit_price=Decimal("400.00"),
            is_active=False,
            updated_at=t2,
            deleted_at=t2
        )
        db.add_all([p_active, p_deleted])
        db.commit()
    finally:
        db.close()

    since_iso = t1.isoformat()
    response = client.get(f"/api/v1/sync/catalog?since={since_iso}")
    assert response.status_code == 200
    data = response.json()

    # p_active should be in products list
    assert len(data["products"]) == 1
    assert data["products"][0]["product_id"] == "prod_active"

    # p_deleted should be in deleted_item_ids and NOT in products
    assert "prod_deleted_item" in data["deleted_item_ids"]
    assert all(p["product_id"] != "prod_deleted_item" for p in data["products"])


def test_catalog_pull_pagination_limit():
    db = SessionLocal()
    now = datetime.now(timezone.utc)
    try:
        for i in range(5):
            p = Product(
                product_id=f"prod_{i}",
                barcode=f"5555000{i}",
                name=f"Item {i}",
                unit_price=Decimal("500.00"),
                is_active=True,
                updated_at=now + timedelta(seconds=i)
            )
            db.add(p)
        db.commit()
    finally:
        db.close()

    # Query with limit=2
    response = client.get("/api/v1/sync/catalog?limit=2")
    assert response.status_code == 200
    data = response.json()
    assert len(data["products"]) == 2
    assert data["has_more"] is True


def test_catalog_pull_invalid_since():
    response = client.get("/api/v1/sync/catalog?since=not-a-date")
    assert response.status_code == 422
