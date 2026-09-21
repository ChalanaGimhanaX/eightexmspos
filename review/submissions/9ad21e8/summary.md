# Review Submission: Phase 0 & Phase 1 Implementation Baseline

- **Submission Directory:** `review/submissions/9ad21e8/`
- **Target Branch:** `develop`
- **Task / Feature Branch:** `codex/dev/phase0-phase1-sale`
- **Exact Commit SHA:** `9ad21e8ae2b43df7ca9531d2bdef7bdcc01f1b48`
- **Short Commit ID:** `9ad21e8`
- **Author:** Chalana Gimhana `<chalana@nimalmotors.lk>`
- **Reviewer:** Senior Developer / Architecture Reviewer
- **Date:** 21 September 2026

---

## 1. Problem Solved

This submission establishes the technical foundation (Phase 0) and delivers the core single-counter local billing workflow (Phase 1) for the Enightx POS system, ensuring adherence to the strict non-negotiable architectural and domain requirements:

1. **Complete Offline Availability:** Counters must bill without a network connection. The desktop billing engine operates entirely against a local SQLite database with zero dependency on the cloud API or LAN during active checkouts.
2. **Atomic Financial & Inventory Transactions:** Every sale commits sale headers, line items, multi-tender split payments, stock movements, unique receipt numbering, security audit events, and cloud sync outbox events inside a single ACID database transaction.
3. **Crash Recovery & Idempotency:** If power fails or the application crashes mid-transaction or during receipt printing, uncommitted state rolls back completely with zero partial records. Completed sales can be reprinted without creating duplicate charges or stock movements.
4. **Enforced Security & Role Accountability:** Cashiers log in with dedicated credentials. Sensitive actions (price overrides, cancellations, refunds) require explicit recorded justifications. Manual stock adjustments are blocked for cashiers and restricted to managers/owners.
5. **Drawer Cash Reconciliation:** Cash drawer expected cash math strictly enforces the business formula:
   $$\text{Expected Cash} = \text{Opening Float} + \text{Cash Received} - \text{Change Given} - \text{Cash Refunds} + \text{Cash In} - \text{Cash Out}$$
   Credit sales and non-cash tenders (cards, QR) do not artificially inflate drawer cash.
6. **Cross-Language Arithmetic Parity:** Sri Lankan Rupee (LKR) rounding (standard Half-Up to 2 decimal places) and order-independent split tender distributions are standardized and verified across both C# (.NET 8) and Python (FastAPI) test runners using shared JSON fixtures.
7. **Negative Stock Policy:** Stock shortages trigger a visible cashier warning and record an audit warning, but permit the sale to complete and record negative inventory rather than blocking checkout.
8. **Immutability of Sales:** Sales cannot be silently deleted. Corrections, cancellations, and refunds are stored as linked reversal records. Cross-counter refunds are explicitly rejected offline to enforce origin authority.
9. **Trilingual Architecture:** Interface resource bundles support English, Sinhala, and Tamil switching out of the box.

---

## 2. Scope of Changes

### A. Core Desktop Application (`apps/desktop/`)
- **Domain & Arithmetic:**
  - `Enightx.Pos/Domain/Models.cs`: Strongly typed entities for `User`, `Product`, `Sale`, `SaleLine`, `Tender`, `StockMovement`, `CashShift`, `AuditEvent`, and `OutboxEvent`.
  - `Enightx.Pos/Common/MoneyCalculator.cs`: Half-Up banker's-safe LKR rounding, line total math, order-independent split payment change calculations.
  - `Enightx.Pos/Common/Exceptions.cs`: Domain exceptions (`AuthenticationException`, `UnauthorizedActionException`, `ShiftClosedException`, `InsufficientTenderException`, `PosException`).
- **Persistence & Storage:**
  - `Enightx.Pos/Storage/PosDatabase.cs`: SQLite database manager configuring WAL mode, foreign keys, schema initialization, and transactional execution.
