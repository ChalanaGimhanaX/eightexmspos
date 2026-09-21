# AGENT.md - Enightx POS Teammate Guide & Workflow Protocol

Welcome to the **Enightx POS** engineering team! This document defines your role, current project state, active branches, security rules, and standard operating procedures.

---

## 1. Current Project State & Integration Status

All branches are synchronized with the central repository at **`https://github.com/ChalanaGimhanaX/eightexmspos.git`**.

### Current Baseline
- **`main` & `develop`**: Both are fully updated and merged with:
  1. **Phase 0 & 1 (Desktop POS)**: C# .NET 8 WPF offline-first desktop counter terminal with SQLite WAL persistence, split tender support, price overrides, line voids, and local user/product auto-seeding.
  2. **Phase 2 Task 1 (Cloud Sync Persistence - YOUR WORK)**: Durable PostgreSQL persistence for batch events, deduplication, idempotency tracking, and transaction safety in `apps/api/src/enightx_api/`.
  3. **Auto-Updater System**: Live self-updater with `https://posapi.eightexms.site/downloads/version.json` and in-place executable replacement.
- **Production Public API**: Live at `https://posapi.eightexms.site/health` (HTTP/2 200 OK via Nginx reverse proxy to FastAPI on port `8010`).

---

## 2. Environment & Access

You operate directly on the VPS under your dedicated non-root developer account:
- **Host**: `5.189.170.180`
- **User**: `enightx-dev`
- **Workspace**: `/srv/enightx/workspaces/teammate/enightx-pos`
- **PostgreSQL Dev DB**: `127.0.0.1:5432 / enightx_pos_dev` (credentials stored in `~/.pgpass` and `apps/api/.env`).
- **Python Virtualenv**: `apps/api/.venv/bin/` (Python 3.12)
- **.NET SDK**: .NET 8.0.131 (`dotnet build`, `dotnet test`)

---

## 3. The Standard 5-Step Dev Process

Follow this lifecycle for every task you undertake:

### Step 1: Start from Clean Latest `develop`
```bash
cd /srv/enightx/workspaces/teammate/enightx-pos
git checkout develop
git pull origin develop
git checkout -b codex/teammate/<task-name>
```
*Naming convention: `codex/teammate/<short-descriptive-name>` (e.g. `codex/teammate/device-heartbeat`, `codex/teammate/shift-reconciliation`)*

### Step 2: Implementation Standards
- **Offline First**: The desktop POS never requires a live network connection to complete a checkout.
- **Outbox Pattern**: All sync events must be committed in SQLite outbox first, then pushed to the cloud API asynchronously.
- **Currency Arithmetic**: Strict Sri Lankan Rupee (LKR) rounding: 2 decimal places with `MidpointRounding.AwayFromZero` (Half-Up).
- **Timezone**: Store and exchange all timestamps in ISO-8601 UTC format.
- **Database Safety**: Only use `enightx_pos_dev` or `enightx_pos_staging`. Never attempt to access or query `eightex_pos`, `radio_db`, or `postgres`.

### Step 3: Run the Test Suites
Both suites must pass with 0 errors before submitting:
```bash
# 1. Cloud API Tests (Pytest)
apps/api/.venv/bin/pytest tests/api -v

# 2. Desktop & Core Domain Tests (.NET)
dotnet test --no-build -v normal
```

### Step 4: Create a Review Submission Bundle
When your feature or fix is complete, prepare your submission evidence:
```bash
SUB_DIR="/srv/enightx/review/submissions/$(date +%Y-%m-%d)-<task-name>"
mkdir -p "$SUB_DIR"

apps/api/.venv/bin/pytest tests/api -v > "$SUB_DIR/pytest_results.txt" 2>&1
dotnet test -v normal > "$SUB_DIR/dotnet_test_results.txt" 2>&1
git log -n 1 > "$SUB_DIR/commit_info.txt"
git diff develop...HEAD > "$SUB_DIR/diff.patch"
```
Create `$SUB_DIR/summary.md` detailing:
1. Feature summary and problem addressed.
2. List of modified files.
3. Commit SHA (`git rev-parse HEAD`).
4. Test execution results.
5. Database migrations or API schema changes (if any).

### Step 5: Push Branch & Notify
Push your branch to GitHub:
```bash
git push origin codex/teammate/<task-name>
```
Notify the lead agent that your submission is ready for review in `/srv/enightx/review/submissions/`. The lead agent will review your bundle, merge into `develop`, and deploy to production!

---

## 4. Next Recommended Tasks from Backlog

Choose your next task from the priority backlog:

1. **Phase 2 Task 2: Cloud Product Catalog Pull Sync**
   - Implement an API endpoint `GET /api/v1/sync/catalog` allowing desktop POS terminals to fetch newly updated product prices, names, and tax rates since a given timestamp (`since_utc`).
2. **Phase 2 Task 3: Device Registration & Heartbeat**
   - Complete `apps/api/src/enightx_api/routers/devices.py` with device authentication tokens and terminal health reporting (`POST /api/v1/devices/heartbeat`).
3. **Phase 3 Task 1: Shift & Drawer Reconciliation Endpoint**
   - Implement an endpoint `POST /api/v1/shifts/close` to ingest closed shift cash drawer audit data, opening float, cash drops, and discrepancies for reporting.
