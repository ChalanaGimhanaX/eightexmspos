# Technical Stack & Architecture Decisions

**Date:** 21 September 2026  
**Status:** Approved baseline for Phase 0 & Phase 1.

---

## 1. Selected Stack & Pinned Versions

### A. Windows Desktop POS (`apps/desktop`)
- **Language / Runtime:** C# 12 on **.NET 8.0 LTS** (`net8.0` for Core Domain, `net8.0-windows` for WPF UI).
- **Presentation Framework:** Windows Presentation Foundation (WPF) with MVVM pattern.
- **Local Embedded Database:** **SQLite 3.45+** accessed via `Microsoft.Data.Sqlite` 8.0.8.
- **Serialization:** `System.Text.Json` 8.0.4.
- **Key Constraints:**
  - Independent offline operation: Each counter runs a separate SQLite database file. No SQLite sharing over network shares (SMB/NFS).
  - WAL mode (`PRAGMA journal_mode = WAL;`) and enforced foreign keys (`PRAGMA foreign_keys = ON;`).
  - Separation of domain engine from UI: `Enightx.Pos` is platform-agnostic (`net8.0`) so all accounting, transactions, and tests execute identically on Linux and Windows. `Enightx.Pos.Wpf` (`net8.0-windows`) implements Windows hardware and UI interactions.

### B. Cloud Backend API (`apps/api`)
- **Language / Runtime:** **Python 3.12+**.
- **Web Framework:** **FastAPI 0.115.0** running on **Uvicorn 0.30.6**.
- **Data Validation & Contract Serialization:** **Pydantic 2.9.2**.
- **Database & Migrations:** **PostgreSQL 16+**, **SQLAlchemy 2.0.35**, **Alembic 1.13.2**.
- **Key Constraints:**
  - API provides sync aggregation, authentication, and read-only reporting.
  - The API does **not** sit in the critical path of checkout: a counter can bill indefinitely offline within its valid licence.

---

## 2. Shared Financial Arithmetic Rules

To guarantee cross-platform consistency between C# and Python:
1. **No Binary Floating Point:** Floating point (`float`, `double`) is strictly prohibited for monetary values.
   - C#: `decimal` (128-bit decimal floating/scaled integer).
   - Python: `decimal.Decimal` with explicit context (`ROUND_HALF_UP`).
2. **Monetary Precision:** Sri Lankan Rupee (LKR). Stored and displayed to 2 decimal places (cents).
3. **Rounding Rules:**
   - Line Total = `Round(Quantity * UnitPrice * (1 - LineDiscountRate) - LineDiscountFixed, 2, MidpointRounding.AwayFromZero)`
   - Tax Calculation = Calculated per line on the net taxable amount and rounded to 2 decimal places.
   - Bill Subtotal = Sum of line totals.
   - Bill Total = `Subtotal - BillDiscountAmount + TaxTotal`.
   - Cash Change = `CashTendered - BillTotal`.
4. **Shift Drawer Expected Cash Formula:**
   $$\text{Expected Cash} = \text{Opening Float} + \text{Cash Received} - \text{Change Given} - \text{Cash Refunds} + \text{Cash In} - \text{Cash Out}$$
   Non-cash tenders (Card, QR, Credit) and uncollected credit sales do not affect physical drawer cash.

---

## 3. Atomic Local Sale Boundary

A local sale is committed in exactly **one** SQLite transaction containing:
1. `Sale` header (status `Completed`, immutable UTC timestamp, actor ID).
2. `SaleLine` items (capturing product snapshot, quantity, unit price, discounts, tax).
3. `Tender` records (tender type, amount tendered, change given).
4. `StockMovement` entries (decreasing projected stock on hand).
5. `ShiftCashMovement` entry (crediting drawer cash for cash sales).
6. `AuditEvent` record (immutable log of the transaction, cashier ID, timestamp).
7. `ReceiptSequence` increment (atomic counter per branch/counter: `B{branch}-C{counter}-{seq:D6}`).
8. `OutboxEvent` record (ready for asynchronous sync with monotonic sequence number).

If any step fails, the entire transaction rolls back cleanly. Printing occurs **after** the commit succeeds. If the printer fails, the receipt is marked available for reprint without re-running the financial transaction.