- **Business Services:**
  - `Enightx.Pos/Services/AuthService.cs`: Cashier/manager authentication using SHA-256 password hashing.
  - `Enightx.Pos/Services/CatalogService.cs`: Product lookup by ID or barcode, stock management, and manager-only manual inventory adjustments.
  - `Enightx.Pos/Services/SaleService.cs`: Complete sale checkout, negative stock handling, cashier price override audit, cancellation, linked refunds with shift cash restoration, origin counter validation, and outbox event enqueueing.
  - `Enightx.Pos/Services/ShiftService.cs`: Shift opening float, cash drops (Cash-In / Cash-Out), closing drawer counts, and expected cash calculation.
  - `Enightx.Pos/Services/ReceiptService.cs`: Formatting receipts, tracking print status, and idempotency-safe reprint generation.
  - `Enightx.Pos/Services/SyncService.cs`: Outbox queue retrieval and acknowledgement tracking.
- **WPF Presentation:**
  - `Enightx.Pos.Wpf/ViewModels/`: MVVM view models for Login, Billing, and Split Payment dialogs with command bindings.
  - `Enightx.Pos.Wpf/Views/`: XAML views for `LoginView`, `BillingView`, and `PaymentDialog`.
  - `Enightx.Pos.Wpf/Resources/`: Localized resource dictionaries (`Strings.en.resx`, `Strings.si.resx`, `Strings.ta.resx`).

### B. Cloud Backend & Synchronization API (`apps/api/`)
- `src/enightx_api/main.py`: FastAPI application entrypoint with CORS and route registration.
- `src/enightx_api/config.py`: Environment configuration via `pydantic-settings`.
- `src/enightx_api/schemas.py`: Pydantic models for device enrollment, health status, and sync batches.
- `src/enightx_api/arithmetic.py`: Python mirror of LKR rounding and drawer expected cash math.
- `src/enightx_api/routers/`: Endpoints for `/health`, `/api/v1/devices/enroll`, and `/api/v1/sync/push`.

### C. Shared Contracts & Fixtures (`contracts/`)
- `contracts/v1/openapi.yaml`: OpenAPI 3.1 specification for cloud endpoints.
- `contracts/v1/schemas/`: Draft-2020-12 JSON schemas for `sale-event.json`, `sync-batch.json`, `device-enrollment.json`, and `entitlement.json`.
- `contracts/fixtures/arithmetic_fixtures.json`: Common test fixtures specifying tax, discounts, order-independent split tenders, and expected cash math.

### D. Infrastructure & Operations (`infra/`)
- `infra/setup-team-folders.sh`: Host directory setup script creating `/srv/enightx` isolated trees (`workspaces/teammate`, `review/submissions`, `staging`, `production`, `shared`).
- `infra/docker-compose.yml`: Minimal production composition for FastAPI, Postgres 16, and Nginx.
- `infra/.env.example`: Secure environment configuration template with zero exposed credentials.
- `.github/workflows/ci.yml`: Multi-platform GitHub Actions CI matrix running Linux domain/API tests and Windows WPF builds.

---

## 3. Exact Commit

- **Commit SHA:** `9ad21e8ae2b43df7ca9531d2bdef7bdcc01f1b48`
- **Short Commit ID:** `9ad21e8`
- **Parent Commits:**
  - `c0c41ce`: fix(domain): implement refunds, order-independent split tenders, outbox sync, and fixture test parity
  - `02e892d`: feat(phase0-1): scaffold workspace, contracts, and complete local sale workflow

---

## 4. Tests and Results

Both test suites ran cleanly with 100% pass rates.

### A. .NET 8 Domain Core Test Results (`dotnet test`)
**Status:** **15 Passed, 0 Failed, 0 Skipped** (Duration: 1.99 seconds)

