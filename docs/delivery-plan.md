# Delivery plan and acceptance checks

## Milestones

1. Desktop foundation: runnable installer, local schema/migrations, device identity, individual staff login, language resources, catalog and atomic sale/payment/receipt workflow.
2. Independent counters: durable event protocol, LAN relay, idempotency, disconnect/reconnect, immutable reversals and conflict visibility. Prove this before expanding the feature catalogue.
3. Shop operations: receiving, returns, transfers, cash shifts, credit collections, inventory counts, permissions and reports.
4. Cloud and licences: tenant isolation, business synchronization, read-only owner dashboard, operational provider dashboard, trial/permanent/subscription entitlement, payment verification and lockout.
5. Pilot readiness: real hardware, reviewed translations, tax handling, backup restore, installer/update tests, support procedure and customer onboarding.

This is an order of work, not a delivery-date estimate. No application has been built yet.

## First engineering milestone

Two Windows counters can complete cash sales independently, survive restart, reconnect through a relay, and reconcile to matching sales/cash/stock projections without duplicated transactions. Include the minimum product, login, receipt and audit screens necessary to exercise the workflow.

Proposed capacity fixture: two counters, 10,000 catalog products and 100,000 historical sale lines. Proposed local targets: p95 product lookup below 200 ms and sale commit below 500 ms, excluding printing/payment networks. Measure on documented reference hardware before setting supported capacity. These are test targets, not verified performance or commercial limits.

## Acceptance matrix

| ID | Scenario | Required evidence |
| --- | --- | --- |
| A01 | Cash sale commits, printer fails | Sale/tender/stock/audit/outbox exist once; reprint creates no second sale |
| A02 | Process terminates during commit | On restart all transaction effects exist or none do |
| A03 | Internet fails, relay remains | Both counters bill and exchange updates locally |
| A04 | Relay and internet fail | Both bill independently; pending events remain after restart |
| A05 | Event duplicated over LAN and cloud; acknowledgement lost | One business effect; retry eventually acknowledged |
| A06 | Both counters sell final unit while isolated | Both sales retained; reconciled stock negative and owner alerted |
| A07 | Events arrive out of order | Missing dependencies held; eventual projections equal after all events arrive |
| A08 | Cashier overrides/refunds/cancels | Reason and actor logged; linked movements correct; original retained |
| A09 | Cashier attempts direct stock adjustment | Rejected by business/service layer, not only hidden in UI |
| A10 | Shift includes change, cash refund, card and credit sale | Expected cash matches a hand-calculated fixture; variance preserved |
| A11 | Two counters try to refund one sale | Origin-authority rule prevents duplicate authorization; disconnected non-origin refund waits |
| A12 | Stock count overlaps sales; branch transfer incomplete | Count cutoff respected; transfer stays in-transit until received |
| A13 | Trial/subscription expires offline | New commits and business viewing blocked; existing data preserved |
| A14 | Remote freeze while device offline | Provider sees pending; applied on connection with acknowledgement |
| A15 | Duplicate payment callback, incorrect signature, stale licence | No double extension; invalid/stale inputs rejected |
| A16 | Expired device renewed | Fresh entitlement restores permitted access with same historical data |
| A17 | Owner dashboard has stale branch | Branch/counter freshness visible; totals not represented as current |
| A18 | Cross-tenant record IDs or provider user requests business data | Denied; no leakage in API, exports, logs or backup access |
| A19 | Restore snapshot / clone device | No receipt or event ID reuse; restore reconciles against acknowledged history |
| A20 | Switch English/Sinhala/Tamil | Navigation and validation translate; catalog values preserved; tested receipt glyphs render |
| A21 | Permanent licence with no subscription | Base functionality remains valid without monthly payment |
| A22 | Tax/discount/split-tender fixture | Line totals, bill totals, change and credit balance match specified rounding rules |
| A23 | Product price edited concurrently | Conflict visible; historical bills unchanged |
| A24 | Backup restored on replacement hardware | Records and unsynced events recover; keys and device re-enrollment work |

## Gates before a real shop relies on the product

- Resolve the commercial and operational questions listed in the specification.
- Verify cash/stock accounting with sample shop records and sign off expected outputs.
- Test selected hardware and translated text with actual users.
- Validate applicable Sri Lankan invoice and privacy requirements and payment integration terms.
- Complete isolation, failure, restore and upgrade checks; document known offline limits.
- Establish escalation, backup ownership, support hours and recovery instructions.

## Immediate next implementation task

Scaffold the desktop solution, domain model and local storage; implement one cash sale end-to-end with an audit event and durable outbox. Use meaningful transaction/failure tests from A01, A02 and A22. Bring a second counter into testing before building the rest of the modules.
