from datetime import datetime, timezone
from decimal import Decimal
from sqlalchemy.orm import Session
from .database import SessionLocal, engine, Base
from .models import Category, Product

DEFAULT_CATEGORIES = [
    {
        "category_id": "cat_brakes",
        "tenant_id": "TENANT_LK_01",
        "name": "Brakes & Suspension",
        "description": "Brake pads, rotors, shoes, shock absorbers",
        "is_active": True
    },
    {
        "category_id": "cat_filters",
        "tenant_id": "TENANT_LK_01",
        "name": "Filters & Maintenance",
        "description": "Oil, air, and fuel filters",
        "is_active": True
    },
    {
        "category_id": "cat_ignition",
        "tenant_id": "TENANT_LK_01",
        "name": "Ignition & Electrical",
        "description": "Spark plugs, relays, alternators, coils",
        "is_active": True
    },
    {
        "category_id": "cat_lubricants",
        "tenant_id": "TENANT_LK_01",
        "name": "Lubricants & Fluids",
        "description": "Engine oil, transmission fluids, brake fluids",
        "is_active": True
    }
]

DEFAULT_PRODUCTS = [
    {
        "product_id": "prod_001",
        "tenant_id": "TENANT_LK_01",
        "category_id": "cat_brakes",
        "barcode": "4792001001",
        "name": "Brake Pad Front Set (Toyota)",
        "name_si": "ඉදිරිපස බ්‍රේක් පෑඩ් කට්ටලය",
        "name_ta": "முன் பிரேக் பேட் தொகுப்பு",
        "unit_price": Decimal("4500.00"),
        "cost_basis": Decimal("3200.00"),
        "tax_rate": Decimal("0.1800"),
        "is_active": True
    },
    {
        "product_id": "prod_002",
        "tenant_id": "TENANT_LK_01",
        "category_id": "cat_filters",
        "barcode": "4792001002",
        "name": "Oil Filter Element (Denso)",
        "name_si": "ඔයිල් ෆිල්ටරය",
        "name_ta": "எண்ணெய் வடிகட்டி",
        "unit_price": Decimal("1850.00"),
        "cost_basis": Decimal("1200.00"),
        "tax_rate": Decimal("0.1800"),
        "is_active": True
    },
    {
        "product_id": "prod_003",
        "tenant_id": "TENANT_LK_01",
        "category_id": "cat_ignition",
        "barcode": "4792001003",
        "name": "Spark Plug Iridium (NGK)",
        "name_si": "ස්පාර්ක් ප්ලග්",
        "name_ta": "ஸ்பார்க் பிளக்",
        "unit_price": Decimal("2200.00"),
        "cost_basis": Decimal("1500.00"),
        "tax_rate": Decimal("0.1800"),
        "is_active": True
    },
    {
        "product_id": "prod_004",
        "tenant_id": "TENANT_LK_01",
        "category_id": "cat_lubricants",
        "barcode": "4792001004",
        "name": "Synthetic Engine Oil 4L (Mobil 1)",
        "name_si": "එන්ජින් ඔයිල් 4L",
        "name_ta": "என்ஜின் எண்ணெய் 4L",
        "unit_price": Decimal("14500.00"),
        "cost_basis": Decimal("11000.00"),
        "tax_rate": Decimal("0.1800"),
        "is_active": True
    }
]

def seed_database(db: Session | None = None) -> int:
    close_after = False
    if db is None:
        Base.metadata.create_all(bind=engine)
        db = SessionLocal()
        close_after = True

    try:
        now = datetime.now(timezone.utc)
        count = 0
        for cat_data in DEFAULT_CATEGORIES:
            existing = db.query(Category).filter(Category.category_id == cat_data["category_id"]).first()
            if not existing:
                cat = Category(
                    category_id=cat_data["category_id"],
                    tenant_id=cat_data["tenant_id"],
                    name=cat_data["name"],
                    description=cat_data["description"],
                    is_active=cat_data["is_active"],
                    created_at=now,
                    updated_at=now
                )
                db.add(cat)
                count += 1
        db.commit()

        for prod_data in DEFAULT_PRODUCTS:
            existing = db.query(Product).filter(Product.product_id == prod_data["product_id"]).first()
            if not existing:
                prod = Product(
                    product_id=prod_data["product_id"],
                    tenant_id=prod_data["tenant_id"],
                    category_id=prod_data["category_id"],
                    barcode=prod_data["barcode"],
                    name=prod_data["name"],
                    name_si=prod_data["name_si"],
                    name_ta=prod_data["name_ta"],
                    unit_price=prod_data["unit_price"],
                    cost_basis=prod_data["cost_basis"],
                    tax_rate=prod_data["tax_rate"],
                    is_active=prod_data["is_active"],
                    created_at=now,
                    updated_at=now
                )
                db.add(prod)
                count += 1
        db.commit()
        return count
    finally:
        if close_after:
            db.close()

if __name__ == "__main__":
    inserted = seed_database()
    print(f"Seeded {inserted} initial records into actual PostgreSQL database.")