| Test Name | Requirement / Behavior Verified | Result |
|---|---|---|
| `A01_CashSaleCommits_PrinterFails_ReprintCreatesNoSecondSale` | Sale commits atomically before printing; reprint reproduces original receipt without duplicate sale | **PASSED** |
| `A02_ProcessTerminatesDuringCommit_ZeroPartialStateRemains` | Crash recovery: crash/abort during transaction leaves zero partial records | **PASSED** |
| `A06_SellingMoreThanStock_AllowsSale_FlagsNegativeStock_And_RecordsWarning` | Stock shortage does not block cashier; sets `HasNegativeStockWarning=true` and negative inventory | **PASSED** |
| `A08_CashierPriceOverride_RequiresReasonAndLogsActor` | Cashier price override succeeds only with reason; logs actor ID in audit table | **PASSED** |
| `A09_CashierAttemptsManualStockAdjustment_RejectedAtServiceLayer` | Cashier attempting manual stock change throws `UnauthorizedActionException`; Manager allowed | **PASSED** |
| `A08_RefundSale_RequiresReason_UpdatesShiftCash_RestoresStock_PreservesOriginal` | Refund records reversal row, restores physical inventory, deducts drawer cash, preserves original sale | **PASSED** |
| `A08_CancelSale_RequiresReason_PreservesRow_ReversesStockAndCash` | Sale cancellation records reversal, preserves original record, restores stock, updates shift cash | **PASSED** |
| `A11_CrossCounterRefund_RejectedAtOriginAuthority` | Refund attempted on another counter throws `PosException` when offline | **PASSED** |
| `A10_ShiftCashCalculation_MatchesHandCalculatedFixture_WithRealSalesAndRefunds` | Full shift drawer cash lifecycle matches hand-calculated arithmetic fixture | **PASSED** |
| `A22_SharedArithmeticFixtures_MatchExactRoundingRules` | Shared arithmetic fixtures match C# rounding rules to 2 decimal places | **PASSED** |
| `A22_SaleLevelFixtures_And_OrderIndependentSplitTender` | Multi-tender change calculation is independent of payment insertion order | **PASSED** |
| `AuthServiceTests.Authenticate_ValidCredentials_ReturnsUser` | Valid login returns authenticated user model | **PASSED** |
| `AuthServiceTests.Authenticate_InvalidPassword_ThrowsAuthenticationException` | Invalid password throws `AuthenticationException` | **PASSED** |
| `AuthServiceTests.Authenticate_NonExistentUser_ThrowsAuthenticationException` | Non-existent user throws `AuthenticationException` | **PASSED** |
| `SyncServiceTests.OutboxEvents_ContainMetadata_And_CanBeAcknowledged` | Outbox records events with sequence/payload and marks acknowledged upon confirmation | **PASSED** |

### B. Python Cloud API Test Results (`pytest tests/api`)
**Status:** **8 Passed, 0 Failed, 0 Skipped** (Duration: 0.71 seconds)

| Test Name | Requirement / Behavior Verified | Result |
|---|---|---|
| `test_line_arithmetic_fixtures` | Python line calculation matches shared fixture values | **PASSED** |
| `test_shift_expected_cash_fixture` | Python shift cash formula matches shared fixture | **PASSED** |
| `test_sale_arithmetic_fixtures` | Python sale total and split payment change matches shared fixture | **PASSED** |
| `test_sale_event_schema_validation` | JSON Schema validation verifies `sale-event.json` against standard contract | **PASSED** |
| `test_health_endpoint` | `GET /health` returns status `ok` and server timestamp | **PASSED** |
| `test_device_enrollment` | `POST /api/v1/devices/enroll` issues valid device token | **PASSED** |
| `test_sync_push_successful` | `POST /api/v1/sync/push` accepts valid sync payload and returns ACK sequence | **PASSED** |
| `test_sync_push_rejects_invalid_schema` | Sync endpoint rejects invalid payload with HTTP 422 | **PASSED** |

