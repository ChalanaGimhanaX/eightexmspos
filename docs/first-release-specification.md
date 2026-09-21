# First-release specification

Planning baseline: 21 September 2026.

## Purpose

Provide fast billing, accountable cash handling, and traceable inventory for Sri Lankan shops. Start with a general-purpose product; bike showroom, restaurant, pharmacy, repair, and other specialist workflows are optional future modules.

## Agreed requirements

| Area | Decision |
| --- | --- |
| Counter application | Installable Windows desktop software |
| Availability | Every counter continues independently through internet, LAN, or main-computer failure |
| Owner access | Read-only web overview, showing synchronization freshness |
| Languages | Switchable English, Sinhala, and Tamil interface |
| Commercial limits | No artificial limits on branches, counters, users, products, or bills |
| Subscription | From LKR 10,000/month for the business; requested extra features priced separately |
| Other licences | Two-day trial and permanent purchase option |
| Permanent purchase | Default features included; price, upgrade entitlement, and support terms unresolved |
| Offline licence | Continue until the locally stored authorized expiry; remote freeze waits for reconnection |
| Lockout | Full business-function lockout at expiry/freeze; preserve all data |
| Identity | Each cashier has a separate account |
| Cashier powers | Refunds, cancellations, and price overrides allowed and audited |
| Manual stock adjustments | Owner/manager only |
| Stock shortage | Warn, allow sale, record the warning, and flag negative stock |
| Receipts | Unique sequence per branch and counter; statutory invoice format validated separately |
| History | Completed transactions remain; corrections use linked reversals or amendments |
| Provider dashboard | Licences, payments, features, device health, backups, and synchronization status |

These rules reflect the discussion, including defaults delegated to the designer. Requirements below expand them into a proposed implementation baseline rather than claiming every detail was explicitly approved.

## Default product scope

### Sales and payments

- Product search, barcode entry, quantities, discounts, price overrides, held bills, receipt printing and reprinting.
- Cash, externally processed card and QR payments, split tender, credit sales, and subsequent collections.
- Returns and partial/full refunds linked to the original sale where available; reversal restores stock only when goods are actually returned to saleable inventory.
- Store original prices, tax, discounts, unit conversions, cost basis and receipt text values with the sale so later catalog changes do not rewrite history.
- Card/QR recording is not payment processing. Never mark a provider payment confirmed solely from a customer screenshot. Record manual cashier confirmation separately from verified provider confirmation.
- Cash change is separate from tender and revenue. Do not store card numbers or security codes.

### Inventory and purchasing

- Products, categories, barcodes, units, pack conversions, prices, opening quantities, suppliers, purchase records and receiving.
- Receipts of stock, supplier returns, customer returns, damage, counts and adjustments each create stock movements.
- Branch transfers use dispatch, in-transit and receipt stages; goods do not appear at both branches at once.
- Stock on hand derives from movements, not direct edits to a quantity cell.
- Proposed costing: moving weighted average, with deterministic reconciliation and reports marked provisional while relevant events are missing. Final costing rules require fixture tests before reporting profit.
- Inventory counts capture a count session and movement cutoff; concurrent sales must not be overwritten by a blind quantity reset.

### Staff and cash control

- Owner, manager and cashier roles; cashier powers configurable by owner.
- Opening float, logged cash-in/out, cashier/counter shifts, counted closing balance, expected cash and variance.
- Expected cash = opening float + cash received - change given - cash refunds + cash-in - cash-out. Credit sales and non-cash tenders do not increase drawer cash.
- Refunds, cancellations, price overrides and manual stock adjustments require reasons.
- Owner corrections never silently erase source transactions. A cancelled sale remains traceable, with linked cash and inventory effects.

### Reports and owner overview

- Sales, returns, discounts, tender totals, cash variance, gross profit, inventory movement, low/negative stock, customer balances and supplier balances.
- Filter by date, branch and cashier, subject to role permissions.
- Owner web overview is read-only in this release. It displays each branch's last successful sync and missing/stale counters, rather than implying delayed totals are live.
- Gross profit is not net profit; label it accordingly. Exclude unreceived credit amounts from cash totals.

