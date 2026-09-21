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

KNOWN_TEST_PROD_IDS = [
    "prod_bp_01", "prod_bp_02", "prod_old", "prod_new",
    "prod_active", "prod_deleted_item",
    "prod_0", "prod_1", "prod_2", "prod_3", "prod_4",
    "prod_paged_0", "prod_paged_1", "prod_paged_2"
]
KNOWN_TEST_CAT_IDS = ["cat_test_brakes", "cat_test_paged_1", "cat_test_paged_2"]

def cleanup_test_data(db):
    db.query(Product).filter(Product.product_id.in_(KNOWN_TEST_PROD_IDS)).delete(synchronize_session=False)
    db.query(Category).filter(Category.category_id.in_(KNOWN_TEST_CAT_IDS)).delete(synchronize_session=False)
    db.commit()

@pytest.fixture(autouse=True)
def setup_catalog_db():
    Base.metadata.create_all(bind=engine)
    db = SessionLocal()
    try:
        cleanup_test_data(db)
    finally:
        db.close()
    yield
    db = SessionLocal()
    try:
        cleanup_test_data(db)
    finally:
        db.close()


def test_catalog_pull_empty():
    # Far-future timestamp should return empty delta
    future = (datetime.now(timezone.utc) + timedelta(days=365)).isoformat()
    response = client.get(f"/api/v1/sync/catalog?since={future}")
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
            category_id="cat_test_brakes",
            tenant_id="TENANT_LK_01",
            name="Test Brakes",
            description="Braking systems and parts",
            is_active=True,
            updated_at=now
        )
        db.add(cat)
        db.commit()

        p1 = Product(
            product_id="prod_bp_01",
            tenant_id="TENANT_LK_01",
            category_id="cat_test_brakes",
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
            category_id="cat_test_brakes",
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

    cat_match = next((c for c in data["categories"] if c["category_id"] == "cat_test_brakes"), None)
    assert cat_match is not None
    assert cat_match["name"] == "Test Brakes"

    prod1_resp = next((p for p in data["products"] if p["product_id"] == "prod_bp_01"), None)
    assert prod1_resp is not None
    assert prod1_resp["barcode"] == "4792001001"
    assert prod1_resp["unit_price"] == "4500.00"
    assert prod1_resp["cost_basis"] == "3200.00"
    assert prod1_resp["tax_rate"] == "0.1800"
    assert prod1_resp["name_si"] == "ඉදිරිපස බ්‍රේක් පෑඩ්"
    assert prod1_resp["category_id"] == "cat_test_brakes"
    assert prod1_resp["is_active"] is True

    prod2_resp = next((p for p in data["products"] if p["product_id"] == "prod_bp_02"), None)
    assert prod2_resp is not None
    assert prod2_resp["unit_price"] == "3800.00"


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

    since_iso = t1.isoformat()
    response = client.get(f"/api/v1/sync/catalog?since={since_iso}")
    assert response.status_code == 200
    data = response.json()

    # prod_new should be in results, prod_old should NOT be
    new_found = any(p["product_id"] == "prod_new" for p in data["products"])
    old_found = any(p["product_id"] == "prod_old" for p in data["products"])
    assert new_found is True
    assert old_found is False


def test_catalog_pull_deleted_items():
    db = SessionLocal()
    t1 = datetime.now(timezone.utc) - timedelta(hours=1)
    t2 = datetime.now(timezone.utc)

    try:
        p_active = Product(
            product_id="prod_active",
            barcode="33333333",
            name="Active Product",
            unit_price=Decimal("300.00"),
            is_active=True,
            updated_at=t2
        )
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

    assert any(p["product_id"] == "prod_active" for p in data["products"])
    assert "prod_deleted_item" in data["deleted_item_ids"]
    assert all(p["product_id"] != "prod_deleted_item" for p in data["products"])


def test_catalog_pull_pagination_limit_and_offset():
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

    # Query with limit=2, since=now-1s
    since_iso = (now - timedelta(seconds=1)).isoformat()
    response1 = client.get(f"/api/v1/sync/catalog?since={since_iso}&limit=2&offset=0")
    assert response1.status_code == 200
    data1 = response1.json()
    assert len(data1["products"]) == 2
    assert data1["has_more"] is True

    # Next page with offset=2, limit=2
    response2 = client.get(f"/api/v1/sync/catalog?since={since_iso}&limit=2&offset=2")
    assert response2.status_code == 200
    data2 = response2.json()
    assert len(data2["products"]) == 2
    assert data2["has_more"] is True

    # Page 1 and Page 2 products must be completely distinct
    ids_page1 = {p["product_id"] for p in data1["products"]}
    ids_page2 = {p["product_id"] for p in data2["products"]}
    assert ids_page1.isdisjoint(ids_page2)


def test_catalog_pull_category_limit_triggers_has_more():
    db = SessionLocal()
    now = datetime.now(timezone.utc)
    try:
        c1 = Category(category_id="cat_test_paged_1", name="Paged Cat 1", is_active=True, updated_at=now)
        c2 = Category(category_id="cat_test_paged_2", name="Paged Cat 2", is_active=True, updated_at=now)
        db.add_all([c1, c2])
        db.commit()
    finally:
        db.close()

    since_iso = (now - timedelta(seconds=1)).isoformat()
    response = client.get(f"/api/v1/sync/catalog?since={since_iso}&limit=2")
    assert response.status_code == 200
    data = response.json()
    # When category count reaches limit (2), has_more must be True
    assert len(data["categories"]) == 2
    assert data["has_more"] is True


def test_catalog_pull_invalid_since():
    response = client.get("/api/v1/sync/catalog?since=not-a-date")
    assert response.status_code == 422