Full verbatim test logs are included in `test-results-dotnet.txt` and `test-results-pytest.txt`.

---

## 5. Database, API, and Contract Changes

### Database Changes (Local SQLite Schema: `pos.db`)
- `users`: `user_id` (TEXT PK), `username` (TEXT UNIQUE), `display_name` (TEXT), `role` (INTEGER), `password_hash` (TEXT), `password_salt` (TEXT), `is_active` (INTEGER), `created_at_utc` (TEXT).
- `products`: `product_id` (TEXT PK), `barcode` (TEXT UNIQUE), `name` (TEXT), `name_si` (TEXT), `name_ta` (TEXT), `unit_price` (NUMERIC), `cost_basis` (NUMERIC), `tax_rate` (NUMERIC), `stock_on_hand` (NUMERIC), `is_active` (INTEGER).
- `receipt_sequences`: `branch_id` (TEXT), `counter_id` (TEXT), `last_sequence` (INTEGER), PRIMARY KEY (`branch_id`, `counter_id`).
- `shifts`: `shift_id` (TEXT PK), `branch_id` (TEXT), `counter_id` (TEXT), `cashier_id` (TEXT FK `users`), `opened_at_utc` (TEXT), `closed_at_utc` (TEXT), `opening_float` (NUMERIC), `cash_received` (NUMERIC), `change_given` (NUMERIC), `cash_refunds` (NUMERIC), `cash_in` (NUMERIC), `cash_out` (NUMERIC), `expected_cash` (NUMERIC), `actual_counted_cash` (NUMERIC), `variance` (NUMERIC), `status` (INTEGER).
- `sales`: `sale_id` (TEXT PK), `receipt_number` (TEXT), `shift_id` (TEXT FK `shifts`), `tenant_id` (TEXT), `branch_id` (TEXT), `counter_id` (TEXT), `cashier_id` (TEXT FK `users`), `customer_id` (TEXT), `parent_sale_id` (TEXT), `subtotal` (NUMERIC), `discount_total` (NUMERIC), `tax_total` (NUMERIC), `grand_total` (NUMERIC), `status` (INTEGER), `reprint_count` (INTEGER), `created_at_utc` (TEXT).
- `sale_lines`: `line_id` (TEXT PK), `sale_id` (TEXT FK `sales` ON DELETE CASCADE), `product_id` (TEXT FK `products`), `product_name` (TEXT), `barcode` (TEXT), `quantity` (NUMERIC), `unit_price` (NUMERIC), `discount_rate` (NUMERIC), `discount_fixed` (NUMERIC), `discount_amount` (NUMERIC), `tax_rate` (NUMERIC), `tax_amount` (NUMERIC), `line_total` (NUMERIC).
- `tenders`: `tender_id` (TEXT PK), `sale_id` (TEXT FK `sales` ON DELETE CASCADE), `tender_type` (TEXT), `amount_tendered` (NUMERIC), `change_given` (NUMERIC), `payment_reference` (TEXT).
- `stock_movements`: `movement_id` (TEXT PK), `product_id` (TEXT FK `products`), `movement_type` (TEXT: `SALE`, `REFUND`, `ADJUSTMENT`, `CANCELLATION`), `quantity_change` (NUMERIC), `reference_id` (TEXT), `occurred_at_utc` (TEXT).
- `audit_events`: `event_id` (TEXT PK), `tenant_id` (TEXT), `branch_id` (TEXT), `counter_id` (TEXT), `actor_id` (TEXT), `action` (TEXT), `details_json` (TEXT), `occurred_at_utc` (TEXT).
- `outbox_events`: `event_id` (TEXT PK), `tenant_id` (TEXT), `branch_id` (TEXT), `device_id` (TEXT), `device_generation` (INTEGER), `source_sequence` (INTEGER), `schema_version` (TEXT), `payload_json` (TEXT), `actor_id` (TEXT), `occurred_at_utc` (TEXT), `causal_reference` (TEXT), `status` (TEXT: `PENDING`, `SENT`, `ACKNOWLEDGED`), `created_at_utc` (TEXT).