### Localization and hardware

- LKR amounts; store precise amounts and specify rounding consistently. Store timestamps in UTC and display business dates in Asia/Colombo.
- Resource-based English/Sinhala/Tamil interface. Product translations are separate optional fields; changing interface language does not translate catalog data automatically.
- Initial hardware target: keyboard-input barcode scanners and Windows-supported receipt printers. Exact models, paper sizes, cash drawer commands, and Sinhala/Tamil printing need physical validation before purchase/deployment.
- Configurable tax status, tax rates and effective dates; ordinary receipts and tax invoices must not be conflated. Current Sri Lankan statutory requirements remain a release gate.

## Screens

Desktop: activation/lockout, staff login, counter/shift opening, billing, held bills, sales history, returns/refunds, products, stock movements, receiving, suppliers, customers/collections, transfers, counts/adjustments, shift closing, reports, audit history, settings, backup/recovery, and sync/conflict status.

Owner web: sign-in, business overview, branch/counter freshness, sales/tenders, cash variance, stock alerts and outstanding balances.

Provider web: businesses, licences, subscriptions, payment verification, feature entitlements, device health, pending freeze/unfreeze commands, support grants and provider audit history.

## Permissions baseline

| Action | Cashier | Manager | Owner | Provider staff |
| --- | --- | --- | --- | --- |
| Bill, refund, cancel, override price | Yes, with audit/reasons where sensitive | Yes | Yes | No by default |
| Own shift cash operations | Yes | Yes | Yes | No |
| Manual stock adjustment | No | Yes | Yes | No |
| Staff/role administration | No | Proposed limited scope | Yes | No |
| Business reports | Proposed own shift only | Assigned branches | Business | No by default |
| Licence and device health | View relevant status | View | View | Authorized administration |

Provider access to shop data requires a scoped, time-limited support grant and its own audit trail. Platform operation may involve processing business data; hiding it in the provider UI alone is not a complete privacy guarantee.

## Licence behaviour

- Proposed trial trigger: explicit activation after initial setup, lasting 48 hours. User agreed two days but has not selected the start trigger.
- Subscription devices carry a signed entitlement with a paid-through timestamp. Automatic card renewal and manually verified bank transfers both issue updated entitlements.
- A permanent base licence has no subscription expiry. Cloud-service duration, upgrade rights, and exceptional revocation policy are unresolved; do not treat it as an expiring monthly licence.
- At expiry, prevent new business operations. Proposed checkout rule: recheck immediately before committing; retain an unpaid cart if expired, but never erase or undo an already committed sale.
- Lockout preserves records. Proposed narrow exceptions are renewal/support entry and background upload/backup/licence checks, without business-record viewing. Confirm these exceptions before implementation of lockout UI.
- Offline freeze is shown as pending until acknowledged. Unfreeze/renewal requires a fresh signed entitlement, obtained online or through a controlled signed offline activation file.
- No silent grace period. Clock rollback and full local-administrator tampering cannot be eliminated by a normal offline desktop licence; mitigate and document the limit.

## Commercial and operational decisions still open

1. Permanent price, version/upgrade rights, cloud-service entitlement and support duration.
2. Trial start trigger and lockout recovery exceptions proposed above.
3. Minimum Windows version, hardware capacity, backup retention and acceptable data-loss window.
4. Support response versus resolution: the discussion expressed approximately three hours depending on travel; this is not a guaranteed three-hour repair SLA.
5. Paid feature catalogue, maintenance fees, custom-development ownership and delivery terms.
6. Which staff can receive stock, manage customer credit and view cost prices.
7. Tax invoice validation, privacy/retention obligations and production payment-provider terms.

These do not block a local billing prototype, but relevant items must be resolved before charging customers or production deployment.