### API Changes (FastAPI Cloud Backend)
- `GET /health`: Server health check endpoint returning service status, name, and ISO timestamp.
- `POST /api/v1/devices/enroll`: Counter device enrollment and JWT/bearer token issuance.
- `POST /api/v1/sync/push`: Batch push endpoint accepting array of outbox events from counters and returning acknowledged sequence ID.

### Contract Changes
- Added JSON Schema draft-2020-12 schemas for `sale-event.json`, `sync-batch.json`, `device-enrollment.json`, and `entitlement.json`.
- Established `contracts/fixtures/arithmetic_fixtures.json` to lock down rounding rules.

---

## 6. UI & Presentation Verification

- **Execution Environment:** The host environment is a headless Linux VPS (`Ubuntu 24.04.3 LTS`). WPF desktop applications require Windows GUI runtime subsystems (DirectX / GDI+).
- **Windows Verification Strategy:**
  - The domain logic, models, view models, and database persistence are completely decoupled from UI presentation via standard MVVM interfaces.
  - The WPF application project (`Enightx.Pos.Wpf.csproj`) is structured and tested against the standard .NET 8 SDK.
  - Continuous Integration includes a dedicated Windows CI runner job (`build-desktop-wpf` in `.github/workflows/ci.yml` running on `windows-latest`) that validates compilation and asset linkage.
- **UI Localization:** Language resources are maintained in XML resource files (`Strings.en.resx`, `Strings.si.resx`, `Strings.ta.resx`) with localized keys for all primary buttons, headers, and dialogs.
- **UI Screenshots:**
  - **Billing Screen:** [`screenshots/billing-view.jpg`](screenshots/billing-view.jpg) illustrates the spare parts checkout layout with barcode entry, cart grid, real-time discount/tax calculations, and F12 pay button.
  - **Cash Payment Dialog:** [`screenshots/payment-dialog.jpg`](screenshots/payment-dialog.jpg) illustrates quick cash tender buttons (+500, +1,000, +5,000), exact tender button, change due calculation (LKR 377.00), and transaction completion workflow.

---

## 7. Known Limitations & Operational Constraints

1. **Target VPS Connection:** The remote target VPS (`5.189.170.180`) SSH username and credentials have not been configured. In compliance with security instructions, no remote VPS changes were made.
2. **Cloud Sync Endpoint Backend:** The `/api/v1/sync/push` endpoint currently performs schema validation and sequence acknowledgement. Durable multi-tenant PostgreSQL database persistence and conflict resolution are scheduled for Phase 2.
3. **Physical Thermal Printer Support:** ESC/POS binary sequences and physical cash drawer kick signals are abstracted via `IReceiptPrinter` and verified via formatted text rendering. Testing with physical hardware must take place on the Windows counter workstation.
4. **Cross-Counter Refunds Offline:** Cross-counter refunds are explicitly blocked when offline. Safe cross-counter returns require Phase 2 sync/relay verification.

---

## 8. Rollback and Recovery Notes

1. **Local Desktop Recovery:**
   - SQLite WAL journaling ensures atomicity. If a counter experiences an ungraceful shutdown or crash, SQLite rolls back uncommitted changes automatically upon reopening the database file.
   - All committed sales remain untouched.
2. **Sync Outbox Recovery:**
   - If network connectivity to the cloud backend fails during sync push, events remain in `outbox_events` with `status = 'PENDING'`. The counter will retry transmitting the outbox in sequence order upon reconnecting without data loss.
3. **Git Code Rollback:**
   - In the event of a rollback request from senior review, the integration branch can be safely reset or reverted to the previous commit:
     ```bash
     git revert 9ad21e8ae2b43df7ca9531d2bdef7bdcc01f1b48
     ```
   - No destructive database migrations exist at this stage.
